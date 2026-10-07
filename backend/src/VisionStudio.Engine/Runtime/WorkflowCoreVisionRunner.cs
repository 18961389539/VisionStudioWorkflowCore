using System.Diagnostics;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WorkflowDefinition = VisionStudio.Abstractions.WorkflowDefinition;

namespace VisionStudio.Engine.Runtime;

public sealed class WorkflowCoreVisionRunner(
    WorkflowPlanCache plans,
    ISyncWorkflowRunner syncRunner) : IVisionWorkflowRunner
{

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition workflow,
        VisionRunOptions? options = null,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveRunId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;
        var startedAt = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();
        var data = new VisionWorkflowData { Definition = workflow };
        data.Configure(options);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutMs = Math.Clamp(options?.TimeoutMs ?? 10000, 100, 600000);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            ValidateDebugOptions(workflow, options);
            using var planLease = await plans.AcquireAsync(workflow, timeout.Token);
            var compiled = planLease.Plan;
            // PerNode plugin instances are scoped to the plan identity: same workflow content reuses its cached
            // instances, a different workflow (or edited content) never does.
            data.WorkflowScope = planLease.Key;
            data.PipelineSegments = compiled.PipelineSegments;
            data.ConfigureParallelDispositions(compiled.ControlRegions);
            EnsureDebugSideEffectsAllowed(workflow, compiled.OrderedNodeIds, options, plans);

            data.BeginExecutionTimeline();
            var instance = await syncRunner.RunWorkflowSync(
                compiled.WorkflowCoreId,
                compiled.Version,
                data,
                reference: effectiveRunId,
                token: timeout.Token,
                persistSate: false);

            var deferEncoding = options?.Artifacts?.DeferEncoding == true;
            var preview = deferEncoding ? null : data.BuildPreview();
            var replayInput = deferEncoding ? null : data.BuildReplayInput();
            var overlays = data.SnapshotOverlays();

            var reachedTarget = options?.Mode switch
            {
                DebugRunMode.RunNode => data.HaltNodeId?.Equals(options.TargetNodeId, StringComparison.OrdinalIgnoreCase) == true,
                DebugRunMode.RunFromNode => data.ReportStartReached,
                _ => true
            };

            var workflowComplete = instance.Status == WorkflowStatus.Complete && data.Error is null;
            var success = workflowComplete && reachedTarget;
            var error = data.Error;
            if (error is null && !reachedTarget)
                error = $"Debug target '{options?.TargetNodeId}' was not reached on the executed control-flow path.";
            if (error is null && !workflowComplete)
                error = $"Workflow Core status: {instance.Status}";
            // The internal watchdog cancels the run token, so node faults surface as cancellation messages.
            // Attribute the cause so the text agrees with the timeout error code below.
            if (error is not null && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                error = $"Workflow Core execution timed out after {timeoutMs} ms ({error}).";

            var debugState = data.Halted
                ? options?.Mode == DebugRunMode.Breakpoints ? "Breakpoint" : "Stopped"
                : "Complete";

            var artifacts = deferEncoding
                ? data.CaptureArtifacts(effectiveRunId, success ? data.QualityDisposition ?? "OK" : "ERROR", options!.Artifacts!)
                : null;
            total.Stop();

            return new WorkflowRunResult(
                effectiveRunId,
                success,
                total.Elapsed.TotalMilliseconds,
                preview is not null,
                preview?.Width ?? 0,
                preview?.Height ?? 0,
                data.SnapshotReports(),
                overlays,
                preview?.Jpeg,
                error,
                debugState,
                data.HaltNodeId,
                data.HaltReason,
                data.QualityDisposition,
                data.SnapshotControlFlowDecisions(),
                replayInput)
            {
                DeferredArtifacts = artifacts,
                StartedAt = startedAt,
                NodeImages = data.SnapshotNodeImages(),
                // Workflow Core may surface a step fault as a suspended instance instead of an exception,
                // so the interrupted lifecycle states are re-derived here too: data.Error is set by the node
                // runtime whenever a node faults, including cancellation-induced faults.
                ErrorCode = data.Error is null
                    ? null
                    : cancellationToken.IsCancellationRequested ? VisionRunErrorCodes.Cancelled
                    : timeout.IsCancellationRequested ? VisionRunErrorCodes.Timeout
                    : VisionRunErrorCodes.NodeFailure
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            total.Stop();
            return new WorkflowRunResult(
                effectiveRunId,
                false,
                total.Elapsed.TotalMilliseconds,
                false,
                0,
                0,
                data.SnapshotReports(),
                data.SnapshotOverlays(),
                null,
                $"Workflow Core execution timed out after {Math.Clamp(options?.TimeoutMs ?? 10000, 100, 600000)} ms.",
                "Error",
                ControlFlowDecisions: data.SnapshotControlFlowDecisions())
            {
                StartedAt = startedAt,
                NodeImages = data.SnapshotNodeImages(),
                ErrorCode = VisionRunErrorCodes.Timeout
            };
        }
        // Caller-requested cancellation (client disconnect, controlled stop) is an expected lifecycle
        // outcome, not a fault: keep it distinct from the internal timeout above and from node faults.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            total.Stop();
            return new WorkflowRunResult(
                effectiveRunId,
                false,
                total.Elapsed.TotalMilliseconds,
                false,
                0,
                0,
                data.SnapshotReports(),
                data.SnapshotOverlays(),
                null,
                "Run was cancelled before completion.",
                "Error",
                data.HaltNodeId,
                data.HaltReason,
                ControlFlowDecisions: data.SnapshotControlFlowDecisions())
            {
                StartedAt = startedAt,
                NodeImages = data.SnapshotNodeImages(),
                ErrorCode = VisionRunErrorCodes.Cancelled
            };
        }
        catch (Exception ex)
        {
            total.Stop();
            return new WorkflowRunResult(
                effectiveRunId,
                false,
                total.Elapsed.TotalMilliseconds,
                false,
                0,
                0,
                data.SnapshotReports(),
                data.SnapshotOverlays(),
                null,
                data.Error ?? ex.Message,
                "Error",
                data.HaltNodeId,
                data.HaltReason,
                ControlFlowDecisions: data.SnapshotControlFlowDecisions())
            {
                StartedAt = startedAt,
                NodeImages = data.SnapshotNodeImages(),
                ErrorCode = data.Error is not null ? VisionRunErrorCodes.NodeFailure : VisionRunErrorCodes.RuntimeError
            };
        }
        finally
        {
            data.Dispose();
        }
    }

    private static void ValidateDebugOptions(WorkflowDefinition workflow, VisionRunOptions? options)
    {
        options ??= new VisionRunOptions();
        if (options.Mode is DebugRunMode.RunNode or DebugRunMode.RunFromNode)
        {
            if (string.IsNullOrWhiteSpace(options.TargetNodeId))
                throw new InvalidOperationException($"{options.Mode} requires TargetNodeId.");
            if (!workflow.Nodes.Any(n => n.Id.Equals(options.TargetNodeId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Debug target '{options.TargetNodeId}' does not exist.");
        }

        if (options.Mode == DebugRunMode.Breakpoints && (options.Breakpoints is null || options.Breakpoints.Count == 0))
            throw new InvalidOperationException("Breakpoint debug requires at least one breakpoint node.");

        if (options.Mode != DebugRunMode.Full && workflow.Nodes.Any(n => n.Type.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("V0.29 node debugging remains intentionally disabled for workflows containing Parallel. Parallel-safe stepping requires a stateful debug session and will be added later.");
    }

    private static void EnsureDebugSideEffectsAllowed(
        WorkflowDefinition workflow,
        IReadOnlyList<string> orderedNodeIds,
        VisionRunOptions? options,
        WorkflowPlanCache plans)
    {
        if (options is null || options.Mode == DebugRunMode.Full || options.AllowSideEffects) return;

        var risks = DebugSideEffectPolicy.FindImplicitSideEffectNodes(
            workflow, orderedNodeIds, options.Mode, options.TargetNodeId, plans.RequiresSideEffectApproval);
        if (risks.Count == 0) return;

        var list = string.Join(", ", risks.Select(x => $"{x.Id} ({x.Type})"));
        throw new InvalidOperationException(
            $"Debug mode '{options.Mode}' would implicitly execute node(s) with external side effects: {list}. " +
            "Approve with AllowSideEffects=true, or start a debug session and resume from cached outputs instead of re-running predecessors.");
    }
}
