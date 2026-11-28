using System.Globalization;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision 轮廓节点：对输入的每条轮廓执行 XLD 算子（平滑/分段/裁剪/筛选/并差交/生成），
/// 输出仍是平台轮廓列表，可直接接 contour.properties 或几何拟合节点。
/// </summary>
public sealed class JLVisionContourOpNode : IVisionNodeExecutor
{
    public string Type => "jlvision.contourOp";

    private const int MaxOutputPoints = 2000000;

    private static readonly Dictionary<string,string[]> EffectiveParameters = new(StringComparer.Ordinal)
    {
        ["genCircle"]=["row","column","size","phi"], ["genEllipse"]=["row","column","phi","size","length1"],
        ["genRectangle2"]=["row","column","phi","size","length1"], ["genCross"]=["row","column","size","phi"],
        ["genContourPolygonRoundedXld"]=["row","column","size"], ["closeContours"]=[], ["smoothContours"]=["count"],
        ["segmentContours"]=["count","size","length1"], ["genParallelContour"]=["size","mode"], ["clipContours"]=["row","column","size","length1"],
        ["cropContours"]=["row","column","size","length1","mode"], ["selectShapeXld"]=["mode","min","max"], ["selectContoursXld"]=["mode","min","max"],
        ["selectXldPoint"]=["row","column"], ["unionAdjacentContours"]=["size","length1","mode"], ["unionCollinearContours"]=["size","length1","min","max","mode"],
        ["unionCocircularContours"]=["size","length1","max"], ["regressContoursXld"]=["mode","count"], ["sortContoursXld"]=["mode"],
        ["clipEndPointsContoursXld"]=["mode","length1"], ["addNoiseWhiteContourXld"]=["count","size"],
        ["unionCollinearContoursExtXld"]=["size","length1","min"], ["unionCotangentialContoursXld"]=["size","length1","min"],
        ["unionStraightContoursHistoXld"]=["size"], ["removeXldCont"]=["count"], ["shapeTransXld"]=["mode"]
    };

