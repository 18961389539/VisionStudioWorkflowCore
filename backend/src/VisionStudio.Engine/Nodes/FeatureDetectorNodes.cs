using System.Diagnostics;
using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>feature.corner / feature.ellipse / feature.gaborBank 的共用校验：8 位灰度 + ROI 掩膜；feature.ridge 直接走 VisionGeometryHelpers，不经过这里。</summary>
internal static class FeatureDetectorHelpers
{
    internal static Mat Prepare(VisionImage source, VisionRoi? roi, string nodeType)
    {
        if (source.Mat.Empty() || source.Mat.Width < 1 || source.Mat.Height < 1)
            throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        if (source.Mat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException(
                $"{nodeType} requires an 8-bit image (16-bit or float input must be converted first); received {source.Mat.Depth()}.");
        if (source.Mat.Width > 16384 || source.Mat.Height > 16384 || source.Mat.Total() > 64_000_000)
            throw new InvalidOperationException($"{nodeType} image must be at most 16384 per axis and 64000000 pixels.");
        using var gray = VisionGeometryHelpers.ToGray(source.Mat);
        return VisionGeometryHelpers.ApplyRoi(gray, roi);
    }
}

/// <summary>
/// 角点检测（feature.corner）：Shi-Tomasi / Harris / FAST，输出按响应排序的 PointList。
/// 现场用途：把角点直接喂给 fitLine / fitCircleFixedRadius / bestFitAlign，省掉"先分割再提轮廓"。
/// </summary>
public sealed class FeatureCornerNode : IVisionNodeExecutor
{
    public string Type => "feature.corner";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var method = (node.GetString("method", "shi-tomasi") ?? "shi-tomasi").Trim().ToLowerInvariant();
        if (method is not ("shi-tomasi" or "harris" or "fast"))
            throw new InvalidOperationException("feature.corner method must be shi-tomasi, harris or fast.");

        var fastThreshold = node.GetInt("fastThreshold", 10);
        if (fastThreshold is < 1 or > 255)
            throw new InvalidOperationException("feature.corner fastThreshold must be within 1..255.");

        var maxCorners = node.GetInt("maxCorners", 200);
        if (maxCorners is < 1 or > 5000)
            throw new InvalidOperationException("feature.corner maxCorners must be within 1..5000.");

        var qualityLevel = node.GetDouble("qualityLevel", 0.01);
        if (!double.IsFinite(qualityLevel) || qualityLevel is <= 0 or > 1)
            throw new InvalidOperationException("feature.corner qualityLevel must be within (0, 1].");

        var minDistance = node.GetDouble("minDistance", 10);
        if (!double.IsFinite(minDistance) || minDistance is < 0.5 or > 5000)
            throw new InvalidOperationException("feature.corner minDistance must be within 0.5..5000.");

        var blockSize = node.GetInt("blockSize", 3);
        if (blockSize is < 3 or > 31 || blockSize % 2 == 0)
            throw new InvalidOperationException("feature.corner blockSize must be odd within 3..31.");

        var harrisK = node.GetDouble("harrisK", 0.04);
        if (!double.IsFinite(harrisK) || harrisK is < 0.001 or > 0.25)
            throw new InvalidOperationException("feature.corner harrisK must be within 0.001..0.25.");

        var subpixel = node.GetBool("subpixel", false);

