using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

internal static class DefectNodeHelpers
{
    public static JlImage Gray(VisionImage source, bool real = false)
    {
        if (source.Mat.Depth() == MatType.CV_32F)
            for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
                if (!float.IsFinite(source.Mat.At<float>(y, x))) throw new InvalidOperationException("Defect processing requires finite image pixels.");
        using var native = JLVisionImages.FromMat(source.Mat);
        using var channels = native.CountChannels();
        using var gray = channels.I == 1 ? native.CopyImage() : native.Rgb1ToGray();
        return real ? gray.ConvertImageType("real") : gray.CopyImage();
    }

    public static void SameSize(VisionImage a, VisionImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new InvalidOperationException("Image/reference dimensions must match; align images before comparing.");
    }

    public static JlRegion Domain(JlImage image, int width, int height, VisionRegion? domain, NodeExecutionContext context)
    {
        var result = image.GetDomain();
        try
        {
            VisionRegion? supplied = context.TryGetValue("region", out var value) ? value.Require<VisionRegion>("region") : null;
            foreach (var region in new[] { domain, supplied })
            {
                if (region is null) continue;
                if (region.Width != width || region.Height != height) throw new InvalidOperationException("Region/image dimensions must match.");
                using var native = JLVisionRegions.ToNative(region); using var union = native.Union1();
                var intersection = result.Intersection(union); result.Dispose(); result = intersection;
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static (double Mean, double Deviation, double Min, double Max) Statistics(JlImage image, JlRegion domain)
    {
        var mean = image.Intensity(domain, out double deviation);
        image.MinMaxGray(domain, 0d, out double min, out double max, out double _);
        if (!double.IsFinite(mean) || !double.IsFinite(deviation) || !double.IsFinite(min) || !double.IsFinite(max))
            throw new InvalidOperationException("Image arithmetic produced non-finite statistics.");
        return (mean, deviation, min, max);
    }

    public static NodeExecutorResult ImageResult(JlImage image, Dictionary<string, object?> summary)
        => new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(JLVisionImages.ToMat(image))) }, summary);

    public static double Positive(NodeDefinition node, string key, double fallback)
    {
        var value = node.GetDouble(key, fallback);
        if (!double.IsFinite(value) || value <= 0) throw new InvalidOperationException($"{key} must be finite and greater than zero.");
        return value;
    }
}

public sealed class FlatFieldCorrectNode : IVisionNodeExecutor
{
    public string Type => "image.flatFieldCorrect";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.Require<VisionImage>("image"); var dark = context.Require<VisionImage>("dark"); var bright = context.Require<VisionImage>("bright");
        DefectNodeHelpers.SameSize(source, dark); DefectNodeHelpers.SameSize(source, bright);
        var minGain = DefectNodeHelpers.Positive(node, "minGain", 1);
        var target = node.GetDouble("targetMean", 0);
        if (!double.IsFinite(target) || target < 0) throw new InvalidOperationException("targetMean must be finite and non-negative.");
        var outputType = node.GetString("outputType", "byte");
        if (outputType is not ("byte" or "real")) throw new InvalidOperationException("outputType must be byte or real.");
        using var image = DefectNodeHelpers.Gray(source, true); using var darkImage = DefectNodeHelpers.Gray(dark, true); using var brightImage = DefectNodeHelpers.Gray(bright, true);
        using var gain = brightImage.SubImage(darkImage, 1d, 0d); using var domain = gain.GetDomain();
        var stats = DefectNodeHelpers.Statistics(gain, domain);
        if (stats.Min <= minGain) throw new InvalidOperationException($"Flat-field bright-dark gain must exceed minGain ({minGain}) at every pixel; minimum is {stats.Min}.");
        if (target == 0) target = stats.Mean;
        using var signal = image.SubImage(darkImage, 1d, 0d); using var corrected = signal.DivImage(gain, target, 0d);
        _ = DefectNodeHelpers.Statistics(corrected, domain);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = corrected.ConvertImageType(outputType);
        return ValueTask.FromResult(DefectNodeHelpers.ImageResult(output, new() { ["targetMean"] = target, ["minGain"] = stats.Min, ["outputType"] = outputType }));
    }
}

public sealed class DynamicThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.dynamicThreshold";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (source, scoped) = RegionNodeHelpers.Source(context);
        var offset = DefectNodeHelpers.Positive(node, "offset", 10);
        var polarity = node.GetString("polarity", "both");
        if (polarity is not ("bright" or "dark" or "both")) throw new InvalidOperationException("Invalid defect polarity.");
        var window = node.GetInt("windowSize", 31);
        if (window is < 3 or > 255) throw new InvalidOperationException("windowSize must be between 3 and 255.");
        if (window % 2 == 0) window++;
        using var image = DefectNodeHelpers.Gray(source, true);
        using var background = Background(context, source, image, window);
        using var domain = DefectNodeHelpers.Domain(image, source.Width, source.Height, scoped, context);
        using var limited = image.ReduceDomain(domain);
        using var defects = Find(limited, background, offset, polarity);
        var region = JLVisionRegions.FromNative(defects, source.Width, source.Height, cancellationToken);
        var result = RegionNodeHelpers.Result(region, cancellationToken);
        return ValueTask.FromResult(result with { Summary = new Dictionary<string, object?>(result.Summary) { ["offset"] = offset, ["polarity"] = polarity, ["backgroundSource"] = context.Inputs.ContainsKey("background") ? "input" : "localMean", ["windowSize"] = window } });
    }
    private static JlImage Background(NodeExecutionContext context, VisionImage source, JlImage image, int window)
    {
        if (!context.TryGetValue("background", out var value)) return image.MeanImage(window, window);
        var background = value.Require<VisionImage>("background"); DefectNodeHelpers.SameSize(source, background);
        return DefectNodeHelpers.Gray(background, true);
    }
    private static JlRegion Find(JlImage image, JlImage background, double offset, string polarity)
    {
        if (polarity != "both") return image.DynThreshold(background, offset, polarity == "bright" ? "light" : "dark");
        using var bright = image.DynThreshold(background, offset, "light"); using var dark = image.DynThreshold(background, offset, "dark");
        return bright.Union2(dark);
    }
}

