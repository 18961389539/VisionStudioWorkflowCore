using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

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
        => TransformWithStats(source, t, width, height, token).Region;

    public static (VisionRegion Region, int ObjectsBeforeClip, long AreaBeforeClip, int RemovedEmptyObjects) TransformWithStats(
        VisionRegion source, VisionTransform2D t, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Components.Count == 0) return (new(width, height, []), 0, 0, 0);
        using var native = JLVisionRegions.ToNative(source);
        var matrix = Native(t);
        using var matrixData = matrix.RawData;
        using var transformed = native.AffineTransRegion(matrix, "nearest_neighbor");
        var beforeCount = transformed.CountObj();
        var beforeArea = (long)Math.Round((double)transformed.AreaCenter(out double _, out double _));
        using var bounds = new JlRegion(0d, 0d, (double)height - 1, (double)width - 1);
        using var clipped = transformed.Intersection(bounds);
        var region = JLVisionRegions.FromNative(clipped, width, height, token);
        return (region, beforeCount, beforeArea, Math.Max(0, beforeCount - region.Components.Count));
    }
}

public sealed class ImageRegisterNode : IVisionNodeExecutor
{
    public string Type => "image.register";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
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
            if (deviation < 1e-6) return ValueTask.FromResult(Missing("reference-patch-has-no-texture", x, y, width, height, range, step));
        }
        var parameters = new Dictionary<string, JsonElement>
        {
            ["angleRangeDeg"] = JsonSerializer.SerializeToElement(range), ["angleStepDeg"] = JsonSerializer.SerializeToElement(step),
            ["minScore"] = JsonSerializer.SerializeToElement(score)
        };
        var match = JLVisionMatching.Execute(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source), ["template"] = VisionValue.Image(patch) }),
            new NodeDefinition(node.Id, "match.template", null, null, parameters), false, cancellationToken);
        if (!match.Outputs["found"].Require<bool>("found"))
        {
            var reason = "angle-window-no-candidate";
            if (match.Summary.TryGetValue("rawCandidateCount", out var raw) && Convert.ToInt64(raw) > 0)
                reason = "score-below-minimum";
            else
            {
                // A zero-score diagnostic pass tells low score apart from an angle/texture miss
                // without lowering the production acceptance threshold.
                var diagnosticParameters = new Dictionary<string, JsonElement>(parameters) { ["minScore"] = JsonSerializer.SerializeToElement(0d) };
                var diagnostic = JLVisionMatching.Execute(
                    new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source), ["template"] = VisionValue.Image(patch) }),
                    new NodeDefinition(node.Id, "match.template", null, null, diagnosticParameters), false, cancellationToken);
                if (diagnostic.Outputs["found"].Require<bool>("found") ||
                    diagnostic.Summary.TryGetValue("rawCandidateCount", out var diagnosticRaw) && Convert.ToInt64(diagnosticRaw) > 0)
                    reason = "score-below-minimum";
            }
            return ValueTask.FromResult(Missing(reason, x, y, width, height, range, step));
        }
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
        var templateBox = $"{x},{y},{width},{height}";
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("score", match.Outputs["score"].Require<double>("score")), ("angleDeg", match.Outputs["angle"].Require<double>("angle")), ("centerX", centerX), ("centerY", centerY), ("subpixelRefined", refined), ("patchX", x), ("patchY", y), ("patchWidth", width), ("patchHeight", height), ("templateBox", templateBox), ("angleRangeDeg", range), ("angleStepDeg", step), ("angleWindowCoveredDeg", range * 2));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["score"] = match.Outputs["score"], ["transform"] = VisionValue.Transform(transform),
            ["angle"] = VisionValue.Double(match.Outputs["angle"].Require<double>("angle")),
            ["center"] = VisionValue.Point(new VisionPoint2D(centerX, centerY)),
            ["templateBox"] = VisionValue.String(templateBox), ["reason"] = VisionValue.String("matched"),
            ["diagnostics"] = VisionValue.String(diagnostics)
        }, new Dictionary<string, object?> { ["found"] = true, ["score"] = match.Outputs["score"].Value, ["transform"] = transform,
            ["angleDeg"] = match.Outputs["angle"].Require<double>("angle"), ["centerX"] = centerX, ["centerY"] = centerY,
            ["templateBox"] = templateBox, ["angleRangeDeg"] = range, ["angleStepDeg"] = step,
            ["subpixelRefined"] = refined, ["refineOffsetX"] = refineOffsetX, ["refineOffsetY"] = refineOffsetY,
            ["algorithmBackend"] = "jlvision", ["modelCacheHit"] = match.Summary.GetValueOrDefault("modelCacheHit") }, match.Overlays));
    }
    private static NodeExecutorResult Missing(string reason, int x, int y, int width, int height, double range, double step)
    {
        var templateBox = $"{x},{y},{width},{height}";
        var diag = CoordinateValidation.Diagnostics("image.register", ("found", false), ("reason", reason),
            ("templateBox", templateBox), ("patchX", x), ("patchY", y), ("patchWidth", width), ("patchHeight", height),
            ("angleRangeDeg", range), ("angleStepDeg", step), ("angleWindowCoveredDeg", range * 2));
        return new(new Dictionary<string, VisionValue>
        { ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0), ["reason"] = VisionValue.String(reason),
          ["templateBox"] = VisionValue.String(templateBox), ["diagnostics"] = VisionValue.String(diag) },
        new Dictionary<string, object?> { ["found"] = false, ["reason"] = reason, ["templateBox"] = templateBox,
            ["angleRangeDeg"] = range, ["angleStepDeg"] = step, ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag });
    }
}

