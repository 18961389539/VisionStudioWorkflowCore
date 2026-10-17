using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

internal static class PixelAffine
{
    public static VisionTransform2D Read(NodeExecutionContext context)
    {
        var t = context.Require<VisionTransform2D>("transform");
        var determinant = t.H00 * t.H11 - t.H01 * t.H10;
        if (t.SourceUnit != "px" || t.TargetUnit != "px" || t.Matrix.Any(v => !double.IsFinite(v)) ||
            Math.Abs(t.H20) > 1e-12 || Math.Abs(t.H21) > 1e-12 || Math.Abs(t.H22 - 1) > 1e-12 ||
            !double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
            throw new InvalidOperationException("A finite nonsingular affine transform from px to px is required.");
        return t;
    }

    public static (int Width, int Height) Size(NodeDefinition node, int width, int height)
    {
        var w = node.GetInt("width", 0); var h = node.GetInt("height", 0);
        if (w == 0) w = width;
        if (h == 0) h = height;
        if (w is < 1 or > 16384 || h is < 1 or > 16384 || (long)w * h > 64_000_000)
            throw new InvalidOperationException("Output size must be 1..16384 per axis and at most 64000000 pixels.");
        return (w, h);
    }

    public static JlHomMat2D Native(VisionTransform2D t)
    {
        // Public matrices use x/y pixel centres. JLVision uses row/column pixel corners.
        using var values = new JlTuple(new[] { t.H11, t.H10, t.H12 + .5 * (1 - t.H11 - t.H10),
            t.H01, t.H00, t.H02 + .5 * (1 - t.H01 - t.H00) });
        return new(values);
    }

    public static VisionRegion Transform(VisionRegion source, VisionTransform2D t, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Components.Count == 0) return new(width, height, []);
        using var native = JLVisionRegions.ToNative(source);
        var matrix = Native(t);
        using var matrixData = matrix.RawData;
        using var transformed = native.AffineTransRegion(matrix, "nearest_neighbor");
        using var bounds = new JlRegion(0d, 0d, (double)height - 1, (double)width - 1);
        using var clipped = transformed.Intersection(bounds);
        return JLVisionRegions.FromNative(clipped, width, height, token);
    }
}