        var source = context.RequireImage("image");
        var roi = node.GetRoi();
        using var analysis = FeatureDetectorHelpers.Prepare(source, roi, Type);
        if (Cv2.CountNonZero(analysis) == 0 && roi is not null)
        {
            var emptyDiag = CoordinateValidation.Diagnostics(Type, ("found", false), ("errorCode", "emptyRoi"), ("roi", roi.Type.ToString()));
            return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(source), ["points"] = VisionValue.PointList([]), ["count"] = VisionValue.Integer(0),
                ["maxResponse"] = VisionValue.Double(0), ["meanResponse"] = VisionValue.Double(0), ["found"] = VisionValue.Boolean(false),
                ["diagnostics"] = VisionValue.String(emptyDiag)
            }, new Dictionary<string, object?> { ["found"] = false, ["errorCode"] = "emptyRoi", ["diagnostics"] = emptyDiag }));
        }
        Cv2.MeanStdDev(analysis, out var meanGray, out var stdGray);
        var lowContrast = stdGray.Val0 < 1e-6;
        if (lowContrast)
        {
            var contrastDiag = CoordinateValidation.Diagnostics(Type, ("found", false), ("errorCode", "insufficientContrast"), ("mean", meanGray.Val0), ("std", stdGray.Val0));
            return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(source), ["points"] = VisionValue.PointList([]), ["count"] = VisionValue.Integer(0),
                ["maxResponse"] = VisionValue.Double(0), ["meanResponse"] = VisionValue.Double(0), ["found"] = VisionValue.Boolean(false),
                ["diagnostics"] = VisionValue.String(contrastDiag)
            }, new Dictionary<string, object?> { ["found"] = false, ["errorCode"] = "insufficientContrast", ["diagnostics"] = contrastDiag }));
        }

        var detected = FeatureDetectionAlgorithms.Corners(analysis, method, fastThreshold, maxCorners, qualityLevel,
            minDistance, blockSize, harrisK, subpixel, cancellationToken);
        var ordered = detected.Points;
        var meanResponse = detected.MeanResponse;
        var peak = detected.PeakResponse;

        var overlays = new List<VisionOverlay>();
        for (var index = 0; index < Math.Min(ordered.Length, 400); index++)
        {
            overlays.Add(new VisionOverlay($"corner-{index}", VisionOverlayType.Point,
                X: ordered[index].X, Y: ordered[index].Y,
                Stroke: index == 0 ? "#ff4d4f" : "#52c41a", StrokeWidth: index == 0 ? 2 : 1));
        }
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("method", method), ("count", ordered.Length), ("maxCorners", maxCorners),
            ("requested", detected.Requested), ("refined", detected.Refined), ("discarded", detected.Discarded),
            ("errorCode", ordered.Length > 0 ? "none" : "noCorners"),
            ("fastThreshold", fastThreshold),
            ("qualityLevel", qualityLevel), ("minDistance", minDistance), ("blockSize", blockSize),
            ("subpixel", subpixel), ("maxResponse", peak), ("meanResponse", meanResponse),
            ("roi", roi?.Type.ToString() ?? "FullImage"),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = VisionValue.Image(source),
                ["points"] = VisionValue.PointList(ordered),
                ["count"] = VisionValue.Integer(ordered.Length),
                ["maxResponse"] = VisionValue.Double(peak),
                ["meanResponse"] = VisionValue.Double(meanResponse),
                ["found"] = VisionValue.Boolean(ordered.Length > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["method"] = method, ["count"] = ordered.Length, ["maxCorners"] = maxCorners,
                ["fastThreshold"] = fastThreshold,
                ["qualityLevel"] = qualityLevel, ["minDistance"] = minDistance, ["blockSize"] = blockSize,
                ["subpixel"] = subpixel,
                ["maxResponse"] = Math.Round(peak, 4), ["meanResponse"] = Math.Round(meanResponse, 4),
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }, overlays));
    }

}

/// <summary>
/// 椭圆检测（feature.ellipse）：Canny → 轮廓 → 椭圆拟合 → 一致性过滤。
/// 斜视圆孔、O 型圈、透视变形的圆，一步给出中心/长短半轴/倾角/覆盖率/残差。
/// </summary>
public sealed class FeatureEllipseNode : IVisionNodeExecutor
{
    public string Type => "feature.ellipse";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var cannyLow = node.GetDouble("cannyLow", 50);
        var cannyHigh = node.GetDouble("cannyHigh", 150);
        if (!double.IsFinite(cannyLow) || !double.IsFinite(cannyHigh) || cannyLow is < 0 or > 255 || cannyHigh is < 0 or > 255)
            throw new InvalidOperationException("feature.ellipse Canny thresholds must be within 0..255.");
        if (cannyHigh < cannyLow)
            throw new InvalidOperationException("feature.ellipse cannyHigh must be >= cannyLow.");

        var minArea = node.GetDouble("minArea", 200);
        var maxArea = node.GetDouble("maxArea", 10_000_000);
        if (!double.IsFinite(minArea) || !double.IsFinite(maxArea) || minArea <= 0 || maxArea < minArea)
            throw new InvalidOperationException("feature.ellipse requires 0 < minArea <= maxArea.");

