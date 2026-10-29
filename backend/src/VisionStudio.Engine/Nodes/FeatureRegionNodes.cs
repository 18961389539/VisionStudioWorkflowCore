using System.Text.Json;
using System.Runtime.InteropServices;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

internal static class GrayMorphology
{
    public static NodeExecutorResult Execute(string nodeType, NodeExecutionContext context, NodeDefinition node, string operation, CancellationToken token, bool applyRoi = false)
    {
        token.ThrowIfCancellationRequested();
        if (operation is not ("topHat" or "bottomHat" or "gradient"))
            throw new InvalidOperationException($"{nodeType} unsupported gray-morphology operation '{operation}'.");
        if (node is null) throw new InvalidOperationException($"{nodeType} requires a node definition.");
        var emptyPolicy = (node.GetString("emptyPolicy", "fail") ?? "fail").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("fail" or "passthrough"))
            throw new InvalidOperationException($"{nodeType} emptyPolicy must be fail or passthrough.");
        var source = context.RequireImage("image");
        if (source.Mat.Empty() || source.Width < 1 || source.Height < 1)
        {
            if (emptyPolicy == "fail")
                throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
            var emptyMat = new Mat(Math.Max(1, source.Height), Math.Max(1, source.Width), MatType.CV_8UC1, Scalar.All(0));
            var emptyDiag = CoordinateValidation.Diagnostics(nodeType,
                ("operation", operation), ("emptyPolicy", emptyPolicy), ("emptyResult", true),
                ("nonZeroCount", 0), ("meanIntensity", 0d), ("maxIntensity", 0d), ("minIntensity", 0d), ("stdIntensity", 0d));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(VisionImage.Own(emptyMat)),
                    ["meanIntensity"] = VisionValue.Double(0), ["maxIntensity"] = VisionValue.Double(0),
                    ["minIntensity"] = VisionValue.Double(0), ["stdIntensity"] = VisionValue.Double(0),
                    ["nonZeroCount"] = VisionValue.Integer(0), ["diagnostics"] = VisionValue.String(emptyDiag)
                },
                new Dictionary<string, object?>
                {
                    ["operation"] = operation, ["emptyPolicy"] = emptyPolicy, ["emptyResult"] = true,
                    ["nonZeroCount"] = 0, ["algorithmBackend"] = "jlvision", ["diagnostics"] = emptyDiag
                });
        }
        if (source.Width > 16384 || source.Height > 16384 || (long)source.Width * source.Height > 64_000_000)
            throw new InvalidOperationException($"{nodeType} image must be at most 16384 per axis and 64000000 pixels.");
        if (source.Mat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException($"{nodeType} requires an 8-bit image (color is converted to gray).");
        var width = node.GetInt("kernelWidth", 15); var height = node.GetInt("kernelHeight", 15);
        if (width is < 1 or > 255 || height is < 1 or > 255 || width % 2 == 0 || height % 2 == 0)
            throw new InvalidOperationException($"{nodeType} kernel dimensions must be odd in 1..255.");
        if (width > source.Width * 2 + 1 || height > source.Height * 2 + 1)
            throw new InvalidOperationException($"{nodeType} kernel is unreasonably large for the image dimensions.");
        if (width > source.Width && height > source.Height)
            throw new InvalidOperationException($"{nodeType} kernel exceeds the image on both axes; choose a smaller structuring element.");
        using var gray = ColorChannels.Gray(source);
        using var first = operation switch
        {
            "topHat" => gray.GrayOpeningRect(width, height),
            "bottomHat" => gray.GrayClosingRect(width, height),
            _ => gray.GrayDilationRect(width, height)
        };
        using var second = operation == "gradient" ? gray.GrayErosionRect(width, height) : null;
        using var output = operation == "topHat" ? gray.SubImage(first, 1d, 0d) : first.SubImage(second ?? gray, 1d, 0d);
        token.ThrowIfCancellationRequested();
        var outputMat = JLVisionImages.ToMat(output);
        outputMat = PreprocessImageHelpers.ApplyRoiMask(outputMat, applyRoi ? node.GetRoi() : null, token);
        if (outputMat.Empty() || outputMat.Width != source.Width || outputMat.Height != source.Height)
            throw new InvalidOperationException($"{nodeType} produced an unexpected empty or resized result.");
        var total = outputMat.Total();
        var nonZeroCount = total > 0 ? JLVisionOperators.CountNonZero(outputMat) : 0;
        var fgRatio = total > 0 ? (double)nonZeroCount / total : 0;
        JLVisionOperators.MinMax(outputMat, out double minIntensity, out double maxIntensity, out Point minLoc, out Point maxLoc);
        var meanIntensity = JLVisionOperators.Mean(outputMat, out double stdIntensity);
        var energy = meanIntensity * total; // sum of intensities (mean * pixel count)
        var kernelArea = (long)width * height;
        var diag = CoordinateValidation.Diagnostics(nodeType,
            ("operation", operation), ("kernelWidth", width), ("kernelHeight", height), ("kernelArea", kernelArea),
            ("nonZeroRatio", fgRatio), ("nonZeroCount", nonZeroCount),
            ("meanIntensity", meanIntensity), ("maxIntensity", maxIntensity), ("minIntensity", minIntensity), ("stdIntensity", stdIntensity),
            ("energy", energy), ("peakX", maxLoc.X), ("peakY", maxLoc.Y), ("minX", minLoc.X), ("minY", minLoc.Y),
            ("width", outputMat.Width), ("height", outputMat.Height),
            ("sourceWidth", source.Width), ("sourceHeight", source.Height), ("sourceChannels", source.Mat.Channels()),
            ("emptyResult", nonZeroCount == 0));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(outputMat)),
                ["meanIntensity"] = VisionValue.Double(meanIntensity),
                ["maxIntensity"] = VisionValue.Double(maxIntensity),
                ["minIntensity"] = VisionValue.Double(minIntensity),
                ["stdIntensity"] = VisionValue.Double(stdIntensity),
                ["nonZeroCount"] = VisionValue.Integer(nonZeroCount),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["operation"] = operation, ["kernelWidth"] = width, ["kernelHeight"] = height, ["kernelArea"] = kernelArea,
                ["meanIntensity"] = meanIntensity, ["maxIntensity"] = maxIntensity, ["minIntensity"] = minIntensity,
                ["stdIntensity"] = stdIntensity, ["nonZeroCount"] = nonZeroCount, ["nonZeroRatio"] = fgRatio,
                ["energy"] = energy, ["peakX"] = maxLoc.X, ["peakY"] = maxLoc.Y, ["emptyResult"] = nonZeroCount == 0,
                ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag
            });
    }
}

