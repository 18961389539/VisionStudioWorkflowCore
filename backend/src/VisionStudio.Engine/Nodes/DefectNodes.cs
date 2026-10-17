using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using System.Text.Json;
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
        var scale = node.GetDouble("scale", 1);
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1000) throw new InvalidOperationException("Difference scale must be finite, greater than zero, and at most 1000.");
        var threshold = node.GetDouble("threshold", 0);
        if (!double.IsFinite(threshold) || threshold < 0) throw new InvalidOperationException("Difference threshold must be finite and non-negative.");
        using var image = DefectNodeHelpers.Gray(source, true); using var baseline = DefectNodeHelpers.Gray(reference, true);
        var mode = node.GetString("mode", "absolute").Trim().ToLowerInvariant();
        using var difference = mode switch
        {
            "absolute" => image.AbsDiffImage(baseline, scale),
            "signed" => image.SubImage(baseline, scale, 0d),
            // HALCON-style normalized difference: illumination-ratio change in [-1,1].
            "normalized" => Normalized(image, baseline),
            _ => throw new InvalidOperationException("Difference mode must be absolute, signed, or normalized.")
        };
        using var domain = difference.GetDomain(); var stats = DefectNodeHelpers.Statistics(difference, domain);
        cancellationToken.ThrowIfCancellationRequested();
        var outputs = new Dictionary<string, VisionValue>();
        var summary = new Dictionary<string, object?>
        {
            ["mode"] = mode, ["scale"] = scale, ["meanDifference"] = stats.Mean, ["stdDevDifference"] = stats.Deviation,
            ["minDifference"] = stats.Min, ["maxDifference"] = stats.Max, ["outputType"] = "real"
        };
        double? aboveFraction = null;
        VisionRegion? changedRegion = null;
        long changedPixels = 0;
        if (threshold > 0)
        {
            using var diffMat = JLVisionImages.ToMat(difference);
            diffMat.GetArray(out float[] pixels);
            var above = pixels.Count(v => Math.Abs(v) >= threshold);
            aboveFraction = pixels.Length == 0 ? 0 : (double)above / pixels.Length;
            summary["threshold"] = threshold; summary["aboveThresholdFraction"] = aboveFraction;
            // Thresholded change region (8-connected objects) so downstream region
            // tools can consume the difference directly instead of re-thresholding.
            var mask = pixels.Select(v => Math.Abs(v) >= threshold ? (byte)255 : (byte)0).ToArray();
            changedRegion = ImageDifferenceMetrics.RegionFromMask(mask, source.Width, source.Height);
            changedPixels = above;
            summary["changedPixelCount"] = changedPixels;
            summary["changedComponents"] = changedRegion.Components.Count;
        }
        // Global SSIM similarity summary on the grayscale inputs (whole-image
        // statistics; 1 = identical). Complements the pixel-difference statistics.
        double similarity;
        using (var currentGray = RecognitionImageHelpers.ToGray8(source.Mat))
        using (var referenceGray = RecognitionImageHelpers.ToGray8(reference.Mat))
        {
            currentGray.GetArray(out byte[] currentBytes);
            referenceGray.GetArray(out byte[] referenceBytes);
            similarity = ImageDifferenceMetrics.GlobalSsim(currentBytes, referenceBytes);
        }
        summary["similarity"] = similarity;
        var result = DefectNodeHelpers.ImageResult(difference, summary);
        foreach (var (key, value) in new Dictionary<string, VisionValue>
        {
            ["mean"] = VisionValue.Double(stats.Mean), ["stdDev"] = VisionValue.Double(stats.Deviation),
            ["min"] = VisionValue.Double(stats.Min), ["max"] = VisionValue.Double(stats.Max)
        }) outputs[key] = value;
        if (aboveFraction is not null) outputs["aboveThresholdFraction"] = VisionValue.Double(aboveFraction.Value);
        outputs["similarity"] = VisionValue.Double(similarity);
        if (changedRegion is not null)
        {
            outputs["region"] = VisionValue.Region(changedRegion);
            outputs["changedPixelCount"] = VisionValue.Integer(changedPixels);
        }
        var merged = new Dictionary<string, VisionValue>(result.Outputs);
        foreach (var pair in outputs) merged[pair.Key] = pair.Value;
        return ValueTask.FromResult(result with { Outputs = merged });
    }

    private static JlImage Normalized(JlImage image, JlImage baseline)
    {
        using var diff = image.SubImage(baseline, 1d, 0d);
        using var sum = image.AddImage(baseline, 1d, 1e-6); // epsilon avoids 0/0 on black pixels
        return diff.DivImage(sum, 1d, 0d);
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
        var maxArea = node.GetDouble("maxArea", 0);
        if (!double.IsFinite(maxArea) || maxArea < 0 || maxArea > 0 && maxArea < minArea) throw new InvalidOperationException("maxArea must be 0 (unbounded) or at least minArea.");
        var registrationTolerance = node.GetInt("registrationTolerance", 0);
        if (registrationTolerance is < 0 or > 25) throw new InvalidOperationException("registrationTolerance must be between 0 and 25 pixels.");
        var compensation = node.GetString("compensation", "none").Trim().ToLowerInvariant();
        if (compensation is not ("none" or "offset" or "gainoffset")) throw new InvalidOperationException("compensation must be none, offset, or gainOffset.");
        var openSize = node.GetInt("morphologyOpen", 0);
        var closeSize = node.GetInt("morphologyClose", 0);
        if (openSize is not (0 or 3 or 5 or 7 or 9 or 11 or 15)) throw new InvalidOperationException("morphologyOpen must be 0, 3, 5, 7, 9, 11, or 15.");
        if (closeSize is not (0 or 3 or 5 or 7 or 9 or 11 or 15)) throw new InvalidOperationException("morphologyClose must be 0, 3, 5, 7, 9, 11, or 15.");
        var variationK = node.GetDouble("variationK", 0);
        if (!double.IsFinite(variationK) || variationK < 0 || variationK > 20) throw new InvalidOperationException("variationK must be between 0 (off) and 20.");
        var variationWindow = node.GetInt("variationWindow", 15);
        if (variationWindow is < 3 or > 101 || variationWindow % 2 == 0) throw new InvalidOperationException("variationWindow must be odd and between 3 and 101.");
        var majorArea = node.GetDouble("majorArea", 500);
        var criticalArea = node.GetDouble("criticalArea", 5000);
        if (!double.IsFinite(majorArea) || !double.IsFinite(criticalArea) || majorArea < 0 || criticalArea < majorArea)
            throw new InvalidOperationException("Defect grading requires 0 <= majorArea <= criticalArea.");

        // Golden-template preparation (PatInspect / HALCON variation-model ideas):
        // exhaustive small-shift registration, then least-squares illumination
        // compensation of the reference toward the inspected image. Both are opt-in
        // (defaults keep the historical pixel-exact behaviour).
        VisionImage? preparedReference = null;
        byte[]? effectiveRefBytes = null; byte[]? sourceBytesCache = null;
        var shiftX = 0; var shiftY = 0; var gain = 1d; var offset = 0d;
        try
        {
            if (registrationTolerance > 0 || compensation != "none" || variationK > 0)
            {
                using var sourceGray = RecognitionImageHelpers.ToGray8(source.Mat);
                using var referenceGray = RecognitionImageHelpers.ToGray8(reference.Mat);
                sourceGray.GetArray(out byte[] imageBytes);
                referenceGray.GetArray(out byte[] referenceBytes);
                var width = source.Width; var height = source.Height;
                if (registrationTolerance > 0)
                {
                    var shift = ReferenceAlignment.SearchShift(imageBytes, referenceBytes, width, height, registrationTolerance);
                    shiftX = shift.ShiftX; shiftY = shift.ShiftY;
                }
                var aligned = new byte[imageBytes.Length];
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var rx = x - shiftX; var ry = y - shiftY;
                    aligned[y * width + x] = rx >= 0 && rx < width && ry >= 0 && ry < height ? referenceBytes[ry * width + rx] : imageBytes[y * width + x];
                }
                if (compensation != "none")
                {
                    var (fittedGain, fittedOffset) = ReferenceAlignment.FitGainOffset(imageBytes, aligned);
                    gain = compensation == "gainoffset" ? fittedGain : 1d;
                    offset = fittedOffset;
                    for (var i = 0; i < aligned.Length; i++)
                        aligned[i] = (byte)Math.Clamp(Math.Round(gain * aligned[i] + offset), 0, 255);
                }
                using var alignedMat = new Mat(height, width, MatType.CV_8UC1);
                alignedMat.SetArray(aligned);
                preparedReference = VisionImage.Own(alignedMat.Clone());
                effectiveRefBytes = aligned; sourceBytesCache = imageBytes;
            }
            var effectiveReference = preparedReference ?? reference;
            using var image = DefectNodeHelpers.Gray(source, true); using var baseline = DefectNodeHelpers.Gray(effectiveReference, true);
            using var domain = DefectNodeHelpers.Domain(image, source.Width, source.Height, scoped, context);
            using var difference = node.GetString("polarity", "both") switch
            {
                "both" => image.AbsDiffImage(baseline, 1d), "bright" => image.SubImage(baseline, 1d, 0d), "dark" => baseline.SubImage(image, 1d, 0d),
                _ => throw new InvalidOperationException("Invalid defect polarity.")
            };
            if (domain.AreaCenter(out double _, out double _) > 0) _ = DefectNodeHelpers.Statistics(difference, domain);
            using var limited = difference.ReduceDomain(domain);
            using var detected = variationK > 0
                ? VariationThreshold(source, effectiveRefBytes!, sourceBytesCache!, domain, threshold, variationK, variationWindow, node.GetString("polarity", "both"), cancellationToken)
                : limited.Threshold(threshold, (double)float.MaxValue);
            using var cleaned = Clean(detected, openSize, closeSize);
            using var connected = cleaned.Connection();
            var upperArea = maxArea > 0 ? maxArea : Math.Max(minArea, (double)source.Width * source.Height);
            using var filtered = connected.SelectShape("area", "and", minArea, upperArea);
            var regionResult = JLVisionRegions.FromNative(filtered, source.Width, source.Height, cancellationToken);
            var result = RegionNodeHelpers.Result(regionResult, cancellationToken);

            // Per-defect grading from the actual region objects (area + bounding box).
            var defects = regionResult.Components.Select((component, index) =>
            {
                var area = component.Runs.Sum(r => (long)r.EndColumn - r.StartColumn + 1);
                var x = component.Runs.Min(r => r.StartColumn); var y = component.Runs.Min(r => r.Row);
                return new
                {
                    index, area,
                    x, y,
                    width = component.Runs.Max(r => r.EndColumn) - x + 1,
                    height = component.Runs.Max(r => r.Row) - y + 1,
                    grade = area >= criticalArea ? "critical" : area >= majorArea ? "major" : "minor"
                };
            }).ToArray();
            var maxDefectArea = defects.Length == 0 ? 0d : defects.Max(d => (double)d.area);
            var grade = defects.Length == 0 ? "none" : defects.Any(d => d.grade == "critical") ? "critical" : defects.Any(d => d.grade == "major") ? "major" : "minor";
            var outputs = new Dictionary<string, VisionValue>(result.Outputs)
            {
                ["defects"] = VisionValue.String(JsonSerializer.Serialize(defects)),
                ["maxDefectArea"] = VisionValue.Double(maxDefectArea),
                ["grade"] = VisionValue.String(grade)
            };
            return ValueTask.FromResult(result with
            {
                Outputs = outputs,
                Summary = new Dictionary<string, object?>(result.Summary)
                {
                    ["threshold"] = threshold, ["minArea"] = minArea, ["maxArea"] = maxArea,
                    ["polarity"] = node.GetString("polarity", "both"),
                    ["registrationTolerance"] = registrationTolerance, ["shiftX"] = shiftX, ["shiftY"] = shiftY,
                    ["compensation"] = compensation, ["gain"] = Math.Round(gain, 6), ["offset"] = Math.Round(offset, 4),
                    ["morphologyOpen"] = openSize, ["morphologyClose"] = closeSize,
                    ["variationK"] = variationK, ["variationWindow"] = variationWindow,
                    ["grade"] = grade, ["maxDefectArea"] = maxDefectArea
                }
            });
        }
        finally { preparedReference?.Dispose(); }
    }

    /// <summary>
    /// Variation-model thresholding: a pixel is a defect when its absolute difference
    /// reaches threshold + variationK * local reference standard deviation. Textured
    /// reference areas therefore tolerate more change than flat ones (HALCON
    /// compare_variation_model with a locally estimated variation image; a true
    /// multi-sample model would need multiple reference inputs, which this node's
    /// single reference port cannot supply - that remains a documented limit).
    /// </summary>
    private static JlRegion VariationThreshold(VisionImage source, byte[] referenceBytes, byte[] sourceBytes, JlRegion domain, double threshold, double variationK, int variationWindow, string polarity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var width = source.Width; var height = source.Height;
        var localStd = ReferenceAlignment.LocalStdDev(referenceBytes, width, height, variationWindow);
        var mask = new byte[width * height];
        for (var i = 0; i < mask.Length; i++)
        {
            var diff = polarity switch
            {
                "bright" => (double)sourceBytes[i] - referenceBytes[i],
                "dark" => (double)referenceBytes[i] - sourceBytes[i],
                "both" => Math.Abs((double)sourceBytes[i] - referenceBytes[i]),
                _ => throw new InvalidOperationException("Invalid defect polarity.")
            };
            mask[i] = diff >= threshold + variationK * localStd[i] ? (byte)255 : (byte)0;
        }
        using var maskMat = new Mat(height, width, MatType.CV_8UC1);
        maskMat.SetArray(mask);
        using var maskImage = JLVisionImages.FromMat(maskMat);
        using var maskRegion = maskImage.Threshold(1d, 255d);
        return maskRegion.Intersection(domain);
    }

    private static JlRegion Clean(JlRegion detected, int openSize, int closeSize)
    {
        JlRegion current = detected.CopyObj(1, -1);
        try
        {
            if (openSize > 0) { var next = current.OpeningRectangle1(openSize, openSize); current.Dispose(); current = next; }
            if (closeSize > 0) { var next = current.ClosingRectangle1(closeSize, closeSize); current.Dispose(); current = next; }
            return current;
        }
        catch { current.Dispose(); throw; }
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