public sealed class ImageRegisterNode : IVisionNodeExecutor
{
    public string Type => "image.register";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image"); var reference = context.RequireImage("reference");
        var x = node.GetInt("x", 0); var y = node.GetInt("y", 0);
        var width = node.GetInt("width", 0); var height = node.GetInt("height", 0);
        if (width == 0) width = reference.Width - x;
        if (height == 0) height = reference.Height - y;
        var range = node.GetDouble("angleRangeDeg", 15); var step = node.GetDouble("angleStepDeg", 1); var score = node.GetDouble("minScore", .7);
        if (x < 0 || y < 0 || width < 8 || height < 8 || (long)x + width > reference.Width || (long)y + height > reference.Height || width > source.Width || height > source.Height)
            throw new InvalidOperationException("Reference patch must be at least 8x8, inside the reference, and fit in the scene.");
        if (!double.IsFinite(range) || range is < 0 or > 180 || !double.IsFinite(step) || step is < .1 or > 30 || !double.IsFinite(score) || score is <= 0 or > 1)
            throw new InvalidOperationException("Invalid registration angle range, step or minimum score.");
        using var referenceNative = JLVisionImages.FromMat(reference.Mat);
        using var patchNative = referenceNative.CropRectangle1(y, x, y + height - 1, x + width - 1);
        using var patch = VisionImage.Own(JLVisionImages.ToMat(patchNative));
        using (var native = JLVisionImages.FromMat(patch.Mat))
        using (var channels = native.CountChannels())
        using (var gray = channels.I == 1 ? native.CopyImage() : native.Rgb1ToGray())
        using (var domain = gray.GetDomain())
        {
            _ = gray.Intensity(domain, out double deviation);
            if (deviation < 1e-6) return ValueTask.FromResult(Missing("reference-patch-has-no-texture"));
        }
        var parameters = new Dictionary<string, JsonElement>
        {
            ["angleRangeDeg"] = JsonSerializer.SerializeToElement(range), ["angleStepDeg"] = JsonSerializer.SerializeToElement(step),
            ["minScore"] = JsonSerializer.SerializeToElement(score)
        };
        var match = JLVisionMatching.Execute(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source), ["template"] = VisionValue.Image(patch) }),
            new NodeDefinition(node.Id, "match.template", null, null, parameters), false, cancellationToken);
        if (!match.Outputs["found"].Require<bool>("found")) return ValueTask.FromResult(Missing("no-match"));
        // Matching returns the axis-aligned bounds. Undo the rotated patch bounds to recover its true centre.
        var angle = match.Outputs["angle"].Require<double>("angle") * Math.PI / 180;
        var c = Math.Cos(angle); var s = Math.Sin(angle);
        var halfX = (width - 1) / 2d; var halfY = (height - 1) / 2d;
        var centerX = match.Outputs["x"].Require<double>("x") + Math.Abs(c) * halfX + Math.Abs(s) * halfY;
        var centerY = match.Outputs["y"].Require<double>("y") + Math.Abs(s) * halfX + Math.Abs(c) * halfY;
        // Optional subpixel translation refinement: for near-axis-aligned matches the
        // correlation peak around the integer match is parabolically interpolated.
        // Rotated matches keep the integer result (their axis-aligned bounds are not a
        // valid correlation window), so refinement never silently degrades them.
        var refineOffsetX = 0d; var refineOffsetY = 0d; var refined = false;
        if (node.GetBool("subpixelRefine", false) && Math.Abs(match.Outputs["angle"].Require<double>("angle")) <= 5)
        {
            using var sceneGray = RecognitionImageHelpers.ToGray8(source.Mat);
            using var patchGray = RecognitionImageHelpers.ToGray8(patch.Mat);
            sceneGray.GetArray(out byte[] sceneBytes); patchGray.GetArray(out byte[] patchBytes);
            var originX = (int)Math.Round(match.Outputs["x"].Require<double>("x"));
            var originY = (int)Math.Round(match.Outputs["y"].Require<double>("y"));
            var refinement = RegistrationRefinement.Refine(sceneBytes, source.Width, patchBytes, width, height, originX, originY);
            if (refinement.PeakScore > 0)
            {
                refineOffsetX = refinement.OffsetX; refineOffsetY = refinement.OffsetY; refined = true;
                centerX += refineOffsetX; centerY += refineOffsetY;
            }
        }
        // Inverse of the image-coordinate counterclockwise pose returned by NCC.
        var transform = new VisionTransform2D("CurrentImage", "ReferenceImage", "px", c, -s, x + halfX - c * centerX + s * centerY,
            s, c, y + halfY - s * centerX - c * centerY, 0, 0, 1);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["score"] = match.Outputs["score"], ["transform"] = VisionValue.Transform(transform),
            ["angle"] = VisionValue.Double(match.Outputs["angle"].Require<double>("angle")),
            ["center"] = VisionValue.Point(new VisionPoint2D(centerX, centerY))
        }, new Dictionary<string, object?> { ["found"] = true, ["score"] = match.Outputs["score"].Value, ["transform"] = transform,
            ["angleDeg"] = match.Outputs["angle"].Require<double>("angle"), ["centerX"] = centerX, ["centerY"] = centerY,
            ["subpixelRefined"] = refined, ["refineOffsetX"] = refineOffsetX, ["refineOffsetY"] = refineOffsetY,
            ["algorithmBackend"] = "JLVision", ["modelCacheHit"] = match.Summary.GetValueOrDefault("modelCacheHit") }, match.Overlays));
    }
    private static NodeExecutorResult Missing(string reason) => new(new Dictionary<string, VisionValue>
        { ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0) },
        new Dictionary<string, object?> { ["found"] = false, ["reason"] = reason, ["algorithmBackend"] = "JLVision" });
}

public sealed class ImageWarpAffineNode : IVisionNodeExecutor
{
    public string Type => "image.warpAffine";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image"); var t = PixelAffine.Read(context);
        var (width, height) = PixelAffine.Size(node, source.Width, source.Height);
        var interpolation = node.GetString("interpolation", "linear");
        if (interpolation is not ("nearest" or "linear")) throw new InvalidOperationException("Interpolation must be nearest or linear.");
        using var native = JLVisionImages.FromMat(source.Mat); var matrix = PixelAffine.Native(t);
        using var matrixData = matrix.RawData;
        using var warped = native.AffineTransImageSize(matrix, interpolation == "nearest" ? "nearest_neighbor" : "bilinear", width, height);
        // Exclude a one-pixel source margin for bilinear interpolation (conservative at exact integer positions).
        var margin = interpolation == "linear" ? 1 : 0;
        using var support = new JlRegion();
        if (source.Width > 2 * margin && source.Height > 2 * margin) support.GenRectangle1((double)margin, (double)margin, (double)source.Height - 1 - margin, (double)source.Width - 1 - margin);
        else support.GenEmptyRegion();
        var valid = PixelAffine.Transform(JLVisionRegions.FromNative(support, source.Width, source.Height, cancellationToken), t, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        { ["image"] = VisionValue.Image(VisionImage.Own(JLVisionImages.ToMat(warped))), ["validRegion"] = VisionValue.Region(valid) },
            new Dictionary<string, object?> { ["width"] = width, ["height"] = height, ["interpolation"] = interpolation, ["algorithmBackend"] = "JLVision" }));
    }
}