public sealed class ImageWarpAffineNode : IVisionNodeExecutor
{
    public string Type => "image.warpAffine";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
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
        var validPixelCount = valid.Components.Sum(component => component.Runs.Sum(run => (long)run.EndColumn - run.StartColumn + 1));
        if (validPixelCount <= 0)
            throw new InvalidOperationException("image.warpAffine validRegion is empty; the transform maps all source support outside the output canvas.");
        var determinant = t.H00 * t.H11 - t.H01 * t.H10;
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type,
            ("width", width), ("height", height), ("interpolation", interpolation),
            ("sourceWidth", source.Width), ("sourceHeight", source.Height), ("algorithmBackend", "jlvision"),
            ("canvasAnchor", "output (0,0); translation terms are preserved when width/height change"),
            ("determinant", determinant), ("supportMarginPx", margin), ("validPixelCount", validPixelCount),
            ("validRegionContract", interpolation == "linear" ? "bilinear source support eroded by 1 px" : "nearest-neighbor source pixel support"),
            ("coordinateContract", "source-to-target pixel-centre affine; origin top-left; homogeneous normalized; JLVision row/column corner conversion +0.5"),
            ("matrix", new[] { t.H00, t.H01, t.H02, t.H10, t.H11, t.H12, t.H20, t.H21, t.H22 }));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(JLVisionImages.ToMat(warped))),
            ["validRegion"] = VisionValue.Region(valid),
            ["validPixelCount"] = VisionValue.Integer(validPixelCount),
            ["diagnostics"] = VisionValue.String(diag)
        },
            new Dictionary<string, object?> { ["width"] = width, ["height"] = height, ["interpolation"] = interpolation, ["validPixelCount"] = validPixelCount, ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag }));
    }
}

