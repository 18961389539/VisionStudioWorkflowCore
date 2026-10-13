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
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(ordered), ["count"] = VisionValue.Integer(ordered.Length) },
            new Dictionary<string, object?> { ["count"] = ordered.Length, ["maxItems"] = maximum, ["order"] = "input" }));
    }
}

/// <summary>Explicit visual loop-body boundary; Workflow Core repeats it once per DetectionList item.</summary>
public sealed class LoopEndNode : IVisionNodeExecutor
{
    public string Type => "flow.loopEnd";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?> { ["boundary"] = "loopEnd" }));
    }
}
