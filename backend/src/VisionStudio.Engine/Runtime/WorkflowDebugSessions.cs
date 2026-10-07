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
    public bool Disposed { get; private set; }

    internal VisionWorkflowData Data { get; }
    internal WorkflowPlanCache.Lease PlanLease { get; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);

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

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Gate.Dispose();
        PlanLease.Dispose();
        Data.Dispose();
    }
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
            if (session.Data.Error is not null)
                throw new InvalidOperationException($"Debug session is faulted: {session.Data.Error}");
            if (!session.Data.Halted)
                throw new InvalidOperationException("Debug session is not halted; there is nothing to continue.");

            session.Data.ResumeFromHalt();
            return await RunSegmentAsync(session, resetTimeline: false, ct);
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
            session.Touch();
            var runId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            var total = Stopwatch.StartNew();
            string? error = null;
            try
            {
                session.Data.ForceExecuteNode(node.Id);
                var outcome = await nodeRuntime.ExecuteAsync(session.Data, node.Id, ct);
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
