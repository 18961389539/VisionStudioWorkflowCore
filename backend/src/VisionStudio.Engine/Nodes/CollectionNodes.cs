using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

internal static class DetectionListInput
{
    public static IReadOnlyList<VisionDetection> Read(NodeExecutionContext context)
    {
        var source = context.Require<IReadOnlyList<VisionDetection>>("detections");
        if (source.Any(x =>
            !double.IsFinite(x.X) || !double.IsFinite(x.Y) || !double.IsFinite(x.Width) || !double.IsFinite(x.Height) ||
            !double.IsFinite(x.Area) || !double.IsFinite(x.Perimeter) || !double.IsFinite(x.Circularity) ||
            !double.IsFinite(x.Circle.X) || !double.IsFinite(x.Circle.Y) || !double.IsFinite(x.Circle.Radius) ||
            !double.IsFinite(x.BoundingBox.X) || !double.IsFinite(x.BoundingBox.Y) ||
            !double.IsFinite(x.BoundingBox.Width) || !double.IsFinite(x.BoundingBox.Height) || !double.IsFinite(x.BoundingBox.AngleDeg) ||
            x.Width < 0 || x.Height < 0 || x.Area < 0 || x.Perimeter < 0 || x.Circularity is < 0 or > 1 ||
            x.Circle.Radius < 0 || x.BoundingBox.Width < 0 || x.BoundingBox.Height < 0 ||
            x.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1)))
            throw new InvalidOperationException("DetectionList contains non-finite geometry or measurement values.");
        return source;
    }
}

public sealed class DetectionFilterNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.filter";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var items = DetectionListInput.Read(context);
        var minArea=node.GetDouble("minArea",0); var maxArea=node.GetDouble("maxArea",0); var minCircularity=node.GetDouble("minCircularity",0); var maxCircularity=node.GetDouble("maxCircularity",1);
        var minConfidence=node.GetDouble("minConfidence",-1); var classId=node.GetInt("classId",-1);
        if (!double.IsFinite(minArea)||!double.IsFinite(maxArea)||!double.IsFinite(minCircularity)||!double.IsFinite(maxCircularity)||!double.IsFinite(minConfidence)||minArea<0||maxArea<0||minCircularity<0||maxCircularity>1||minCircularity>maxCircularity||maxArea>0&&minArea>maxArea||minConfidence is < -1 or > 1||classId < -1)
            throw new InvalidOperationException("Filter bounds must be finite and ordered: areas >= 0 (maxArea=0 means unlimited), circularity in [0,1].");
        var filtered=items.Where(d=>d.Area>=minArea&&(maxArea<=0||d.Area<=maxArea)&&d.Circularity>=minCircularity&&d.Circularity<=maxCircularity&&
            (minConfidence < 0 || d.Confidence is { } confidence && confidence >= minConfidence) &&
            (classId < 0 || d.ClassId == classId)).Select((d,i)=>d with{Index=i}).ToArray();
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"detections",VisionValue.Detections(filtered)},{"count",VisionValue.Integer(filtered.Length)}},new Dictionary<string,object?>{{"inputCount",items.Count},{"count",filtered.Length},{"minArea",minArea},{"maxArea",maxArea<=0?"unlimited":maxArea},{"minCircularity",minCircularity},{"maxCircularity",maxCircularity},{"minConfidence",minConfidence<0?"unlimited":minConfidence},{"classId",classId<0?"unlimited":classId}}));
    }
}

