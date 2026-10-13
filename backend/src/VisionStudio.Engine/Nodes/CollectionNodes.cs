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
        cancellationToken.ThrowIfCancellationRequested(); var items=DetectionListInput.Read(context); var key=node.GetString("sortBy","area").Trim().ToLowerInvariant(); var descending=node.GetBool("descending",true);
        IOrderedEnumerable<VisionDetection> ordered=key switch
        {
            "x"=>descending?items.OrderByDescending(x=>x.X):items.OrderBy(x=>x.X),
            "y"=>descending?items.OrderByDescending(x=>x.Y):items.OrderBy(x=>x.Y),
            "circularity"=>descending?items.OrderByDescending(x=>x.Circularity):items.OrderBy(x=>x.Circularity),
            "perimeter"=>descending?items.OrderByDescending(x=>x.Perimeter):items.OrderBy(x=>x.Perimeter),
            "area"=>descending?items.OrderByDescending(x=>x.Area):items.OrderBy(x=>x.Area),
            _=>throw new InvalidOperationException("sortBy must be area, x, y, circularity, or perimeter.")
        };
        var result=ordered.Select((d,i)=>d with{Index=i}).ToArray();
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"detections",VisionValue.Detections(result)}},new Dictionary<string,object?>{{"count",result.Length},{"sortBy",key},{"descending",descending}}));
    }
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
