using System.Collections.Concurrent;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record DebugSessionCreateRequest(WorkflowDefinition Workflow, VisionRunOptions Options);

public sealed record DebugSessionRunNodeRequest(string NodeId);

/// <summary>
/// Process-wide owner of live debug sessions: enforces a capacity limit and an idle timeout, holds the
/// hardware lease for the whole session lifetime (paused sessions keep devices owned), and guarantees
/// that native vision resources (Mats, plan leases) and hardware leases are released on delete,
/// eviction, idle expiry or host shutdown.
/// </summary>
public sealed class DebugSessionService : IDisposable, IAsyncDisposable
{
    private sealed class Entry(WorkflowDebugSession session, DeviceLease lease)
    {
        public WorkflowDebugSession Session { get; } = session;
        public DeviceLease Lease { get; } = lease;
    }

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly WorkflowDebugSessionService _engine;
    private readonly RuntimeDependencyManifestService _dependencies;
    private readonly DeviceLeaseRegistry _leases;
    /// <summary>F01：调试会话的每次执行都受统一设备动作授权约束（允许副作用 ≠ 上一动作已核对）。</summary>
    private readonly DeviceActionAuthorizationService _authorization;
    /// <summary>N07：调试片段的节点报告/耗时/结果必须耐久提交——追溯提交失败不得解除动作意图。</summary>
    private readonly RunTraceRecorder _traces;
    /// <summary>N01：模块展开器——动作分类与完成判定必须消费展开后的执行计划。</summary>
    private readonly WorkflowModuleExpander _modules;
    /// <summary>扩展安全契约：显式动作声明的目录解析入口（与内置分类走同一判定）。</summary>
    private readonly VisionNodeRegistry _catalog;
    private readonly int _maxSessions;
    private readonly TimeSpan _idleTimeout;
    private readonly ILogger<DebugSessionService> _logger;
    private readonly Timer _sweeper;
    private int _disposed;
    private int _sweeping;

