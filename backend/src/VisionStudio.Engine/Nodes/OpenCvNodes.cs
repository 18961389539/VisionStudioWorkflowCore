using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class SyntheticImageNode : IVisionNodeExecutor
{
    private const int PatternRectangleWidth = 115;
    private const int PatternRectangleHeight = 70;
    private const int PatternCircleRadius = 80;
    private const double RectangleOffsetX = -250;
    private const double RectangleOffsetY = -175;
    private const double LineStartOffsetX = -280;
    private const double LineStartOffsetY = 180;
    private const double LineEndOffsetX = 280;
    private const double LineEndOffsetY = 145;

    public string Type => "image.synthetic";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var width = node.GetInt("width", 1280);
        var height = node.GetInt("height", 960);
        var centerX = node.GetInt("centerX", width / 2);
        var centerY = node.GetInt("centerY", height / 2);
        var radius = node.GetInt("radius", PatternCircleRadius);
        var placementMode = node.GetString("placementMode", "fixed");
        var sampleIndex = node.GetInt("sampleIndex", 0);
        var offsetStepX = node.GetDouble("offsetStepX", 20);
        var offsetStepY = node.GetDouble("offsetStepY", 0);
        var angleStepDeg = node.GetDouble("angleStepDeg", 5);
        var randomSeed = node.GetInt("randomSeed", 1);
        var maxOffsetX = node.GetDouble("maxOffsetX", 160);
        var maxOffsetY = node.GetDouble("maxOffsetY", 100);
        var maxAngleDeg = node.GetDouble("maxAngleDeg", 15);

        if (sampleIndex is < 0 or > 10_000)
            throw new InvalidOperationException("Sample Index must be between 0 and 10000.");
        if (new[] { offsetStepX, offsetStepY, angleStepDeg, maxOffsetX, maxOffsetY, maxAngleDeg }.Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("Synthetic image placement parameters must be finite numbers.");
        if (maxOffsetX < 0 || maxOffsetY < 0 || maxAngleDeg < 0 || maxAngleDeg > 180)
            throw new InvalidOperationException("Random shift ranges must be non-negative and maximum angle must be between 0 and 180 degrees.");

        var offsetX = 0d;
        var offsetY = 0d;
        var angleDeg = 0d;
        switch (placementMode)
        {
            case "fixed":
                break;
            case "sequence":
                offsetX = sampleIndex * offsetStepX;
                offsetY = sampleIndex * offsetStepY;
                angleDeg = sampleIndex * angleStepDeg;
                break;
            case "seededRandom":
            {
                var state = unchecked((uint)randomSeed) ^ unchecked((uint)sampleIndex * 0x9E3779B9u) ^ 0xA341316Cu;
                if (state == 0) state = 0x6D2B79F5u;
                offsetX = (NextUnit(ref state) * 2 - 1) * maxOffsetX;
                offsetY = (NextUnit(ref state) * 2 - 1) * maxOffsetY;
                angleDeg = (NextUnit(ref state) * 2 - 1) * maxAngleDeg;
                break;
            }
            case "randomEachRun":
            {
                var random = Random.Shared;
                var placementFound = false;
                for (var attempt = 0; attempt < 2048; attempt++)
                {
                    offsetX = (random.NextDouble() * 2 - 1) * maxOffsetX;
                    offsetY = (random.NextDouble() * 2 - 1) * maxOffsetY;
                    angleDeg = (random.NextDouble() * 2 - 1) * maxAngleDeg;
                    if (!FitsImage(width, height, centerX, centerY, radius, offsetX, offsetY, angleDeg))
                        continue;

                    placementFound = true;
                    break;
                }

                if (!placementFound)
                    throw new InvalidOperationException($"Could not place a random pattern inside the {width}×{height} image. Reduce the random shift/angle ranges or enlarge the image.");
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported synthetic image placement mode '{placementMode}'.");
        }

        var anchorX = centerX + offsetX;
        var anchorY = centerY + offsetY;
        var radians = angleDeg * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        (double X, double Y) Transform(double x, double y) =>
            (anchorX + cos * x + sin * y, anchorY - sin * x + cos * y);
        var rectangle = new[]
        {
            Transform(RectangleOffsetX, RectangleOffsetY),
            Transform(RectangleOffsetX + PatternRectangleWidth - 1, RectangleOffsetY),
            Transform(RectangleOffsetX + PatternRectangleWidth - 1, RectangleOffsetY + PatternRectangleHeight - 1),
            Transform(RectangleOffsetX, RectangleOffsetY + PatternRectangleHeight - 1)
        };
        var lineStart = Transform(LineStartOffsetX, LineStartOffsetY);
        var lineEnd = Transform(LineEndOffsetX, LineEndOffsetY);

        var requestedOffsetX = offsetX;
        var requestedOffsetY = offsetY;
        var placementAdjusted = false;
        if (placementMode != "fixed")
        {
            if (!FitsImage(width, height, centerX, centerY, radius, offsetX, offsetY, angleDeg))
            {
                if (!TryFitWithinImage(width, height, centerX, centerY, radius, angleDeg, ref offsetX, ref offsetY))
                    throw new InvalidOperationException($"The rotated synthetic pattern cannot fit inside the {width}×{height} image. Reduce its angle or enlarge the image.");

                placementAdjusted = true;
                anchorX = centerX + offsetX;
                anchorY = centerY + offsetY;
                radians = angleDeg * Math.PI / 180d;
                cos = Math.Cos(radians);
                sin = Math.Sin(radians);
                (double X, double Y) AdjustedTransform(double x, double y) =>
                    (anchorX + cos * x + sin * y, anchorY - sin * x + cos * y);
                for (var i = 0; i < rectangle.Length; i++)
                {
                    var localX = i is 1 or 2 ? RectangleOffsetX + PatternRectangleWidth - 1 : RectangleOffsetX;
                    var localY = i is 2 or 3 ? RectangleOffsetY + PatternRectangleHeight - 1 : RectangleOffsetY;
                    rectangle[i] = AdjustedTransform(localX, localY);
                }
                lineStart = AdjustedTransform(LineStartOffsetX, LineStartOffsetY);
                lineEnd = AdjustedTransform(LineEndOffsetX, LineEndOffsetY);
            }
        }

        var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(28));
        Cv2.Circle(mat, new Point((int)Math.Round(anchorX), (int)Math.Round(anchorY)), radius, Scalar.All(225), -1, LineTypes.AntiAlias);
        if (Math.Abs(angleDeg % 360d) < 1e-9)
            Cv2.Rectangle(mat, new Rect((int)Math.Round(anchorX + RectangleOffsetX), (int)Math.Round(anchorY + RectangleOffsetY), PatternRectangleWidth, PatternRectangleHeight), Scalar.All(95), -1);
        else
            Cv2.FillConvexPoly(mat, rectangle.Select(ToPixel).ToArray(), Scalar.All(95), LineTypes.AntiAlias);
        Cv2.Line(mat, ToPixel(lineStart), ToPixel(lineEnd), Scalar.All(125), 5, LineTypes.AntiAlias);
        var image = VisionImage.Own(mat);

        var overlays = new List<VisionOverlay>();
        if (Math.Abs(angleDeg % 360d) < 1e-9)
            overlays.Add(new("synthetic-rect", VisionOverlayType.Rectangle,
                X: anchorX + RectangleOffsetX, Y: anchorY + RectangleOffsetY,
                Width: PatternRectangleWidth, Height: PatternRectangleHeight, Stroke: "#40a9ff", StrokeWidth: 1.5));
        else
            overlays.Add(new("synthetic-rect", VisionOverlayType.Polygon,
                Points: rectangle.Select(point => new VisionOverlayPoint(point.X, point.Y)).ToArray(), Stroke: "#40a9ff", StrokeWidth: 1.5));
        overlays.Add(new("synthetic-line", VisionOverlayType.Line,
            X: lineStart.X, Y: lineStart.Y, X2: lineEnd.X, Y2: lineEnd.Y, Stroke: "#9254de", StrokeWidth: 1.5));

        var synthMat = image.Mat;
        JLVisionOperators.MinMax(synthMat, out double synthMin, out double synthMax);
        var synthMean = JLVisionOperators.Mean(synthMat);
        var synthDiag = CoordinateValidation.Diagnostics(Type, ("width", width), ("height", height), ("channels", synthMat.Channels()),
            ("placementMode", placementMode), ("sampleIndex", sampleIndex), ("meanIntensity", synthMean), ("minIntensity", synthMin), ("maxIntensity", synthMax));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image), ["width"] = VisionValue.Integer(width), ["height"] = VisionValue.Integer(height), ["channels"] = VisionValue.Integer(synthMat.Channels()), ["meanIntensity"] = VisionValue.Double(synthMean), ["minIntensity"] = VisionValue.Double(synthMin), ["maxIntensity"] = VisionValue.Double(synthMax), ["diagnostics"] = VisionValue.String(synthDiag) },
            new Dictionary<string, object?>
            {
                ["size"] = $"{width}x{height}",
                ["circle"] = $"({anchorX:0.##},{anchorY:0.##}) r={radius}",
                ["placementMode"] = placementMode,
                ["sampleIndex"] = sampleIndex,
                ["offsetX"] = Math.Round(offsetX, 3),
                ["offsetY"] = Math.Round(offsetY, 3),
                ["angleDeg"] = Math.Round(angleDeg, 3),
                ["placementAdjusted"] = placementAdjusted,
                ["requestedOffsetX"] = Math.Round(requestedOffsetX, 3),
                ["requestedOffsetY"] = Math.Round(requestedOffsetY, 3)
            },
            overlays));
    }

    private static Point ToPixel((double X, double Y) point)
        => new((int)Math.Round(point.X), (int)Math.Round(point.Y));

    private static bool FitsImage(int width, int height, int centerX, int centerY, int radius,
        double offsetX, double offsetY, double angleDeg)
    {
        const double margin = 3;
        var bounds = GetPatternBounds(centerX, centerY, radius, offsetX, offsetY, angleDeg);
        var maxX = width - 1 - margin;
        var maxY = height - 1 - margin;
        return bounds.MinX >= margin && bounds.MinY >= margin
            && bounds.MaxX <= maxX && bounds.MaxY <= maxY;
    }

    private static bool TryFitWithinImage(int width, int height, int centerX, int centerY, int radius,
        double angleDeg, ref double offsetX, ref double offsetY)
    {
        const double margin = 3;
        const double roundingSafety = 1e-6;
        var maxX = width - 1 - margin;
        var maxY = height - 1 - margin;
        var bounds = GetPatternBounds(centerX, centerY, radius, offsetX, offsetY, angleDeg);
        if (!double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MinY)
            || !double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MaxY)
            || bounds.MaxX - bounds.MinX > maxX - margin
            || bounds.MaxY - bounds.MinY > maxY - margin)
            return false;

        var shiftX = bounds.MinX < margin ? margin - bounds.MinX + roundingSafety
            : bounds.MaxX > maxX ? maxX - bounds.MaxX - roundingSafety : 0;
        var shiftY = bounds.MinY < margin ? margin - bounds.MinY + roundingSafety
            : bounds.MaxY > maxY ? maxY - bounds.MaxY - roundingSafety : 0;
        offsetX += shiftX;
        offsetY += shiftY;
        return FitsImage(width, height, centerX, centerY, radius, offsetX, offsetY, angleDeg);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) GetPatternBounds(
        int centerX, int centerY, int radius, double offsetX, double offsetY, double angleDeg)
    {
        var anchorX = centerX + offsetX;
        var anchorY = centerY + offsetY;
        var radians = angleDeg * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        (double X, double Y) Transform(double x, double y) =>
            (anchorX + cos * x + sin * y, anchorY - sin * x + cos * y);

        var points = new[]
        {
            Transform(RectangleOffsetX, RectangleOffsetY),
            Transform(RectangleOffsetX + PatternRectangleWidth - 1, RectangleOffsetY),
            Transform(RectangleOffsetX + PatternRectangleWidth - 1, RectangleOffsetY + PatternRectangleHeight - 1),
            Transform(RectangleOffsetX, RectangleOffsetY + PatternRectangleHeight - 1),
            Transform(LineStartOffsetX, LineStartOffsetY),
            Transform(LineEndOffsetX, LineEndOffsetY)
        };
        return (
            Math.Min(anchorX - radius, points.Min(point => point.X)),
            Math.Min(anchorY - radius, points.Min(point => point.Y)),
            Math.Max(anchorX + radius, points.Max(point => point.X)),
            Math.Max(anchorY + radius, points.Max(point => point.Y)));
    }

    private static double NextUnit(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0x00FFFFFFu) / 16777216d;
    }
}

