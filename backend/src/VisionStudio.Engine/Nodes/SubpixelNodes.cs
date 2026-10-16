using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 第五批（2026-10-14）· 5B 亚像素测量族共享核心。
/// 1D 剖面采样（沿线 + 垂直条带平均）→ 梯度 → 极性/对比度候选 → 抛物线/质心亚像素细化。
/// 与既有 measure.caliper 的分工：卡尺只报整数像素最强梯度点；本族输出亚像素位置、
/// 拟合误差与有效采样点数，且"未找到合格边缘"返回 found=false（业务结果）而非异常。
/// </summary>
internal static class SubpixelEdgeCore
{
    /// <summary>沿搜索线采样 1D 灰度剖面（垂直条带平均）。ROI 掩膜外采样点跳过；整点无效时为 NaN。</summary>
    public static double[] SampleProfile(Mat gray, Mat? mask, VisionPoint2D start, VisionPoint2D end, int sampleWidth, out VisionPoint2D[] points, out double spacing)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 2)
            throw new InvalidOperationException("The search line is too short (minimum 2 px).");

        var nx = -dy / length;
        var ny = dx / length;
        var steps = Math.Max(2, (int)Math.Ceiling(length));
        spacing = length / steps;
        var profile = new double[steps + 1];
        points = new VisionPoint2D[steps + 1];
        var half = Math.Max(1, sampleWidth) / 2d;

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var x = start.X + dx * t;
            var y = start.Y + dy * t;
            points[i] = new VisionPoint2D(x, y);

            double sum = 0;
            var count = 0;
            var from = (int)Math.Ceiling(-half);
            var to = (int)Math.Floor(half);
            for (var w = from; w <= to; w++)
            {
                var px = (int)Math.Round(x + nx * w);
                var py = (int)Math.Round(y + ny * w);
                if (px < 0 || py < 0 || px >= gray.Cols || py >= gray.Rows) continue;
                if (mask is not null && mask.At<byte>(py, px) == 0) continue;
                sum += gray.At<byte>(py, px);
                count++;
            }
            profile[i] = count > 0 ? sum / count : double.NaN;
        }

        return profile;
    }

    /// <summary>
    /// 边缘搜索结果。Position 为沿搜索线距起点的亚像素距离（px，已按采样间距换算）；
    /// FitError 为五个梯度采样点对最小二乘抛物线的 RMS 残差（灰度级，-1 表示不可可靠拟合）；
    /// SampleCount 为有效梯度采样数。
    /// </summary>
    public readonly record struct EdgeResult(
        bool Found,
        double Position,
        double Strength,
        double FitError,
        int SampleCount,
        string Reason)
    {
        public static EdgeResult NotFound(string reason) => new(false, 0, 0, 0, 0, reason);
    }

    public static EdgeResult FindEdge(double[] profile, double spacing, string polarity, double minContrast, string direction, string refine)
    {
        var edge = JLVisionEdgeMeasurement.Find(profile, spacing, polarity, minContrast, direction, refine);
        return new(edge.Found, edge.Position, edge.Strength, edge.Error, edge.Count, edge.Reason);
    }

    /// <summary>沿搜索线的坐标 → 图像点。</summary>
    public static VisionPoint2D Locate(VisionPoint2D start, VisionPoint2D end, double position)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return new VisionPoint2D(start.X + dx * position / length, start.Y + dy * position / length);
    }

    /// <summary>把搜索线起点/终点收敛到图像范围内（钳制），返回是否发生修正。</summary>
    public static (VisionPoint2D Start, VisionPoint2D End, bool Clamped) ClampLine(VisionPoint2D start, VisionPoint2D end, int width, int height)
    {
        var s = new VisionPoint2D(Math.Clamp(start.X, 0, width - 1), Math.Clamp(start.Y, 0, height - 1));
        var e = new VisionPoint2D(Math.Clamp(end.X, 0, width - 1), Math.Clamp(end.Y, 0, height - 1));
        return (s, e, Math.Abs(s.X - start.X) > 1e-9 || Math.Abs(s.Y - start.Y) > 1e-9 || Math.Abs(e.X - end.X) > 1e-9 || Math.Abs(e.Y - end.Y) > 1e-9);
    }

    /// <summary>解析搜索线：searchLine 输入端口优先，否则用参数坐标。</summary>
    public static VisionLine2D ResolveSearchLine(NodeExecutionContext context, NodeDefinition node)
    {
        if (context.TryGetValue("searchLine", out var value))
        {
            var line = value.Require<VisionLine2D>("searchLine");
            if (!double.IsFinite(line.Start.X) || !double.IsFinite(line.Start.Y) || !double.IsFinite(line.End.X) || !double.IsFinite(line.End.Y))
                throw new InvalidOperationException("The connected search line has non-finite endpoints.");
            return MeasurementFixture.Apply(line, MeasurementFixture.Resolve(context));
        }
        var start = new VisionPoint2D(node.GetDouble("startX", 120), node.GetDouble("startY", 240));
        var end = new VisionPoint2D(node.GetDouble("endX", 520), node.GetDouble("endY", 240));
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y))
            throw new InvalidOperationException("Search line parameters must be finite numbers.");
        return MeasurementFixture.Apply(new VisionLine2D(start, end), MeasurementFixture.Resolve(context));
    }
}

