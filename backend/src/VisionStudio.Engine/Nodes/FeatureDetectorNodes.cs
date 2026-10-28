using System.Diagnostics;
using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>特征检测四个节点的共用校验（2026-10-28）：8 位灰度 + ROI 掩膜，三处都走同一条路径。</summary>
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
        if (method is not ("shi-tomasi" or "harris"))
            throw new InvalidOperationException("feature.corner method must be shi-tomasi or harris.");

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

        var points = new List<VisionPoint2D>();
        using var mask = new Mat();
        var corners = Cv2.GoodFeaturesToTrack(
            analysis, maxCorners, qualityLevel, minDistance, mask, blockSize,
            useHarrisDetector: method == "harris", k: harrisK) ?? [];
        foreach (var corner in corners) points.Add(new VisionPoint2D(corner.X, corner.Y));

        if (subpixel && points.Count > 0)
        {
            // cornerSubPix 把角点从像素栅格拉到亚像素；计量场景要的就是这一步。
            var refined = points.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
            Cv2.CornerSubPix(analysis, refined, new Size(5, 5), new Size(-1, -1),
                new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.01));
            points = refined.Select(point => new VisionPoint2D(point.X, point.Y)).ToList();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var response = new Mat();
        Cv2.CornerMinEigenVal(analysis, response, blockSize);
        Cv2.MinMaxLoc(response, out _, out var maxResponse, out _, out _);
        var scaled = points
            .Select(point => (Point: point, Response: SampleResponse(response, point)))
            .OrderByDescending(entry => entry.Response)
            .ToArray();
        var ordered = scaled.Select(entry => entry.Point).ToArray();
        var meanResponse = scaled.Length > 0 ? scaled.Average(entry => entry.Response) : 0d;
        var peak = scaled.Length > 0 ? scaled.Max(entry => entry.Response) : 0d;

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
                ["qualityLevel"] = qualityLevel, ["minDistance"] = minDistance, ["blockSize"] = blockSize,
                ["subpixel"] = subpixel,
                ["maxResponse"] = Math.Round(peak, 4), ["meanResponse"] = Math.Round(meanResponse, 4),
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }, overlays));
    }

    private static double Distance(VisionPoint2D a, VisionPoint2D b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double SampleResponse(Mat response, VisionPoint2D point)
    {
        var x = (int)Math.Round(point.X);
        var y = (int)Math.Round(point.Y);
        if (x < 0 || y < 0 || x >= response.Cols || y >= response.Rows) return 0;
        return response.At<float>(y, x);
    }
}

/// <summary>
/// 椭圆检测（feature.ellipse）：Canny → 轮廓 → 椭圆拟合 → 一致性过滤。
/// 斜视圆孔、O 型圈、透视变形的圆，一步给出中心/长短半轴/倾角/覆盖率/残差。
/// </summary>
public sealed class FeatureEllipseNode : IVisionNodeExecutor
{
    public string Type => "feature.ellipse";

    private const int CoverageBins = 72;

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
        // 细线边缘（1 px 描边、刻线）在裸 Canny 上会碎成几十段：先做 3x3 平滑再求梯度。
        using var smoothed = new Mat();
        Cv2.GaussianBlur(analysis, smoothed, new Size(3, 3), 0);
        using var edges = new Mat();
        Cv2.Canny(smoothed, edges, cannyLow, cannyHigh);
        cancellationToken.ThrowIfCancellationRequested();

        Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
        var candidates = new List<EllipseCandidate>();
        foreach (var contour in contours)
        {
            if (contour.Length < minPoints) continue;
            RotatedRect fit;
            try
            {
                using var mat = Mat.FromArray(contour);
                fit = Cv2.FitEllipse((InputArray)mat);
            }
            catch (OpenCVException) { continue; }

            var halfWidth = fit.Size.Width / 2.0;
            var halfHeight = fit.Size.Height / 2.0;
            if (halfWidth <= 0.5 || halfHeight <= 0.5) continue;
            var semiMajor = Math.Max(halfWidth, halfHeight);
            var semiMinor = Math.Min(halfWidth, halfHeight);
            var area = Math.PI * semiMajor * semiMinor;
            if (area < minArea || area > maxArea) continue;
            var aspect = semiMinor / semiMajor;
            if (aspect < minAspect || aspect > maxAspect) continue;

            // OpenCV 的 RotatedRect.Angle 是"宽边与 x 轴"的夹角；主轴角度要按长短边换算。
            var majorAngle = halfWidth >= halfHeight ? fit.Angle : fit.Angle + 90.0;
            majorAngle = NormalizeDeg(majorAngle);

            var radians = majorAngle * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            var residuals = new double[contour.Length];
            var bins = new bool[CoverageBins];
            var sumSquared = 0d;
            for (var index = 0; index < contour.Length; index++)
            {
                var dx = contour[index].X - fit.Center.X;
                var dy = contour[index].Y - fit.Center.Y;
                var localX = dx * cos + dy * sin;
                var localY = -dx * sin + dy * cos;
                var normalized = Math.Sqrt((localX / semiMajor) * (localX / semiMajor) + (localY / semiMinor) * (localY / semiMinor));
                var radius = Math.Sqrt(localX * localX + localY * localY);
                residuals[index] = (normalized - 1.0) * radius;
                sumSquared += residuals[index] * residuals[index];
                var parametric = Math.Atan2(localY / semiMinor, localX / semiMajor);
                var bin = (int)Math.Floor((parametric + Math.PI) / (2 * Math.PI) * CoverageBins) % CoverageBins;
                bins[bin] = true;
            }
            var rms = Math.Sqrt(sumSquared / contour.Length);
            if (maxRms > 0 && rms > maxRms) continue;
            var coverage = bins.Count(occupied => occupied) * 360.0 / CoverageBins;
            if (coverage < minCoverage) continue;

            candidates.Add(new EllipseCandidate(
                fit.Center.X, fit.Center.Y, semiMajor, semiMinor, majorAngle,
                rms, coverage, contour.Length, area,
                Math.Clamp(1.0 - rms / Math.Max(1e-6, semiMajor), 0, 1) * (coverage / 360.0)));
        }