        var minAspect = node.GetDouble("minAspectRatio", 1);
        var maxAspect = node.GetDouble("maxAspectRatio", 1);
        if (!double.IsFinite(minAspect) || !double.IsFinite(maxAspect) || minAspect is <= 0 or > 1 || maxAspect is <= 0 or > 1 || maxAspect < minAspect)
            throw new InvalidOperationException("feature.ellipse aspect ratios must satisfy 0 < minAspectRatio <= maxAspectRatio <= 1.");

        var minCoverage = node.GetDouble("minCoverageDeg", 200);
        if (!double.IsFinite(minCoverage) || minCoverage is < 10 or > 360)
            throw new InvalidOperationException("feature.ellipse minCoverageDeg must be within 10..360.");

        var maxRms = node.GetDouble("maxRmsError", 2);
        if (!double.IsFinite(maxRms) || maxRms is < 0 or > 1000)
            throw new InvalidOperationException("feature.ellipse maxRmsError must be within 0..1000 (0 disables).");

        var maxEllipses = node.GetInt("maxEllipses", 10);
        if (maxEllipses is < 1 or > 200)
            throw new InvalidOperationException("feature.ellipse maxEllipses must be within 1..200.");

        var minPoints = node.GetInt("minPoints", 20);
        if (minPoints is < 5 or > 100000)
            throw new InvalidOperationException("feature.ellipse minPoints must be within 5..100000.");

        var source = context.RequireImage("image");
        var roi = node.GetRoi();
        using var analysis = FeatureDetectorHelpers.Prepare(source, roi, Type);
        var emptyRoi = roi is not null && Cv2.CountNonZero(analysis) == 0;
        var detected = FeatureDetectionAlgorithms.Ellipses(analysis, cannyLow, cannyHigh, minPoints, minArea, maxArea,
            minAspect, maxAspect, minCoverage, maxRms, maxEllipses, cancellationToken);
        var ordered = detected.Ordered;
        // 截断必须可见：maxEllipses 默认 10，去重后多于 10 个时下游只能看到前 10 个。
        var inputCount = detected.InputCount;
        var truncated = inputCount > ordered.Length;

        var overlays = new List<VisionOverlay>();
        for (var index = 0; index < ordered.Length; index++)
        {
            // 叠加图类型只有 Point/Line/Circle/Rectangle/Polygon/Contour/Text：
            // 椭圆用外接圆表示，长短轴与倾角在标签里写明。
            overlays.Add(new VisionOverlay($"ellipse-{index}", VisionOverlayType.Circle,
                X: ordered[index].CenterX, Y: ordered[index].CenterY,
                Radius: ordered[index].SemiMajor,
                Label: $"a={ordered[index].SemiMajor:0.0} b={ordered[index].SemiMinor:0.0} {ordered[index].AngleDeg:0.0}deg",
                Stroke: index == 0 ? "#52c41a" : "#597ef7", StrokeWidth: index == 0 ? 2.5 : 1.5));
        }
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", ordered.Length), ("inputCount", inputCount), ("truncated", truncated),
            ("errorCode", ordered.Length > 0 ? "none" : emptyRoi ? "emptyRoi" : detected.ContourCount == 0 ? "noCandidate" : "allFiltered"),
            ("contours", detected.ContourCount), ("candidates", detected.CandidateCount),
            ("minArea", minArea), ("minCoverageDeg", minCoverage), ("maxRmsError", maxRms),
            ("roi", roi?.Type.ToString() ?? "FullImage"),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["image"] = VisionValue.Image(source),
            ["ellipses"] = VisionValue.String(JsonSerializer.Serialize(ordered.Select(candidate => new
            {
                x = Math.Round(candidate.CenterX, 3),
                y = Math.Round(candidate.CenterY, 3),
                semiMajor = Math.Round(candidate.SemiMajor, 3),
                semiMinor = Math.Round(candidate.SemiMinor, 3),
                angleDeg = Math.Round(candidate.AngleDeg, 3),
                rmsError = Math.Round(candidate.Rms, 4),
                coverageDeg = Math.Round(candidate.Coverage, 2),
                supportPoints = candidate.SupportPoints,
                area = Math.Round(candidate.Area, 2)
            }))),
            ["count"] = VisionValue.Integer(ordered.Length),
            ["inputCount"] = VisionValue.Integer(inputCount),
            ["truncated"] = VisionValue.Boolean(truncated),
            ["found"] = VisionValue.Boolean(ordered.Length > 0),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (ordered.Length > 0)
        {
            var best = ordered[0];
            outputs["ellipse"] = VisionValue.String(JsonSerializer.Serialize(new
            {
                x = Math.Round(best.CenterX, 3),
                y = Math.Round(best.CenterY, 3),
                semiMajor = Math.Round(best.SemiMajor, 3),
                semiMinor = Math.Round(best.SemiMinor, 3),
                angleDeg = Math.Round(best.AngleDeg, 3)
            }));
            outputs["center"] = VisionValue.Point(new VisionPoint2D(best.CenterX, best.CenterY));
            outputs["majorAxis"] = VisionValue.Double(best.SemiMajor * 2);
            outputs["minorAxis"] = VisionValue.Double(best.SemiMinor * 2);
            outputs["angleDeg"] = VisionValue.Double(best.AngleDeg);
            outputs["rmsError"] = VisionValue.Double(best.Rms);
            outputs["coverageDeg"] = VisionValue.Double(best.Coverage);
        }

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = ordered.Length, ["contours"] = detected.ContourCount, ["candidates"] = detected.CandidateCount,
                ["minArea"] = minArea, ["minCoverageDeg"] = minCoverage, ["maxRmsError"] = maxRms,
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }, overlays));
    }

}