public sealed class GrayHistogramNode : IVisionNodeExecutor
{
    public string Type => "measure.grayHistogram";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var low = node.GetDouble("lowPercent", 5); var high = node.GetDouble("highPercent", 95);
        if (!double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high > 100 || low > high)
            throw new InvalidOperationException("Histogram percentiles must satisfy 0 <= lowPercent <= highPercent <= 100.");
        var (source, scoped) = RegionNodeHelpers.Source(context);
        if (source.Width < 1 || source.Height < 1) throw new InvalidOperationException("measure.grayHistogram requires a non-empty image.");
        using var gray = ColorChannels.Gray(source);
        using var domain = DefectNodeHelpers.Domain(gray, source.Width, source.Height, scoped, context);
        var count = domain.AreaCenter(out double _, out double _);
        var bins = new long[256]; var cdf = new double[256];
        if (count > 0)
        {
            using var absolute = gray.GrayHisto(domain, out var relative);
            using (relative)
            {
                if (absolute.Length != 256) throw new InvalidOperationException("Expected a 256-bin byte histogram.");
                for (var i = 0; i < bins.Length; i++) bins[i] = absolute[i].L;
            }
            if (bins.Sum() != count || bins.Any(v => v < 0)) throw new InvalidOperationException("Native histogram count mismatch.");
        }
        long cumulative = 0;
        for (var i = 0; i < bins.Length; i++) { cumulative += bins[i]; cdf[i] = count == 0 ? 0 : (double)cumulative / count; }
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(count > 0), ["pixelCount"] = VisionValue.Integer(count),
            ["binsJson"] = VisionValue.String(JsonSerializer.Serialize(bins)), ["cdfJson"] = VisionValue.String(JsonSerializer.Serialize(cdf))
        };
        var summary = new Dictionary<string, object?> { ["found"] = count > 0, ["pixelCount"] = count, ["algorithmBackend"] = "jlvision" };
        double? meanOut = null, stdOut = null, lowOut = null, highOut = null;
        if (count > 0)
        {
            var mean = bins.Select((v, i) => (double)v * i).Sum() / count;
            var variance = bins.Select((v, i) => v * (i - mean) * (i - mean)).Sum() / count;
            var entropy = -bins.Where(v => v > 0).Sum(v => { var p = (double)v / count; return p * Math.Log2(p); });
            var stats = new Dictionary<string, double> { ["mean"] = mean, ["stdDev"] = Math.Sqrt(variance), ["minGray"] = Quantile(0), ["maxGray"] = Quantile(100),
                ["median"] = Quantile(50), ["entropy"] = entropy, ["percentileLow"] = Quantile(low), ["percentileHigh"] = Quantile(high) };
            foreach (var (key, value) in stats) { outputs[key] = VisionValue.Double(value); summary[key] = value; }
            meanOut = mean; stdOut = Math.Sqrt(variance); lowOut = stats["percentileLow"]; highOut = stats["percentileHigh"];
        }
        var diag = CoordinateValidation.Diagnostics(Type, ("found", count > 0), ("pixelCount", count), ("lowPercent", low), ("highPercent", high),
            ("mean", meanOut), ("stdDev", stdOut), ("percentileLow", lowOut), ("percentileHigh", highOut),
            ("width", source.Width), ("height", source.Height));
        outputs["diagnostics"] = VisionValue.String(diag);
        summary["diagnostics"] = diag;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));

        double Quantile(double percent)
        {
            var target = Math.Max(1d, percent / 100 * count); long sum = 0;
            for (var i = 0; i < bins.Length; i++) { sum += bins[i]; if (sum >= target) return i; }
            throw new InvalidOperationException("Histogram percentile could not be resolved.");
        }
    }
}

