using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>Prepares a bounded DetectionList for the structured workflow ForEach primitive.</summary>
public sealed class DetectionForEachNode : IVisionNodeExecutor
{
    public string Type => "flow.forEach";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maximum = node.GetInt("maxItems", 100);
        if (maximum is < 1 or > 500) throw new InvalidOperationException("flow.forEach maxItems must be between 1 and 500.");
        var detections = DetectionListInput.Read(context);
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
        var diag = CoordinateValidation.Diagnostics(Type, ("boundary", "loopEnd"), ("marker", "loopEnd"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["boundary"] = "loopEnd", ["diagnostics"] = diag }));
    }
}