    public DebugSessionService(
        WorkflowDebugSessionService engine,
        RuntimeDependencyManifestService dependencies,
        DeviceLeaseRegistry leases,
        DeviceActionAuthorizationService authorization,
        WorkflowModuleExpander modules,
        RunTraceRecorder traces,
        VisionNodeRegistry catalog,
        IConfiguration configuration,
        ILogger<DebugSessionService> logger)
    {
        _engine = engine;
        _dependencies = dependencies;
        _leases = leases;
        _authorization = authorization;
        _modules = modules;
        _traces = traces;
        _catalog = catalog;
        _maxSessions = Math.Clamp(configuration.GetValue("DebugSessions:MaxSessions", 4), 1, 32);
        _idleTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("DebugSessions:IdleTimeoutSeconds", 300), 30, 3600));
        _logger = logger;
        _sweeper = new Timer(_ => { _ = SweepIdleAsync(); }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<(string SessionId, WorkflowRunResult Result)> CreateAsync(
        WorkflowDefinition workflow,
        VisionRunOptions options,
        CancellationToken ct)
    {
        await SweepIdleAsync();
        await EvictIfFullAsync();

        // F01：含副作用的调试会话在首次运行（到达断点前可能已执行副作用节点——探针已确认
        // "write 在 breakpoint 之前执行"）前必须通过统一动作授权闸门，并耐久登记意图。
        // AllowSideEffects 只代表用户同意执行副作用，不代表上一动作已经安全核对。
        // N07：会话 ID 提前生成——意图的 RelatedRunId 必须能定位到本次调试会话（此前传 null）。
        var sessionId = Guid.NewGuid().ToString("N");
        // N01：用**展开后的执行计划**判定副作用（模块内的设备动作不能被漏检）。
        var effectiveWorkflow = (await _modules.ExpandAsync(workflow, ct)).Workflow;
        DeviceActionHandle? guard = null;
        if (DeviceSideEffectClassifier.HasDeviceSideEffects(effectiveWorkflow, _catalog))
            guard = await _authorization.BeginWorkflowIntentAsync("debug.session.create", effectiveWorkflow, sessionId, ct);
        try
        {
            var started = await CreateCoreAsync(workflow, options, sessionId, ct);
            // C01：成功到达断点 + 报告成功 + 设备动作确认完成才解除意图；失败/取消保留为 Unknown。
            // A03：Create 到断点前可能已执行设备动作——追溯证据必须先于意图闭合耐久提交；
            // 提交失败保留 Unknown 并传播失败状态（绝不返回成功）。
            if (guard is not null && started.Result.Success)
            {
                await SubmitFragmentEvidenceAsync(started.Result, workflow, options, started.SessionId, guard);
                if (!DeviceSideEffectClassifier.RequiresManualReconciliation(effectiveWorkflow, started.Result, _catalog))
                    guard.Complete();
                else
                    guard.MarkUnknown("the debug session reached its breakpoint but an accepted device action still awaits completion confirmation");
            }
            return started;
        }
        finally { guard?.Dispose(); }
    }

    private async Task<(string SessionId, WorkflowRunResult Result)> CreateCoreAsync(
        WorkflowDefinition workflow,
        VisionRunOptions options,
        string sessionId,
        CancellationToken ct)
    {
        // The lease must cover the engine's initial run to the first breakpoint (it can already touch
        // hardware), so the id is reserved by the caller and the lease owner matches the session for its lifetime.
        WorkflowRuntimeReferences references;
        try
        {
            references = await _dependencies.ExtractReferencesAsync(workflow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ApiConflictException(
                $"Cannot arbitrate debug session hardware references for workflow '{workflow.Id}': {ex.Message}", ex);
        }

        var lease = _leases.AcquireOrThrow(
            DeviceLeaseOwner.DebugSession(sessionId),
            DeviceLeaseResource.FromReferences(references),
            $"Cannot start a debug session for workflow '{workflow.Id}'");

        (WorkflowDebugSession Session, WorkflowRunResult Result) started;
        try
        {
            // A02：会话片段的真实派发携带会话租约令牌——令牌被换代/撤销后旧会话不能再写设备。
            using var commandScope = DeviceCommandOwnership.Enter(lease);
            started = await _engine.StartAsync(workflow, options, ct, sessionId);
        }
        catch (InvalidOperationException ex)
        {
            lease.Dispose();
            throw new ApiValidationException(ex.Message, ex);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        _sessions[started.Session.SessionId] = new Entry(started.Session, lease);
        _logger.LogInformation(
            "Debug session {SessionId} started: halt={HaltNodeId}, reports={ReportCount}.",
            started.Session.SessionId, started.Result.HaltNodeId ?? "-", started.Result.NodeReports.Count);
        return (started.Session.SessionId, started.Result);
    }

    public async Task<WorkflowRunResult> ContinueAsync(string sessionId, CancellationToken ct)
    {
        var entry = Require(sessionId);
        EnsureLeaseHeld(entry, "continue");
        // F01：每次继续执行都必须重新通过授权闸门——运行期间产生安全故障（未闭合意图）后，
        // 已有会话也不能继续下发动作。
        // N01：继续执行同样用展开后的执行计划判定（模块内的设备动作不能被漏检）。
        var effectiveWorkflow = (await _modules.ExpandAsync(entry.Session.Workflow, ct)).Workflow;
        DeviceActionHandle? guard = null;
        if (DeviceSideEffectClassifier.HasDeviceSideEffects(effectiveWorkflow, _catalog))
            guard = await _authorization.BeginWorkflowIntentAsync("debug.session.continue", effectiveWorkflow, sessionId, ct);
        try
        {
            // A02：会话片段的真实派发携带会话租约令牌——令牌被换代/撤销后旧会话不能再写设备。
            using var commandScope = DeviceCommandOwnership.Enter(entry.Lease);
            var result = await _engine.ContinueAsync(entry.Session, ct);
            if (guard is not null && result.Success)
            {
                // N07/A03：片段动作证据（节点报告/耗时/结果）必须先耐久提交——提交失败不得解除意图，
                // 并传播失败状态（保留 Unknown 供核对）。
                await SubmitFragmentEvidenceAsync(result, entry.Session.Workflow, entry.Session.Options, entry.Session.SessionId, guard);
                if (!DeviceSideEffectClassifier.RequiresManualReconciliation(effectiveWorkflow, result, _catalog))
                    guard.Complete();
                else
                    guard.MarkUnknown("the debug session continued successfully but an accepted device action still awaits completion confirmation");
            }
            return result;
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiConflictException(ex.Message, ex);
        }
        finally { guard?.Dispose(); }
    }

    public async Task<WorkflowRunResult> RunNodeAsync(string sessionId, string nodeId, CancellationToken ct)
    {
        var entry = Require(sessionId);
        if (string.IsNullOrWhiteSpace(nodeId) ||
            !entry.Session.Workflow.Nodes.Any(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase)))
            throw new ApiNotFoundException($"Node '{nodeId}' is not part of debug session '{sessionId}'.");
        EnsureLeaseHeld(entry, "run-node");
        // F01：单节点执行与"继续"同一契约——未闭合意图存在时不得再下发任何动作。
        // N01：单节点执行同样用展开后的执行计划判定。
        var effectiveWorkflow = (await _modules.ExpandAsync(entry.Session.Workflow, ct)).Workflow;
        DeviceActionHandle? guard = null;
        if (DeviceSideEffectClassifier.HasDeviceSideEffects(effectiveWorkflow, _catalog))
            guard = await _authorization.BeginWorkflowIntentAsync("debug.session.run-node", effectiveWorkflow, sessionId, ct);
        try
        {
            // A02：会话片段的真实派发携带会话租约令牌——令牌被换代/撤销后旧会话不能再写设备。
            using var commandScope = DeviceCommandOwnership.Enter(entry.Lease);
            var result = await _engine.RunNodeWithCachedInputsAsync(entry.Session, nodeId, ct);
            if (guard is not null && result.Success)
            {
                // N07/A03：单节点片段同样先提交耐久证据；提交失败保留 Unknown 并传播失败状态。
                await SubmitFragmentEvidenceAsync(result, entry.Session.Workflow, entry.Session.Options, entry.Session.SessionId, guard);
                if (!DeviceSideEffectClassifier.RequiresManualReconciliation(effectiveWorkflow, result, _catalog))
                    guard.Complete();
                else
                    guard.MarkUnknown("the debug node run returned success but an accepted device action still awaits completion confirmation");
            }
            return result;
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiConflictException(ex.Message, ex);
        }
        finally { guard?.Dispose(); }
    }

    /// <summary>
    /// N07/A03：把调试片段的完整证据（节点报告/耗时/结果/关联会话与片段 RunId）耐久提交到运行追溯。
    /// 提交失败必须**传播失败状态并保留动作意图 Unknown**（供管理员核对）——
    /// 旧实现只记日志后调用方照常 Complete()，"intent stays unresolved" 的日志与实际行为相反，
    /// 造成"实际写了设备但无追溯、无待核对意图、调用方仍看到成功"的假象。
    /// </summary>
    private async Task SubmitFragmentEvidenceAsync(
        WorkflowRunResult result, WorkflowDefinition workflow, VisionRunOptions options, string sessionId, DeviceActionHandle guard)
    {
        try
        {
            // FinalizeAsync 自带 10 秒有界超时且失败向上抛（"failure surfaces to the caller"）。
            await _traces.FinalizeAsync(result, workflow,
                new RunTraceContext("DebugFragment", DebugMode: options.Mode.ToString(), Note: sessionId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Debug fragment evidence for session {SessionId} (fragment run {RunId}) could not be persisted; the action intent stays unresolved.",
                sessionId, result.RunId);
            guard.MarkUnknown($"the debug fragment evidence could not be persisted ({ex.Message}); the action outcome is not closed");
            throw new ApiUnavailableException(
                $"Debug session {sessionId}: the device action ran but its fragment evidence (run {result.RunId}) could not be persisted ({ex.Message}). " +
                "The action intent stays unresolved — reconcile the device state via 'POST /api/production/device-actions/resolve' before issuing new actions.");
        }
    }

    public async Task<DebugSessionSnapshot> SnapshotAsync(string sessionId, CancellationToken ct = default)
    {
        var entry = Require(sessionId);
        try
        {
            // 会话快照与执行共享 Gate：执行中会等待当前片段完成，释放中会被拒绝，不会读到已释放资源
            return await _engine.SnapshotAsync(entry.Session, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiConflictException(ex.Message, ex);
        }
    }

    public async Task<bool> DeleteAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry)) return false;
        await ReleaseAsync(entry, "deleted");
        return true;
    }

    public int ActiveSessionCount => _sessions.Count;

    private Entry Require(string sessionId)
        => _sessions.TryGetValue(sessionId, out var entry)
            ? entry
            : throw new ApiNotFoundException($"Debug session '{sessionId}' does not exist (it may have expired).");

    /// <summary>
    /// Paused sessions keep hardware leased; resuming must assert the lease is still held so a session
    /// can never touch hardware after its ownership was lost.
    /// </summary>
    private void EnsureLeaseHeld(Entry entry, string operation)
    {
        var lease = entry.Lease;
        if (!lease.IsReleased && _leases.IsHeldBy(lease.Owner, lease.Resources)) return;
        throw new ApiConflictException(
            $"Debug session '{entry.Session.SessionId}' no longer holds its hardware lease; the '{operation}' request was blocked. Delete the session and start a new one.");
    }

    private async Task EvictIfFullAsync()
    {
        while (_sessions.Count >= _maxSessions)
        {
            var oldest = _sessions.Values.OrderBy(x => x.Session.LastActivityAt).FirstOrDefault();
            if (oldest is null) return;
            if (_sessions.TryRemove(oldest.Session.SessionId, out var evicted))
                await ReleaseAsync(evicted, "evicted (capacity)");
        }
    }

    private async Task SweepIdleAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // 释放现在需要等待执行退出，耗时可能超过扫描间隔：重入直接跳过本轮
        if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _sessions.Values)
            {
                if (now - entry.Session.LastActivityAt <= _idleTimeout) continue;
                if (_sessions.TryRemove(entry.Session.SessionId, out var expired))
                    await ReleaseAsync(expired, "expired (idle)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debug session idle sweep failed.");
        }
        finally { Interlocked.Exchange(ref _sweeping, 0); }
    }

    private async Task ReleaseAsync(Entry entry, string reason)
    {
        try
        {
            // 先阻止新操作并等待正在执行的 continue / run-node 退出（必要时取消它），再释放原生
            // 视觉资源与硬件租约——释放不再发生在执行使用资源的中途，执行收尾的 Gate 也不再撞上已销毁的信号量。
            await entry.Session.ReleaseAsync();
            _logger.LogInformation("Debug session {SessionId} released: {Reason}.", entry.Session.SessionId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debug session {SessionId} release failed.", entry.Session.SessionId);
        }
        finally
        {
            entry.Lease.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sweeper.Dispose();
        // 同步桥：等待每个会话的执行退出后再释放（语义与 DisposeAsync 相同，供同步调用点使用）
        ReleaseAllAsync("host shutdown").GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sweeper.Dispose();
        await ReleaseAllAsync("host shutdown");
    }

    private async Task ReleaseAllAsync(string reason)
    {
        foreach (var entry in _sessions.Values) await ReleaseAsync(entry, reason);
        _sessions.Clear();
    }
}