public sealed class RegionMorphologyNode : IVisionNodeExecutor
{
    public string Type => "region.morphology";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var source = context.Require<VisionRegion>("region");
        if (source.Width < 1 || source.Height < 1) throw new InvalidOperationException("region.morphology requires positive canvas dimensions.");
        if (source.Components is null) throw new InvalidOperationException("region.morphology region.Components must be non-null.");
        if (source.Width > 16384 || source.Height > 16384)
            throw new InvalidOperationException("region.morphology canvas must be at most 16384 per axis.");
        var width = node.GetInt("kernelWidth", 3); var height = node.GetInt("kernelHeight", 3);
        if (width is < 1 or > 255 || height is < 1 or > 255 || width % 2 == 0 || height % 2 == 0)
            throw new InvalidOperationException("Region kernel width and height must be odd integers in 1..255.");
        var op = CoordinateValidation.RequireNonEmpty(node.GetString("operation", "open"), "operation").ToLowerInvariant();
        if (op is not ("erode" or "dilate" or "open" or "close"))
            throw new InvalidOperationException("Unsupported region morphology operation.");
        var unionFirst = node.GetBool("unionFirst", false);
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "empty"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail")) throw new InvalidOperationException("region.morphology emptyPolicy must be empty or fail.");
        using var native = JLVisionRegions.ToNative(source);
        using var input = unionFirst ? native.Union1() : native.CopyObj(1, -1);
        using var processed = op switch
        {
            "erode" => input.ErosionRectangle1(width, height), "dilate" => input.DilationRectangle1(width, height),
            "open" => input.OpeningRectangle1(width, height), "close" => input.ClosingRectangle1(width, height),
            _ => throw new InvalidOperationException("Unsupported region morphology operation.")
        };
        using var bounds = new JlRegion(0d, 0d, (double)source.Height - 1, (double)source.Width - 1);
        using var clipped = processed.Intersection(bounds);
        var outputRegion = JLVisionRegions.FromNative(clipped, source.Width, source.Height, cancellationToken);
        var baseResult = RegionNodeHelpers.Result(outputRegion, cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.Complete(baseResult, cancellationToken, () =>
        {
            var outCount = baseResult.Summary.TryGetValue("count", out var cs) ? Convert.ToInt32(cs) : 0;
            if (outCount == 0 && emptyPolicy == "fail")
                throw new InvalidOperationException("region.morphology produced an empty region and emptyPolicy=fail.");
            var inputArea = JLVisionRegions.PixelArea(source);
            var outputArea = JLVisionRegions.PixelArea(outputRegion);
            var mappings = RegionNodeHelpers.SourceMappings(outputRegion, source, null, "morphology", cancellationToken);
            var bridgedPairs = RegionNodeHelpers.BridgedPairs(mappings);
            var warnings = bridgedPairs.Count > 0 ? new[] { new { code = "objects-bridged", pairs = bridgedPairs } } : Array.Empty<object>();
            var diag = CoordinateValidation.Diagnostics(Type, ("operation", op), ("kernelWidth", width), ("kernelHeight", height),
                ("unionFirst", unionFirst), ("emptyPolicy", emptyPolicy), ("outputCount", outCount), ("inputObjects", source.Components.Count),
                ("inputArea", inputArea), ("outputArea", outputArea), ("addedPixels", outputArea - inputArea),
                ("bridgedObjectPairs", bridgedPairs), ("warningCount", warnings.Length));
            var outputs = new Dictionary<string, VisionValue>(baseResult.Outputs)
            {
                ["diagnostics"] = VisionValue.String(diag), ["sourceMappings"] = VisionValue.String(mappings),
                ["warningCount"] = VisionValue.Integer(warnings.Length)
            };
            var summary = new Dictionary<string, object?>(baseResult.Summary)
            {
                ["operation"] = op, ["kernelWidth"] = width, ["kernelHeight"] = height, ["unionFirst"] = unionFirst,
                ["inputArea"] = inputArea, ["outputArea"] = outputArea, ["addedPixels"] = outputArea - inputArea,
                ["bridgedObjectPairs"] = bridgedPairs, ["sourceMappings"] = mappings,
                ["warningCount"] = warnings.Length, ["diagnostics"] = diag
            };
            return baseResult with { Outputs = outputs, Summary = summary };
        }));
    }
}