        // 同一处边缘的内、外两条轮廓会各拟合出一个椭圆：按中心与半轴近似度去重，保留支持点多的。
        var deduped = new List<EllipseCandidate>();
        foreach (var candidate in candidates.OrderByDescending(item => item.SupportPoints))
        {
            var tolerance = Math.Max(2.0, 0.1 * candidate.SemiMinor);
            var duplicate = deduped.Any(existing =>
                Math.Abs(existing.CenterX - candidate.CenterX) <= tolerance &&
                Math.Abs(existing.CenterY - candidate.CenterY) <= tolerance &&
                Math.Abs(existing.SemiMajor - candidate.SemiMajor) <= 0.2 * candidate.SemiMajor &&
                Math.Abs(existing.SemiMinor - candidate.SemiMinor) <= 0.2 * candidate.SemiMinor);
            if (!duplicate) deduped.Add(candidate);
        }

        var ordered = deduped.Take(maxEllipses).ToArray();

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
            Type, ("count", ordered.Length), ("contours", contours.Length), ("candidates", candidates.Count),
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
                ["count"] = ordered.Length, ["contours"] = contours.Length, ["candidates"] = candidates.Count,
                ["minArea"] = minArea, ["minCoverageDeg"] = minCoverage, ["maxRmsError"] = maxRms,
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }, overlays));
    }

    private static double NormalizeDeg(double degrees)
    {
        var value = degrees % 180.0;
        if (value < 0) value += 180.0;
        return value;
    }

    private sealed record EllipseCandidate(
        double CenterX, double CenterY, double SemiMajor, double SemiMinor, double AngleDeg,
        double Rms, double Coverage, int SupportPoints, double Area, double Confidence);
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
                    extractWidth ? "true" : "false", lineModel, completeJunctions ? "true" : "false");
                raw.AddRange(JlContourCodec.FromNative(curves, cancellationToken));
            }
            finally { curves?.Dispose(); }
        }
        cancellationToken.ThrowIfCancellationRequested();

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

        var source = context.RequireImage("image");
        var roi = node.GetRoi();
        using var analysis = FeatureDetectorHelpers.Prepare(source, roi, Type);
        if (wavelength > Math.Min(analysis.Width, analysis.Height) / 2.0)
            throw new InvalidOperationException(
                "feature.gaborBank wavelength must not exceed half the image; lower it or crop the ROI.");

        var sigma = sigmaRatio * wavelength;
        var kernelSize = (int)Math.Round(6 * sigma) | 1;
        if (kernelSize < 3) kernelSize = 3;

        using var gray32 = new Mat();
        analysis.ConvertTo(gray32, MatType.CV_32F);
        using var accumulated = new Mat(analysis.Rows, analysis.Cols, MatType.CV_32FC1, Scalar.All(0));
        using var best = new Mat(analysis.Rows, analysis.Cols, MatType.CV_32FC1, Scalar.All(0));
        using var bestDirection = new Mat(analysis.Rows, analysis.Cols, MatType.CV_32FC1, Scalar.All(0));
        var perOrientation = new double[orientations];
        var degreesStep = 180.0 / orientations;

        for (var index = 0; index < orientations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var degrees = degreesStep * index;
            using var kernel = Cv2.GetGaborKernel(
                new Size(kernelSize, kernelSize), sigma, degrees * Math.PI / 180.0, wavelength, gamma, 0, MatType.CV_32F);
            using var response = new Mat();
            Cv2.Filter2D(gray32, response, MatType.CV_32F, kernel);
            using var squared = new Mat();
            Cv2.Multiply(response, response, squared);
            perOrientation[index] = Cv2.Mean(squared).Val0;

            if (energyMode == "sum")
            {
                Cv2.Add(accumulated, squared, accumulated);
            }
            else
            {
                // max 模式同时记录该像素最强响应来自哪个方向，用于方向图。
                for (var y = 0; y < squared.Rows; y++)
                for (var x = 0; x < squared.Cols; x++)
                {
                    var value = squared.At<float>(y, x);
                    if (value <= best.At<float>(y, x)) continue;
                    best.Set(y, x, value);
                    bestDirection.Set(y, x, (float)degrees);
                }
            }
        }

        using var combined = energyMode == "sum" ? accumulated.Clone() : best.Clone();
        Cv2.MinMaxLoc(combined, out var minEnergy, out var maxEnergy, out _, out _);
        var normalized = new Mat();
        if (maxEnergy > 1e-9)
        {
            combined.ConvertTo(normalized, MatType.CV_8UC1, 255.0 / maxEnergy, 0);
        }
        else
        {
            combined.ConvertTo(normalized, MatType.CV_8UC1);
        }

        var total = perOrientation.Sum();
        var dominantDegrees = 0d;
        if (total > 1e-12)
        {
            var bestIndex = 0;
            for (var index = 1; index < orientations; index++)
                if (perOrientation[index] > perOrientation[bestIndex]) bestIndex = index;
            dominantDegrees = degreesStep * bestIndex;
        }
        var minimumOrientation = perOrientation.Length > 0 ? Math.Max(perOrientation.Min(), 1e-12) : 1e-12;
        var maximumOrientation = perOrientation.Length > 0 ? perOrientation.Max() : 0;
        var anisotropy = minimumOrientation > 0 ? maximumOrientation / minimumOrientation : 0;

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["image"] = VisionValue.Image(VisionImage.Own(normalized)),
            ["energy"] = VisionValue.String(JsonSerializer.Serialize(perOrientation.Select((value, index) => new
            {
                angleDeg = Math.Round(degreesStep * index, 2),
                energy = Math.Round(value, 6)
            }))),
            ["dominantDirectionDeg"] = VisionValue.Double(dominantDegrees),
            ["anisotropy"] = VisionValue.Double(anisotropy),
            ["meanEnergy"] = VisionValue.Double(Cv2.Mean(combined).Val0),
            ["maxEnergy"] = VisionValue.Double(maxEnergy),
            ["count"] = VisionValue.Integer(orientations),
            ["found"] = VisionValue.Boolean(maxEnergy > 1e-9),
            ["diagnostics"] = VisionValue.String(string.Empty)
        };

        if (emitDirectionMap)
        {
            using var directionMap = new Mat();
            if (energyMode == "sum")
            {
                // sum 模式下没有天然的方向图，用单一波长的最大响应方向近似不了，明确输出空图。
                directionMap.Create(analysis.Rows, analysis.Cols, MatType.CV_8UC1);
                directionMap.SetTo(Scalar.All(0));
            }
            else
            {
                bestDirection.ConvertTo(directionMap, MatType.CV_8UC1, 255.0 / 180.0, 0);
            }
            outputs["directionMap"] = VisionValue.Image(VisionImage.Own(directionMap));
        }

        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("wavelength", wavelength), ("orientations", orientations), ("sigma", sigma),
            ("kernelSize", kernelSize), ("gamma", gamma), ("energyMode", energyMode),
            ("dominantDirectionDeg", dominantDegrees), ("anisotropy", anisotropy),
            ("meanEnergy", Cv2.Mean(combined).Val0), ("maxEnergy", maxEnergy),
            ("roi", roi?.Type.ToString() ?? "FullImage"),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        outputs["diagnostics"] = VisionValue.String(diagnostics);

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wavelength"] = wavelength, ["orientations"] = orientations, ["sigma"] = sigma,
                ["kernelSize"] = kernelSize, ["gamma"] = gamma, ["energyMode"] = energyMode,
                ["dominantDirectionDeg"] = dominantDegrees, ["anisotropy"] = Math.Round(anisotropy, 4),
                ["meanEnergy"] = Cv2.Mean(combined).Val0, ["maxEnergy"] = maxEnergy,
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }
}
