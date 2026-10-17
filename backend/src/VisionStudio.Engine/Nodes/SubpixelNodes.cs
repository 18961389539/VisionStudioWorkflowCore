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
    public static double[] SampleProfile(Mat gray, Mat? mask, VisionPoint2D start, VisionPoint2D end, int sampleWidth,
        out VisionPoint2D[] points, out double spacing, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sampleWidth = NormalizeSampleWidth(sampleWidth);
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
        var half = sampleWidth / 2;
        var rows = gray.Rows;
        var columns = gray.Cols;

        for (var i = 0; i <= steps; i++)
        {
            if ((i & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            var t = i / (double)steps;
            var x = start.X + dx * t;
            var y = start.Y + dy * t;
            points[i] = new VisionPoint2D(x, y);

            double sum = 0;
            var count = 0;
            for (var w = -half; w <= half; w++)
            {
                var sampleX = x + nx * w;
                var sampleY = y + ny * w;
                if (!TryBilinearSample(gray, mask, sampleX, sampleY, rows, columns, out var intensity)) continue;
                sum += intensity;
                count++;
            }
            profile[i] = count > 0 ? sum / count : double.NaN;
        }

        return profile;
    }

    private static bool TryBilinearSample(Mat gray, Mat? mask, double x, double y, int rows, int columns, out double intensity)
    {
        intensity = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        var nearestX = (int)Math.Round(x);
        var nearestY = (int)Math.Round(y);
        if (nearestX < 0 || nearestY < 0 || nearestX >= columns || nearestY >= rows
            || mask is not null && mask.At<byte>(nearestY, nearestX) == 0)
            return false;

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var xFraction = x - x0;
        var yFraction = y - y0;
        double weightedSum = 0;
        double totalWeight = 0;
        for (var dy = 0; dy <= 1; dy++)
        for (var dx = 0; dx <= 1; dx++)
        {
            var weight = (dx == 0 ? 1 - xFraction : xFraction) * (dy == 0 ? 1 - yFraction : yFraction);
            if (weight <= 0) continue;
            var px = x0 + dx;
            var py = y0 + dy;
            if (px < 0 || py < 0 || px >= columns || py >= rows
                || mask is not null && mask.At<byte>(py, px) == 0)
                continue;
            weightedSum += gray.At<byte>(py, px) * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0) return false;
        intensity = weightedSum / totalWeight;
        return double.IsFinite(intensity);
    }

    public static int NormalizeSampleWidth(int sampleWidth)
    {
        var normalized = Math.Clamp(sampleWidth, 1, 99);
        return normalized % 2 == 0 ? Math.Min(99, normalized + 1) : normalized;
    }

    public static string ChoiceParameter(NodeDefinition node, string name, string fallback, params string[] allowed)
    {
        var value = node.GetString(name, fallback).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)) value = fallback.Trim().ToLowerInvariant();
        if (allowed.Contains(value, StringComparer.Ordinal)) return value;
        throw new InvalidOperationException($"Parameter '{name}' must be one of [{string.Join(", ", allowed)}].");
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

    public static EdgeResult FindEdge(double[] profile, double spacing, string polarity, double minContrast, string direction, string refine,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edge = JLVisionEdgeMeasurement.Find(profile, spacing, polarity, minContrast, direction, refine, cancellationToken);
        return new(edge.Found, edge.Position, edge.Strength, edge.Error, edge.Count, edge.Reason);
    }

    public static double RequireFiniteParameter(NodeDefinition node, string name, double fallback)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value))
            throw new InvalidOperationException($"Parameter '{name}' must be a finite number.");
        return value;
    }

    public static int ClampedIntParameter(NodeDefinition node, string name, int fallback, int minimum, int maximum)
    {
        var value = RequireFiniteParameter(node, name, fallback);
        // Match the frontend's Math.round so fractional legacy/workflow values preview and execute identically.
        return (int)Math.Clamp(Math.Floor(value + 0.5), minimum, maximum);
    }

    /// <summary>沿搜索线的坐标 → 图像点。</summary>
    public static VisionPoint2D Locate(VisionPoint2D start, VisionPoint2D end, double position)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return new VisionPoint2D(start.X + dx * position / length, start.Y + dy * position / length);
    }

    /// <summary>Clip the segment to the image rectangle without changing its direction.</summary>
    public static bool SegmentBoundsOutsideImage(VisionPoint2D start, VisionPoint2D end, int width, int height) =>
        Math.Max(start.X, end.X) < 0 || Math.Min(start.X, end.X) > width - 1
        || Math.Max(start.Y, end.Y) < 0 || Math.Min(start.Y, end.Y) > height - 1;

    public static (VisionPoint2D Start, VisionPoint2D End, bool Clamped, bool Intersects) ClampLine(
        VisionPoint2D start, VisionPoint2D end, int width, int height, double maximumCoordinate = 10_000_000)
    {
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Cannot clip a search line to an empty image.");
        if (!double.IsFinite(maximumCoordinate) || maximumCoordinate <= 0
            || new[] { start.X, start.Y, end.X, end.Y }.Any(value => !double.IsFinite(value) || Math.Abs(value) > maximumCoordinate))
            throw new InvalidOperationException($"Search line coordinates must be finite and within +/-{maximumCoordinate:0} pixels.");

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var t0 = 0d;
        var t1 = 1d;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-15) return q >= 0;
            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0) return false;
                t1 = Math.Min(t1, r);
            }
            return t0 <= t1;
        }

        if (!Clip(-dx, start.X) || !Clip(dx, width - 1d - start.X)
            || !Clip(-dy, start.Y) || !Clip(dy, height - 1d - start.Y) || t0 > t1)
            return (start, end, true, false);

        var clippedStart = new VisionPoint2D(
            Math.Clamp(start.X + t0 * dx, 0, width - 1),
            Math.Clamp(start.Y + t0 * dy, 0, height - 1));
        var clippedEnd = new VisionPoint2D(
            Math.Clamp(start.X + t1 * dx, 0, width - 1),
            Math.Clamp(start.Y + t1 * dy, 0, height - 1));
        var clamped = Math.Abs(clippedStart.X - start.X) > 1e-9 || Math.Abs(clippedStart.Y - start.Y) > 1e-9
            || Math.Abs(clippedEnd.X - end.X) > 1e-9 || Math.Abs(clippedEnd.Y - end.Y) > 1e-9;
        return (clippedStart, clippedEnd, clamped, true);
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
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 5, 1, 99));
        var minContrast = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "minContrast", 8));
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "any", "any", "rising", "falling");
        var direction = SubpixelEdgeCore.ChoiceParameter(node, "direction", "strongest", "strongest", "firstfromstart", "firstfromend");
        var refine = SubpixelEdgeCore.ChoiceParameter(node, "refine", "parabolic", "parabolic", "none");
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null)
                mask = roi.BuildMaskForMeasurement(sourceImage.Mat.Size(), cancellationToken);
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                if (MeasurementMath.Hypot(search.End.X - search.Start.X, search.End.Y - search.Start.Y) < 2)
                    throw new InvalidOperationException("The search line is too short (minimum 2 px).");
                var (start, end, clamped, intersects) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
                var clippedLine = new VisionLine2D(start, end);
                var searchLine = intersects ? clippedLine : search;
                var clippedLength = MeasurementMath.Hypot(end.X - start.X, end.Y - start.Y);
                var edge = !intersects
                    ? SubpixelEdgeCore.EdgeResult.NotFound("search-line-outside-image")
                    : clippedLength < 2
                        ? SubpixelEdgeCore.EdgeResult.NotFound("search-line-clipped-too-short")
                        : FindClippedEdge(gray, mask, start, end, sampleWidth, polarity, minContrast, direction, refine, cancellationToken);

                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(edge.Found),
                    ["searchLine"] = VisionValue.Line(searchLine),
                    ["sampleCount"] = VisionValue.Integer(edge.SampleCount)
                };
                if (edge.Found)
                {
                    var point = SubpixelEdgeCore.Locate(start, end, edge.Position);
                    outputs["point"] = VisionValue.Point(point);
                    outputs["strength"] = VisionValue.Double(edge.Strength);
                    outputs["fitError"] = VisionValue.Double(edge.FitError);
                }

                var overlays = new List<VisionOverlay>();
                if (intersects)
                    overlays.Add(new VisionOverlay("edge-search", VisionOverlayType.Line,
                        X: start.X, Y: start.Y, X2: end.X, Y2: end.Y, Stroke: "#597ef7", StrokeWidth: 1.5));
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
                        ["sampleCount"] = edge.SampleCount,
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

    private static SubpixelEdgeCore.EdgeResult FindClippedEdge(Mat gray, Mat? mask, VisionPoint2D start, VisionPoint2D end,
        int sampleWidth, string polarity, double minContrast, string direction, string refine, CancellationToken cancellationToken)
    {
        var profile = SubpixelEdgeCore.SampleProfile(gray, mask, start, end, sampleWidth, out _, out var spacing, cancellationToken);
        return SubpixelEdgeCore.FindEdge(profile, spacing, polarity, minContrast, direction, refine, cancellationToken);
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
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 5, 1, 99));
        var minContrast = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "minContrast", 8));
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "any", "any", "rising", "falling");
        var direction = SubpixelEdgeCore.ChoiceParameter(node, "direction", "strongest", "strongest", "firstfromstart", "firstfromend");
        var refine = SubpixelEdgeCore.ChoiceParameter(node, "refine", "parabolic", "parabolic", "none");
        var edgeLength = Math.Clamp(SubpixelEdgeCore.RequireFiniteParameter(node, "edgeLength", 0), 0, 10000);
        var caliperCount = SubpixelEdgeCore.ClampedIntParameter(node, "caliperCount", 9, 3, 101);
        var minValidCalipers = SubpixelEdgeCore.ClampedIntParameter(node, "minValidCalipers", 3, 3, 101);
        var maxFitOutliers = SubpixelEdgeCore.ClampedIntParameter(node, "maxFitOutliers", 0, 0, 16);
        var fitOutlierThreshold = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "fitOutlierThreshold", 2));
        var maxFitError = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "maxFitError", 0));
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null)
                mask = roi.BuildMaskForMeasurement(sourceImage.Mat.Size(), cancellationToken);
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                var (start, end, baseClamped, baseIntersects) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
                var length = MeasurementMath.Hypot(search.End.X - search.Start.X, search.End.Y - search.Start.Y);
                if (length < 2)
                    throw new InvalidOperationException("The search line is too short (minimum 2 px).");
                var span = edgeLength > 0 ? edgeLength : length;
                var tangentX = -(search.End.Y - search.Start.Y) / length;
                var tangentY = (search.End.X - search.Start.X) / length;
                var points = new List<VisionPoint2D>(caliperCount);
                var strengths = new List<double>(caliperCount);
                var failures = 0;
                var outsideImage = 0;
                var shortAfterClip = 0;
                var profilesWithoutFiniteSamples = 0;
                var profilesWithInsufficientContiguousSamples = 0;
                var profilesClipped = 0;
                var anyProfileClipped = false;
                var overlays = new List<VisionOverlay>();
                if (baseIntersects)
                    overlays.Add(new("caliper-line-search", VisionOverlayType.Line, X: start.X, Y: start.Y, X2: end.X, Y2: end.Y, Stroke: "#597ef7", StrokeWidth: 1.5));
                for (var i = 0; i < caliperCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = caliperCount == 1 ? 0 : (i / (double)(caliperCount - 1) - 0.5) * span;
                    var offsetX = tangentX * offset;
                    var offsetY = tangentY * offset;
                    var requestedStart = new VisionPoint2D(search.Start.X + offsetX, search.Start.Y + offsetY);
                    var requestedEnd = new VisionPoint2D(search.End.X + offsetX, search.End.Y + offsetY);
                    if (SubpixelEdgeCore.SegmentBoundsOutsideImage(requestedStart, requestedEnd, gray.Cols, gray.Rows))
                    { failures++; outsideImage++; continue; }
                    var (profileStart, profileEnd, profileClamped, profileIntersects) =
                        SubpixelEdgeCore.ClampLine(requestedStart, requestedEnd, gray.Cols, gray.Rows, 10_005_000);
                    if (profileClamped) { profilesClipped++; anyProfileClipped = true; }
                    if (!profileIntersects) { failures++; outsideImage++; continue; }
                    if (MeasurementMath.Hypot(profileEnd.X - profileStart.X, profileEnd.Y - profileStart.Y) < 2)
                    { failures++; shortAfterClip++; continue; }
                    overlays.Add(new($"caliper-line-profile-{i}", VisionOverlayType.Line,
                        X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                        Stroke: "#91d5ff", StrokeWidth: 0.8));
                    var profile = SubpixelEdgeCore.SampleProfile(gray, mask, profileStart, profileEnd, sampleWidth, out _, out var lineSpacing, cancellationToken);
                    var edge = SubpixelEdgeCore.FindEdge(profile, lineSpacing, polarity, minContrast, direction, refine, cancellationToken);
                    if (!edge.Found)
                    {
                        failures++;
                        if (edge.Reason == "profile-has-no-finite-samples") profilesWithoutFiniteSamples++;
                        if (edge.Reason == "profile-has-insufficient-contiguous-samples") profilesWithInsufficientContiguousSamples++;
                        continue;
                    }
                    points.Add(SubpixelEdgeCore.Locate(profileStart, profileEnd, edge.Position));
                    strengths.Add(edge.Strength);
                }
                var fit = FitCaliperLine(points, maxFitOutliers, fitOutlierThreshold);
                var fitErrorExceedsLimit = fit is not null && maxFitError > 0 && fit.Error > maxFitError;
                var found = fit is not null && fit.UsedIndices.Length >= minValidCalipers && !fitErrorExceedsLimit;
                var reportedSearchLine = baseIntersects ? new VisionLine2D(start, end) : search;

                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(found),
                    ["searchLine"] = VisionValue.Line(reportedSearchLine),
                    ["sampleCount"] = VisionValue.Integer(fit?.UsedIndices.Length ?? points.Count),
                    ["ignoredCount"] = VisionValue.Integer(fit?.IgnoredIndices.Length ?? 0)
                };

                if (fit is not null)
                {
                    var usedPoints = fit.UsedIndices.Select(index => points[index]).ToArray();
                    var usedStrengths = fit.UsedIndices.Select(index => strengths[index]).ToArray();
                    if (found)
                    {
                        outputs["point"] = VisionValue.Point(fit.Point);
                        outputs["line"] = VisionValue.Line(fit.Line);
                        outputs["strength"] = VisionValue.Double(usedStrengths.Average());
                        outputs["fitError"] = VisionValue.Double(fit.Error);
                    }
                    overlays.AddRange(usedPoints.Select((point, index) => new VisionOverlay(
                        $"caliper-line-fit-point-{index}", VisionOverlayType.Point, X: point.X, Y: point.Y, Stroke: "#52c41a", StrokeWidth: 2.5)));
                    overlays.AddRange(fit.IgnoredIndices.Select((index, ignoredIndex) => new VisionOverlay(
                        $"caliper-line-ignored-point-{ignoredIndex}", VisionOverlayType.Point, X: points[index].X, Y: points[index].Y, Stroke: "#fa541c", StrokeWidth: 3)));
                    overlays.Add(new VisionOverlay("caliper-line-edge", VisionOverlayType.Line,
                        Label: $"N={usedPoints.Length} RMS={fit.Error:0.###}",
                        X: fit.Line.Start.X, Y: fit.Line.Start.Y, X2: fit.Line.End.X, Y2: fit.Line.End.Y,
                        Stroke: "#f5222d", StrokeWidth: 2.5));
                    overlays.Add(new VisionOverlay("caliper-line-point", VisionOverlayType.Point, X: fit.Point.X, Y: fit.Point.Y, Stroke: "#f5222d", StrokeWidth: 3));
                }
                else
                {
                    outputs["ignoredCount"] = VisionValue.Integer(0);
                    overlays.AddRange(points.Select((point, index) => new VisionOverlay(
                        $"caliper-line-candidate-point-{index}", VisionOverlayType.Point,
                        X: point.X, Y: point.Y, Stroke: "#faad14", StrokeWidth: 2.5)));
                }

                var reason = found ? "ok"
                    : points.Count > 0 && points.Count < 3 ? "too-few-edge-points"
                    : points.Count >= 3 && (points.Count < minValidCalipers
                        || fit is not null && fit.UsedIndices.Length < minValidCalipers) ? "insufficient-valid-calipers"
                    : fitErrorExceedsLimit ? "fit-error-exceeds-tolerance"
                    : points.Count > 0 ? "degenerate-line-fit"
                    : outsideImage == caliperCount ? "search-region-outside-image"
                    : outsideImage + shortAfterClip == caliperCount && shortAfterClip > 0 ? "search-region-clipped-too-short"
                    : profilesWithoutFiniteSamples == caliperCount - outsideImage - shortAfterClip && profilesWithoutFiniteSamples > 0
                        ? "profile-has-no-finite-samples"
                    : profilesWithoutFiniteSamples + profilesWithInsufficientContiguousSamples == caliperCount - outsideImage - shortAfterClip
                        && profilesWithInsufficientContiguousSamples > 0 ? "profile-has-insufficient-contiguous-samples"
                    : "no-edge-above-threshold";

                return ValueTask.FromResult(new NodeExecutorResult(
                    outputs,
                    new Dictionary<string, object?>
                    {
                        ["found"] = found,
                        ["reason"] = reason,
                        ["edgeX"] = fit is not null ? Math.Round(fit.Point.X, 3) : null,
                        ["edgeY"] = fit is not null ? Math.Round(fit.Point.Y, 3) : null,
                        ["strength"] = fit is not null ? Math.Round(fit.UsedIndices.Select(index => strengths[index]).Average(), 3) : null,
                        ["fitError"] = fit is not null ? Math.Round(fit.Error, 4) : null,
                        ["sampleCount"] = fit?.UsedIndices.Length ?? points.Count,
                        ["fitPoints"] = fit?.UsedIndices.Length ?? 0,
                        ["ignoredByFit"] = fit?.IgnoredIndices.Length ?? 0,
                        ["caliperCount"] = caliperCount,
                        ["minContrast"] = minContrast,
                        ["edgeLength"] = edgeLength,
                        ["effectiveCaliperSpan"] = span,
                        ["rejectedCalipers"] = failures,
                        ["maxFitOutliers"] = maxFitOutliers,
                        ["fitOutlierThreshold"] = fitOutlierThreshold,
                        ["maxFitError"] = maxFitError,
                        ["minValidCalipers"] = minValidCalipers,
                        ["sampleWidth"] = sampleWidth,
                        ["polarity"] = polarity,
                        ["direction"] = direction,
                        ["refine"] = refine,
                        ["lineClampedToImage"] = baseClamped || anyProfileClipped,
                        ["profilesIntersectingImage"] = caliperCount - outsideImage,
                        ["profilesOutsideImage"] = outsideImage,
                        ["profilesClippedToImage"] = profilesClipped,
                        ["profilesTooShortAfterClip"] = shortAfterClip,
                        ["profilesWithoutFiniteSamples"] = profilesWithoutFiniteSamples,
                        ["profilesWithInsufficientContiguousSamples"] = profilesWithInsufficientContiguousSamples,
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

    private sealed record CaliperLineFitResult(VisionPoint2D Point, VisionLine2D Line, double Error, int[] UsedIndices, int[] IgnoredIndices);

    private static CaliperLineFitResult? FitCaliperLine(IReadOnlyList<VisionPoint2D> points, int maxOutliers, double threshold)
    {
        var used = Enumerable.Range(0, points.Count).ToList();
        var ignored = new List<int>();
        var fit = FitCaliperLineCore(used.Select(index => points[index]).ToArray());
        if (fit is null) return null;
        var residuals = LineResiduals(used, points, fit.Value.Line);
        for (var i = 0; i < maxOutliers && used.Count > 3; i++)
        {
            var worst = Array.IndexOf(residuals, residuals.Max());
            if (residuals[worst] <= threshold) break;
            var removedIndex = used[worst];
            var candidateUsed = used.Where((_, index) => index != worst).ToList();
            var candidateFit = FitCaliperLineCore(candidateUsed.Select(index => points[index]).ToArray());
            if (candidateFit is null) break;
            used = candidateUsed;
            ignored.Add(removedIndex);
            fit = candidateFit;
            residuals = LineResiduals(used, points, fit.Value.Line);
        }
        return new(fit.Value.Point, fit.Value.Line, fit.Value.Error, used.ToArray(), ignored.ToArray());
    }

    private static (VisionPoint2D Point, VisionLine2D Line, double Error)? FitCaliperLineCore(IReadOnlyList<VisionPoint2D> points)
    {
        if (points.Count < 3) return null;
        var cx = points.Average(p => p.X);
        var cy = points.Average(p => p.Y);
        var xx = points.Sum(p => (p.X - cx) * (p.X - cx));
        var yy = points.Sum(p => (p.Y - cy) * (p.Y - cy));
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

    private static double[] LineResiduals(IReadOnlyList<int> indices, IReadOnlyList<VisionPoint2D> points, VisionLine2D line)
    {
        var length = line.Length;
        if (length < 1e-9) return Enumerable.Repeat(double.PositiveInfinity, indices.Count).ToArray();
        var nx = -(line.End.Y - line.Start.Y) / length;
        var ny = (line.End.X - line.Start.X) / length;
        return indices.Select(index => Math.Abs((points[index].X - line.Start.X) * nx + (points[index].Y - line.Start.Y) * ny)).ToArray();
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
        if (Math.Abs(center.X) > 10_000_000 || Math.Abs(center.Y) > 10_000_000 || nominalRadius > 10_000_000)
            throw new InvalidOperationException("Caliper circle coordinates and radius must be within 10000000 pixels.");

        var angleStart = SubpixelEdgeCore.RequireFiniteParameter(node, "angleStartDeg", -180);
        var angleEnd = SubpixelEdgeCore.RequireFiniteParameter(node, "angleEndDeg", 180);
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
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)
            || Math.Abs(center.X) > 10_000_000 || Math.Abs(center.Y) > 10_000_000)
            throw new InvalidOperationException("Caliper circle center after coordinate correction must be finite and within +/-10000000 pixels.");
        var requestedMode = node.GetString("samplingMode", "").Trim().ToLowerInvariant();
        // Old workflows only have angleStepDeg. Keep their fixed-step spacing until the user
        // explicitly switches to count mode; new nodes use a direct caliper count.
        var samplingMode = requestedMode switch
        {
            "count" or "step" => requestedMode,
            "" => node.Parameters?.ContainsKey("angleStepDeg") == true ? "step" : "count",
            _ => throw new InvalidOperationException("Parameter 'samplingMode' must be one of [count, step].")
        };
        var angleStep = Math.Clamp(SubpixelEdgeCore.RequireFiniteParameter(node, "angleStepDeg", 5), 0.5, 45);
        var caliperCount = SubpixelEdgeCore.ClampedIntParameter(node, "caliperCount", 72, 3, 361);
        var minValidCalipers = SubpixelEdgeCore.ClampedIntParameter(node, "minValidCalipers", 3, 3, 361);
        var band = Math.Clamp(SubpixelEdgeCore.RequireFiniteParameter(node, "radialBand", 8), 1, 500);
        var minContrast = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "minContrast", 8));
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "any", "any", "rising", "falling");
        var searchDirection = SubpixelEdgeCore.ChoiceParameter(node, "searchDirection", "strongest", "strongest", "firstfromstart", "firstfromend");
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 3, 1, 99));
        var maxDeviation = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "maxRadiusDeviation", 0));
        var maxFitOutliers = SubpixelEdgeCore.ClampedIntParameter(node, "maxFitOutliers", 0, 0, 16);
        var fitOutlierThreshold = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "fitOutlierThreshold", 2));
        var fitRadiusTolerance = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "fitRadiusTolerance", 0));
        var maxFitError = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "maxFitError", 0));
        var innerRadius = Math.Max(0, nominalRadius - band);
        var outerRadius = nominalRadius + band;
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null)
                mask = roi.BuildMaskForMeasurement(sourceImage.Mat.Size(), cancellationToken);
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                var angles = samplingMode == "count"
                    ? BuildAnglesByCount(angleStart, angleEnd, caliperCount, out var effectiveStep)
                    : BuildAnglesByStep(angleStart, angleEnd, angleStep, out effectiveStep);
                var edgePoints = new List<VisionPoint2D>();
                var rejected = 0;
                var rejectedByDeviation = 0;
                var spokesOutsideImage = 0;
                var spokesTooShortAfterClip = 0;
                var spokesWithoutFiniteSamples = 0;
                var spokesWithInsufficientContiguousSamples = 0;
                var spokesWithEdgeCandidate = 0;
                var spokesClipped = 0;
                var spokeOverlays = new List<VisionOverlay>(angles.Length);
                for (var spokeIndex = 0; spokeIndex < angles.Length; spokeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var angleDeg = angles[spokeIndex];
                    var radians = angleDeg * Math.PI / 180.0;
                    var dirX = Math.Cos(radians);
                    var dirY = Math.Sin(radians);
                    // A radial search cannot continue through a negative radius and emerge on the far side.
                    // Clip the inner limit at the center so first-from-start always searches outward.
                    var start = new VisionPoint2D(center.X + dirX * innerRadius, center.Y + dirY * innerRadius);
                    var end = new VisionPoint2D(center.X + dirX * outerRadius, center.Y + dirY * outerRadius);
                    if (SubpixelEdgeCore.SegmentBoundsOutsideImage(start, end, gray.Cols, gray.Rows))
                    { rejected++; spokesOutsideImage++; continue; }
                    var (profileStart, profileEnd, spokeClipped, spokeIntersects) =
                        SubpixelEdgeCore.ClampLine(start, end, gray.Cols, gray.Rows, 20_000_500);
                    if (spokeClipped) spokesClipped++;
                    if (!spokeIntersects) { rejected++; spokesOutsideImage++; continue; }
                    if (MeasurementMath.Hypot(profileEnd.X - profileStart.X, profileEnd.Y - profileStart.Y) < 2)
                    { rejected++; spokesTooShortAfterClip++; continue; }
                    var profile = SubpixelEdgeCore.SampleProfile(gray, mask, profileStart, profileEnd, sampleWidth, out _, out var spokeSpacing, cancellationToken);
                    var edge = SubpixelEdgeCore.FindEdge(profile, spokeSpacing, polarity, minContrast, searchDirection, "parabolic", cancellationToken);
                    if (!edge.Found)
                    {
                        spokeOverlays.Add(new($"caliper-circle-search-{spokeIndex}", VisionOverlayType.Line,
                            X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                            Stroke: "#bfbfbf", StrokeWidth: 0.7));
                        rejected++;
                        if (edge.Reason == "profile-has-no-finite-samples") spokesWithoutFiniteSamples++;
                        if (edge.Reason == "profile-has-insufficient-contiguous-samples") spokesWithInsufficientContiguousSamples++;
                        continue;
                    }
                    spokesWithEdgeCandidate++;
                    var clippedStartRadius = (profileStart.X - center.X) * dirX + (profileStart.Y - center.Y) * dirY;
                    var edgeRadius = clippedStartRadius + edge.Position;
                    if (maxDeviation > 0 && Math.Abs(edgeRadius - nominalRadius) > maxDeviation)
                    {
                        var candidate = new VisionPoint2D(center.X + dirX * edgeRadius, center.Y + dirY * edgeRadius);
                        spokeOverlays.Add(new($"caliper-circle-search-{spokeIndex}", VisionOverlayType.Line,
                            X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                            Stroke: "#faad14", StrokeWidth: 1));
                        spokeOverlays.Add(new($"caliper-circle-radius-rejected-{spokeIndex}", VisionOverlayType.Point,
                            Label: $"ΔR={edgeRadius - nominalRadius:+0.##;-0.##;0}", X: candidate.X, Y: candidate.Y,
                            Stroke: "#faad14", StrokeWidth: 3));
                        rejected++;
                        rejectedByDeviation++;
                        continue;
                    }
                    spokeOverlays.Add(new($"caliper-circle-search-{spokeIndex}", VisionOverlayType.Line,
                        X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                        Stroke: "#91d5ff", StrokeWidth: 0.7));
                    edgePoints.Add(new VisionPoint2D(center.X + dirX * edgeRadius, center.Y + dirY * edgeRadius));
                }

                var hasEnoughPoints = edgePoints.Count >= 3;
                var fit = hasEnoughPoints ? FitCircleWithOutlierRejection(edgePoints, maxFitOutliers, fitOutlierThreshold) : null;
                var radiusOutsideTolerance = fit is not null && fitRadiusTolerance > 0
                    && Math.Abs(fit.Circle.Radius - nominalRadius) > fitRadiusTolerance;
                var fitErrorExceedsLimit = fit is not null && maxFitError > 0 && fit.Rmse > maxFitError;
                var hasMinimumCalipers = fit is not null
                    ? fit.UsedIndices.Length >= minValidCalipers
                    : edgePoints.Count >= minValidCalipers;
                var found = fit is not null && hasMinimumCalipers && !radiusOutsideTolerance && !fitErrorExceedsLimit;
                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(found),
                    ["sampleCount"] = VisionValue.Integer(fit?.UsedIndices.Length ?? edgePoints.Count),
                    ["ignoredCount"] = VisionValue.Integer(fit?.IgnoredIndices.Length ?? 0)
                };
                var overlays = new List<VisionOverlay>
                {
                    new("caliper-circle-nominal", VisionOverlayType.Circle, X: center.X, Y: center.Y, Radius: nominalRadius, Stroke: "#597ef7", StrokeWidth: 1)
                };
                overlays.AddRange(spokeOverlays);
                var reason = found ? "ok"
                    : edgePoints.Count >= 3 && !hasMinimumCalipers ? "insufficient-valid-calipers"
                    : radiusOutsideTolerance ? "fitted-radius-outside-tolerance"
                    : fitErrorExceedsLimit ? "fit-error-exceeds-tolerance"
                    : edgePoints.Count == 0 && rejectedByDeviation > 0 && rejectedByDeviation == spokesWithEdgeCandidate
                        ? "all-spokes-exceed-max-radius-deviation"
                    : edgePoints.Count == 0 && spokesOutsideImage == angles.Length ? "search-region-outside-image"
                    : edgePoints.Count == 0 && spokesOutsideImage + spokesTooShortAfterClip == angles.Length && spokesTooShortAfterClip > 0
                        ? "search-region-clipped-too-short"
                    : edgePoints.Count == 0 && spokesWithoutFiniteSamples == angles.Length - spokesOutsideImage - spokesTooShortAfterClip
                        && spokesWithoutFiniteSamples > 0 ? "profile-has-no-finite-samples"
                    : edgePoints.Count == 0 && spokesWithoutFiniteSamples + spokesWithInsufficientContiguousSamples == angles.Length - spokesOutsideImage - spokesTooShortAfterClip
                        && spokesWithInsufficientContiguousSamples > 0 ? "profile-has-insufficient-contiguous-samples"
                    : edgePoints.Count == 0 ? "no-edge-above-threshold"
                    : edgePoints.Count < 3 ? "too-few-edge-points"
                    : "degenerate-circle-fit";
                var summary = new Dictionary<string, object?>
                {
                    ["found"] = found,
                    ["reason"] = reason,
                    ["spokes"] = angles.Length,
                    ["sampleCount"] = fit?.UsedIndices.Length ?? edgePoints.Count,
                    ["edgePoints"] = edgePoints.Count,
                    ["rejectedSpokes"] = rejected,
                    ["rejectedByDeviation"] = rejectedByDeviation,
                    ["spokesWithEdgeCandidate"] = spokesWithEdgeCandidate,
                    ["spokesOutsideImage"] = spokesOutsideImage,
                    ["spokesTooShortAfterClip"] = spokesTooShortAfterClip,
                    ["spokesWithoutFiniteSamples"] = spokesWithoutFiniteSamples,
                    ["spokesWithInsufficientContiguousSamples"] = spokesWithInsufficientContiguousSamples,
                    ["spokesClippedToImage"] = spokesClipped,
                    ["centerX"] = Math.Round(center.X, 4),
                    ["centerY"] = Math.Round(center.Y, 4),
                    ["nominalRadius"] = Math.Round(nominalRadius, 3),
                    ["angleStartDeg"] = Math.Round(angleStart, 4),
                    ["angleEndDeg"] = Math.Round(angleEnd, 4),
                    ["samplingMode"] = samplingMode,
                    ["caliperCount"] = angles.Length,
                    ["angleStepDeg"] = effectiveStep,
                    ["radialBand"] = band,
                    ["radialSearchMinRadius"] = innerRadius,
                    ["radialSearchMaxRadius"] = outerRadius,
                    ["minContrast"] = minContrast,
                    ["sampleWidth"] = sampleWidth,
                    ["maxRadiusDeviation"] = maxDeviation,
                    ["fitRadiusTolerance"] = fitRadiusTolerance,
                    ["maxFitError"] = maxFitError,
                    ["minValidCalipers"] = minValidCalipers,
                    ["maxFitOutliers"] = maxFitOutliers,
                    ["fitOutlierThreshold"] = fitOutlierThreshold,
                    ["polarity"] = polarity,
                    ["searchDirection"] = searchDirection,
                    ["refine"] = "parabolic",
                    ["autoGray"] = autoGray,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage"
                };

                if (fit is not null)
                {
                    var usedIndices = fit.UsedIndices;
                    var usedPoints = usedIndices.Select(i => edgePoints[i]).ToArray();
                    var circle = fit.Circle;
                    // Roundness/radius extrema must be measured around the fitted center;
                    // using the nominal center would turn a small center offset into false runout.
                    var usedRadii = usedPoints.Select(point => MeasurementMath.Hypot(point.X - circle.X, point.Y - circle.Y)).ToArray();
                    var rmse = fit.Rmse;
                    outputs["sampleCount"] = VisionValue.Integer(usedPoints.Length);
                    outputs["ignoredCount"] = VisionValue.Integer(fit.IgnoredIndices.Length);
                    summary["fittedRadius"] = Math.Round(circle.Radius, 3);
                    summary["radiusDelta"] = Math.Round(circle.Radius - nominalRadius, 3);
                    summary["radiusSpread"] = Math.Round(usedRadii.Max() - usedRadii.Min(), 3);
                    summary["fitError"] = Math.Round(rmse, 4);
                    summary["fitPoints"] = usedPoints.Length;
                    summary["ignoredByFit"] = fit.IgnoredIndices.Length;
                    if (found)
                    {
                        outputs["circle"] = VisionValue.Circle(circle);
                        outputs["center"] = VisionValue.Point(new VisionPoint2D(circle.X, circle.Y));
                        outputs["radius"] = VisionValue.Double(circle.Radius);
                        outputs["radiusMin"] = VisionValue.Double(usedRadii.Min());
                        outputs["radiusMax"] = VisionValue.Double(usedRadii.Max());
                        outputs["fitError"] = VisionValue.Double(rmse);
                    }
                    overlays.Add(new VisionOverlay("caliper-circle-fit", VisionOverlayType.Circle,
                        Label: $"R={circle.Radius:0.###}",
                        X: circle.X, Y: circle.Y, Radius: circle.Radius, Stroke: found ? "#52c41a" : "#fa541c", StrokeWidth: 2));
                    overlays.AddRange(usedPoints.Select((point, i) => new VisionOverlay(
                        $"caliper-circle-point-{i}", VisionOverlayType.Point, X: point.X, Y: point.Y, Stroke: "#52c41a", StrokeWidth: 2.5)));
                    overlays.AddRange(fit.IgnoredIndices.Select((index, i) => new VisionOverlay(
                        $"caliper-circle-ignored-{i}", VisionOverlayType.Point, X: edgePoints[index].X, Y: edgePoints[index].Y, Stroke: "#fa541c", StrokeWidth: 3)));
                }
                else
                {
                    outputs["ignoredCount"] = VisionValue.Integer(0);
                    overlays.AddRange(edgePoints.Select((point, index) => new VisionOverlay(
                        $"caliper-circle-candidate-point-{index}", VisionOverlayType.Point,
                        X: point.X, Y: point.Y, Stroke: "#faad14", StrokeWidth: 2.5)));
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

    private sealed record CircleFitResult(VisionCircle Circle, int[] UsedIndices, int[] IgnoredIndices, double Rmse);

    private static CircleFitResult? FitCircleWithOutlierRejection(IReadOnlyList<VisionPoint2D> points, int maxOutliers, double threshold)
    {
        var used = Enumerable.Range(0, points.Count).ToList();
        var ignored = new List<int>();
        var fittedCircle = TryFitCircle(used.Select(i => points[i]).ToArray());
        if (fittedCircle is null) return null;
        var circle = fittedCircle.Value;
        var residuals = Residuals(used, points, circle);
        for (var i = 0; i < maxOutliers && used.Count > 3; i++)
        {
            var worst = Array.IndexOf(residuals, residuals.Max());
            if (residuals[worst] <= threshold) break;
            var removedIndex = used[worst];
            var candidateUsed = used.Where((_, index) => index != worst).ToList();
            var candidateFit = TryFitCircle(candidateUsed.Select(index => points[index]).ToArray());
            if (candidateFit is null) break;
            used = candidateUsed;
            ignored.Add(removedIndex);
            circle = candidateFit.Value;
            residuals = Residuals(used, points, circle);
        }
        return new(circle, used.ToArray(), ignored.ToArray(), Math.Sqrt(residuals.Sum(x => x * x) / residuals.Length));
    }

    private static VisionCircle? TryFitCircle(IReadOnlyList<VisionPoint2D> points)
    {
        if (points.Count < 3) return null;
        var centerX = points.Average(point => point.X);
        var centerY = points.Average(point => point.Y);
        var xx = points.Sum(point => (point.X - centerX) * (point.X - centerX));
        var yy = points.Sum(point => (point.Y - centerY) * (point.Y - centerY));
        var xy = points.Sum(point => (point.X - centerX) * (point.Y - centerY));
        var spread = xx + yy;
        var determinant = xx * yy - xy * xy;
        // A very short arc can look almost like a straight line and yield a finite but wildly
        // incorrect circle. Reject poorly conditioned point clouds instead of reporting that
        // unstable radius as a successful measurement.
        const double minimumNormalizedSpread = 2e-3;
        if (!double.IsFinite(spread) || !double.IsFinite(determinant) || spread < 1e-8
            || determinant <= spread * spread * minimumNormalizedSpread)
            return null;
        try { return MultiPointFit.Circle(points.ToArray()); }
        catch (InvalidOperationException) { return null; }
    }

    private static double[] Residuals(IReadOnlyList<int> indices, IReadOnlyList<VisionPoint2D> points, VisionCircle circle) =>
        indices.Select(i => Math.Abs(MeasurementMath.Hypot(points[i].X - circle.X, points[i].Y - circle.Y) - circle.Radius)).ToArray();

    private static (double Span, bool FullCircle) ArcSpan(double start, double end)
    {
        var raw = end - start;
        if (Math.Abs(raw) < 1e-9) return (0, false);
        var span = raw % 360;
        if (Math.Abs(span) < 1e-9) return (360, true);
        if (span < 0) span += 360;
        return (span, false);
    }

    private static double[] BuildAnglesByCount(double start, double end, int count, out double effectiveStep)
    {
        var (span, fullCircle) = ArcSpan(start, end);
        if (span == 0)
        {
            effectiveStep = 0;
            return [start];
        }
        count = Math.Clamp(count, 3, 361);
        effectiveStep = fullCircle ? span / count : span / (count - 1);
        var angles = new double[count];
        for (var i = 0; i < count; i++) angles[i] = start + i * effectiveStep;
        return angles;
    }

    private static double[] BuildAnglesByStep(double start, double end, double step, out double effectiveStep)
    {
        var (span, fullCircle) = ArcSpan(start, end);
        effectiveStep = step;
        if (span == 0) return [start];

        var intervalCount = span / effectiveStep;
        var nearestIntervalCount = (int)Math.Round(intervalCount);
        var reachesEnd = nearestIntervalCount > 0
            && Math.Abs(nearestIntervalCount * effectiveStep - span) <= 1e-9;
        var count = fullCircle && reachesEnd
            ? nearestIntervalCount
            : (int)Math.Floor(intervalCount) + 1;
        var maximumCount = fullCircle ? 360 : 361;
        if (count > maximumCount)
        {
            effectiveStep = span / (fullCircle ? maximumCount : maximumCount - 1);
            count = maximumCount;
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