public sealed class DetectionSortNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.sort";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = DetectionListInput.Read(context);
        var keys = new[]
        {
            (node.GetString("sortBy", "area"), node.GetBool("descending", true), required: true),
            (node.GetString("sortBy2", "none"), node.GetBool("descending2", true), required: false),
            (node.GetString("sortBy3", "none"), node.GetBool("descending3", true), required: false)
        };
        var topN = node.GetInt("topN", 0);
        if (topN < 0) throw new InvalidOperationException("collection.detections.sort topN cannot be negative.");
        IOrderedEnumerable<VisionDetection>? ordered = null;
        foreach (var (name, descending, required) in keys)
        {
            var key = name.Trim().ToLowerInvariant();
            if (key is "none" or "")
            {
                if (required) throw new InvalidOperationException("sortBy must name a detection field.");
                continue;
            }
            ordered = ordered is null
                ? Order(items, key, descending)
                : Then(ordered, key, descending);
        }
        var sorted = (ordered ?? items.OrderBy(_ => 0)).ToArray();
        if (topN > 0) sorted = sorted.Take(topN).ToArray();
        var result = sorted.Select((detection, index) => detection with { Index = index }).ToArray();
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(result) },
            new Dictionary<string, object?>
            {
                ["count"] = result.Length,
                ["sortBy"] = keys[0].Item1,
                ["descending"] = keys[0].Item2,
                ["sortBy2"] = keys[1].Item1,
                ["sortBy3"] = keys[2].Item1,
                ["topN"] = topN
            }));
    }

    private static IOrderedEnumerable<VisionDetection> Order(IReadOnlyList<VisionDetection> items, string key, bool descending)
        => descending
            ? items.OrderBy(item => Rank(item, key).Missing).ThenByDescending(item => Rank(item, key).Value)
            : items.OrderBy(item => Rank(item, key).Missing).ThenBy(item => Rank(item, key).Value);

    private static IOrderedEnumerable<VisionDetection> Then(IOrderedEnumerable<VisionDetection> items, string key, bool descending)
        => descending
            ? items.ThenBy(item => Rank(item, key).Missing).ThenByDescending(item => Rank(item, key).Value)
            : items.ThenBy(item => Rank(item, key).Missing).ThenBy(item => Rank(item, key).Value);

    private static (int Missing, double Value) Rank(VisionDetection item, string key) => key switch
    {
        "area" => (0, item.Area),
        "x" => (0, item.X),
        "y" => (0, item.Y),
        "circularity" => (0, item.Circularity),
        "perimeter" => (0, item.Perimeter),
        "width" => (0, item.Width),
        "height" => (0, item.Height),
        "confidence" => item.Confidence is { } confidence ? (0, confidence) : (1, 0),
        "classid" => item.ClassId is { } classId ? (0, classId) : (1, 0),
        _ => throw new InvalidOperationException("sortBy must be area, x, y, circularity, perimeter, width, height, confidence, or classId.")
    };
}

public sealed class DetectionAtIndexNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.at";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var items=DetectionListInput.Read(context); var index=node.GetInt("index",0);
        if(index<0) throw new InvalidOperationException("Detection index is zero-based and cannot be negative.");
        var outputs=new Dictionary<string,VisionValue>{{"found",VisionValue.Boolean(index<items.Count)},{"index",VisionValue.Integer(index)}};
        if(index<items.Count){var d=items[index];outputs["x"]=VisionValue.Double(d.X);outputs["y"]=VisionValue.Double(d.Y);outputs["width"]=VisionValue.Double(d.Width);outputs["height"]=VisionValue.Double(d.Height);outputs["area"]=VisionValue.Double(d.Area);outputs["perimeter"]=VisionValue.Double(d.Perimeter);outputs["circularity"]=VisionValue.Double(d.Circularity);outputs["center"]=VisionValue.Point(new(d.Circle.X,d.Circle.Y));outputs["circle"]=VisionValue.Circle(d.Circle);outputs["rectangle"]=VisionValue.Rectangle(d.BoundingBox);if(d.Confidence is{}confidence)outputs["confidence"]=VisionValue.Double(confidence);if(d.ClassId is{}classId)outputs["classId"]=VisionValue.Integer(classId);}
        return ValueTask.FromResult(new NodeExecutorResult(outputs,new Dictionary<string,object?>{{"index",index},{"found",index<items.Count},{"count",items.Count}}));
    }
}

public sealed class DetectionCountNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.count";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var items=DetectionListInput.Read(context);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"count",VisionValue.Integer(items.Count)}},new Dictionary<string,object?>{{"count",items.Count}}));
    }
}
