using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision 几何计算节点：点/线/线段距离、夹角、交点、直线位姿、椭圆取点与滤波信息。
/// 纯数值节点，不需要图像，常用于机器人引导的偏移换算与标定后处理。
/// </summary>
public sealed class JLVisionGeometryNode : IVisionNodeExecutor
{
    public string Type => "jlvision.geometry";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = node.GetString("operator", "distancePp").Trim();
        var op = JlGeometryOperations.Resolve(key);
        OperatorParameterForms.Validate(node);

        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row1", 0), "row1");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column1", 0), "column1");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row2", 10), "row2");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column2", 10), "column2");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row3", 10), "row3");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column3", 0), "column3");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row4", 0), "row4");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column4", 10), "column4");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "radius1", 10), "radius1");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "radius2", 10), "radius2");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "phi", 0), "phi");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "angle", Math.PI / 2), "angle");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size", 1), "size");

        var args = new JlGeometryArgs
        {
            Row1 = OperatorParameterForms.Number(node, "row1", 0),
            Column1 = OperatorParameterForms.Number(node, "column1", 0),
            Row2 = OperatorParameterForms.Number(node, "row2", 10),
            Column2 = OperatorParameterForms.Number(node, "column2", 10),
            Row3 = OperatorParameterForms.Number(node, "row3", 10),
            Column3 = OperatorParameterForms.Number(node, "column3", 0),
            Row4 = OperatorParameterForms.Number(node, "row4", 0),
            Column4 = OperatorParameterForms.Number(node, "column4", 10),
            Radius1 = OperatorParameterForms.Number(node, "radius1", 10),
            Radius2 = OperatorParameterForms.Number(node, "radius2", 10),
            Phi = OperatorParameterForms.Number(node, "phi", 0),
            Angle = OperatorParameterForms.Number(node, "angle", Math.PI / 2),
            Size = OperatorParameterForms.Number(node, "size", 1),
            Mode = node.GetString("mode", string.Empty).Trim()
        };

        var result = op.Run(args);
        var payload = new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = result.Value };
        foreach (var (name, value) in result.Extra) payload[name] = value;

        // 调试可视化：把四个输入点画成轮廓 + 在画布上点出坐标，便于核对几何算子输入。
        var plot = new List<VisionPoint2D>
        {
            new(args.Column1, args.Row1), new(args.Column2, args.Row2),
            new(args.Column3, args.Row3), new(args.Column4, args.Row4)
        };
        // Geometry outputs describe computed results. Debug input points have a separate port.
        VisionPoint2D? computedPoint = null;
        if (key is "getPointsEllipse" or "projectionPl" || key == "intersectionLl" && payload["isParallel"] == 0)
            computedPoint = new VisionPoint2D(payload["column"], payload["row"]);
        else if (key == "linePosition")
            computedPoint = new VisionPoint2D(payload["columnCenter"], payload["rowCenter"]);
        var viewport = JLVisionNodeSupport.PreviewViewport(computedPoint is { } resultForBounds ? plot.Append(resultForBounds) : plot);
        using var overlaySurface = JLVisionNodeSupport.Canvas(viewport.Width, viewport.Height);
        foreach (var inputForPreview in plot) Cv2.Circle(overlaySurface, viewport.Project(inputForPreview), 2, new Scalar(0, 255, 255), -1);
        var points = computedPoint is { } resultPoint ? new[] { new VisionContour(0, new[] { resultPoint }, -1, false, 0, 0, 0,
            new(resultPoint.X, resultPoint.Y, 0, 0), new(resultPoint.X, resultPoint.Y, 0)) { Closed = false } } : Array.Empty<VisionContour>();
        var inputPoints = plot.Select((p, i) => new VisionContour(i, new[] { p }, -1, false, 0, 0, 0,
            new(p.X, p.Y, 0, 0), new(p.X, p.Y, 0)) { Closed = false }).ToArray();
        if (computedPoint is { } computed)
            Cv2.Circle(overlaySurface, viewport.Project(computed), 3, new Scalar(0, 255, 0), -1);
        var outputs = new Dictionary<string, VisionValue>
        {
            ["value"] = VisionValue.Double(result.Value),
            ["values"] = VisionValue.String(JsonSerializer.Serialize(payload, JsonOptions)),
            ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
            ["points"] = VisionValue.Contours(points),
            ["inputPoints"] = VisionValue.Contours(inputPoints),
            ["count"] = VisionValue.Integer(points.Length),
            ["found"] = VisionValue.Boolean(points.Length > 0),
            ["componentCount"] = VisionValue.Integer(payload.Count),
            ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                node.Type, "standard", 0, ("operator", key), ("value", result.Value), ("components", payload.Count), ("previewTransform", viewport)))
        };
        if (computedPoint is { } selectedOutput) outputs["point"] = VisionValue.Point(selectedOutput);
        var summary = new Dictionary<string, object?>
        {
            ["algorithmBackend"] = "jlvision",
            ["operator"] = key,
            ["operatorLabel"] = op.Label,
            ["parametersUsed"] = op.Note,
            ["value"] = result.Value,
            ["values"] = payload
        };
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }
}
