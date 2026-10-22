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

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var mode = node.GetString("mode", "manual").Trim().ToLowerInvariant();
        if (mode == "adaptive")
        {
            var adaptive = await new AdaptiveThresholdNode().ExecuteAsync(context, node, cancellationToken).ConfigureAwait(false);
            return EnrichThresholdResult(adaptive, "adaptive", node.GetBool("invert", false), cancellationToken);
        }
        if (mode == "otsu")
        {
            var otsu = await new OtsuThresholdNode().ExecuteAsync(context, node, cancellationToken).ConfigureAwait(false);
            return EnrichThresholdResult(otsu, "otsu", node.GetBool("invert", false), cancellationToken);
        }
        if (mode != "manual") throw new InvalidOperationException($"Unsupported threshold mode '{mode}'.");

        var sourceImage = context.RequireImage("image");
        var source = sourceImage.Mat;
        if (source.Empty()) throw new InvalidOperationException("image.threshold requires a non-empty image.");
        var threshold = node.GetDouble("threshold", 128);
        if (!double.IsFinite(threshold) || threshold is < 0 or > 255)
            throw new InvalidOperationException("Manual threshold must be finite in 0..255.");
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();

        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray);
        Mat? output = null;
        try
        {
            var thresholded = ImageTransformOutput.Create(target =>
                JLVisionOperators.Threshold(gray, target, threshold, 255, invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary));
            output = PreprocessImageHelpers.ApplyRoiMask(thresholded, roi, cancellationToken);
            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(output)),
                ["threshold"] = VisionValue.Double(threshold)
            };
            var summary = new Dictionary<string, object?>
            {
                ["mode"] = "manual",
                ["threshold"] = threshold,
                ["invert"] = invert,
                ["autoGray"] = converted,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            };
            DefectNodeHelpers.AttachByteStats(outputs, summary, output, Type, ("mode", "manual"), ("threshold", threshold), ("invert", invert), ("autoGray", converted));
            outputs["nonZeroCount"] = VisionValue.Integer(Convert.ToInt64(summary["nonZeroCount"]!));
            cancellationToken.ThrowIfCancellationRequested();
            return new NodeExecutorResult(outputs, summary);
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

public sealed class LargestBlobNode : IVisionNodeExecutor
{
    public string Type => "measure.blob";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var sourceImage = context.RequireImage("image");
        var source = sourceImage.Mat;
        if (source.Empty()) throw new InvalidOperationException("measure.blob requires a non-empty image.");
        var minArea = node.GetDouble("minArea", 1000);
        var roi = node.GetRoi();

        Mat? masked = null;
        try
        {
            var analysis = source;
            if (roi is not null)
            {
                using var mask = roi.BuildMask(source.Size());
                masked = new Mat(source.Rows, source.Cols, source.Type(), Scalar.All(0));
                source.CopyTo(masked, mask);
                analysis = masked;
            }

            cancellationToken.ThrowIfCancellationRequested();
            JLVisionOperators.FindContours(analysis, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            cancellationToken.ThrowIfCancellationRequested();
            var best = contours
                .Select(c => (Contour: c, Area: JLVisionOperators.ContourArea(c)))
                .Where(x => x.Area >= minArea)
                .OrderByDescending(x => x.Area)
                .FirstOrDefault();

            var blobs = contours
                .Select((c, contourIndex) =>
                {
                    if ((contourIndex & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var area = JLVisionOperators.ContourArea(c);
                    var perimeter = 0d;
                    for (var i = 0; i < c.Length; i++)
                    {
                        if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var a = c[i]; var b = c[(i + 1) % c.Length];
                        perimeter += Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                    }
                    return new
                    {
                        contour = c, area, perimeter,
                        circularity = perimeter > 0 ? Math.Min(1, 4 * Math.PI * area / (perimeter * perimeter)) : 0,
                        x = c.Length == 0 ? 0 : c.Min(q => q.X), y = c.Length == 0 ? 0 : c.Min(q => q.Y),
                        width = c.Length == 0 ? 0 : c.Max(q => q.X) - c.Min(q => q.X) + 1,
                        height = c.Length == 0 ? 0 : c.Max(q => q.Y) - c.Min(q => q.Y) + 1
                    };
                })
                .Where(x => x.area >= minArea)
                .OrderByDescending(x => x.area)
                .ToArray();
            if (best.Contour is null)
            {
                // Mature blob tools report "no object" as a result, not a workflow failure.
                var emptyDiag = CoordinateValidation.Diagnostics(Type, ("found", false), ("count", 0), ("minArea", minArea));
                return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage),
                    ["found"] = VisionValue.Boolean(false),
                    ["count"] = VisionValue.Integer(0),
                    ["blobs"] = VisionValue.String("[]"),
                    ["diagnostics"] = VisionValue.String(emptyDiag)
                }, new Dictionary<string, object?> { ["found"] = false, ["count"] = 0, ["minArea"] = minArea, ["diagnostics"] = emptyDiag }));
            }