/// <summary>
/// 脊线 / 中心线（feature.ridge）：JLVision linesGauss 的一等节点封装。
/// 划痕、焊缝、线缆、印线这类细长目标要的是"路径 + 宽度"，不是区域。
/// </summary>
public sealed class FeatureRidgeNode : IVisionNodeExecutor
{
    public string Type => "feature.ridge";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var sigma = node.GetDouble("sigma", 1.5);
        if (!double.IsFinite(sigma) || sigma is < 0.1 or > 100)
            throw new InvalidOperationException("feature.ridge sigma must be within 0.1..100.");

        var low = node.GetDouble("low", 5);
        var high = node.GetDouble("high", 10);
        if (!double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high < low)
            throw new InvalidOperationException("feature.ridge thresholds must satisfy 0 <= low <= high.");

        var polarity = (node.GetString("polarity", "light") ?? "light").Trim().ToLowerInvariant();
        if (polarity is not ("light" or "dark" or "both"))
            throw new InvalidOperationException("feature.ridge polarity must be light, dark or both.");

        var lineModel = (node.GetString("lineModel", "bar-shaped") ?? "bar-shaped").Trim().ToLowerInvariant();
        if (lineModel is not ("bar-shaped" or "parabolic" or "gaussian"))
            throw new InvalidOperationException("feature.ridge lineModel must be bar-shaped, parabolic or gaussian.");

        var extractWidth = node.GetBool("extractWidth", true);
        var completeJunctions = node.GetBool("completeJunctions", true);
        var minLength = node.GetDouble("minLength", 10);
        if (!double.IsFinite(minLength) || minLength is < 0 or > 1e6)
            throw new InvalidOperationException("feature.ridge minLength must be within 0..1000000.");

        var source = context.RequireImage("image");
        var roi = node.GetRoi();
        using var gray = VisionGeometryHelpers.ToGray(source.Mat);
        using var masked = VisionGeometryHelpers.ApplyRoi(gray, roi);