public sealed class RegionMorphologyNode : IVisionNodeExecutor
{
    public string Type => "region.morphology";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.Require<VisionRegion>("region");
        var width = node.GetInt("kernelWidth", 3); var height = node.GetInt("kernelHeight", 3);
        if (width is < 1 or > 255 || height is < 1 or > 255 || width % 2 == 0 || height % 2 == 0) throw new InvalidOperationException("Region kernel width and height must be odd integers in 1..255.");
        using var native = JLVisionRegions.ToNative(source);
        using var input = node.GetBool("unionFirst", false) ? native.Union1() : native.CopyObj(1, -1);
        using var processed = node.GetString("operation", "open") switch
        {
            "erode" => input.ErosionRectangle1(width, height), "dilate" => input.DilationRectangle1(width, height),
            "open" => input.OpeningRectangle1(width, height), "close" => input.ClosingRectangle1(width, height),
            _ => throw new InvalidOperationException("Unsupported region morphology operation.")
        };
        using var bounds = new JlRegion(0d, 0d, (double)source.Height - 1, (double)source.Width - 1);
        using var clipped = processed.Intersection(bounds);
        return ValueTask.FromResult(RegionNodeHelpers.Result(JLVisionRegions.FromNative(clipped, source.Width, source.Height, cancellationToken), cancellationToken));
    }
}

public sealed class RegionTransformNode : IVisionNodeExecutor
{
    public string Type => "region.transform";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.Require<VisionRegion>("region"); var t = PixelAffine.Read(context);
        var (width, height) = PixelAffine.Size(node, source.Width, source.Height);
        return ValueTask.FromResult(RegionNodeHelpers.Result(PixelAffine.Transform(source, t, width, height, cancellationToken), cancellationToken));
    }
}

internal sealed record RegionObjectProperties(int Index, double Area, VisionPoint2D Center, double Orientation,
    double Perimeter, double Circularity, double Rectangularity, VisionRectangle2D Rectangle, VisionCircle Circle);

internal static class RegionMeasurements
{
    public static IReadOnlyList<RegionObjectProperties> Read(VisionRegion source, CancellationToken token)
    {
        var result = new List<RegionObjectProperties>();
        using var native = JLVisionRegions.ToNative(source);
        for (var i = 0; i < source.Components.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var item = native.SelectObj(i + 1);
            var area = item.AreaCenter(out double row, out double col);
            item.SmallestRectangle1(out int top, out int left, out int bottom, out int right);
            // DetectionList consumers use Circle's centre for flow.forEach.center/x/y.
            // Centre the enclosing circle at the pixel centroid rather than at the
            // smallest-circle centre, which differs for asymmetric regions.
            var radius = Math.Sqrt(source.Components[i].Runs.Max(run =>
                Math.Max(Math.Pow(run.StartColumn - col, 2), Math.Pow(run.EndColumn - col, 2)) + Math.Pow(run.Row - row, 2)));
            using var orientation = item.OrientationRegion(); using var perimeter = item.Contlength();
            using var circularity = item.Circularity(); using var rectangularity = item.Rectangularity();
            result.Add(new(i, area, new(col, row), -orientation.D * 180 / Math.PI, perimeter.D, circularity.D, rectangularity.D,
                new(left, top, right - left + 1, bottom - top + 1), new(col, row, radius)));
        }
        return result;
    }
}

public sealed class RegionPropertiesNode : IVisionNodeExecutor
{
    public string Type => "region.properties";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = node.GetInt("index", 0);
        if (index < 0) throw new InvalidOperationException("Region index must be nonnegative.");
        var properties = RegionMeasurements.Read(context.Require<VisionRegion>("region"), cancellationToken);
        var found = index < properties.Count;
        var outputs = new Dictionary<string, VisionValue> { ["propertiesJson"] = VisionValue.String(JsonSerializer.Serialize(properties)),
            ["count"] = VisionValue.Integer(properties.Count), ["found"] = VisionValue.Boolean(found) };
        if (found)
        {
            var p = properties[index];
            outputs["area"] = VisionValue.Double(p.Area); outputs["center"] = VisionValue.Point(p.Center);
            outputs["orientation"] = VisionValue.Double(p.Orientation); outputs["perimeter"] = VisionValue.Double(p.Perimeter);
            outputs["circularity"] = VisionValue.Double(p.Circularity); outputs["rectangularity"] = VisionValue.Double(p.Rectangularity);
            outputs["rectangle"] = VisionValue.Rectangle(p.Rectangle);
        }
        var summary = new Dictionary<string, object?> { ["count"] = properties.Count, ["found"] = found, ["index"] = index, ["properties"] = properties };
        if (found) { summary["area"] = properties[index].Area; summary["orientation"] = properties[index].Orientation; }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }
}