    private static void ValidateOperatorContract(NodeDefinition node, JlContourOperator op)
    {
        var allowed=EffectiveParameters.GetValueOrDefault(op.Key,[]);
        var catalog=BuiltInNodeCatalog.Require(node.Type);
        var defaults=catalog.Parameters.Where(p=>p.Type=="number")
            .Select(p=>OperatorParameterForms.Resolve(node,catalog,p))
            .ToDictionary(p=>p.Name,p=>Convert.ToDouble(p.DefaultValue,CultureInfo.InvariantCulture));
        var irrelevant=defaults.Where(p=>!allowed.Contains(p.Key)&&node.GetDouble(p.Key,p.Value)!=p.Value).Select(p=>p.Key).ToArray();
        var mode=OperatorParameterForms.Resolve(node,catalog,catalog.Parameters.Single(p=>p.Name=="mode"));
        var modeDefault=Convert.ToString(mode.DefaultValue,CultureInfo.InvariantCulture)??"";
        if(!allowed.Contains("mode") && !string.IsNullOrWhiteSpace(node.GetString("mode",modeDefault))) irrelevant=[..irrelevant,"mode"];
        if(irrelevant.Length>0) throw new InvalidOperationException($"jlvision.contourOp operator='{op.Key}' rejects irrelevant non-default parameter(s): {string.Join(",",irrelevant)}; effective parameters: {string.Join(",",allowed)}.");
    }

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = node.GetString("operator", "smoothContours").Trim();
        var op = JlContourOperations.Resolve(key);
        ValidateOperatorContract(node,op);
        var input = op.IgnoresInput ? Array.Empty<VisionContour>() : context.Require<IReadOnlyList<VisionContour>>("contour");
        OperatorParameterForms.Validate(node);

        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size", 5), "size");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row", 0), "row");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column", 0), "column");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "phi", 0), "phi");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "length1", 10), "length1");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "length2", 10), "length2");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "min", 0), "min");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "max", 1e9), "max");

        if (op.NeedsSecond && !context.TryGetValue("contour2", out _))
            throw new InvalidOperationException($"JLVision contour operator '{key}' requires a contour2 input.");
        if (op.NeedsRegion && !context.TryGetValue("region", out _))
            throw new InvalidOperationException($"JLVision contour operator '{key}' requires a region input.");

        var source = op.IgnoresInput ? new List<VisionContour>() : [.. input];
        if (!op.IgnoresInput && source.Count == 0)
            throw new InvalidOperationException("jlvision.contourOp requires at least one input contour.");
        var generatedFrame=CoordinateValidation.RequireFrame(node.GetString("coordinateFrame","ImagePixel"),"coordinateFrame");
        var generatedUnit=CoordinateValidation.RequireUnit(node.GetString("unit","px"),"unit");
        if(op.NeedsSecond)
        {
            var secondInput=context.Require<IReadOnlyList<VisionContour>>("contour2");
            if(secondInput.Count==0) throw new InvalidOperationException($"JLVision contour operator '{key}' requires at least one contour in contour2.");
            var first=source[0];
            if(secondInput.Any(c=>!string.Equals(c.CoordinateFrame,first.CoordinateFrame,StringComparison.OrdinalIgnoreCase)||!string.Equals(c.Unit,first.Unit,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"jlvision.contourOp operator='{key}' set-input coordinate contract mismatch: contour frame/unit must match contour2.");
        }

        JlXLDCont? second = null;
        JlRegion? region = null;
        var output = new List<VisionContour>();
        try
        {
            if (op.NeedsSecond && context.TryGetValue("contour2", out var secondValue)
                && secondValue.Value is IReadOnlyList<VisionContour> secondContours && secondContours.Count > 0)
                second = ToXld(secondContours[0]);
            if (op.NeedsSecond && second is null)
                throw new InvalidOperationException($"JLVision contour operator '{key}' requires at least one contour in contour2.");

            if (op.NeedsRegion && context.TryGetValue("region", out var regionValue) && regionValue.Value is VisionRegion visionRegion)
                region = JLVisionRegions.ToNative(visionRegion);

            var size = OperatorParameterForms.Number(node, "size", 5);
            var iterations = node.GetInt("count", 3);
            var row = OperatorParameterForms.Number(node, "row", 0);
            var column = OperatorParameterForms.Number(node, "column", 0);
            var phi = OperatorParameterForms.Number(node, "phi", 0);
            var length1 = OperatorParameterForms.Number(node, "length1", 10);
            var length2 = OperatorParameterForms.Number(node, "length2", 10);
            var min = OperatorParameterForms.Number(node, "min", 0);
            var max = OperatorParameterForms.Number(node, "max", 1e9);
            var mode = node.GetString("mode", string.Empty).Trim();

            var items = op.IgnoresInput ? new List<VisionContour?> { null } : [.. source.Select(item => (VisionContour?)item)];
            foreach (var contour in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var owned = contour is null ? new JlXLDCont() : ToXld(contour);
                var local = new JlContourArgs
                {
                    Contour = owned,
                    Second = second,
                    Region = region,
                    Size = size,
                    Count = iterations,
                    Row = row,
                    Column = column,
                    Phi = phi,
                    Length1 = length1,
                    Length2 = length2,
                    Min = min,
                    Max = max,
                    Mode = mode
                };
                using var result = op.Run(local);
                var firstOutput = output.Count;
                AppendContours(result, output, cancellationToken);
                if (contour is not null)
                    for (var i = firstOutput; i < output.Count; i++) output[i] = output[i] with { Unit = contour.Unit, CoordinateFrame = contour.CoordinateFrame };
                else
                    for (var i = firstOutput; i < output.Count; i++) output[i] = output[i] with { Unit = generatedUnit, CoordinateFrame = generatedFrame };
            }

            var pointCount = output.Sum(item => item.Points.Count);
            var viewport = JLVisionNodeSupport.PreviewViewport(output.SelectMany(item => item.Points));
            using var overlaySurface = JLVisionNodeSupport.Canvas(viewport.Width, viewport.Height);
            JLVisionNodeSupport.DrawPreviewContours(overlaySurface, output, new Scalar(0, 255, 0), viewport);
            var outputs = new Dictionary<string, VisionValue>
            {
                ["contours"] = VisionValue.Contours(output),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["count"] = VisionValue.Integer(output.Count),
                ["pointCount"] = VisionValue.Integer(pointCount),
                ["operator"] = VisionValue.String(key),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("operator", key), ("inputCount", source.Count),
                    ("outputCount", output.Count), ("outputPointCount", pointCount), ("previewTransform", viewport)))
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["operator"] = key,
                ["operatorLabel"] = op.Label,
                ["parametersUsed"] = op.Note,
                ["inputCount"] = source.Count,
                ["outputCount"] = output.Count,
                ["outputPointCount"] = output.Sum(item => item.Points.Count)
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision contour operator '{key}' failed: {ex.Message} (parameters: {op.Note}).", ex);
        }
        finally
        {
            second?.Dispose();
            region?.Dispose();
        }
    }

    /// <summary>把结果 XLD 的每个对象拆成平台轮廓，保留多段结构。</summary>
    private static void AppendContours(JlXLDCont source, List<VisionContour> output, CancellationToken token)
    {
        foreach (var contour in JlContourCodec.FromNative(source, token, MaxOutputPoints))
            output.Add(contour with { Index = output.Count });
    }

    private static JlXLDCont ToXld(VisionContour contour)
    {
        return JlContourCodec.ToNative(new[] { contour });
    }
}