public sealed class RegionTransformNode : IVisionNodeExecutor
{
    public string Type => "region.transform";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var source = context.Require<VisionRegion>("region");
        if (source.Width < 1 || source.Height < 1) throw new InvalidOperationException("region.transform requires a non-empty region canvas.");
        if (source.Components.Count > 100_000) throw new InvalidOperationException("region.transform refuses more than 100000 region objects.");
        var transform = PixelAffine.Read(context);
        var (width, height) = PixelAffine.Size(node, source.Width, source.Height);
        var det = transform.H00 * transform.H11 - transform.H01 * transform.H10;
        var transformed = PixelAffine.TransformWithStats(source, transform, width, height, cancellationToken);
        var result = RegionNodeHelpers.Result(transformed.Region, cancellationToken);
        var area = result.Outputs.TryGetValue("area", out var a) ? a.Value : null;
        var count = result.Outputs.TryGetValue("count", out var c) ? c.Value : null;
        var clippedPixels = Math.Max(0, transformed.AreaBeforeClip - JLVisionRegions.PixelArea(transformed.Region));
        var matrix = new[] { transform.H00, transform.H01, transform.H02, transform.H10, transform.H11, transform.H12, transform.H20, transform.H21, transform.H22 };
        var diag = CoordinateValidation.Diagnostics(Type,
            ("sourceWidth", source.Width), ("sourceHeight", source.Height), ("width", width), ("height", height),
            ("determinant", det), ("sourceObjects", source.Components.Count), ("objectsBeforeClip", transformed.ObjectsBeforeClip),
            ("areaBeforeClip", transformed.AreaBeforeClip), ("area", area), ("count", count),
            ("clippedPixels", clippedPixels), ("removedEmptyObjects", transformed.RemovedEmptyObjects),
            ("coordinateContract", "source-to-target pixel-centre affine; origin top-left; homogeneous normalized; JLVision row/column corner conversion +0.5"),
            ("matrix", matrix));
        var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
        var summary = new Dictionary<string, object?>(result.Summary)
        {
            ["determinant"] = det, ["clippedPixels"] = clippedPixels, ["removedEmptyObjects"] = transformed.RemovedEmptyObjects,
            ["coordinateContract"] = "image-pixel-center-affine-v1", ["matrix"] = matrix, ["diagnostics"] = diag
        };
        return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
    }
}