/// <summary>
/// 5B-1 · feature.edgeSubpixel：沿搜索线做亚像素边缘定位。
/// 输出 found / point / strength / fitError / sampleCount / searchLine；
/// 未找到超过对比度阈值的边缘时 found=false（summary.reason 说明原因），不抛异常。
/// </summary>
public sealed class EdgeSubpixelNode : IVisionNodeExecutor
{
    public string Type => "feature.edgeSubpixel";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Subpixel edge detection requires a non-empty image.");

        var search = SubpixelEdgeCore.ResolveSearchLine(context, node);
        var sampleWidth = Math.Clamp(node.GetInt("sampleWidth", 5), 1, 99);
        var minContrast = Math.Max(0, node.GetDouble("minContrast", 8));
        var polarity = node.GetString("polarity", "any").Trim().ToLowerInvariant();
        var direction = node.GetString("direction", "strongest").Trim().ToLowerInvariant();
        var refine = node.GetString("refine", "parabolic").Trim().ToLowerInvariant();
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null) mask = roi.BuildMask(sourceImage.Mat.Size());
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                var (start, end, clamped) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
                var profile = SubpixelEdgeCore.SampleProfile(gray, mask, start, end, sampleWidth, out var points, out var spacing);
                var edge = SubpixelEdgeCore.FindEdge(profile, spacing, polarity, minContrast, direction, refine);

                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(edge.Found),
                    ["searchLine"] = VisionValue.Line(new VisionLine2D(start, end))
                };
                if (edge.Found)
                {
                    var point = SubpixelEdgeCore.Locate(start, end, edge.Position);
                    outputs["point"] = VisionValue.Point(point);
                    outputs["strength"] = VisionValue.Double(edge.Strength);
                    outputs["fitError"] = VisionValue.Double(edge.FitError);
                    outputs["sampleCount"] = VisionValue.Integer(edge.SampleCount);
                }

                var overlays = new List<VisionOverlay>
                {
                    new("edge-search", VisionOverlayType.Line, X: start.X, Y: start.Y, X2: end.X, Y2: end.Y, Stroke: "#597ef7", StrokeWidth: 1.5)
                };
                if (edge.Found)
                {
                    var point = SubpixelEdgeCore.Locate(start, end, edge.Position);
                    overlays.Add(new VisionOverlay("edge-subpixel", VisionOverlayType.Point, X: point.X, Y: point.Y, Stroke: "#f5222d", StrokeWidth: 3));
                }

                return ValueTask.FromResult(new NodeExecutorResult(
                    outputs,
                    new Dictionary<string, object?>
                    {
                        ["found"] = edge.Found,
                        ["reason"] = edge.Reason,
                        ["position"] = edge.Found ? Math.Round(edge.Position, 4) : null,
                        ["strength"] = edge.Found ? Math.Round(edge.Strength, 3) : null,
                        ["fitError"] = edge.Found && edge.FitError >= 0 ? Math.Round(edge.FitError, 4) : null,
                        ["sampleCount"] = edge.Found ? edge.SampleCount : null,
                        ["polarity"] = polarity,
                        ["direction"] = direction,
                        ["refine"] = refine,
                        ["sampleWidth"] = sampleWidth,
                        ["minContrast"] = minContrast,
                        ["lineClampedToImage"] = clamped,
                        ["autoGray"] = autoGray,
                        ["roi"] = roi?.Type.ToString() ?? "FullImage"
                    },
                    overlays));
            }
            finally
            {
                owned?.Dispose();
            }
        }
        finally
        {
            mask?.Dispose();
        }
    }
}

