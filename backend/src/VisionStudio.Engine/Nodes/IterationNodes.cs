using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>Prepares a bounded DetectionList for the structured workflow ForEach primitive.</summary>
public sealed class DetectionForEachNode : IVisionNodeExecutor
{
    public string Type => "flow.forEach";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var maximum = DetectionListInput.ReadIntegerParameter(node, "maxItems", 100, 1, 500);
        var detections = DetectionListInput.Read(context, cancellationToken);
        if (detections.Count > maximum) throw new InvalidOperationException($"flow.forEach received {detections.Count} detections, exceeding maxItems={maximum}.");
        var ordered = detections.Select((d, i) => d with { Index = i }).ToArray();
        var diag = CoordinateValidation.Diagnostics(Type, ("count", ordered.Length), ("maxItems", maximum), ("empty", ordered.Length == 0), ("order", "input"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(ordered), ["count"] = VisionValue.Integer(ordered.Length), ["isEmpty"] = VisionValue.Boolean(ordered.Length == 0), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["count"] = ordered.Length, ["maxItems"] = maximum, ["order"] = "input", ["diagnostics"] = diag }));
    }
}

/// <summary>Explicit visual loop-body boundary; Workflow Core repeats it once per DetectionList item.</summary>
public sealed class LoopEndNode : IVisionNodeExecutor
{
    public string Type => "flow.loopEnd";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

if (node is null) throw new InvalidOperationException("flow.loopEnd requires a node definition.");
        if (string.IsNullOrWhiteSpace(node.Id)) throw new InvalidOperationException("flow.loopEnd requires a node id.");
        if (node.Id.Length > 128) throw new InvalidOperationException("flow.loopEnd node id must be at most 128 characters.");
        var id = node.Id.Trim();
        var emitIndex = node.GetBool("emitIterationHint", false);
        var diag = CoordinateValidation.Diagnostics(Type, ("boundary", "loopEnd"), ("marker", "loopEnd"), ("nodeId", id), ("emitIterationHint", emitIndex));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["boundary"] = VisionValue.String("loopEnd"),
                ["marker"] = VisionValue.String("loopEnd"),
                ["nodeId"] = VisionValue.String(id),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["boundary"] = "loopEnd", ["marker"] = "loopEnd", ["nodeId"] = id, ["emitIterationHint"] = emitIndex, ["diagnostics"] = diag }));
    }
}