internal sealed record RegionObjectProperties(int Index, double Area, VisionPoint2D Center, double Orientation,
    double Perimeter, double Circularity, double Rectangularity, VisionRectangle2D Rectangle, VisionCircle Circle,
    string? SourceRegionId);

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
                new(left, top, right - left + 1, bottom - top + 1), new(col, row, radius), source.SourceRegionId));
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

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var selectBy = node.GetString("selectBy", "index").Trim().ToLowerInvariant();
        if (selectBy is not ("index" or "largest")) throw new InvalidOperationException("region.properties selectBy must be index or largest.");
        var index = node.GetInt("index", 0);
        if (index < 0) throw new InvalidOperationException("region.properties index must be nonnegative.");
        var emptyPolicy = node.GetString("emptyPolicy", "empty").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail")) throw new InvalidOperationException("region.properties emptyPolicy must be empty or fail.");
        var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1) throw new InvalidOperationException("region.properties requires a non-empty region canvas.");
        if (region.Components.Count > 100_000) throw new InvalidOperationException("region.properties refuses more than 100000 region objects.");
        var properties = RegionMeasurements.Read(region, cancellationToken);
        var requestedIndex = index;
        if (selectBy == "largest" && properties.Count > 0)
            index = properties.OrderByDescending(p => p.Area).ThenBy(p => p.Center.X).ThenBy(p => p.Center.Y).ThenBy(p => p.Index).First().Index;
        var found = index < properties.Count;
        if (!found && emptyPolicy == "fail")
            throw new InvalidOperationException($"region.properties found no object: requestedIndex={requestedIndex}, selectedIndex={index}, objectCount={properties.Count}, selectBy={selectBy}.");
        RegionObjectProperties? selected = found ? properties[index] : null;
        double aspect = 0, equiv = 0, rectW = 0, rectH = 0;
        if (selected is { } p0)
        {
            rectW = p0.Rectangle.Width; rectH = p0.Rectangle.Height;
            aspect = rectH > 1e-12 ? rectW / rectH : 0;
            equiv = Math.Sqrt(Math.Max(0, p0.Area) / Math.PI) * 2;
        }
        var diag = CoordinateValidation.Diagnostics(Type, ("count", properties.Count), ("requestedIndex", requestedIndex),
            ("selectedIndex", found ? index : -1), ("selectBy", selectBy), ("found", found), ("emptyPolicy", emptyPolicy),
            ("sourceRegionId", region.SourceRegionId), ("coordinateSystem", region.CoordinateSystem),
            ("width", region.Width), ("height", region.Height),
            ("area", selected?.Area ?? 0), ("orientation", selected?.Orientation ?? 0),
            ("aspectRatio", aspect), ("equivalentDiameter", equiv));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["propertiesJson"] = VisionValue.String(JsonSerializer.Serialize(properties)),
            ["count"] = VisionValue.Integer(properties.Count),
            ["found"] = VisionValue.Boolean(found),
            ["diagnostics"] = VisionValue.String(diag)
        };
        if (selected is { } p)
        {
            outputs["area"] = VisionValue.Double(p.Area); outputs["center"] = VisionValue.Point(p.Center);
            outputs["orientation"] = VisionValue.Double(p.Orientation); outputs["perimeter"] = VisionValue.Double(p.Perimeter);
            outputs["circularity"] = VisionValue.Double(p.Circularity); outputs["rectangularity"] = VisionValue.Double(p.Rectangularity);
            outputs["rectangle"] = VisionValue.Rectangle(p.Rectangle);
            outputs["rectWidth"] = VisionValue.Double(rectW); outputs["rectHeight"] = VisionValue.Double(rectH);
            outputs["aspectRatio"] = VisionValue.Double(aspect); outputs["equivalentDiameter"] = VisionValue.Double(equiv);
            outputs["selectedIndex"] = VisionValue.Integer(index);
        }
        var summary = new Dictionary<string, object?> { ["count"] = properties.Count, ["found"] = found, ["requestedIndex"] = requestedIndex, ["selectedIndex"] = found ? index : -1, ["selectBy"] = selectBy, ["emptyPolicy"] = emptyPolicy, ["sourceRegionId"] = region.SourceRegionId, ["properties"] = properties, ["diagnostics"] = diag };
        if (selected is { } s) { summary["area"] = s.Area; summary["orientation"] = s.Orientation; summary["aspectRatio"] = aspect; }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }
}

public sealed class RegionToDetectionsNode : IVisionNodeExecutor
{
    public string Type => "region.toDetections";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

