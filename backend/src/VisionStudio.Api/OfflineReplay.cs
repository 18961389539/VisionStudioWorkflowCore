using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed record OfflineReplayContext(
    string SourceRunId,
    RunTraceRecord Trace,
    WorkflowDefinition Workflow,
    bool Replayable,
    string? BlockReason,
    bool HasReplayInput,
    string? ReplaySourceNodeId,
    IReadOnlyList<string> OrderedNodeIds,
    string ExecutionModel);

public sealed record OfflineReplayRequest(
    WorkflowDefinition? Workflow = null,
    VisionRunOptions? Options = null);

public sealed record NodeReplayComparison(
    string NodeId,
    string NodeType,
    bool? BaselineSuccess,
    bool? ReplaySuccess,
    double? BaselineDurationMs,
    double? ReplayDurationMs,
    double? DurationDeltaMs,
    bool SummaryChanged,
    string Status);

public sealed record OfflineReplayResult(
    string SourceRunId,
    string RunId,
    bool Success,
    double TotalDurationMs,
    bool PreviewAvailable,
    int PreviewWidth,
    int PreviewHeight,
    IReadOnlyList<VisionOverlay> Overlays,
    IReadOnlyList<NodeRunReport> NodeReports,
    string? Error,
    string DebugState,
    string? HaltNodeId,
    string? HaltReason,
    string? QualityDisposition,
    IReadOnlyList<ControlFlowDecision> ControlFlowDecisions,
    IReadOnlyList<NodeReplayComparison> Comparison,
    string? BaselineDisposition,
    bool DispositionChanged,
    string ExecutionModel);