public sealed class RegionFromRoiNode : IVisionNodeExecutor
{
    public string Type => "region.fromRoi";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var width = node.GetInt("canvasWidth", 512); var height = node.GetInt("canvasHeight", 384);
        if (context.TryGetValue("image", out var image)) { var source = image.Require<VisionImage>("image"); width = source.Width; height = source.Height; }
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > 64_000_000)
            throw new InvalidOperationException("ROI canvas must be 1..16384 per axis and at most 64000000 pixels.");
        var roi = context.TryGetValue("roi", out var input) ? input.Require<VisionRoi>("roi") : node.GetRoi();
        if (roi is null) throw new InvalidOperationException("Connect a ROI or configure a valid roi parameter.");
        // Native region generation clips to the most recently initialized image
        // extent. Establish this canvas before rasterizing so the first run does
        // not inherit a smaller image from another node on this worker thread.
        using var nativeCanvas = new JlImage("byte", width, height);
        using var native = Rasterize(roi, width, height, cancellationToken);
        using var bounds = new JlRegion(0d, 0d, (double)height - 1, (double)width - 1);
        using var clipped = native.Intersection(bounds);
        using var inverted = node.GetBool("invert", false) ? bounds.Difference(clipped) : null;
        return ValueTask.FromResult(RegionShapeProcessing.Result(inverted ?? clipped, width, height, cancellationToken));
    }
    private static JlRegion Rasterize(VisionRoi roi, int canvasWidth, int canvasHeight, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        static void Check(params double[] values)
        {
            if (values.Any(v => !double.IsFinite(v) || Math.Abs(v) > 32768)) throw new InvalidOperationException("ROI coordinates and sizes must be finite within +/-32768.");
        }
        var result = new JlRegion();
        try
        {
            var x = roi.X ?? 0; var y = roi.Y ?? 0;
            switch (roi.Type)
            {
                case VisionRoiType.Rectangle:
                    var w = roi.Width ?? 0; var h = roi.Height ?? 0; Check(x, y, w, h);
                    if (w < 0 || h < 0) throw new InvalidOperationException("ROI rectangle dimensions must be nonnegative.");
                    var left = Math.Ceiling(x); var top = Math.Ceiling(y); var right = Math.Ceiling(x + w) - 1; var bottom = Math.Ceiling(y + h) - 1;
                    if (right < left || bottom < top) result.GenEmptyRegion(); else result.GenRectangle1(top, left, bottom, right);
                    break;
                case VisionRoiType.Circle:
                    var radius = roi.Radius ?? 0; Check(x, y, radius);
                    if (radius < 0) throw new InvalidOperationException("ROI radius must be nonnegative.");
                    if (radius == 0) result.GenEmptyRegion(); else result.GenCircle(y, x, radius);
                    break;
                case VisionRoiType.Polygon:
                    var points = roi.Points ?? [];
                    if (points.Count is < 3 or > 1000 || points.Distinct().Count() < 3) throw new InvalidOperationException("ROI polygon requires 3..1000 points and at least three distinct vertices.");
                    foreach (var point in points)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Check(point.X, point.Y);
                    }
                    using (var rows = new JlTuple(points.Select(p => p.Y).ToArray()))
                    using (var cols = new JlTuple(points.Select(p => p.X).ToArray())) result.GenRegionPolygonFilled(rows, cols);
                    break;
                case VisionRoiType.Brush:
                {
                    using var mask = roi.BuildMask(new Size(canvasWidth, canvasHeight), cancellationToken);
                    var pixels = new byte[checked(canvasWidth * canvasHeight)];
                    Marshal.Copy(mask.Data, pixels, 0, pixels.Length);
                    var rows = new List<int>();
                    var starts = new List<int>();
                    var ends = new List<int>();
                    for (var row = 0; row < canvasHeight; row++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var column = 0;
                        while (column < canvasWidth)
                        {
                            while (column < canvasWidth && pixels[row * canvasWidth + column] == 0) column++;
                            if (column >= canvasWidth) break;
                            var start = column;
                            while (column < canvasWidth && pixels[row * canvasWidth + column] > 0) column++;
                            rows.Add(row); starts.Add(start); ends.Add(column - 1);
                            if (rows.Count > VisionRegion.MaxRuns)
                                throw new InvalidOperationException("Brush ROI exceeds the 1000000-run limit.");
                        }
                    }
                    if (rows.Count == 0) result.GenEmptyRegion();
                    else
                    {
                        using var nativeRows = new JlTuple(rows.ToArray());
                        using var nativeStarts = new JlTuple(starts.ToArray());
                        using var nativeEnds = new JlTuple(ends.ToArray());
                        result.GenRegionRuns(nativeRows, nativeStarts, nativeEnds);
                    }
                    break;
                }
                default: throw new InvalidOperationException("Unsupported ROI shape.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}

internal static class RegionShapeProcessing
{
    public static NodeExecutorResult Result(JlRegion native, int width, int height, CancellationToken token)
    {
        var result = RegionNodeHelpers.Result(JLVisionRegions.FromNative(native, width, height, token), token);
        return result with { Summary = new Dictionary<string, object?>(result.Summary) { ["algorithmBackend"] = "jlvision" } };
    }
    public static NodeExecutorResult Execute(string nodeType, NodeExecutionContext context, NodeDefinition node, string operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (operation is not ("hull" or "boundary" or "skeleton"))
            throw new InvalidOperationException($"{nodeType} unsupported region-shape operation '{operation}'.");
        var mode = operation == "boundary" ? node.GetString("boundaryMode", "inner").Trim().ToLowerInvariant() : null;
        if (operation == "boundary" && mode is not ("inner" or "outer"))
            throw new InvalidOperationException($"{nodeType} boundaryMode must be inner or outer.");
        var source = context.Require<VisionRegion>("region");
        if (source.Width < 1 || source.Height < 1)
            throw new InvalidOperationException($"{nodeType} requires a non-empty region canvas.");
        if (source.Width > 16384 || source.Height > 16384 || (long)source.Width * source.Height > 64_000_000)
            throw new InvalidOperationException($"{nodeType} region canvas must be at most 16384 per axis and 64000000 pixels.");
        if (source.Components.Count > 100_000)
            throw new InvalidOperationException($"{nodeType} refuses more than 100000 region objects.");
        // Reject runs that escape the canvas — corrupted upstream regions.
        foreach (var component in source.Components)
        {
            token.ThrowIfCancellationRequested();
            var runIndex = 0;
            foreach (var run in component.Runs)
            {
                if ((runIndex++ & 1023) == 0) token.ThrowIfCancellationRequested();
                if (run.Row < 0 || run.Row >= source.Height || run.StartColumn < 0 || run.EndColumn >= source.Width || run.StartColumn > run.EndColumn)
                    throw new InvalidOperationException($"{nodeType} region runs must lie inside the canvas with startColumn <= endColumn.");
            }
        }
        var unionFirst = node.GetBool("unionFirst", false);
        var inputObjectCount = source.Components.Count;
        var inputPixelArea = JLVisionRegions.PixelArea(source);
        using var native = JLVisionRegions.ToNative(source);
        using var input = unionFirst ? native.Union1() : native.CopyObj(1, -1);
        using var processed = operation switch { "hull" => input.ShapeTrans("convex"), "boundary" => input.Boundary(mode!), _ => input.Skeleton() };
        using var bounds = new JlRegion(0d, 0d, (double)source.Height - 1, (double)source.Width - 1);
        using var clipped = processed.Intersection(bounds);
        var result = Result(clipped, source.Width, source.Height, token);
        return RegionNodeHelpers.Complete(result, token, () =>
        {
            var region = result.Outputs["region"].Require<VisionRegion>("region");
            var metrics = RegionShapeMetrics.Compute(region, operation, token);
            var outputPixelArea = JLVisionRegions.PixelArea(region);
            var areaRatio = inputPixelArea > 0 ? (double)outputPixelArea / inputPixelArea : 0d;
            var area = result.Outputs.TryGetValue("area", out var areaValue) ? areaValue.Value : null;
            var count = result.Outputs.TryGetValue("count", out var countValue) ? countValue.Value : null;
            var found = result.Outputs.TryGetValue("found", out var foundValue) && foundValue.Value is true;
            var diag = CoordinateValidation.Diagnostics(nodeType,
                ("operation", operation), ("boundaryMode", mode), ("unionFirst", unionFirst),
                ("width", source.Width), ("height", source.Height),
                ("inputObjectCount", inputObjectCount), ("inputPixelArea", inputPixelArea),
                ("outputPixelArea", outputPixelArea), ("areaRatio", areaRatio),
                ("area", area), ("count", count), ("found", found),
                ("length", metrics.Length), ("endpoints", metrics.Endpoints), ("junctions", metrics.Junctions),
                ("vertexCount", metrics.VertexCount), ("emptyInput", inputPixelArea <= 0));
            var outputs = new Dictionary<string, VisionValue>(result.Outputs)
            {
                ["length"] = VisionValue.Double(metrics.Length),
                ["endpoints"] = VisionValue.Integer(metrics.Endpoints),
                ["junctions"] = VisionValue.Integer(metrics.Junctions),
                ["vertexCount"] = VisionValue.Integer(metrics.VertexCount),
                ["diagnostics"] = VisionValue.String(diag)
            };
            var summary = new Dictionary<string, object?>(result.Summary)
            {
                ["operation"] = operation, ["boundaryMode"] = mode, ["unionFirst"] = unionFirst,
                ["inputObjectCount"] = inputObjectCount, ["inputPixelArea"] = inputPixelArea,
                ["outputPixelArea"] = outputPixelArea, ["areaRatio"] = areaRatio,
                ["length"] = metrics.Length, ["endpoints"] = metrics.Endpoints,
                ["junctions"] = metrics.Junctions, ["vertexCount"] = metrics.VertexCount,
                ["emptyInput"] = inputPixelArea <= 0, ["diagnostics"] = diag
            };
            return result with { Outputs = outputs, Summary = summary };
        });
    }
}

internal static class RegionShapeMetrics
{
    public readonly record struct Metrics(double Length, long Endpoints, long Junctions, long VertexCount);

    public static Metrics Compute(VisionRegion region, string operation, CancellationToken token)
    {
        var pixelArea = JLVisionRegions.PixelArea(region);
        if (region.Components.Count == 0 || pixelArea <= 0)
            return new Metrics(0, 0, 0, 0);

        if (operation == "boundary")
            return new Metrics(pixelArea, 0, 0, 0);

        using var mask = JLVisionRegions.Mask(region);
        if (operation == "skeleton")
        {
            CountTopology(mask, out var endpoints, out var junctions, token);
            return new Metrics(pixelArea, endpoints, junctions, 0);
        }

        // convex hull: JLVision contour extraction, then plain polygon metrics.
        JLVisionOperators.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        long vertices = 0;
        double perimeter = 0;
        foreach (var contour in contours)
        {
            token.ThrowIfCancellationRequested();
            if (contour.Length < 1) continue;
            vertices += contour.Length;
            for (var i = 0; i < contour.Length; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var next = contour[(i + 1) % contour.Length];
                perimeter += MeasurementMath.Hypot(next.X - contour[i].X, next.Y - contour[i].Y);
            }
        }
        return new Metrics(perimeter, 0, 0, vertices);
    }

    static void CountTopology(Mat mask, out long endpoints, out long junctions, CancellationToken token)
    {
        endpoints = 0; junctions = 0;
        if (mask.Empty() || mask.Type() != MatType.CV_8UC1) return;
        var w = mask.Width; var h = mask.Height;
        byte At(int y, int x)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return 0;
            return mask.At<byte>(y, x);
        }
        for (var y = 0; y < h; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
            {
                if (At(y, x) == 0) continue;
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (At(y + dy, x + dx) != 0) n++;
                }
                if (n == 1) endpoints++;
                else if (n >= 3) junctions++;
            }
        }
    }
}

public sealed class RegionConvexHullNode : IVisionNodeExecutor
{
    public string Type => "region.convexHull";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1)
            throw new InvalidOperationException("region.convexHull requires positive canvas dimensions.");
        if (region.Components is null)
            throw new InvalidOperationException("region.convexHull region.Components must be non-null.");
        var result = RegionShapeProcessing.Execute(Type, context, node, "hull", cancellationToken);
        if (!result.Outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("region.convexHull diagnostics missing.");
        return ValueTask.FromResult(result);
    }
}
public sealed class RegionBoundaryNode : IVisionNodeExecutor
{
    public string Type => "region.boundary";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1)
            throw new InvalidOperationException("region.boundary requires positive canvas dimensions.");
        if (region.Components is null)
            throw new InvalidOperationException("region.boundary region.Components must be non-null.");
        var result = RegionShapeProcessing.Execute(Type, context, node, "boundary", cancellationToken);
        if (!result.Outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("region.boundary diagnostics missing.");
        return ValueTask.FromResult(result);
    }
}
public sealed class RegionSkeletonNode : IVisionNodeExecutor
{
    public string Type => "region.skeleton";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1)
            throw new InvalidOperationException("region.skeleton requires positive canvas dimensions.");
        if (region.Components is null)
            throw new InvalidOperationException("region.skeleton region.Components must be non-null.");
        var result = RegionShapeProcessing.Execute(Type, context, node, "skeleton", cancellationToken);
        if (!result.Outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("region.skeleton diagnostics missing.");
        return ValueTask.FromResult(result);
    }
}