        IReadOnlyList<VisionContour> contours;
        var raw = new List<VisionContour>();
        using var native = JLVisionImages.FromMat(masked);
        foreach (var side in polarity == "both" ? new[] { "light", "dark" } : new[] { polarity })
        {
            using var nativeSigma = new JlTuple(sigma);
            using var nativeLow = new JlTuple(low);
            using var nativeHigh = new JlTuple(high);
            JlXLDCont? curves = null;
            try
            {
                curves = native.LinesGauss(nativeSigma, nativeLow, nativeHigh, side,
                    extractWidth ? "true" : "false", lineModel, "false");
                raw.AddRange(JlContourCodec.FromNative(curves, cancellationToken));
            }
            finally { curves?.Dispose(); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bridgeCount = 0;
        if (completeJunctions) bridgeCount = CompleteRidgeJunctions(raw, sigma);
        contours = raw.Where(contour => contour.Points.Count >= 2 && ContourLength(contour) >= minLength).ToArray();
        var points = new List<VisionPoint2D>();
        var widths = new List<double>();
        foreach (var contour in contours)
        {
            points.AddRange(contour.Points);
            if (contour.Attributes is { Count: > 0 })
            {
                foreach (var key in contour.Attributes.Keys)
                {
                    if (!key.Equals("width", StringComparison.OrdinalIgnoreCase)) continue;
                    widths.AddRange(contour.Attributes[key]);
                    break;
                }
            }
        }
        var totalLength = contours.Sum(ContourLength);
        var meanWidth = widths.Count > 0 ? widths.Average() : 0d;

        var overlays = new List<VisionOverlay>();
        for (var index = 0; index < contours.Count && index < 200; index++)
        {
            var pts = contours[index].Points;
            for (var step = 1; step < pts.Count; step += 3)
            {
                overlays.Add(new VisionOverlay($"ridge-{index}-{step}", VisionOverlayType.Line,
                    X: pts[step - 1].X, Y: pts[step - 1].Y, X2: pts[step].X, Y2: pts[step].Y,
                    Stroke: index == 0 ? "#52c41a" : "#597ef7", StrokeWidth: 1.5));
            }
        }
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", contours.Count), ("totalLength", totalLength), ("meanWidth", meanWidth),
            ("sigma", sigma), ("low", low), ("high", high), ("polarity", polarity), ("lineModel", lineModel),
            ("completeJunctions", completeJunctions), ("junctionBridgeCount", bridgeCount),
            ("sigmaWarning", sigma > Math.Min(masked.Width, masked.Height) / 20d ? "sigmaMayMergeAdjacentRidges" : "none"),
            ("recommendedMaxSigma", Math.Min(masked.Width, masked.Height) / 20d),
            ("minLength", minLength), ("pointCount", points.Count),
            ("roi", roi?.Type.ToString() ?? "FullImage"),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = VisionValue.Image(source),
                ["contours"] = VisionValue.Contours(contours),
                ["points"] = VisionValue.PointList(points.ToArray()),
                ["count"] = VisionValue.Integer(contours.Count),
                ["totalLength"] = VisionValue.Double(totalLength),
                ["meanWidth"] = VisionValue.Double(meanWidth),
                ["found"] = VisionValue.Boolean(contours.Count > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = contours.Count, ["totalLength"] = Math.Round(totalLength, 3),
                ["meanWidth"] = Math.Round(meanWidth, 4), ["sigma"] = sigma, ["low"] = low, ["high"] = high,
                ["polarity"] = polarity, ["lineModel"] = lineModel, ["minLength"] = minLength,
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "jlvision", ["diagnostics"] = diagnostics
            }, overlays));
    }

    private static int CompleteRidgeJunctions(List<VisionContour> contours, double sigma)
    {
        var bridges = 0;
        var used = new bool[contours.Count];
        var maxEndpointDistance = Math.Max(2d, sigma * 2d);
        for (var i = 0; i < contours.Count; i++)
        {
            if (used[i] || contours[i].Points.Count < 2) continue;
            for (var j = i + 1; j < contours.Count; j++)
            {
                if (used[j] || contours[j].Points.Count < 2) continue;
                if (!TryBridgeRidges(contours[i], contours[j], maxEndpointDistance, out var merged)) continue;
                contours[i] = merged;
                used[j] = true;
                bridges++;
            }
        }
        for (var i = contours.Count - 1; i >= 0; i--) if (used[i]) contours.RemoveAt(i);
        return bridges;
    }

    private static bool TryBridgeRidges(VisionContour left, VisionContour right, double maxDistance,
        out VisionContour merged)
    {
        merged = left;
        var leftStart = Tangent(left.Points, false);
        var leftEnd = Tangent(left.Points, true);
        var rightStart = Tangent(right.Points, false);
        var rightEnd = Tangent(right.Points, true);
        var candidates = new (VisionPoint2D Left, VisionPoint2D Right, bool ReverseRight, VisionPoint2D LeftTangent, VisionPoint2D RightTangent)[]
        {
            (left.Points[^1], right.Points[0], false, leftEnd, rightStart),
            (left.Points[^1], right.Points[^1], true, leftEnd, rightEnd),
            (left.Points[0], right.Points[0], true, leftStart, rightStart),
            (left.Points[0], right.Points[^1], false, leftStart, rightEnd)
        };
        var matches = candidates.Where(item => MeasurementMath.Hypot(item.Left.X - item.Right.X, item.Left.Y - item.Right.Y) <= maxDistance)
            .OrderBy(item => MeasurementMath.Hypot(item.Left.X - item.Right.X, item.Left.Y - item.Right.Y)).ToArray();
        if (matches.Length == 0) return false;
        var best = matches[0];
        var dot = Math.Abs(best.LeftTangent.X * best.RightTangent.X + best.LeftTangent.Y * best.RightTangent.Y);
        // Parallel or collinear ridges are never bridged; only near-endpoint crossings are joined.
        if (dot >= Math.Cos(20 * Math.PI / 180)) return false;
        var rightPoints = best.ReverseRight ? right.Points.Reverse().ToArray() : right.Points.ToArray();
        var points = new List<VisionPoint2D>(left.Points.Count + rightPoints.Length + 2) { left.Points[0] };
        points.AddRange(left.Points.Skip(1));
        points.Add(best.Right);
        points.AddRange(rightPoints.Skip(1));
        var leftWidths = Widths(left);
        var rightWidths = Widths(right);
        var widths = new List<double>(leftWidths);
        widths.Add((leftWidths[^1] + rightWidths[0]) / 2d);
        widths.AddRange(rightWidths.Skip(1));
        var minX = points.Min(point => point.X); var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X); var maxY = points.Max(point => point.Y);
        merged = left with
        {
            Points = points,
            Perimeter = ContourLength(left with { Points = points }),
            BoundingBox = new VisionRectangle2D(minX, minY, maxX - minX, maxY - minY),
            Attributes = new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase) { ["width"] = widths }
        };
        return true;
    }

    private static VisionPoint2D Tangent(IReadOnlyList<VisionPoint2D> points, bool fromEnd)
    {
        var last = fromEnd ? points.Count - 1 : 0;
        var previous = fromEnd ? points.Count - 2 : 1;
        var dx = points[last].X - points[previous].X;
        var dy = points[last].Y - points[previous].Y;
        var length = Math.Max(1e-12, Math.Sqrt(dx * dx + dy * dy));
        return new VisionPoint2D(dx / length, dy / length);
    }

    private static IReadOnlyList<double> Widths(VisionContour contour)
    {
        if (contour.Attributes is not null)
            foreach (var pair in contour.Attributes)
                if (pair.Key.Equals("width", StringComparison.OrdinalIgnoreCase) && pair.Value.Count == contour.Points.Count)
                    return pair.Value;
        return Enumerable.Repeat(0d, contour.Points.Count).ToArray();
    }

    private static double ContourLength(VisionContour contour)
    {
        var length = 0d;
        for (var index = 1; index < contour.Points.Count; index++)
        {
            var a = contour.Points[index - 1];
            var b = contour.Points[index];
            length += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        return length;
    }
}