public sealed class ImageDifferenceNode : IVisionNodeExecutor
{
    public string Type => "image.difference";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.Require<VisionImage>("image"); var reference = context.Require<VisionImage>("reference");
        DefectNodeHelpers.SameSize(source, reference);
        using var image = DefectNodeHelpers.Gray(source, true); using var baseline = DefectNodeHelpers.Gray(reference, true);
        var mode = node.GetString("mode", "absolute");
        using var difference = mode switch { "absolute" => image.AbsDiffImage(baseline, 1d), "signed" => image.SubImage(baseline, 1d, 0d), _ => throw new InvalidOperationException("Difference mode must be absolute or signed.") };
        using var domain = difference.GetDomain(); var stats = DefectNodeHelpers.Statistics(difference, domain);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DefectNodeHelpers.ImageResult(difference, new() { ["mode"] = mode, ["meanDifference"] = stats.Mean, ["minDifference"] = stats.Min, ["maxDifference"] = stats.Max, ["outputType"] = "real" }));
    }
}

public sealed class ReferenceCompareNode : IVisionNodeExecutor
{
    public string Type => "defect.referenceCompare";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (source, scoped) = RegionNodeHelpers.Source(context); var reference = context.Require<VisionImage>("reference");
        DefectNodeHelpers.SameSize(source, reference);
        var threshold = DefectNodeHelpers.Positive(node, "threshold", 15);
        var minArea = node.GetDouble("minArea", 5);
        if (!double.IsFinite(minArea) || minArea < 0) throw new InvalidOperationException("minArea must be finite and non-negative.");
        using var image = DefectNodeHelpers.Gray(source, true); using var baseline = DefectNodeHelpers.Gray(reference, true);
        using var domain = DefectNodeHelpers.Domain(image, source.Width, source.Height, scoped, context);
        using var difference = node.GetString("polarity", "both") switch
        {
            "both" => image.AbsDiffImage(baseline, 1d), "bright" => image.SubImage(baseline, 1d, 0d), "dark" => baseline.SubImage(image, 1d, 0d),
            _ => throw new InvalidOperationException("Invalid defect polarity.")
        };
        if (domain.AreaCenter(out double _, out double _) > 0) _ = DefectNodeHelpers.Statistics(difference, domain);
        using var limited = difference.ReduceDomain(domain); using var detected = limited.Threshold(threshold, (double)float.MaxValue);
        using var connected = detected.Connection(); using var filtered = connected.SelectShape("area", "and", minArea, Math.Max(minArea, (double)source.Width * source.Height));
        var result = RegionNodeHelpers.Result(JLVisionRegions.FromNative(filtered, source.Width, source.Height, cancellationToken), cancellationToken);
        return ValueTask.FromResult(result with { Summary = new Dictionary<string, object?>(result.Summary) { ["threshold"] = threshold, ["minArea"] = minArea, ["polarity"] = node.GetString("polarity", "both") } });
    }
}

public sealed class RegionIntensityNode : IVisionNodeExecutor
{
    public string Type => "measure.regionIntensity";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (source, scoped) = RegionNodeHelpers.Source(context);
        _ = context.Require<VisionRegion>("region");
        using var image = DefectNodeHelpers.Gray(source, true); using var domain = DefectNodeHelpers.Domain(image, source.Width, source.Height, scoped, context);
        var result = RegionNodeHelpers.Result(JLVisionRegions.FromNative(domain, source.Width, source.Height, cancellationToken), cancellationToken);
        if (!result.Outputs["found"].Require<bool>("found")) return ValueTask.FromResult(result);
        try
        {
            var stats = DefectNodeHelpers.Statistics(image, domain);
            var values = new Dictionary<string, double> { ["mean"] = stats.Mean, ["stdDev"] = stats.Deviation, ["min"] = stats.Min, ["max"] = stats.Max, ["range"] = stats.Max - stats.Min };
            var outputs = new Dictionary<string, VisionValue>(result.Outputs); var summary = new Dictionary<string, object?>(result.Summary);
            foreach (var (key, value) in values) { outputs[key] = VisionValue.Double(value); summary[key] = value; }
            return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
        }
        catch { result.Outputs["image"].Require<VisionImage>("image").Dispose(); throw; }
    }
}