public sealed class RegionToDetectionsNode : IVisionNodeExecutor
{
    public string Type => "region.toDetections";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var properties = RegionMeasurements.Read(context.Require<VisionRegion>("region"), cancellationToken);
        var detections = properties.Select(p => new VisionDetection(p.Index, p.Center.X, p.Center.Y, p.Rectangle.Width, p.Rectangle.Height,
            p.Area, p.Perimeter, p.Circularity, p.Circle, p.Rectangle)).ToArray();
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(detections),
            ["count"] = VisionValue.Integer(detections.Length), ["found"] = VisionValue.Boolean(detections.Length > 0) },
            new Dictionary<string, object?> { ["count"] = detections.Length, ["found"] = detections.Length > 0 }));
    }
}

public sealed class LineSpacingNode : IVisionNodeExecutor
{
    public string Type => "measure.lineSpacing";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = context.Require<VisionLine2D>("a"); var b = context.Require<VisionLine2D>("b");
        var count = node.GetInt("sampleCount", 11); var direction = node.GetString("direction", "normal"); var angle = node.GetDouble("angleDeg", 90);
        if (new[] { a.Start.X, a.Start.Y, a.End.X, a.End.Y, b.Start.X, b.Start.Y, b.End.X, b.End.Y, angle }.Any(v => !double.IsFinite(v)) ||
            !double.IsFinite(a.Length) || !double.IsFinite(b.Length) || a.Length < 1e-9 || b.Length < 1e-9 || count is < 2 or > 1000)
            throw new InvalidOperationException("Line coordinates must be finite and nondegenerate; spacing samples must be 2..1000.");
        if (direction is not ("normal" or "angle")) throw new InvalidOperationException("Spacing direction must be normal or angle.");
        var dx = direction == "normal" ? -(a.End.Y - a.Start.Y) / a.Length : Math.Cos(angle * Math.PI / 180);
        var dy = direction == "normal" ? (a.End.X - a.Start.X) / a.Length : Math.Sin(angle * Math.PI / 180);
        var bx = b.End.X - b.Start.X; var by = b.End.Y - b.Start.Y;
        var determinant = dx * by - dy * bx;
        if (Math.Abs(determinant) < b.Length * 1e-10) throw new InvalidOperationException("Sampling direction is parallel to line B.");
        var samples = new List<object>(); var distances = new List<double>(); var overlays = new List<VisionOverlay>();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var f = (double)i / (count - 1); var p = new VisionPoint2D(a.Start.X + f * (a.End.X - a.Start.X), a.Start.Y + f * (a.End.Y - a.Start.Y));
            var qx = b.Start.X - p.X; var qy = b.Start.Y - p.Y;
            var distance = (qx * by - qy * bx) / determinant; var u = (qx * dy - qy * dx) / determinant;
            if (u < -1e-9 || u > 1 + 1e-9) continue;
            var end = new VisionPoint2D(p.X + distance * dx, p.Y + distance * dy);
            if (!double.IsFinite(distance) || !double.IsFinite(end.X) || !double.IsFinite(end.Y)) throw new InvalidOperationException("Spacing calculation overflowed.");
            distances.Add(Math.Abs(distance)); samples.Add(new { Index = i, Start = p, End = end, Distance = Math.Abs(distance) });
            if (overlays.Count < 200) overlays.Add(new($"spacing-{i}", VisionOverlayType.Line, X: p.X, Y: p.Y, X2: end.X, Y2: end.Y));
        }
        var found = distances.Count > 0;
        var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(found), ["sampleCount"] = VisionValue.Integer(distances.Count), ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)) };
        if (found)
        {
            var mean = distances.Average(); outputs["distance"] = VisionValue.Double(mean); outputs["min"] = VisionValue.Double(distances.Min());
            outputs["max"] = VisionValue.Double(distances.Max()); outputs["stdDev"] = VisionValue.Double(Math.Sqrt(distances.Average(v => Math.Pow(v - mean, 2))));
        }
        var summary = new Dictionary<string, object?> { ["found"] = found, ["sampleCount"] = distances.Count, ["unit"] = "px", ["direction"] = direction };
        if (found) { summary["distance"] = outputs["distance"].Value; summary["min"] = outputs["min"].Value; summary["max"] = outputs["max"].Value; summary["stdDev"] = outputs["stdDev"].Value; }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
    }
}