/// <summary>
/// Gabor 纹理能量（feature.gaborBank）：固定波长、多方向的 Gabor 滤波器组。
/// 织纹、拉丝、磨砂面的方向性能量图，是纹理缺陷检测的标准前置。
/// </summary>
public sealed class FeatureGaborBankNode : IVisionNodeExecutor
{
    public string Type => "feature.gaborBank";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var wavelength = node.GetDouble("wavelength", 8);
        if (!double.IsFinite(wavelength) || wavelength is < 1.5 or > 500)
            throw new InvalidOperationException("feature.gaborBank wavelength must be within 1.5..500 pixels.");

        var orientations = node.GetInt("orientations", 6);
        if (orientations is < 1 or > 24)
            throw new InvalidOperationException("feature.gaborBank orientations must be within 1..24.");

        var sigmaRatio = node.GetDouble("sigmaRatio", 0.56);
        if (!double.IsFinite(sigmaRatio) || sigmaRatio is < 0.2 or > 1.5)
            throw new InvalidOperationException("feature.gaborBank sigmaRatio must be within 0.2..1.5.");

        var gamma = node.GetDouble("gamma", 0.5);
        if (!double.IsFinite(gamma) || gamma is < 0.1 or > 1)
            throw new InvalidOperationException("feature.gaborBank gamma must be within 0.1..1.");

