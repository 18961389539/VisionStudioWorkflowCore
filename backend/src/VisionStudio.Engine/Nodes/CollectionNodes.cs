using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

internal static class DetectionListInput
{
    public static int ReadIntegerParameter(NodeDefinition node, string name, int fallback, int minimum, int maximum = int.MaxValue)
    {
        if (node.Parameters is not null && node.Parameters.TryGetValue(name, out var raw) && raw.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"{node.Type} {name} must be an integer number.");
        return ResultFileWriter.Integer(node, name, fallback, minimum, maximum);
    }

    public static IReadOnlyList<VisionDetection> Read(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var source = context.Require<IReadOnlyList<VisionDetection>>("detections");
        foreach (var x in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(x.X) || !double.IsFinite(x.Y) || !double.IsFinite(x.Width) || !double.IsFinite(x.Height) ||
                !double.IsFinite(x.Area) || !double.IsFinite(x.Perimeter) || !double.IsFinite(x.Circularity) ||
                !double.IsFinite(x.Circle.X) || !double.IsFinite(x.Circle.Y) || !double.IsFinite(x.Circle.Radius) ||
                !double.IsFinite(x.BoundingBox.X) || !double.IsFinite(x.BoundingBox.Y) ||
                !double.IsFinite(x.BoundingBox.Width) || !double.IsFinite(x.BoundingBox.Height) || !double.IsFinite(x.BoundingBox.AngleDeg) ||
                x.Width < 0 || x.Height < 0 || x.Area < 0 || x.Perimeter < 0 || x.Circularity is < 0 or > 1 ||
                x.Circle.Radius < 0 || x.BoundingBox.Width < 0 || x.BoundingBox.Height < 0 ||
                x.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
                throw new InvalidOperationException("DetectionList contains non-finite geometry or measurement values.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return source;
    }
}

public sealed class DetectionFilterNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.filter";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

        var emptyPolicy = (node.GetString("emptyPolicy", "empty") ?? "empty").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail"))
            throw new InvalidOperationException(Type + " emptyPolicy must be empty or fail.");
        var items = DetectionListInput.Read(context, cancellationToken);
        var minArea = node.GetDouble("minArea", 0); var maxArea=node.GetDouble("maxArea",0); var minCircularity=node.GetDouble("minCircularity",0); var maxCircularity=node.GetDouble("maxCircularity",1);
        var minConfidence=node.GetDouble("minConfidence",-1); var classId=DetectionListInput.ReadIntegerParameter(node,"classId",-1,-1);
        if (!double.IsFinite(minArea)||!double.IsFinite(maxArea)||!double.IsFinite(minCircularity)||!double.IsFinite(maxCircularity)||!double.IsFinite(minConfidence)||minArea<0||maxArea<0||minCircularity<0||maxCircularity>1||minCircularity>maxCircularity||maxArea>0&&minArea>maxArea||minConfidence is < -1 or > 1||classId < -1)
            throw new InvalidOperationException("Filter bounds must be finite and ordered: areas >= 0 (maxArea=0 means unlimited), circularity in [0,1].");
        var filtered=items.Where(d=>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return d.Area>=minArea&&(maxArea<=0||d.Area<=maxArea)&&d.Circularity>=minCircularity&&d.Circularity<=maxCircularity&&
            (minConfidence < 0 || d.Confidence is { } confidence && confidence >= minConfidence) &&
            (classId < 0 || d.ClassId == classId);
        }).Select((d,i)=>d with{Index=i}).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (filtered.Length == 0 && emptyPolicy == "fail")
            throw new InvalidOperationException("collection.detections.filter produced no detections and emptyPolicy=fail.");
        var diag=CoordinateValidation.Diagnostics(Type,("inputCount",items.Count),("keptCount",filtered.Length),("rejectedCount",items.Count-filtered.Length),("minArea",minArea),("maxArea",maxArea),("classId",classId),("emptyPolicy",emptyPolicy));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"detections",VisionValue.Detections(filtered)},{"count",VisionValue.Integer(filtered.Length)},{"rejectedCount",VisionValue.Integer(items.Count-filtered.Length)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"inputCount",items.Count},{"count",filtered.Length},{"diagnostics",diag},{"minArea",minArea},{"maxArea",maxArea<=0?"unlimited":maxArea},{"minCircularity",minCircularity},{"maxCircularity",maxCircularity},{"minConfidence",minConfidence<0?"unlimited":minConfidence},{"classId",classId<0?"unlimited":classId}}));
    }
}