/// <summary>
/// Deterministic offline replay over persisted trace input evidence. V0.47 deliberately
/// supports single-image acquisition workflows and deterministic software-only workflows.
/// Synchronized multi-camera FrameSet replay requires a future multi-image artifact format.
/// </summary>
public sealed class OfflineReplayService(
    TraceabilityStore traces,
    RunTraceRecorder recorder,
    IVisionWorkflowRunner runner,
    RunStore runs,
    VisionWorkflowCompiler compiler,
    WorkflowModuleExpander modules,
    ProductionRuntimeService production)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<OfflineReplayContext> GetContextAsync(string runId, CancellationToken ct)
    {
        var trace = await traces.GetAsync(runId, ct)
            ?? throw new ApiNotFoundException($"Trace '{runId}' was not found.");
        var workflow = await traces.GetWorkflowSnapshotAsync(runId, ct)
            ?? throw new ApiConflictException($"Trace '{runId}' does not have a resolvable workflow snapshot.");
        var expanded = await modules.ExpandAsync(workflow, ct);
        var capability = EvaluateCapability(trace, expanded.Workflow);
        var compiled = compiler.Compile(expanded.Workflow);
        return new OfflineReplayContext(
            runId,
            trace,
            workflow,
            capability.Replayable,
            capability.Reason,
            trace.HasReplayInput,
            trace.ReplaySourceNodeId,
            compiled.OrderedNodeIds,
            "Deterministic re-execution: Run Node / Run From Here warm up predecessors from the persisted replay input; no suspended native process state is retained.");
    }

    public Task<OfflineReplayResult> ExecuteAsync(string sourceRunId, OfflineReplayRequest request, CancellationToken ct)
        => ExecuteAsync(sourceRunId, request, "Replay", ct);

    public async Task<OfflineReplayResult> ExecuteAsync(string sourceRunId, OfflineReplayRequest request, string traceSource, CancellationToken ct, bool persistReplayInput = true, bool persistTrace = true)
    {
        var baseline = await traces.GetAsync(sourceRunId, ct)
            ?? throw new ApiNotFoundException($"Trace '{sourceRunId}' was not found.");
        var baselineWorkflow = await traces.GetWorkflowSnapshotAsync(sourceRunId, ct)
            ?? throw new ApiConflictException($"Trace '{sourceRunId}' does not have a resolvable workflow snapshot.");

        var expandedBaseline = await modules.ExpandAsync(baselineWorkflow, ct);
        var capability = EvaluateCapability(baseline, expandedBaseline.Workflow);
        if (!capability.Replayable)
            throw new ApiConflictException(capability.Reason ?? "Trace is not replayable.");

        var editedWorkflow = request.Workflow ?? baselineWorkflow;
        EnsureReplayCompatible(baselineWorkflow, editedWorkflow);
        var expandedEdited = await modules.ExpandAsync(editedWorkflow, ct);
        var executable = PrepareExecutableWorkflow(sourceRunId, expandedEdited.Workflow, baseline);
        var options = request.Options ?? new VisionRunOptions(DebugRunMode.Full);
        var context = new RunTraceContext(traceSource, DebugMode: options.Mode.ToString());
        var runId = RunTraceRecorder.NewRunId();
        // 回放可能保留真实硬件节点（设备写、机器人指令等）：引用硬件资产时取运行期租约，纯离线路径不占租约
        using var lease = await production.AcquireRunLeaseAsync(runId, executable, $"Cannot replay trace '{sourceRunId}'", ct);
        if (persistTrace) await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, editedWorkflow, context);
        var result = await runner.RunAsync(executable, options, runId, ct);

        // Store the user-visible workflow without the private internal replay path. Tuning previews are
        // intentionally transient so slider/ROI interaction does not flood durable trace history.
        // Finalization ignores request cancellation so a disconnect cannot erase the replay evidence.
        if (persistTrace)
            await recorder.FinalizeAsync(persistReplayInput ? result : result with { ReplayInput = null }, editedWorkflow, context);
        runs.Put(result);

        var comparisons = Compare(baseline.NodeReports, result.NodeReports);
        var replayDisposition = result.QualityDisposition ?? (result.Success ? "OK" : "ERROR");
        return new OfflineReplayResult(
            sourceRunId,
            result.RunId,
            result.Success,
            result.TotalDurationMs,
            result.PreviewAvailable,
            result.PreviewWidth,
            result.PreviewHeight,
            result.Overlays,
            result.NodeReports,
            result.Error,
            result.DebugState,
            result.HaltNodeId,
            result.HaltReason,
            result.QualityDisposition,
            result.ControlFlowDecisions ?? [],
            comparisons,
            baseline.Disposition,
            !string.Equals(baseline.Disposition, replayDisposition, StringComparison.OrdinalIgnoreCase),
            "offline-replay");
    }

    public Task<OfflineReplayResult> ExecuteImageAsync(string sourceRef, string imagePath, WorkflowDefinition workflow, string traceSource, CancellationToken ct, bool persistReplayInput = false)
        => ExecuteImageAsync(sourceRef, imagePath, workflow, traceSource, new VisionRunOptions(DebugRunMode.Full), ct, persistReplayInput, persistTrace: true);

    public async Task<OfflineReplayResult> ExecuteImageAsync(string sourceRef, string imagePath, WorkflowDefinition workflow, string traceSource, VisionRunOptions options, CancellationToken ct, bool persistReplayInput = false, bool persistTrace = true)
    {
        var expanded = await modules.ExpandAsync(workflow, ct);
        var executableWorkflow = expanded.Workflow;
        if (executableWorkflow.Nodes.Any(x => x.Type.Equals("camera.syncCapture", StringComparison.OrdinalIgnoreCase)))
            throw new ApiConflictException("Dataset image validation does not support synchronized FrameSet acquisition yet.");
        var acquisitionNodes = executableWorkflow.Nodes.Where(x => x.Type.Equals("image.acquire", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (acquisitionNodes.Length != 1)
            throw new ApiConflictException("Media-library validation requires exactly one image.acquire node in the candidate workflow.");
        if (!File.Exists(imagePath))
            throw new ApiNotFoundException($"Validation image '{sourceRef}' is unavailable.");

        var executable = PrepareExecutableWorkflowFromImagePath(executableWorkflow, imagePath);
        var context = new RunTraceContext(traceSource, DebugMode: options.Mode.ToString());
        var runId = RunTraceRecorder.NewRunId();
        using var lease = await production.AcquireRunLeaseAsync(runId, executable, $"Cannot validate image '{sourceRef}'", ct);
        if (persistTrace) await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, workflow, context);
        var result = await runner.RunAsync(executable, options, runId, ct);
        if (persistTrace)
            await recorder.FinalizeAsync(persistReplayInput ? result : result with { ReplayInput = null }, workflow, context);
        runs.Put(result);
        return new OfflineReplayResult(
            sourceRef, result.RunId, result.Success, result.TotalDurationMs, result.PreviewAvailable, result.PreviewWidth, result.PreviewHeight,
            result.Overlays, result.NodeReports, result.Error, result.DebugState, result.HaltNodeId, result.HaltReason, result.QualityDisposition,
            result.ControlFlowDecisions ?? [], [], null, false, "dataset-image-replay");
    }

    private WorkflowDefinition PrepareExecutableWorkflowFromImagePath(WorkflowDefinition workflow, string imagePath)
    {
        var acquisition = workflow.Nodes.Single(x => x.Type.Equals("image.acquire", StringComparison.OrdinalIgnoreCase));
        var nodes = workflow.Nodes.Select(node =>
        {
            if (!node.Id.Equals(acquisition.Id, StringComparison.OrdinalIgnoreCase)) return node;
            var parameters = node.Parameters is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(node.Parameters, StringComparer.OrdinalIgnoreCase);
            parameters["__replayInputPath"] = JsonSerializer.SerializeToElement(imagePath, _json);
            parameters["__replayCameraId"] = JsonSerializer.SerializeToElement("dataset-media", _json);
            parameters["__replayTimestamp"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("O"), _json);
            return node with { Parameters = parameters };
        }).ToArray();
        return workflow with { Nodes = nodes };
    }

    private (bool Replayable, string? Reason) EvaluateCapability(RunTraceRecord trace, WorkflowDefinition workflow)
    {
        if (workflow.Nodes.Any(x => x.Type.Equals("camera.syncCapture", StringComparison.OrdinalIgnoreCase)))
            return (false, "V0.47 does not replay synchronized FrameSet acquisition. Use single-image traces for offline replay; multi-camera replay will require persisted FrameSet artifacts.");

        var acquisitionNodes = workflow.Nodes.Where(x => x.Type.Equals("image.acquire", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (acquisitionNodes.Length > 1)
            return (false, "V0.47 supports one image.acquire source per replay workflow.");
        if (acquisitionNodes.Length == 1 && !trace.HasReplayInput)
            return (false, "This trace has no persisted replay input. Run the workflow again after upgrading to V0.47, or use a deterministic software-only workflow.");
        return (true, null);
    }

    private WorkflowDefinition PrepareExecutableWorkflow(string sourceRunId, WorkflowDefinition workflow, RunTraceRecord baseline)
    {
        var acquisition = workflow.Nodes.FirstOrDefault(x => x.Type.Equals("image.acquire", StringComparison.OrdinalIgnoreCase));
        if (acquisition is null) return workflow;

        var replayPath = traces.FindReplayInputPath(sourceRunId)
            ?? throw new ApiConflictException("Replay input artifact has expired or is unavailable.");
        var nodes = workflow.Nodes.Select(node =>
        {
            if (!node.Id.Equals(acquisition.Id, StringComparison.OrdinalIgnoreCase)) return node;
            var parameters = node.Parameters is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(node.Parameters, StringComparer.OrdinalIgnoreCase);
            parameters["__replayInputPath"] = JsonSerializer.SerializeToElement(replayPath, _json);
            var acquisitionReport = baseline.NodeReports.LastOrDefault(x => x.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
            if (acquisitionReport is not null)
            {
                if (TrySummaryLong(acquisitionReport.Summary, "sequence", out var sequence))
                    parameters["__replaySequence"] = JsonSerializer.SerializeToElement(sequence, _json);
                if (TrySummaryString(acquisitionReport.Summary, "timestamp", out var timestamp))
                    parameters["__replayTimestamp"] = JsonSerializer.SerializeToElement(timestamp, _json);
                if (TrySummaryString(acquisitionReport.Summary, "camera", out var cameraId))
                    parameters["__replayCameraId"] = JsonSerializer.SerializeToElement(cameraId, _json);
            }
            return node with { Parameters = parameters };
        }).ToArray();
        return workflow with { Nodes = nodes };
    }

    private static bool TrySummaryString(IReadOnlyDictionary<string, object?> summary, string key, out string value)
    {
        value = string.Empty;
        if (!summary.TryGetValue(key, out var raw) || raw is null) return false;
        if (raw is JsonElement element)
        {
            value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }
        value = raw.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TrySummaryLong(IReadOnlyDictionary<string, object?> summary, string key, out long value)
    {
        value = 0;
        if (!summary.TryGetValue(key, out var raw) || raw is null) return false;
        if (raw is JsonElement element)
            return element.ValueKind == JsonValueKind.Number ? element.TryGetInt64(out value) : long.TryParse(element.ToString(), out value);
        try { value = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture); return true; }
        catch { return false; }
    }

    private static void EnsureReplayCompatible(WorkflowDefinition baseline, WorkflowDefinition edited)
    {
        static string NodeKey(NodeDefinition node) => $"{node.Id}|{node.Type}".ToLowerInvariant();
        static string EdgeKey(EdgeDefinition edge) => $"{edge.SourceNodeId}|{edge.SourcePort}|{edge.TargetNodeId}|{edge.TargetPort}|{edge.Kind}".ToLowerInvariant();

        var baselineNodes = baseline.Nodes.Select(NodeKey).OrderBy(x => x).ToArray();
        var editedNodes = edited.Nodes.Select(NodeKey).OrderBy(x => x).ToArray();
        if (!baselineNodes.SequenceEqual(editedNodes))
            throw new ApiValidationException("Offline replay may change node parameters/ROI/name/layout, but node IDs and node types must remain identical to the source trace.");

        var baselineEdges = baseline.Edges.Select(EdgeKey).OrderBy(x => x).ToArray();
        var editedEdges = edited.Edges.Select(EdgeKey).OrderBy(x => x).ToArray();
        if (!baselineEdges.SequenceEqual(editedEdges))
            throw new ApiValidationException("Offline replay may not change workflow topology. Save topology changes as a new Job/Workflow and validate them separately.");
    }

    private bool SummaryEquals(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
        => string.Equals(JsonSerializer.Serialize(left, _json), JsonSerializer.Serialize(right, _json), StringComparison.Ordinal);

    private IReadOnlyList<NodeReplayComparison> Compare(IReadOnlyList<NodeRunReport> baseline, IReadOnlyList<NodeRunReport> replay)
    {
        var left = baseline.GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var right = replay.GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var ids = left.Keys.Concat(right.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return ids.Select(id =>
        {
            left.TryGetValue(id, out var before);
            right.TryGetValue(id, out var after);
            var summaryChanged = before is not null && after is not null && !SummaryEquals(before.Summary, after.Summary);
            var status = before is null ? "New" : after is null ? "NotExecuted" : before.Success != after.Success ? "StatusChanged" : summaryChanged ? "Changed" : "Same";
            return new NodeReplayComparison(
                id,
                after?.NodeType ?? before?.NodeType ?? "unknown",
                before?.Success,
                after?.Success,
                before?.DurationMs,
                after?.DurationMs,
                before is not null && after is not null ? after.DurationMs - before.DurationMs : null,
                summaryChanged,
                status);
        }).ToArray();
    }
}