/// <summary>
/// 5B-2 · measure.caliperLine：卡尺找线——沿搜索线定位最强（或指定方向）边缘，
/// 输出过边缘点且垂直于搜索方向的直线（搜索线横穿边缘时即工件边缘本身）。
/// </summary>
public sealed class CaliperLineNode : IVisionNodeExecutor
{
    public string Type => "measure.caliperLine";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Caliper line measurement requires a non-empty image.");

        var search = SubpixelEdgeCore.ResolveSearchLine(context, node);
        var sampleWidth = Math.Clamp(node.GetInt("sampleWidth", 5), 1, 99);
        var minContrast = Math.Max(0, node.GetDouble("minContrast", 8));
        var polarity = node.GetString("polarity", "any").Trim().ToLowerInvariant();
        var direction = node.GetString("direction", "strongest").Trim().ToLowerInvariant();
        var refine = node.GetString("refine", "parabolic").Trim().ToLowerInvariant();
        var edgeLength = Math.Clamp(node.GetDouble("edgeLength", 0), 0, 10000);
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null) mask = roi.BuildMask(sourceImage.Mat.Size());
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                var (start, end, clamped) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
                var length = MeasurementMath.Hypot(end.X - start.X, end.Y - start.Y);
                if (length < 2)
                    throw new InvalidOperationException("The search line is too short after clamping to the image (minimum 2 px).");
                var span = edgeLength > 0 ? edgeLength : length;
                var caliperCount = Math.Clamp(node.GetInt("caliperCount", 9), 3, 101);
                var tangentX = -(end.Y - start.Y) / length;
                var tangentY = (end.X - start.X) / length;
                var points = new List<VisionPoint2D>(caliperCount);
                var strengths = new List<double>(caliperCount);
                var failures = 0;
                for (var i = 0; i < caliperCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = caliperCount == 1 ? 0 : (i / (double)(caliperCount - 1) - 0.5) * span;
                    var offsetX = tangentX * offset;
                    var offsetY = tangentY * offset;
                    var profileStart = new VisionPoint2D(start.X + offsetX, start.Y + offsetY);
                    var profileEnd = new VisionPoint2D(end.X + offsetX, end.Y + offsetY);
                    var profile = SubpixelEdgeCore.SampleProfile(gray, mask, profileStart, profileEnd, sampleWidth, out _, out var lineSpacing);
                    var edge = SubpixelEdgeCore.FindEdge(profile, lineSpacing, polarity, minContrast, direction, refine);
                    if (!edge.Found) { failures++; continue; }
                    points.Add(SubpixelEdgeCore.Locate(profileStart, profileEnd, edge.Position));
                    strengths.Add(edge.Strength);
                }
                var fit = FitCaliperLine(points);
                var found = fit is not null;

                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(found),
                    ["searchLine"] = VisionValue.Line(new VisionLine2D(start, end))
                };
                var overlays = new List<VisionOverlay>
                {
                    new("caliper-line-search", VisionOverlayType.Line, X: start.X, Y: start.Y, X2: end.X, Y2: end.Y, Stroke: "#597ef7", StrokeWidth: 1.5)
                };

                if (found)
                {
                    var (point, line, fitError) = fit!.Value;
                    outputs["point"] = VisionValue.Point(point);
                    outputs["line"] = VisionValue.Line(line);
                    outputs["strength"] = VisionValue.Double(strengths.Average());
                    outputs["fitError"] = VisionValue.Double(fitError);
                    outputs["sampleCount"] = VisionValue.Integer(points.Count);
                    overlays.Add(new VisionOverlay("caliper-line-edge", VisionOverlayType.Line,
                        Label: $"N={points.Count} RMS={fitError:0.###}",
                        X: line.Start.X, Y: line.Start.Y, X2: line.End.X, Y2: line.End.Y,
                        Stroke: "#f5222d", StrokeWidth: 2.5));
                    overlays.Add(new VisionOverlay("caliper-line-point", VisionOverlayType.Point, X: point.X, Y: point.Y, Stroke: "#f5222d", StrokeWidth: 3));
                }

                return ValueTask.FromResult(new NodeExecutorResult(
                    outputs,
                    new Dictionary<string, object?>
                    {
                        ["found"] = found,
                        ["reason"] = found ? "ok" : points.Count == 0 ? "no-edge-above-threshold" : points.Count < 3 ? "too-few-edge-points" : "degenerate-line-fit",
                        ["edgeX"] = found ? Math.Round(fit!.Value.Point.X, 3) : null,
                        ["edgeY"] = found ? Math.Round(fit!.Value.Point.Y, 3) : null,
                        ["strength"] = found ? Math.Round(strengths.Average(), 3) : null,
                        ["fitError"] = found ? Math.Round(fit!.Value.Error, 4) : null,
                        ["sampleCount"] = points.Count,
                        ["caliperCount"] = caliperCount,
                        ["rejectedCalipers"] = failures,
                        ["polarity"] = polarity,
                        ["direction"] = direction,
                        ["lineClampedToImage"] = clamped,
                        ["autoGray"] = autoGray,
                        ["roi"] = roi?.Type.ToString() ?? "FullImage"
                    },
                    overlays));
            }
            finally
            {
                owned?.Dispose();
            }
        }
        finally
        {
            mask?.Dispose();
        }
    }

    private static (VisionPoint2D Point, VisionLine2D Line, double Error)? FitCaliperLine(IReadOnlyList<VisionPoint2D> points)
    {
        if (points.Count < 3) return null;
        var cx = points.Average(p => p.X);
        var cy = points.Average(p => p.Y);
        var xx = points.Sum(p => (p.X - cx) * (p.X - cx));
        var yy = points.Sum(p => (p.Y - cy) * (p.Y - cy));
        var xy = points.Sum(p => (p.X - cx) * (p.Y - cy));
        if (xx + yy < 1e-8) return null;
        var fitted = JLVisionPointFitting.Line(points.ToArray());
        var ux = (fitted.End.X - fitted.Start.X) / fitted.Length;
        var uy = (fitted.End.Y - fitted.Start.Y) / fitted.Length;
        var nx = -uy;
        var ny = ux;
        var residuals = points.Select(p => (p.X - cx) * nx + (p.Y - cy) * ny).ToArray();
        var error = Math.Sqrt(residuals.Sum(r => r * r) / residuals.Length);
        var half = (points.Max(p => (p.X - cx) * ux + (p.Y - cy) * uy) - points.Min(p => (p.X - cx) * ux + (p.Y - cy) * uy)) / 2;
        if (!double.IsFinite(error) || half < 1e-4) return null;
        return (new VisionPoint2D(cx, cy), new VisionLine2D(new(cx - ux * half, cy - uy * half), new(cx + ux * half, cy + uy * half)), error);
    }
}

