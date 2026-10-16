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
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string operation, CancellationToken token, bool applyRoi = false)
    {
        token.ThrowIfCancellationRequested();
        var width = node.GetInt("kernelWidth", 15); var height = node.GetInt("kernelHeight", 15);
        if (width is < 1 or > 255 || height is < 1 or > 255 || width % 2 == 0 || height % 2 == 0)
            throw new InvalidOperationException("Gray morphology kernel dimensions must be odd in 1..255.");
        using var gray = ColorChannels.Gray(context.RequireImage("image"));
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
        outputMat = PreprocessImageHelpers.ApplyRoiMask(outputMat, applyRoi ? node.GetRoi() : null);
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(outputMat)) },
            new Dictionary<string, object?> { ["operation"] = operation, ["kernelWidth"] = width, ["kernelHeight"] = height, ["algorithmBackend"] = "JLVision" });
    }
}

public sealed class ImageTopHatNode : IVisionNodeExecutor
{
    public string Type => "image.topHat";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(GrayMorphology.Execute(context, node, "topHat", cancellationToken));
}
public sealed class ImageBottomHatNode : IVisionNodeExecutor
{
    public string Type => "image.bottomHat";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(GrayMorphology.Execute(context, node, "bottomHat", cancellationToken));
}
public sealed class ImageMorphGradientNode : IVisionNodeExecutor
{
    public string Type => "image.morphGradient";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(GrayMorphology.Execute(context, node, "gradient", cancellationToken));
}

public sealed class GrayHistogramNode : IVisionNodeExecutor
{
    public string Type => "measure.grayHistogram";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var low = node.GetDouble("lowPercent", 5); var high = node.GetDouble("highPercent", 95);
        if (!double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high > 100 || low > high)
            throw new InvalidOperationException("Histogram percentiles must satisfy 0 <= lowPercent <= highPercent <= 100.");
        var (source, scoped) = RegionNodeHelpers.Source(context);
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
        var summary = new Dictionary<string, object?> { ["found"] = count > 0, ["pixelCount"] = count, ["algorithmBackend"] = "JLVision" };
        if (count > 0)
        {
            var mean = bins.Select((v, i) => (double)v * i).Sum() / count;
            var variance = bins.Select((v, i) => v * (i - mean) * (i - mean)).Sum() / count;
            var entropy = -bins.Where(v => v > 0).Sum(v => { var p = (double)v / count; return p * Math.Log2(p); });
            var stats = new Dictionary<string, double> { ["mean"] = mean, ["stdDev"] = Math.Sqrt(variance), ["minGray"] = Quantile(0), ["maxGray"] = Quantile(100),
                ["median"] = Quantile(50), ["entropy"] = entropy, ["percentileLow"] = Quantile(low), ["percentileHigh"] = Quantile(high) };
            foreach (var (key, value) in stats) { outputs[key] = VisionValue.Double(value); summary[key] = value; }
        }
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
        var width = node.GetInt("canvasWidth", 512); var height = node.GetInt("canvasHeight", 384);
        if (context.TryGetValue("image", out var image)) { var source = image.Require<VisionImage>("image"); width = source.Width; height = source.Height; }
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > 64_000_000)
            throw new InvalidOperationException("ROI canvas must be 1..16384 per axis and at most 64000000 pixels.");
        var roi = context.TryGetValue("roi", out var input) ? input.Require<VisionRoi>("roi") : node.GetRoi();
        if (roi is null) throw new InvalidOperationException("Connect a ROI or configure a valid roi parameter.");
        using var native = Rasterize(roi, width, height);
        using var bounds = new JlRegion(0d, 0d, (double)height - 1, (double)width - 1);
        using var clipped = native.Intersection(bounds);
        return ValueTask.FromResult(RegionShapeProcessing.Result(clipped, width, height, cancellationToken));
    }
    private static JlRegion Rasterize(VisionRoi roi, int canvasWidth, int canvasHeight)
    {
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
                    foreach (var point in points) Check(point.X, point.Y);
                    using (var rows = new JlTuple(points.Select(p => p.Y).ToArray()))
                    using (var cols = new JlTuple(points.Select(p => p.X).ToArray())) result.GenRegionPolygonFilled(rows, cols);
                    break;
                case VisionRoiType.Brush:
                {
                    using var mask = roi.BuildMask(new Size(canvasWidth, canvasHeight));
                    var pixels = new byte[checked(canvasWidth * canvasHeight)];
                    Marshal.Copy(mask.Data, pixels, 0, pixels.Length);
                    var rows = new List<int>();
                    var starts = new List<int>();
                    var ends = new List<int>();
                    for (var row = 0; row < canvasHeight; row++)
                    {
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
        return result with { Summary = new Dictionary<string, object?>(result.Summary) { ["algorithmBackend"] = "JLVision" } };
    }
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mode = node.GetString("boundaryMode", "inner");
        if (operation == "boundary" && mode is not ("inner" or "outer")) throw new InvalidOperationException("Boundary side must be inner or outer.");
        var source = context.Require<VisionRegion>("region");
        using var native = JLVisionRegions.ToNative(source);
        using var input = node.GetBool("unionFirst", false) ? native.Union1() : native.CopyObj(1, -1);
        using var processed = operation switch { "hull" => input.ShapeTrans("convex"), "boundary" => input.Boundary(mode), _ => input.Skeleton() };
        using var bounds = new JlRegion(0d, 0d, (double)source.Height - 1, (double)source.Width - 1);
        using var clipped = processed.Intersection(bounds);
        return Result(clipped, source.Width, source.Height, token);
    }
}
public sealed class RegionConvexHullNode : IVisionNodeExecutor
{
    public string Type => "region.convexHull";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(RegionShapeProcessing.Execute(context, node, "hull", cancellationToken));
}
public sealed class RegionBoundaryNode : IVisionNodeExecutor
{
    public string Type => "region.boundary";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(RegionShapeProcessing.Execute(context, node, "boundary", cancellationToken));
}
public sealed class RegionSkeletonNode : IVisionNodeExecutor
{
    public string Type => "region.skeleton";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(RegionShapeProcessing.Execute(context, node, "skeleton", cancellationToken));
}