        var emptyPolicy = (node.GetString("emptyPolicy", "empty") ?? "empty").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail"))
            throw new InvalidOperationException(Type + " emptyPolicy must be empty or fail.");

var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1) throw new InvalidOperationException("region.toDetections requires a non-empty region canvas.");
        var maxCount = node.GetInt("maxCount", 0);
        if (maxCount < 0 || maxCount > 100000) throw new InvalidOperationException("region.toDetections maxCount must be 0 (unlimited) or 1..100000.");
        var truncationPolicy = node.GetString("truncationPolicy", "truncate").Trim().ToLowerInvariant();
        if (truncationPolicy is not ("truncate" or "fail")) throw new InvalidOperationException("region.toDetections truncationPolicy must be truncate or fail.");
        var sortBy = node.GetString("sortBy", "area").Trim().ToLowerInvariant();
        if (sortBy is not ("area" or "x" or "y"))
            throw new InvalidOperationException("region.toDetections sortBy must be area, x, or y.");
        var properties = RegionMeasurements.Read(region, cancellationToken);
        var ordered = sortBy switch
        {
            "x" => properties.OrderBy(p => p.Center.X).ThenBy(p => p.Center.Y).ThenBy(p => p.Index),
            "y" => properties.OrderBy(p => p.Center.Y).ThenBy(p => p.Center.X).ThenBy(p => p.Index),
            _ => properties.OrderByDescending(p => p.Area).ThenBy(p => p.Center.X).ThenBy(p => p.Center.Y).ThenBy(p => p.Index)
        };
        var truncated = maxCount > 0 && properties.Count > maxCount;
        if (truncated && truncationPolicy == "fail")
            throw new InvalidOperationException($"region.toDetections sourceCount={properties.Count} exceeds maxCount={maxCount}; truncationPolicy=fail.");
        var selected = (maxCount > 0 ? ordered.Take(maxCount) : ordered).ToArray();
        var detections = selected.Select(p => new VisionDetection(p.Index, p.Center.X, p.Center.Y, p.Rectangle.Width, p.Rectangle.Height,
            p.Area, p.Perimeter, p.Circularity, p.Circle, p.Rectangle)).ToArray();
        if (detections.Length == 0 && emptyPolicy == "fail")
            throw new InvalidOperationException("region.toDetections produced no detections and emptyPolicy=fail.");
        var diag = CoordinateValidation.Diagnostics(Type, ("count", detections.Length), ("sourceCount", properties.Count),
            ("truncated", truncated), ("truncationPolicy", truncationPolicy),
            ("found", detections.Length > 0), ("maxCount", maxCount), ("sortBy", sortBy), ("emptyPolicy", emptyPolicy), ("width", region.Width), ("height", region.Height));
        // 截断必须可见：maxCount 会静默丢弃多余连通域，调用方需要 truncated 才能把"只有 3 个"和"被裁到 3 个"区分开。
        var emittedArea = 0d;
        foreach (var detection in detections) emittedArea += detection.Area;
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["detections"] = VisionValue.Detections(detections),
            ["count"] = VisionValue.Integer(detections.Length),
            ["sourceCount"] = VisionValue.Integer(properties.Count),
            ["found"] = VisionValue.Boolean(detections.Length > 0),
            ["emitted"] = VisionValue.Integer(detections.Length),
            ["truncated"] = VisionValue.Boolean(truncated),
            ["totalArea"] = VisionValue.Double(emittedArea),
            ["diagnostics"] = VisionValue.String(diag)
        },
            new Dictionary<string, object?> { ["count"] = detections.Length, ["found"] = detections.Length > 0, ["sourceCount"] = properties.Count, ["truncated"] = truncated, ["truncationPolicy"] = truncationPolicy, ["totalArea"] = emittedArea, ["diagnostics"] = diag }));
    }
}