            JLVisionOperators.MinEnclosingCircle(best.Contour, out Point2f center, out float radius);
            var typedCenter = new VisionPoint2D(center.X, center.Y);
            var typedCircle = new VisionCircle(center.X, center.Y, radius);

            var stride = Math.Max(1, (int)Math.Ceiling(best.Contour.Length / 160d));
            var contourPoints = best.Contour
                .Where((_, i) => i % stride == 0)
                .Select(p => new VisionOverlayPoint(p.X, p.Y))
                .ToArray();

            var overlays = new List<VisionOverlay>
            {
                new("blob-contour", VisionOverlayType.Contour, Points: contourPoints, Stroke: "#faad14", StrokeWidth: 1.5),
                new("blob-circle", VisionOverlayType.Circle, X: center.X, Y: center.Y, Radius: radius, Stroke: "#52c41a", StrokeWidth: 2),
                new("blob-center", VisionOverlayType.Point, X: center.X, Y: center.Y, Stroke: "#ff4d4f", StrokeWidth: 2),
                new("blob-label", VisionOverlayType.Text, Label: $"X={center.X:0.0} Y={center.Y:0.0} A={best.Area:0}", X: 12, Y: 22, Stroke: "#ffd666", StrokeWidth: 1)
            };

            var diag = CoordinateValidation.Diagnostics(Type,
                ("found", true), ("count", blobs.Length), ("minArea", minArea),
                ("x", Math.Round(center.X, 2)), ("y", Math.Round(center.Y, 2)),
                ("area", Math.Round(best.Area, 2)), ("radius", Math.Round(radius, 2)),
                ("circularity", Math.Round(blobs[0].circularity, 4)));
            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage),
                ["x"] = VisionValue.Double(center.X),
                ["y"] = VisionValue.Double(center.Y),
                ["area"] = VisionValue.Double(best.Area),
                ["radius"] = VisionValue.Double(radius),
                ["center"] = VisionValue.Point(typedCenter),
                ["circle"] = VisionValue.Circle(typedCircle),
                ["found"] = VisionValue.Boolean(true),
                ["count"] = VisionValue.Integer(blobs.Length),
                ["circularity"] = VisionValue.Double(blobs[0].circularity),
                ["width"] = VisionValue.Double(blobs[0].width),
                ["height"] = VisionValue.Double(blobs[0].height),
                ["blobs"] = VisionValue.String(System.Text.Json.JsonSerializer.Serialize(blobs.Select(b => new { area = b.area, perimeter = b.perimeter, circularity = b.circularity, x = b.x, y = b.y, width = b.width, height = b.height }))),
                ["diagnostics"] = VisionValue.String(diag)
            };
            var summary = new Dictionary<string, object?>
            {
                ["x"] = Math.Round(center.X, 2),
                ["y"] = Math.Round(center.Y, 2),
                ["area"] = Math.Round(best.Area, 2),
                ["radius"] = Math.Round(radius, 2),
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["centerType"] = VisionDataType.Point2D.ToString(),
                ["circleType"] = VisionDataType.Circle.ToString(),
                ["diagnostics"] = diag
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
        }
        finally
        {
            masked?.Dispose();
        }
    }
}
