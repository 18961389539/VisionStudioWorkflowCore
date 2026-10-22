using System.Text.Json;
using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision 一维计量节点：矩形或圆弧测量对象 + 四种测量方式（位置/边缘对/阈值/投影）。
/// 与 <c>measure.caliper</c> 相比走的是原生 measure 对象路径，支持圆弧与投影两种原生没有节点承载的形态。
/// </summary>
public sealed class JLVisionMeasure1dNode : IVisionNodeExecutor
{
    public string Type => "jlvision.measure1d";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty())
            throw new InvalidOperationException("jlvision.measure1d requires a non-empty image.");

        var shape = node.GetString("shape", "rectangle").Trim().ToLowerInvariant();
        if (shape is not ("rectangle" or "arc")) throw new InvalidOperationException("shape must be rectangle or arc.");
        var method = node.GetString("method", "measurePos").Trim().ToLowerInvariant() switch
        {
            "measurepairs" or "pairs" => JlMeasureMethod.MeasurePairs,
            "measurethresh" or "thresh" => JlMeasureMethod.MeasureThresh,
            "measureprojection" or "projection" => JlMeasureMethod.MeasureProjection,
            "measurepos" or "pos" => JlMeasureMethod.MeasurePos,
            _ => throw new InvalidOperationException("Unknown 1D measurement method.")
        };
        var sigma = node.GetDouble("sigma", 1);
        if (!double.IsFinite(sigma) || sigma is < 0.4 or > 100)
            throw new InvalidOperationException("jlvision.measure1d sigma must be a finite value between 0.4 and 100.");
        var threshold = node.GetDouble("threshold", 30);
        var transition = node.GetString("transition", "all").Trim();
        var select = node.GetString("select", "all").Trim();

        CoordinateValidation.RequireFinite(sigma, "sigma");
        CoordinateValidation.RequireFinite(threshold, "threshold");
        CoordinateValidation.RequireFinite(node.GetDouble("measureLength1", 40), "measureLength1");
        CoordinateValidation.RequireFinite(node.GetDouble("measureLength2", 2), "measureLength2");
        CoordinateValidation.RequireFinite(node.GetDouble("row", source.Height / 2d), "row");
        CoordinateValidation.RequireFinite(node.GetDouble("column", source.Width / 2d), "column");
        CoordinateValidation.RequireFinite(node.GetDouble("radius", 40), "radius");
        CoordinateValidation.RequireFinite(node.GetDouble("annulusRadius", 10), "annulusRadius");

        var gray = PreprocessImageHelpers.ToGray8(source.Mat, out _, out var ownedGray);
        using (ownedGray)
        using (var image = JLVisionImages.FromMat(gray))
        using (var measure = new JlMeasure())
        {
            var half = node.GetDouble("measureLength1", 40);
            switch (shape)
            {
                case "arc":
                    measure.GenMeasureArc(
                        node.GetDouble("row", source.Height / 2d), node.GetDouble("column", source.Width / 2d),
                        node.GetDouble("radius", 40),
                        node.GetDouble("angleStart", -180) * Math.PI / 180d, node.GetDouble("angleExtent", 360) * Math.PI / 180d,
                        node.GetDouble("annulusRadius", 10), source.Width, source.Height, "bilinear");
                    break;
                default:
                    if (node.GetDouble("measureLength2", 2) <= 0)
                        throw new InvalidOperationException("jlvision.measure1d requires a positive measureLength2.");
                    measure.GenMeasureRectangle2(
                        node.GetDouble("row", source.Height / 2d), node.GetDouble("column", source.Width / 2d),
                        node.GetDouble("phi", 0) * Math.PI / 180d,
                        half, node.GetDouble("measureLength2", 2),
                        source.Width, source.Height, "bilinear");
                    break;
            }

            // 实测 TranslateMeasure 即使传 (0,0) 也会让后续测量全部返回空，封装层不可用，未接入。

            var rows = JlMeasureRunner.Run(measure, method, image, sigma, threshold, transition, select);
            // reset_fuzzy_measure 在未设置模糊函数时会抛 JlTuple 越界，未接入。
            measure.CloseMeasure();

            var finiteRows = rows
                .Select(row => row.Where(pair => double.IsFinite(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value))
                .Where(row => row.Count > 0).ToList();
            rows = finiteRows;

            // 把找到的边缘点抽成平台轮廓，供下游拟合/可视化；同时画到 overlay 上便于调试。
            var edgePoints = new List<VisionPoint2D>();
            foreach (var row in rows)
            {
                AppendPoint(edgePoints, row, "row", "column");
                AppendPoint(edgePoints, row, "rowFirst", "columnFirst");
                AppendPoint(edgePoints, row, "rowSecond", "columnSecond");
            }
            var contours = new List<VisionContour>();
            if (edgePoints.Count >= 2)
                contours.Add(new VisionContour(0, edgePoints, -1, false, 0, 0, 0,
                    new VisionRectangle2D(0, 0, 0, 0), new VisionCircle(0, 0, 0)));
            using var overlaySurface = JLVisionNodeSupport.Canvas(source.Width, source.Height);
            foreach (var point in edgePoints)
                OpenCvSharp.Cv2.Circle(overlaySurface,
                    new OpenCvSharp.Point((int)Math.Round(point.X), (int)Math.Round(point.Y)), 1,
                    new OpenCvSharp.Scalar(0, 255, 255), -1);
            var outputs = new Dictionary<string, VisionValue>
            {
                ["edges"] = VisionValue.String(JsonSerializer.Serialize(rows, JsonOptions)),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["contours"] = VisionValue.Contours(contours),
                ["count"] = VisionValue.Integer(rows.Count),
                ["found"] = VisionValue.Boolean(rows.Count > 0),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("shape", shape), ("method", method.ToString()), ("count", rows.Count)))
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["shape"] = shape,
                ["method"] = method.ToString(),
                ["count"] = rows.Count,
                ["sigma"] = sigma,
                ["threshold"] = threshold,
                ["transition"] = transition,
                ["select"] = select
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
    }

    /// <summary>从测量结果行里取一组 (row, column) 变成平台点；缺键或非有限值则跳过。</summary>
    private static void AppendPoint(List<VisionPoint2D> points, Dictionary<string, double> row, string rowKey, string columnKey)
    {
        if (!row.TryGetValue(rowKey, out var r) || !row.TryGetValue(columnKey, out var c)) return;
        if (!double.IsFinite(r) || !double.IsFinite(c)) return;
        points.Add(new VisionPoint2D(c, r));
    }
}