/// <summary>
/// 5B-3 · measure.caliperCircle：卡尺找圆——沿一圈径向采样条带定位边缘，
/// 对有效边缘点做最小二乘圆拟合，输出圆/半径/圆度（半径极差）与拟合误差。
/// 有效边缘点少于 3 个时 found=false（退化拟合不抛异常，由 reason 说明）。
/// </summary>
public sealed class CaliperCircleNode : IVisionNodeExecutor
{
    public string Type => "measure.caliperCircle";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Caliper circle measurement requires a non-empty image.");

        // circle 输入端口（可来自 roi.transform 的动态圆）优先于参数。
        var center = new VisionPoint2D(node.GetDouble("centerX", 320), node.GetDouble("centerY", 240));
        var nominalRadius = node.GetDouble("radius", 100);
        if (context.TryGetValue("circle", out var circleValue))
        {
            var circle = circleValue.Require<VisionCircle>("circle");
            if (!double.IsFinite(circle.X) || !double.IsFinite(circle.Y) || !double.IsFinite(circle.Radius) || circle.Radius <= 0)
                throw new InvalidOperationException("The connected circle input must have a finite center and positive radius.");
            center = new VisionPoint2D(circle.X, circle.Y);
            nominalRadius = circle.Radius;
        }
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(nominalRadius) || nominalRadius <= 0)
            throw new InvalidOperationException("Caliper circle requires a finite center and a positive radius.");

        var angleStart = node.GetDouble("angleStartDeg", -180);
        var angleEnd = node.GetDouble("angleEndDeg", 180);
        var fixture = MeasurementFixture.Resolve(context);
        if (fixture is not null)
        {
            center = fixture.Apply(center);
            // Arc angles use image axes (positive clockwise). Preserve the unwrapped span,
            // including a full 360-degree circle, when rotating both ends.
            var rotation = Math.Atan2(fixture.H10, fixture.H00) * 180 / Math.PI;
            angleStart += rotation;
            angleEnd += rotation;
        }
        var angleStep = Math.Clamp(node.GetDouble("angleStepDeg", 5), 0.5, 45);
        var band = Math.Clamp(node.GetDouble("radialBand", 8), 1, 500);
        var minContrast = Math.Max(0, node.GetDouble("minContrast", 8));
        var polarity = node.GetString("polarity", "any").Trim().ToLowerInvariant();
        var sampleWidth = Math.Clamp(node.GetInt("sampleWidth", 3), 1, 99);
        var maxDeviation = Math.Max(0, node.GetDouble("maxRadiusDeviation", 0));
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null) mask = roi.BuildMask(sourceImage.Mat.Size());
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                var angles = BuildAngles(angleStart, angleEnd, angleStep, out var effectiveStep);
                var edgePoints = new List<VisionPoint2D>();
                var spokeRadii = new List<double>();
                var rejected = 0;
                var rejectedByDeviation = 0;
                foreach (var angleDeg in angles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var radians = angleDeg * Math.PI / 180.0;
                    var dirX = Math.Cos(radians);
                    var dirY = Math.Sin(radians);
                    // 径向剖面：从 r-band 到 r+band 沿线采样（条带平均方向为切向）。
                    var start = new VisionPoint2D(center.X + dirX * (nominalRadius - band), center.Y + dirY * (nominalRadius - band));
                    var end = new VisionPoint2D(center.X + dirX * (nominalRadius + band), center.Y + dirY * (nominalRadius + band));
                    if (!InsideImage(start, gray) && !InsideImage(end, gray)) { rejected++; continue; }
                    var profile = SubpixelEdgeCore.SampleProfile(gray, mask, start, end, sampleWidth, out _, out var spokeSpacing);
                    var edge = SubpixelEdgeCore.FindEdge(profile, spokeSpacing, polarity, minContrast, "strongest", "parabolic");
                    if (!edge.Found) { rejected++; continue; }
                    var edgeRadius = nominalRadius - band + edge.Position;
                    if (maxDeviation > 0 && Math.Abs(edgeRadius - nominalRadius) > maxDeviation)
                    {
                        rejected++;
                        rejectedByDeviation++;
                        continue;
                    }
                    edgePoints.Add(new VisionPoint2D(center.X + dirX * edgeRadius, center.Y + dirY * edgeRadius));
                    spokeRadii.Add(edgeRadius);
                }

                var found = edgePoints.Count >= 3;
                var outputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(sourceImage.CloneOwned()), ["found"] = VisionValue.Boolean(found) };
                var overlays = new List<VisionOverlay>
                {
                    new("caliper-circle-nominal", VisionOverlayType.Circle, X: center.X, Y: center.Y, Radius: nominalRadius, Stroke: "#597ef7", StrokeWidth: 1)
                };
                var summary = new Dictionary<string, object?>
                {
                    ["found"] = found,
                    ["reason"] = found ? "ok"
                        : rejectedByDeviation > 0 && edgePoints.Count == 0 ? "all-spokes-exceed-max-radius-deviation"
                        : edgePoints.Count == 0 ? "no-edge-above-threshold"
                        : "too-few-edge-points",
                    ["spokes"] = angles.Length,
                    ["edgePoints"] = edgePoints.Count,
                    ["rejectedSpokes"] = rejected,
                    ["rejectedByDeviation"] = rejectedByDeviation,
                    ["nominalRadius"] = Math.Round(nominalRadius, 3),
                    ["angleStepDeg"] = effectiveStep,
                    ["radialBand"] = band,
                    ["polarity"] = polarity,
                    ["autoGray"] = autoGray,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage"
                };

                if (found)
                {
                    var circle = MultiPointFit.Circle(edgePoints.ToArray());
                    var residuals = edgePoints.Select(p => Math.Abs(MeasurementMath.Hypot(p.X - circle.X, p.Y - circle.Y) - circle.Radius)).ToArray();
                    var rmse = Math.Sqrt(residuals.Sum(x => x * x) / residuals.Length);
                    outputs["circle"] = VisionValue.Circle(circle);
                    outputs["center"] = VisionValue.Point(new VisionPoint2D(circle.X, circle.Y));
                    outputs["radius"] = VisionValue.Double(circle.Radius);
                    outputs["radiusMin"] = VisionValue.Double(spokeRadii.Min());
                    outputs["radiusMax"] = VisionValue.Double(spokeRadii.Max());
                    outputs["fitError"] = VisionValue.Double(rmse);
                    outputs["sampleCount"] = VisionValue.Integer(edgePoints.Count);
                    summary["fittedRadius"] = Math.Round(circle.Radius, 3);
                    summary["radiusDelta"] = Math.Round(circle.Radius - nominalRadius, 3);
                    summary["radiusSpread"] = Math.Round(spokeRadii.Max() - spokeRadii.Min(), 3);
                    summary["fitError"] = Math.Round(rmse, 4);
                    overlays.Add(new VisionOverlay("caliper-circle-fit", VisionOverlayType.Circle,
                        Label: $"R={circle.Radius:0.###}",
                        X: circle.X, Y: circle.Y, Radius: circle.Radius, Stroke: "#52c41a", StrokeWidth: 2));
                }

                return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
            }
            finally
            {
                owned?.Dispose();
            }
        }
        finally
        {
            mask?.Dispose();
        }
    }

    private static bool InsideImage(VisionPoint2D p, Mat gray) =>
        p.X >= 0 && p.Y >= 0 && p.X < gray.Cols && p.Y < gray.Rows;

    private static double[] BuildAngles(double start, double end, double step, out double effectiveStep)
    {
        effectiveStep = step;
        if (end <= start) return [start];
        var count = (int)Math.Floor((end - start) / effectiveStep) + 1;
        if (count > 361)
        {
            effectiveStep = (end - start) / 360d;
            count = 361;
        }
        var angles = new double[count];
        for (var i = 0; i < count; i++) angles[i] = start + i * effectiveStep;
        return angles;
    }
}