        var energyMode = (node.GetString("energyMode", "sum") ?? "sum").Trim().ToLowerInvariant();
        if (energyMode is not ("sum" or "max"))
            throw new InvalidOperationException("feature.gaborBank energyMode must be sum or max.");

        var emitDirectionMap = node.GetBool("directionMap", true);
        var energyNormalization = node.GetString("energyNormalization", "normalized").Trim().ToLowerInvariant();
        if (energyNormalization is not ("raw" or "normalized"))
            throw new InvalidOperationException("feature.gaborBank energyNormalization must be raw or normalized.");

        var source = context.RequireImage("image");
        var roi = node.GetRoi();
        using var analysis = FeatureDetectorHelpers.Prepare(source, roi, Type);
        var roiLimit = Math.Min(analysis.Width, analysis.Height) / 2.0;
        if (wavelength > roiLimit)
            throw new InvalidOperationException(
                $"feature.gaborBank errorCode=degenerateKernel: wavelength={wavelength} exceeds ROI limit={roiLimit} (width={analysis.Width}, height={analysis.Height}); lower it or crop the ROI.");

        var bank = FeatureDetectionAlgorithms.GaborBank(
            analysis, wavelength, orientations, sigmaRatio, gamma, energyMode, emitDirectionMap,
            energyNormalization == "normalized", cancellationToken);
        var sigma = bank.Sigma;
        var kernelSize = bank.KernelSize;
        var perOrientation = bank.Energies;
        var meanEnergy = bank.Mean;
        var maxEnergy = bank.Maximum;
        var dominantDegrees = bank.DominantDegrees;
        var anisotropy = bank.Anisotropy;
        var degreesStep = 180.0 / orientations;

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["image"] = VisionValue.Image(VisionImage.Own(bank.Image)),
            ["energy"] = VisionValue.String(JsonSerializer.Serialize(perOrientation.Select((value, index) => new
            {
                angleDeg = Math.Round(degreesStep * index, 2),
                energy = Math.Round(value, 6)
            }))),
            ["dominantDirectionDeg"] = VisionValue.Double(dominantDegrees),
            ["anisotropy"] = VisionValue.Double(anisotropy),
            ["meanEnergy"] = VisionValue.Double(meanEnergy),
            ["maxEnergy"] = VisionValue.Double(maxEnergy),
            ["count"] = VisionValue.Integer(orientations),
            ["found"] = VisionValue.Boolean(maxEnergy > 1e-9),
            ["diagnostics"] = VisionValue.String(string.Empty)
        };

        if (bank.Direction is not null)
            outputs["directionMap"] = VisionValue.Image(VisionImage.Own(bank.Direction));

        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("wavelength", wavelength), ("orientations", orientations), ("sigma", sigma),
            ("kernelSize", kernelSize), ("gamma", gamma), ("energyMode", energyMode),
            ("energyNormalization", energyNormalization), ("gainNormalized", bank.GainNormalized), ("kernelGain", bank.KernelGain), ("roiLimit", roiLimit),
            ("dominantDirectionDeg", dominantDegrees), ("anisotropy", anisotropy),
            ("meanEnergy", meanEnergy), ("maxEnergy", maxEnergy),
            ("roi", roi?.Type.ToString() ?? "FullImage"),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        outputs["diagnostics"] = VisionValue.String(diagnostics);

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wavelength"] = wavelength, ["orientations"] = orientations, ["sigma"] = sigma,
                ["kernelSize"] = kernelSize, ["gamma"] = gamma, ["energyMode"] = energyMode,
                ["energyNormalization"] = energyNormalization, ["gainNormalized"] = bank.GainNormalized, ["kernelGain"] = bank.KernelGain,
                ["dominantDirectionDeg"] = dominantDegrees, ["anisotropy"] = Math.Round(anisotropy, 4),
                ["meanEnergy"] = meanEnergy, ["maxEnergy"] = maxEnergy,
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }
}