public sealed class ThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.threshold";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var mode = node.GetString("mode", "manual").Trim().ToLowerInvariant();
        if (mode != "manual" && context.TryGetValue("thresholdValue", out _))
            throw new InvalidOperationException("image.threshold thresholdValue is only used by mode=manual; adaptive/Otsu derive the threshold themselves. Disconnect it or switch the mode.");
        if (mode == "adaptive")
        {
            // 自适应与 Otsu 已合并为阈值模式的取值：直接调用内部实现，输出由本节点统一补齐。
            var adaptive = AdaptiveThresholdCore.Execute(context, node, Type, cancellationToken);
            return ValueTask.FromResult(EnrichThresholdResult(adaptive, "adaptive", node.GetBool("invert", false), cancellationToken));
        }
        if (mode == "otsu")
        {
            var otsu = OtsuThresholdCore.Execute(context, node, Type, cancellationToken);
            return ValueTask.FromResult(EnrichThresholdResult(otsu, "otsu", node.GetBool("invert", false), cancellationToken));
        }
        if (mode != "manual") throw new InvalidOperationException($"Unsupported threshold mode '{mode}'.");

        var sourceImage = context.RequireImage("image");
        var source = sourceImage.Mat;
        if (source.Empty()) throw new InvalidOperationException("image.threshold requires a non-empty image.");
        // 端口优先：上游统计（良品均值±3σ 等）可以逐批驱动阈值，参数只作默认值。
        var threshold = context.TryGetValue("thresholdValue", out _) ? context.RequireNumber("thresholdValue") : node.GetDouble("threshold", 128);
        if (!double.IsFinite(threshold) || threshold is < 0 or > 255)
            throw new InvalidOperationException("Manual threshold must be finite in 0..255.");
        var maxValue = node.GetDouble("maxValue", 255);
        if (!double.IsFinite(maxValue) || maxValue is < 1 or > 255)
            throw new InvalidOperationException("image.threshold maxValue (foreground value) must be finite in 1..255.");
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();
        var inputBitDepth = PreprocessImageHelpers.InputBitDepth(node, Type);

        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray, inputBitDepth);
        Mat? output = null;
        try
        {
            // manual 的阈值是常量，不需要统计域；这里只取 ROI 掩膜与 ROI 像素数，让占比分母与 adaptive/Otsu 同口径。
            using var domain = ThresholdStatisticsDomain.Create(gray, roi, 0, crop: false, cancellationToken);
            var thresholded = ImageTransformOutput.Create(target =>
                JLVisionOperators.Threshold(gray, target, threshold, maxValue, invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary));
            output = PreprocessImageHelpers.ApplyMask(thresholded, domain.Mask, cancellationToken);
            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(output)),
                ["threshold"] = VisionValue.Double(threshold)
            };
            var summary = new Dictionary<string, object?>
            {
                ["mode"] = "manual",
                ["threshold"] = threshold,
                ["maxValue"] = maxValue,
                ["invert"] = invert,
                ["autoGray"] = converted,
                ["inputBitDepth"] = inputBitDepth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto",
                ["byteScale"] = source.Depth() == MatType.CV_16U ? PreprocessImageHelpers.ByteScale(inputBitDepth) : 1d,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            };
            DefectNodeHelpers.AttachByteStats(outputs, summary, output, Type, ("mode", "manual"), ("threshold", threshold), ("invert", invert), ("autoGray", converted));
            outputs["nonZeroCount"] = VisionValue.Integer(Convert.ToInt64(summary["nonZeroCount"]!));
            // 分母必须是 ROI 像素数：ROI 外已清零但仍占面积，用整图面积会让 ROI 越小、占比越接近 0。
            // 分子是 long，必须显式转 double，否则 10000/20000 走整除得 0。
            var fgRatio = domain.PixelCount > 0 ? (double)Convert.ToInt64(summary["nonZeroCount"]!) / domain.PixelCount : 0d;
            outputs["foregroundRatio"] = VisionValue.Double(fgRatio);
            summary["foregroundRatio"] = Math.Round(fgRatio, 6);
            summary["domainPixels"] = domain.PixelCount;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        catch
        {
            output?.Dispose();
            throw;
        }
        finally
        {
            ownedGray?.Dispose();
        }
    }

    private static NodeExecutorResult EnrichThresholdResult(NodeExecutorResult inner, string mode, bool invert, CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, VisionValue>(inner.Outputs);
        var summary = new Dictionary<string, object?>(inner.Summary) { ["mode"] = mode, ["invert"] = invert };
        if (!outputs.TryGetValue("image", out var imageValue))
            return inner with { Summary = summary };
        var owned = imageValue.Require<VisionImage>("image");
        try
        {
            DefectNodeHelpers.AttachByteStats(outputs, summary, owned.Mat, "image.threshold", ("mode", mode), ("invert", invert));
            outputs["nonZeroCount"] = VisionValue.Integer(Convert.ToInt64(summary["nonZeroCount"]!));
            // foregroundRatio 只由 adaptive/Otsu 核心在统计域内产出（分母是 ROI 像素数）。这里不再兜底，
            // 否则会多出一个"分母是整图面积"的第二真值来源，ROI 越小占比越偏。
            if (!outputs.ContainsKey("threshold") && summary.TryGetValue("threshold", out var thr) && thr is double d)
                outputs["threshold"] = VisionValue.Double(d);
            cancellationToken.ThrowIfCancellationRequested();
            return inner with { Outputs = outputs, Summary = summary };
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }
}