/// <summary>
/// 5B-4 · measure.fitEllipse：对轮廓做椭圆拟合（OpenCV FitEllipse），
/// 按面积/索引选中轮廓；输出旋转矩形（中心/长宽/角度）与位姿。
/// 点数不足 5 或退化（共线）时明确报错——这是参数/数据错误，不是"未找到"。
/// </summary>
public sealed class FitEllipseNode : IVisionNodeExecutor
{
    public string Type => "measure.fitEllipse";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contours = context.Require<IReadOnlyList<VisionContour>>("contours");
        if (contours.Count == 0)
            throw new InvalidOperationException("Ellipse fitting requires at least one contour; the input list is empty.");

        var selectBy = node.GetString("selectBy", "largest").Trim().ToLowerInvariant();
        var index = Math.Max(0, node.GetInt("index", 0));
        var selectedIndex = selectBy switch
        {
            "smallest" => IndexOfExtreme(contours, x => x.Area, ascending: true),
            "first" => 0,
            "last" => contours.Count - 1,
            "index" => Math.Clamp(index, 0, contours.Count - 1),
            _ => IndexOfExtreme(contours, x => x.Area, ascending: false)
        };
        var contour = contours[selectedIndex];
        if (contour.Points.Count < 5)
            throw new InvalidOperationException(
                $"Ellipse fitting needs at least 5 contour points; selected contour #{selectedIndex} has {contour.Points.Count}.");