public sealed class DetectionSortNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.sort";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var items = DetectionListInput.Read(context, cancellationToken);
        var keys = new[]
        {
            (node.GetString("sortBy", "area"), node.GetBool("descending", true), required: true),
            (node.GetString("sortBy2", "none"), node.GetBool("descending2", true), required: false),
            (node.GetString("sortBy3", "none"), node.GetBool("descending3", true), required: false)
        };
        var topN = DetectionListInput.ReadIntegerParameter(node, "topN", 0, 0);
        IOrderedEnumerable<VisionDetection>? ordered = null;
        foreach (var (name, descending, required) in keys)
        {
            var key = name.Trim().ToLowerInvariant();
            if (key is "none" or "")
            {
                if (required) throw new InvalidOperationException("sortBy must name a detection field.");
                continue;
            }
            if (key is not ("area" or "x" or "y" or "circularity" or "perimeter" or "width" or "height" or "confidence" or "classid"))
                throw new InvalidOperationException("sortBy must be area, x, y, circularity, perimeter, width, height, confidence, or classId.");
            ordered = ordered is null
                ? Order(items, key, descending, cancellationToken)
                : Then(ordered, key, descending, cancellationToken);
        }
        IEnumerable<VisionDetection> selected = ordered ?? items.OrderBy(_ => 0);
        if (topN > 0) selected = selected.Take(topN);
        var result = selected.Select((detection, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return detection with { Index = index };
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("count", result.Length), ("sortBy", keys[0].Item1), ("descending", keys[0].Item2), ("empty", result.Length == 0));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(result), ["count"] = VisionValue.Integer(result.Length), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?>
            {
                ["count"] = result.Length,
                ["sortBy"] = keys[0].Item1,
                ["descending"] = keys[0].Item2,
                ["sortBy2"] = keys[1].Item1,
                ["sortBy3"] = keys[2].Item1,
                ["topN"] = topN,
                ["diagnostics"] = diag
            }));
    }

    private static IOrderedEnumerable<VisionDetection> Order(IReadOnlyList<VisionDetection> items, string key, bool descending, CancellationToken cancellationToken)
        => descending
            ? items.OrderBy(item => Rank(item, key, cancellationToken).Missing).ThenByDescending(item => Rank(item, key, cancellationToken).Value)
            : items.OrderBy(item => Rank(item, key, cancellationToken).Missing).ThenBy(item => Rank(item, key, cancellationToken).Value);

    private static IOrderedEnumerable<VisionDetection> Then(IOrderedEnumerable<VisionDetection> items, string key, bool descending, CancellationToken cancellationToken)
        => descending
            ? items.ThenBy(item => Rank(item, key, cancellationToken).Missing).ThenByDescending(item => Rank(item, key, cancellationToken).Value)
            : items.ThenBy(item => Rank(item, key, cancellationToken).Missing).ThenBy(item => Rank(item, key, cancellationToken).Value);

    private static (int Missing, double Value) Rank(VisionDetection item, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return key switch
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
}

public sealed class DetectionAtIndexNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.at";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var items=DetectionListInput.Read(context, cancellationToken); var index=DetectionListInput.ReadIntegerParameter(node,"index",0,0);
        var outputs=new Dictionary<string,VisionValue>{{"found",VisionValue.Boolean(index<items.Count)},{"index",VisionValue.Integer(index)}};
        if(index<items.Count){var d=items[index];outputs["x"]=VisionValue.Double(d.X);outputs["y"]=VisionValue.Double(d.Y);outputs["width"]=VisionValue.Double(d.Width);outputs["height"]=VisionValue.Double(d.Height);outputs["area"]=VisionValue.Double(d.Area);outputs["perimeter"]=VisionValue.Double(d.Perimeter);outputs["circularity"]=VisionValue.Double(d.Circularity);outputs["center"]=VisionValue.Point(new(d.Circle.X,d.Circle.Y));outputs["circle"]=VisionValue.Circle(d.Circle);outputs["rectangle"]=VisionValue.Rectangle(d.BoundingBox);if(d.Confidence is{}confidence)outputs["confidence"]=VisionValue.Double(confidence);if(d.ClassId is{}classId)outputs["classId"]=VisionValue.Integer(classId);}
        var diag=CoordinateValidation.Diagnostics(Type,("index",index),("found",index<items.Count),("count",items.Count)); outputs["diagnostics"]=VisionValue.String(diag);
        return ValueTask.FromResult(new NodeExecutorResult(outputs,new Dictionary<string,object?>{{"index",index},{"found",index<items.Count},{"count",items.Count},{"diagnostics",diag}}));
    }
}

public sealed class DetectionCountNode : IVisionNodeExecutor
{
    public string Type => "collection.detections.count";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

        var emptyPolicy = (node.GetString("emptyPolicy", "zero") ?? "zero").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("zero" or "fail"))
            throw new InvalidOperationException(Type + " emptyPolicy must be zero or fail.");

var items=DetectionListInput.Read(context, cancellationToken);
        if (items.Count == 0 && emptyPolicy == "fail")
            throw new InvalidOperationException("collection.detections.count received an empty list and emptyPolicy=fail.");
        var diag=CoordinateValidation.Diagnostics(Type,("count",items.Count),("empty",items.Count==0),("emptyPolicy",emptyPolicy)); return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"count",VisionValue.Integer(items.Count)},{"isEmpty",VisionValue.Boolean(items.Count==0)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"count",items.Count},{"diagnostics",diag},{"emptyPolicy",emptyPolicy}}));
    }
}