public sealed class LineSpacingNode : IVisionNodeExecutor
{
    public string Type => "measure.lineSpacing";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var a = context.Require<VisionLine2D>("a"); var b = context.Require<VisionLine2D>("b");
        CoordinateValidation.RequireLine(a, "a"); CoordinateValidation.RequireLine(b, "b");
        var count = node.GetInt("sampleCount", 11); var direction = node.GetString("direction", "normal"); var angle = node.GetDouble("angleDeg", 90);
        if (!double.IsFinite(angle) || count is < 2 or > 1000)
            throw new InvalidOperationException("Line spacing angleDeg must be finite; sampleCount must be 2..1000.");
        if (direction is not ("normal" or "angle")) throw new InvalidOperationException("Spacing direction must be normal or angle.");
        var nominal = node.GetDouble("nominalDistance", 0);
        var tolerance = node.GetDouble("tolerance", 0);
        var minCoverageRatio = node.GetDouble("minCoverageRatio", 0);
        var minValidSamples = node.GetInt("minValidSamples", 0);
        var toleranceMode = node.GetString("toleranceMode", "mean").Trim().ToLowerInvariant();
        if (!double.IsFinite(nominal) || nominal < 0) throw new InvalidOperationException("nominalDistance must be finite and non-negative (0 disables the gate).");
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new InvalidOperationException("tolerance must be finite and non-negative.");
        if (nominal > 0 && tolerance <= 0) throw new InvalidOperationException("tolerance must be > 0 when nominalDistance is set.");
        if (!double.IsFinite(minCoverageRatio) || minCoverageRatio is < 0 or > 1) throw new InvalidOperationException("minCoverageRatio must be within 0..1.");
        if (minValidSamples < 0 || minValidSamples > count) throw new InvalidOperationException("minValidSamples must be within 0..sampleCount.");
        if (toleranceMode is not ("mean" or "allsamples")) throw new InvalidOperationException("toleranceMode must be mean or allSamples.");
        var dx = direction == "normal" ? -(a.End.Y - a.Start.Y) / a.Length : Math.Cos(angle * Math.PI / 180);
        var dy = direction == "normal" ? (a.End.X - a.Start.X) / a.Length : Math.Sin(angle * Math.PI / 180);
        var bx = b.End.X - b.Start.X; var by = b.End.Y - b.Start.Y;
        var determinant = dx * by - dy * bx;
        if (Math.Abs(determinant) < b.Length * 1e-10) throw new InvalidOperationException("Sampling direction is parallel to line B.");
        var samples = new List<SpacingSample>(count); var distances = new List<double>(); var overlays = new List<VisionOverlay>
        {
            new("line-a", VisionOverlayType.Line, X: a.Start.X, Y: a.Start.Y, X2: a.End.X, Y2: a.End.Y, Stroke: "#1677ff", StrokeWidth: 2),
            new("line-b", VisionOverlayType.Line, X: b.Start.X, Y: b.Start.Y, X2: b.End.X, Y2: b.End.Y, Stroke: "#52c41a", StrokeWidth: 2)
        };
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var f = (double)i / (count - 1); var p = new VisionPoint2D(a.Start.X + f * (a.End.X - a.Start.X), a.Start.Y + f * (a.End.Y - a.Start.Y));
            var qx = b.Start.X - p.X; var qy = b.Start.Y - p.Y;
            var distance = (qx * by - qy * bx) / determinant; var u = (qx * dy - qy * dx) / determinant;
            if (!double.IsFinite(distance) || !double.IsFinite(u))
            {
                samples.Add(new(i, p, null, null, null, null, false, "non-finite"));
                continue;
            }
            var end = new VisionPoint2D(p.X + distance * dx, p.Y + distance * dy);
            if (!double.IsFinite(end.X) || !double.IsFinite(end.Y))
            {
                samples.Add(new(i, p, null, u, distance, Math.Abs(distance), false, "overflow"));
                continue;
            }
            if (u < -1e-9 || u > 1 + 1e-9)
            {
                // 射线落在 B 的延长线上：距离可算但没有物理意义，只作为覆盖率证据保留。
                samples.Add(new(i, p, end, u, distance, Math.Abs(distance), false, "outside-b-segment"));
                continue;
            }
            distances.Add(Math.Abs(distance));
            samples.Add(new(i, p, end, u, distance, Math.Abs(distance), true, null));
            if (overlays.Count < 202) overlays.Add(new VisionOverlay($"spacing-{i}", VisionOverlayType.Line, X: p.X, Y: p.Y, X2: end.X, Y2: end.Y, Stroke: "#faad14", StrokeWidth: 1));
        }
        var validCount = distances.Count;
        var coverageRatio = (double)validCount / count;
        var coveragePassed = minCoverageRatio <= 0 || coverageRatio >= minCoverageRatio - 1e-12;
        var enoughSamples = validCount >= Math.Max(1, minValidSamples);
        var found = validCount > 0 && coveragePassed && enoughSamples;
        double mean = 0, min = 0, max = 0, stdDev = 0, deviation = 0, maxAbsDeviation = 0;
        if (validCount > 0)
        {
            mean = distances.Average(); min = distances.Min(); max = distances.Max();
            stdDev = Math.Sqrt(distances.Average(v => Math.Pow(v - mean, 2)));
            deviation = nominal > 0 ? mean - nominal : 0;
            maxAbsDeviation = nominal > 0 ? distances.Max(v => Math.Abs(v - nominal)) : distances.Max(v => Math.Abs(v - mean));
        }
        var gateEnabled = nominal > 0;
        var withinTolerance = found && gateEnabled && (toleranceMode == "allsamples" ? maxAbsDeviation <= tolerance + 1e-9 : Math.Abs(deviation) <= tolerance + 1e-9);
        var evaluationState = !gateEnabled ? "not-evaluated" : withinTolerance ? "pass" : "fail";
        var reason = !found
            ? validCount == 0 ? "no-intersection" : !coveragePassed ? "insufficient-coverage" : "insufficient-valid-samples"
            : !gateEnabled ? "not-evaluated"
            : withinTolerance ? "ok"
            : toleranceMode == "allsamples" ? "sample-out-of-tolerance" : "tolerance-exceeded";
        var diag = CoordinateValidation.Diagnostics(Type,
            ("found", found), ("reason", reason), ("sampleCount", validCount), ("requestedSamples", count),
            ("validSampleCount", validCount), ("skippedCount", count - validCount), ("coverageRatio", coverageRatio),
            ("minCoverageRatio", minCoverageRatio), ("minValidSamples", minValidSamples), ("coveragePassed", coveragePassed),
            ("direction", direction), ("angleDeg", angle), ("distance", mean), ("min", min), ("max", max), ("stdDev", stdDev),
            ("nominalDistance", nominal), ("tolerance", tolerance), ("toleranceMode", toleranceMode),
            ("deviation", deviation), ("maxAbsDeviation", maxAbsDeviation), ("withinTolerance", withinTolerance),
            ("evaluationState", evaluationState));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(found),
            ["reason"] = VisionValue.String(reason),
            ["sampleCount"] = VisionValue.Integer(validCount),
            ["requestedSampleCount"] = VisionValue.Integer(count),
            ["validSampleCount"] = VisionValue.Integer(validCount),
            ["skippedCount"] = VisionValue.Integer(count - validCount),
            ["coverageRatio"] = VisionValue.Double(coverageRatio),
            ["coveragePassed"] = VisionValue.Boolean(coveragePassed),
            ["toleranceMode"] = VisionValue.String(toleranceMode),
            ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)),
            ["withinTolerance"] = VisionValue.Boolean(withinTolerance),
            ["evaluationState"] = VisionValue.String(evaluationState),
            ["diagnostics"] = VisionValue.String(diag)
        };
        if (validCount > 0)
        {
            outputs["distance"] = VisionValue.Double(mean); outputs["min"] = VisionValue.Double(min);
            outputs["max"] = VisionValue.Double(max); outputs["stdDev"] = VisionValue.Double(stdDev);
            outputs["deviation"] = VisionValue.Double(deviation);
            outputs["maxAbsDeviation"] = VisionValue.Double(maxAbsDeviation);
        }
        var summary = new Dictionary<string, object?>
        {
            ["found"] = found, ["reason"] = reason, ["sampleCount"] = validCount, ["requestedSampleCount"] = count,
            ["validSampleCount"] = validCount, ["coverageRatio"] = coverageRatio, ["coveragePassed"] = coveragePassed,
            ["minCoverageRatio"] = minCoverageRatio, ["minValidSamples"] = minValidSamples,
            ["unit"] = "px", ["direction"] = direction, ["toleranceMode"] = toleranceMode,
            ["withinTolerance"] = withinTolerance, ["evaluationState"] = evaluationState, ["diagnostics"] = diag
        };
        if (validCount > 0)
        {
            summary["distance"] = mean; summary["min"] = min; summary["max"] = max; summary["stdDev"] = stdDev;
            summary["nominalDistance"] = nominal; summary["tolerance"] = tolerance; summary["deviation"] = deviation;
            summary["maxAbsDeviation"] = maxAbsDeviation;
        }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
    }

    private sealed record SpacingSample(int Index, VisionPoint2D Start, VisionPoint2D? End, double? UOnB,
        double? SignedDistance, double? Distance, bool Valid, string? SkipReason);
    }