        var points = contour.Points.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        RotatedRect ellipse;
        try
        {
            ellipse = JLVisionOperators.FitEllipse(points);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Ellipse fit is degenerate for contour #{selectedIndex} (points may be collinear): {ex.Message}");
        }

        if (!double.IsFinite(ellipse.Center.X) || !double.IsFinite(ellipse.Center.Y) ||
            !double.IsFinite(ellipse.Size.Width) || !double.IsFinite(ellipse.Size.Height) ||
            ellipse.Size.Width <= 0 || ellipse.Size.Height <= 0)
            throw new InvalidOperationException("Ellipse fit produced an invalid rectangle.");

        // 归一化：width = 长轴、height = 短轴，angle = 长轴方向（逆时针，度）。
        double width = ellipse.Size.Width;
        double height = ellipse.Size.Height;
        double angle = ellipse.Angle;
        if (width < height)
        {
            (width, height) = (height, width);
            angle += 90;
        }
        angle = VisionTransform2D.NormalizeAngleDeg(angle);
        var rectangle = new VisionRectangle2D(ellipse.Center.X, ellipse.Center.Y, width, height, angle);
        var center = new VisionPoint2D(ellipse.Center.X, ellipse.Center.Y);

        var overlays = new[]
        {
            new VisionOverlay("fit-ellipse-rect", VisionOverlayType.Rectangle,
                Label: $"{width:0.#}x{height:0.#}@{angle:0.#}deg",
                X: rectangle.X, Y: rectangle.Y, Width: width, Height: height, AngleDeg: angle,
                Stroke: "#722ed1", StrokeWidth: 2)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["contours"] = VisionValue.Contours(contours),
                ["rectangle"] = VisionValue.Rectangle(rectangle),
                ["center"] = VisionValue.Point(center),
                ["pose"] = VisionValue.Pose(new VisionPose2D(center.X, center.Y, angle)),
                ["width"] = VisionValue.Double(width),
                ["height"] = VisionValue.Double(height),
                ["angle"] = VisionValue.Double(angle),
                ["count"] = VisionValue.Integer(contours.Count)
            },
            new Dictionary<string, object?>
            {
                ["selectedIndex"] = selectedIndex,
                ["selectBy"] = selectBy,
                ["center"] = $"{center.X:0.###},{center.Y:0.###}",
                ["width"] = Math.Round(width, 3),
                ["height"] = Math.Round(height, 3),
                ["angleDeg"] = Math.Round(angle, 3),
                ["axisRatio"] = Math.Round(width / height, 4),
                ["pointCount"] = contour.Points.Count
            },
            overlays));
    }

    private static int IndexOfExtreme(IReadOnlyList<VisionContour> contours, Func<VisionContour, double> key, bool ascending)
    {
        var best = 0;
        for (var i = 1; i < contours.Count; i++)
        {
            var compare = key(contours[i]).CompareTo(key(contours[best]));
            if (ascending ? compare < 0 : compare > 0) best = i;
        }
        return best;
    }
}
