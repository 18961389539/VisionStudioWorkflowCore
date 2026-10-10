using System.Diagnostics;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WorkflowDefinition = VisionStudio.Abstractions.WorkflowDefinition;

namespace VisionStudio.Engine.Runtime;

/// <summary>Inspection snapshot of a live debug session (no native Mats cross the wire).</summary>
public sealed record DebugSessionSnapshot(
    string SessionId,
    string State,
    string? HaltNodeId,
    string? HaltReason,
    string? QualityDisposition,
    string? Error,
    IReadOnlyList<NodeRunReport> Reports,
    IReadOnlyList<VisionOverlay> Overlays,
    int PreviewWidth,
    int PreviewHeight,
    byte[]? PreviewJpeg,
    IReadOnlyList<string> ExecutedNodeIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

/// <summary>
/// A live breakpoint debug session. Holds the per-run <see cref="VisionWorkflowData"/> (cached node
/// outputs, overlays, images) and the pinned compiled plan, so segments can resume from the halt
/// point without re-running predecessors. Dispose releases native resources and the plan lease.
/// </summary>
public sealed class WorkflowDebugSession : IDisposable
{
    internal WorkflowDebugSession(
        string sessionId,
        WorkflowDefinition workflow,
        VisionRunOptions options,
        VisionWorkflowData data,
        WorkflowPlanCache.Lease planLease)
    {
        SessionId = sessionId;
        Workflow = workflow;
        Options = options;
        Data = data;
        PlanLease = planLease;
    }

    public string SessionId { get; }
    public WorkflowDefinition Workflow { get; }
    public VisionRunOptions Options { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.UtcNow;
    public bool Disposed => _disposed;

    internal VisionWorkflowData Data { get; }
    internal WorkflowPlanCache.Lease PlanLease { get; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    private volatile bool _disposed;
    private readonly object _releaseSync = new();
    private Task? _releaseTask;
    private volatile CancellationTokenSource? _activeExecution;

    /// <summary>
    /// 登记当前执行片段（continue / run-node），供 <see cref="ReleaseAsync"/> 取消并等待退出。
    /// 必须在持有 <see cref="Gate"/> 时调用；dispose 的注册在释放时清空登记。
    /// </summary>
    internal ExecutionRegistration BeginExecution(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeExecution = cts;
        return new ExecutionRegistration(this, cts);
    }

    internal sealed class ExecutionRegistration(WorkflowDebugSession session, CancellationTokenSource cts) : IDisposable
    {
        public CancellationToken Token => cts.Token;

        public void Dispose()
        {
            if (ReferenceEquals(session._activeExecution, cts)) session._activeExecution = null;
            cts.Dispose();
        }
    }

    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

    public DebugSessionSnapshot Snapshot()
    {
        var preview = Data.BuildPreview();
        return new DebugSessionSnapshot(
            SessionId,
            Data.Error is not null ? "Faulted" : Data.Halted ? "Halted" : "Completed",
            Data.HaltNodeId,
            Data.HaltReason,
            Data.QualityDisposition,
            Data.Error,
            Data.SnapshotReports(),
            Data.SnapshotOverlays(),
            preview?.Width ?? 0,
            preview?.Height ?? 0,
            preview?.Jpeg,
            Data.ExecutedNodeIds,
            CreatedAt,
            LastActivityAt);
    }

    /// <summary>
    /// 释放会话：先阻止新操作（Disposed 门），再取消并等待正在执行的 continue / run-node 退出，
    /// 最后释放计划租约与原生图像资源。所有并发调用者共享同一个释放任务并等待其完成——
    /// 删除请求与宿主清理交错时，任何调用者都不会在资源尚未释放时提前返回，调用方可以安全地
    /// 在 await 完成后释放硬件租约。执行收尾的 Gate.Release() 也不会撞上已销毁的信号量（不销毁 Gate）。
    /// </summary>
    public Task ReleaseAsync()
    {
        lock (_releaseSync)
        {
            return _releaseTask ??= ReleaseCoreAsync();
        }
    }

    private async Task ReleaseCoreAsync()
    {
        _disposed = true;
        try { _activeExecution?.Cancel(); }
        catch (ObjectDisposedException) { /* 与执行收尾的登记释放竞争：等待其退出即可 */ }
        await Gate.WaitAsync();
        try
        {
            PlanLease.Dispose();
            Data.Dispose();
        }
        finally { Gate.Release(); }
    }

    /// <summary>同步桥（测试 / 非 async 调用点）：等待执行退出的语义与 ReleaseAsync 相同。</summary>
    public void Dispose() => ReleaseAsync().GetAwaiter().GetResult();
}

/// <summary>
/// Owns debug-session segments on top of the plan cache: start (run to the first breakpoint),
/// continue (skip completed nodes, resume from the halt point using cached outputs) and
/// run-node (execute exactly one node from cached inputs, upstream untouched).
/// Sessions keep <see cref="VisionWorkflowData"/> alive until disposed - the caller (API layer)
/// owns lifetime, idle timeout and capacity policy.
/// </summary>
public sealed class WorkflowDebugSessionService(
    WorkflowPlanCache plans,
    ISyncWorkflowRunner syncRunner,
    VisionNodeRuntime nodeRuntime)
{
    public async Task<(WorkflowDebugSession Session, WorkflowRunResult Result)> StartAsync(
        WorkflowDefinition workflow,
        VisionRunOptions options,
        CancellationToken ct = default,
        string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != DebugRunMode.Breakpoints)
            throw new InvalidOperationException("Debug sessions currently support Breakpoints mode only.");
        if (options.Breakpoints is null || options.Breakpoints.Count == 0)
            throw new InvalidOperationException("Breakpoint debug requires at least one breakpoint node.");
        if (workflow.Nodes.Any(n => n.Type.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Debug sessions do not support workflows containing Parallel yet.");

        // 会话是交互式调试通道：始终捕获逐节点图像（运行期编码一次，供前端按节点查看中间结果）
        options = options with { CaptureNodeImages = true };

        // The caller may reserve the id up front (API layer takes hardware leases keyed by session id);
        // a missing id falls back to a fresh one for direct engine use.
        sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        var data = new VisionWorkflowData { Definition = workflow };
        data.Configure(options);
        var lease = await plans.AcquireAsync(workflow, ct);
        try
        {
            // Session executions share the same workflow scope as the plan they pinned.
            data.WorkflowScope = lease.Key;
            data.PipelineSegments = lease.Plan.PipelineSegments;
            data.ConfigureParallelDispositions(lease.Plan.ControlRegions);

            var risks = DebugSideEffectPolicy.FindImplicitSideEffectNodes(
                workflow, lease.Plan.OrderedNodeIds, options.Mode, options.TargetNodeId, plans.RequiresSideEffectApproval);
            if (risks.Count > 0 && !options.AllowSideEffects)
            {
                var list = string.Join(", ", risks.Select(x => $"{x.Id} ({x.Type})"));
                throw new InvalidOperationException(
                    $"Debug session would implicitly execute node(s) with external side effects: {list}. " +
                    "Approve with AllowSideEffects=true to run predecessors against live hardware.");
            }

            var session = new WorkflowDebugSession(sessionId, workflow, options, data, lease);
            var result = await RunSegmentAsync(session, resetTimeline: true, ct);
            return (session, result);
        }
        catch
        {
            data.Dispose();
            lease.Dispose();
            throw;
        }
    }

    public async Task<WorkflowRunResult> ContinueAsync(WorkflowDebugSession session, CancellationToken ct)
    {
        EnsureAlive(session);
        await session.Gate.WaitAsync(ct);
        try
        {
            // 拿锁后复查：释放可能在排队等待期间开始——此时已不能触碰被释放的资源
            EnsureAlive(session);
            using var execution = session.BeginExecution(ct);
            if (session.Data.Error is not null)
                throw new InvalidOperationException($"Debug session is faulted: {session.Data.Error}");
            if (!session.Data.Halted)
                throw new InvalidOperationException("Debug session is not halted; there is nothing to continue.");

            session.Data.ResumeFromHalt();
            return await RunSegmentAsync(session, resetTimeline: false, execution.Token);
        }
        finally { session.Gate.Release(); }
    }

    /// <summary>
    /// Executes exactly one node from cached upstream outputs ("run node with cached inputs").
    /// Predecessors are not re-executed and the session's halt state is preserved, so calling this
    /// repeatedly is the parameter-tuning loop for a downstream node.
    /// </summary>
    public async Task<WorkflowRunResult> RunNodeWithCachedInputsAsync(WorkflowDebugSession session, string nodeId, CancellationToken ct)
    {
        EnsureAlive(session);
        var node = session.Data.RequireNode(nodeId);
        await session.Gate.WaitAsync(ct);
        try
        {
            // 拿锁后复查：释放可能在排队等待期间开始——此时已不能触碰被释放的资源
            EnsureAlive(session);
            using var execution = session.BeginExecution(ct);
            session.Touch();
            var runId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            var total = Stopwatch.StartNew();
            string? error = null;
            try
            {
                session.Data.ForceExecuteNode(node.Id);
                var outcome = await nodeRuntime.ExecuteAsync(session.Data, node.Id, execution.Token);
                if (outcome != VisionNodeRuntimeOutcome.Executed)
                    error = $"Node '{node.Id}' was not executed ({outcome}).";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = session.Data.Error ?? ex.Message;
            }
            finally
            {
                session.Data.ClearError();
            }
            total.Stop();
            return BuildResult(runId, session.Data, success: error is null, total.Elapsed.TotalMilliseconds, startedAt, error);
        }
        finally { session.Gate.Release(); }
    }

    /// <summary>
    /// 会话快照：与执行共享同一 Gate，避免在 continue / run-node 执行中或会话释放中
    /// 读取正在变化/已释放的原生资源；执行期间调用会等待当前片段完成。
    /// </summary>
    public async Task<DebugSessionSnapshot> SnapshotAsync(WorkflowDebugSession session, CancellationToken ct = default)
    {
        EnsureAlive(session);
        await session.Gate.WaitAsync(ct);
        try
        {
            EnsureAlive(session);
            session.Touch();
            return session.Snapshot();
        }
        finally { session.Gate.Release(); }
    }

    private async Task<WorkflowRunResult> RunSegmentAsync(WorkflowDebugSession session, bool resetTimeline, CancellationToken ct)
    {
        var data = session.Data;
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();
        var timeoutMs = Math.Clamp(session.Options.TimeoutMs, 100, 600000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            session.Touch();
            if (resetTimeline) data.BeginExecutionTimeline();
            var plan = session.PlanLease.Plan;
            var instance = await syncRunner.RunWorkflowSync(
                plan.WorkflowCoreId,
                plan.Version,
                data,
                reference: runId,
                token: timeout.Token,
                persistSate: false);
            total.Stop();
            return BuildResult(runId, data, success: instance.Status == WorkflowStatus.Complete && data.Error is null, total.Elapsed.TotalMilliseconds, startedAt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            total.Stop();
            return BuildResult(runId, data, success: false, total.Elapsed.TotalMilliseconds, startedAt,
                $"Debug segment timed out after {timeoutMs} ms.");
        }
        catch (Exception ex)
        {
            total.Stop();
            return BuildResult(runId, data, success: false, total.Elapsed.TotalMilliseconds, startedAt, data.Error ?? ex.Message);
        }
    }

    private static WorkflowRunResult BuildResult(
        string runId,
        VisionWorkflowData data,
        bool success,
        double durationMs,
        DateTimeOffset startedAt,
        string? errorOverride = null)
    {
        var preview = data.BuildPreview();
        return new WorkflowRunResult(
            runId,
            success && data.Error is null,
            durationMs,
            preview is not null,
            preview?.Width ?? 0,
            preview?.Height ?? 0,
            data.SnapshotReports(),
            data.SnapshotOverlays(),
            preview?.Jpeg,
            errorOverride ?? data.Error,
            data.Halted ? "Breakpoint" : "Complete",
            data.HaltNodeId,
            data.HaltReason,
            data.QualityDisposition,
            data.SnapshotControlFlowDecisions(),
            data.BuildReplayInput())
        {
            StartedAt = startedAt,
            NodeImages = data.SnapshotNodeImages()
        };
    }

    private static void EnsureAlive(WorkflowDebugSession session)
    {
        if (session.Disposed)
            throw new InvalidOperationException("Debug session has been released.");
    }
}
