using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using System.Text.Json;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

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

    public static void SameGrayDepth(VisionImage a, VisionImage b)
    {
        if (a.Mat.Depth() != b.Mat.Depth()) throw new InvalidOperationException("Image and reference grayscale intensity depths must match.");
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

    public static void AttachByteStats(Dictionary<string, VisionValue> outputs, Dictionary<string, object?> summary, Mat mat, string nodeType, params (string Key, object? Value)[] extra)
    {
        if (mat.Empty()) throw new InvalidOperationException($"{nodeType} produced an empty image.");
        JLVisionOperators.MinMax(mat, out double minIntensity, out double maxIntensity);
        var meanIntensity = JLVisionOperators.Mean(mat, out double stdIntensity);
        var nonZeroCount = JLVisionOperators.CountNonZero(mat);
        outputs["meanIntensity"] = VisionValue.Double(meanIntensity);
        outputs["minIntensity"] = VisionValue.Double(minIntensity);
        outputs["maxIntensity"] = VisionValue.Double(maxIntensity);
        summary["meanIntensity"] = meanIntensity;
        summary["minIntensity"] = minIntensity;
        summary["maxIntensity"] = maxIntensity;
        summary["stdIntensity"] = stdIntensity;
        summary["nonZeroCount"] = nonZeroCount;
        var fields = new List<(string, object?)> { ("meanIntensity", meanIntensity), ("minIntensity", minIntensity), ("maxIntensity", maxIntensity), ("stdIntensity", stdIntensity), ("nonZeroCount", nonZeroCount), ("width", mat.Width), ("height", mat.Height) };
        fields.AddRange(extra);
        var diag = CoordinateValidation.Diagnostics(nodeType, fields.ToArray());
        outputs["diagnostics"] = VisionValue.String(diag);
        summary["diagnostics"] = diag;
    }

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
        => FlatFieldCorrection.ExecuteAsync(context, node, cancellationToken);
}

public sealed class DynamicThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.dynamicThreshold";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

var (source, scoped) = RegionNodeHelpers.Source(context);
        if (source.Width < 1 || source.Height < 1) throw new InvalidOperationException("image.dynamicThreshold requires a non-empty image.");
        var offset = DefectNodeHelpers.Positive(node, "offset", 10);
        var polarity = node.GetString("polarity", "both").Trim().ToLowerInvariant();
        if (polarity is not ("bright" or "dark" or "both")) throw new InvalidOperationException("image.dynamicThreshold polarity must be bright, dark or both.");
        var window = ResultFileWriter.Integer(node, "windowSize", 31, 3, 255);
        if (window is < 3 or > 255) throw new InvalidOperationException("image.dynamicThreshold windowSize must be between 3 and 255.");
        if (window % 2 == 0) window++;
        var backgroundMode = ResultFileWriter.Choice(node, "backgroundMode", "mean", "mean", "median");
        var minimumArea = node.GetDouble("minArea", 0);
        if (!double.IsFinite(minimumArea) || minimumArea < 0) throw new InvalidOperationException("minArea must be finite and non-negative.");
        var bgSource = context.Inputs.ContainsKey("background") ? "input" : "localMean";
        using var image = DefectNodeHelpers.Gray(source, true);
        using var background = Background(context, source, image, window, backgroundMode);
        using var domain = DefectNodeHelpers.Domain(image, source.Width, source.Height, scoped, context);
        using var limited = image.ReduceDomain(domain);
        using var defects = Find(limited, background, offset, polarity);
        using var selected = node.GetBool("invert", false) ? domain.Difference(defects) : defects.Union1();
        using var connected = selected.Connection();
        using var filtered = connected.SelectShape("area", "and", minimumArea, Math.Max(minimumArea, (double)source.Width * source.Height));
        var region = JLVisionRegions.FromNative(filtered, source.Width, source.Height, cancellationToken);
        var result = RegionNodeHelpers.Result(region, cancellationToken);
        var area = result.Outputs.TryGetValue("area", out var a) ? a.Value : null;
        var count = result.Outputs.TryGetValue("count", out var c) ? c.Value : null;
        var diag = NodeCompletionGuards.BuildDiagnostics(Type, NodeCompletionGuards.DiagnosticsLevel(node), 0, 1, ("backgroundMode", backgroundMode), ("invert", node.GetBool("invert", false)), ("minArea", minimumArea), ("offset", offset), ("polarity", polarity), ("windowSize", window),
            ("backgroundSource", bgSource), ("area", area), ("count", count), ("width", source.Width), ("height", source.Height));
        var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
        var maskImage = outputs["image"].Require<VisionImage>("image");
        outputs["mask"] = VisionValue.Image(maskImage.CloneOwned());
        var summary = new Dictionary<string, object?>(result.Summary)
        {
            ["offset"] = offset, ["polarity"] = polarity, ["backgroundSource"] = bgSource, ["windowSize"] = window, ["diagnostics"] = diag
        };
        return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
    }
    private static JlImage Background(NodeExecutionContext context, VisionImage source, JlImage image, int window, string mode)
    {
        if (!context.TryGetValue("background", out var value)) return mode == "median" ? image.MedianRect(window, window) : image.MeanImage(window, window);
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
        var offset = node.GetDouble("offset", 0);
        if (!double.IsFinite(offset) || offset < -255 || offset > 255) throw new InvalidOperationException("Difference offset must be finite and in -255..255.");
        var outputType = node.GetString("outputType", "real").Trim().ToLowerInvariant();
        if (outputType is not ("real" or "byte")) throw new InvalidOperationException("outputType must be real or byte.");
        var mode = node.GetString("mode", node.GetString("diffMode", "absolute")).Trim().ToLowerInvariant();
        using var rawDifference = mode switch
        {
            "absolute" => image.AbsDiffImage(baseline, 1d),
            "signed" => image.SubImage(baseline, 1d, 0d),
            // HALCON-style normalized difference: illumination-ratio change in [-1,1].
            "normalized" => Normalized(image, baseline),
            _ => throw new InvalidOperationException("Difference mode must be absolute, signed, or normalized.")
        };
        using var scaledDifference = rawDifference.ScaleImage(scale, offset);
        using var difference = scaledDifference.ConvertImageType(outputType);
        using var domain = difference.GetDomain(); var stats = DefectNodeHelpers.Statistics(difference, domain);
        cancellationToken.ThrowIfCancellationRequested();
        var outputs = new Dictionary<string, VisionValue>();
        var summary = new Dictionary<string, object?>
        {
            ["mode"] = mode, ["scale"] = scale, ["meanDifference"] = stats.Mean, ["stdDevDifference"] = stats.Deviation,
            ["minDifference"] = stats.Min, ["maxDifference"] = stats.Max, ["outputType"] = outputType, ["offset"] = offset
        };
        double? aboveFraction = null;
        VisionRegion? changedRegion = null;
        long changedPixels = 0;
        if (threshold > 0)
        {
            using var diffMat = JLVisionImages.ToMat(difference);
            using var floatDifference = new Mat();
            diffMat.ConvertTo(floatDifference, MatType.CV_32FC1);
            floatDifference.GetArray(out float[] pixels);
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
        var diag = NodeCompletionGuards.BuildDiagnostics(Type, NodeCompletionGuards.DiagnosticsLevel(node), 0, 1, ("outputType", outputType), ("offset", offset), ("legacyClipIgnored", node.Parameters?.ContainsKey("clip") == true), ("mode", mode), ("scale", scale), ("threshold", threshold),
            ("mean", stats.Mean), ("stdDev", stats.Deviation), ("min", stats.Min), ("max", stats.Max),
            ("similarity", similarity), ("aboveThresholdFraction", aboveFraction), ("changedPixelCount", changedPixels),
            ("width", source.Width), ("height", source.Height));
        outputs["diagnostics"] = VisionValue.String(diag);
        summary["diagnostics"] = diag;
        var merged = new Dictionary<string, VisionValue>(result.Outputs);
        foreach (var pair in outputs) merged[pair.Key] = pair.Value;
        return ValueTask.FromResult(result with { Outputs = merged, Summary = summary });
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

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var (source, scoped) = RegionNodeHelpers.Source(context);
        var packagePath = node.GetString("packagePath", string.Empty).Trim();
        VisionImage reference;
        var referenceSamples = new List<VisionImage>();
        if (context.TryGetValue("reference", out _))
        {
            reference = context.Require<VisionImage>("reference");
            DefectNodeHelpers.SameSize(source, reference); DefectNodeHelpers.SameGrayDepth(source, reference);
            referenceSamples.Add(reference);
        }
        else if (packagePath.Length > 0)
        {
            var paths = VisionStudio.Engine.ExternalTrain.ExternalTrainPackage.ResolveRuntimePaths(packagePath).ReferencePaths;
            if (paths.Count == 0) throw new InvalidOperationException("defect.referenceCompare packagePath has no reference images.");
            foreach (var path in paths)
            {
                using var mat = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Unchanged);
                if (mat.Empty()) throw new InvalidOperationException($"defect.referenceCompare could not load package reference '{path}'.");
                var img = VisionImage.Own(mat.Clone());
                DefectNodeHelpers.SameSize(source, img); DefectNodeHelpers.SameGrayDepth(source, img);
                referenceSamples.Add(img);
            }
            reference = referenceSamples[0];
        }
        else
        {
            reference = context.Require<VisionImage>("reference");
            DefectNodeHelpers.SameSize(source, reference); DefectNodeHelpers.SameGrayDepth(source, reference);
            referenceSamples.Add(reference);
        }
        for (var sampleIndex = 2; sampleIndex <= 5; sampleIndex++)
        {
            if (!context.TryGetValue($"reference{sampleIndex}", out var referenceValue)) continue;
            var sample = referenceValue.Require<VisionImage>($"reference{sampleIndex}");
            DefectNodeHelpers.SameSize(source, sample); DefectNodeHelpers.SameGrayDepth(source, sample);
            referenceSamples.Add(sample);
        }
        var threshold = DefectNodeHelpers.Positive(node, "threshold", 15);
        var minArea = node.GetDouble("minArea", 5);
        if (!double.IsFinite(minArea) || minArea < 0) throw new InvalidOperationException("minArea must be finite and non-negative.");
        var maxArea = node.GetDouble("maxArea", 0);
        if (!double.IsFinite(maxArea) || maxArea < 0 || maxArea > 0 && maxArea < minArea) throw new InvalidOperationException("maxArea must be 0 (unbounded) or at least minArea.");
        var registrationTolerance = node.GetInt("registrationTolerance", 0);
        if (registrationTolerance is < 0 or > 25) throw new InvalidOperationException("registrationTolerance must be between 0 and 25 pixels.");
        var subpixelRegistration = node.GetBool("subpixelRegistration", false);
        if (subpixelRegistration && registrationTolerance == 0)
            throw new InvalidOperationException("subpixelRegistration requires registrationTolerance greater than zero.");
        var maxRegistrationError = node.GetDouble("maxRegistrationError", 0);
        if (!double.IsFinite(maxRegistrationError) || maxRegistrationError < 0 || maxRegistrationError > 65535)
            throw new InvalidOperationException("maxRegistrationError must be finite and between 0 and 65535.");
        if (maxRegistrationError > 0 && registrationTolerance == 0)
            throw new InvalidOperationException("maxRegistrationError requires registrationTolerance greater than zero.");
        var compensation = node.GetString("compensation", "none").Trim().ToLowerInvariant();
        if (compensation is not ("none" or "offset" or "gainoffset")) throw new InvalidOperationException("compensation must be none, offset, or gainOffset.");
        var polarity = node.GetString("polarity", "both").Trim().ToLowerInvariant();
        if (polarity is not ("both" or "bright" or "dark")) throw new InvalidOperationException("polarity must be both, bright, or dark.");
        var openSize = node.GetInt("morphologyOpen", 0);
        var closeSize = node.GetInt("morphologyClose", 0);
        if (openSize is not (0 or 3 or 5 or 7 or 9 or 11 or 13 or 15)) throw new InvalidOperationException("morphologyOpen must be 0, 3, 5, 7, 9, 11, 13, or 15.");
        if (closeSize is not (0 or 3 or 5 or 7 or 9 or 11 or 13 or 15)) throw new InvalidOperationException("morphologyClose must be 0, 3, 5, 7, 9, 11, 13, or 15.");
        var variationK = node.GetDouble("variationK", 0);
        if (!double.IsFinite(variationK) || variationK < 0 || variationK > 20) throw new InvalidOperationException("variationK must be between 0 (off) and 20.");
        var variationWindow = node.GetInt("variationWindow", 15);
        if (variationWindow is < 3 or > 101 || variationWindow % 2 == 0) throw new InvalidOperationException("variationWindow must be odd and between 3 and 101.");
        var majorArea = node.GetDouble("majorArea", 500);
        var criticalArea = node.GetDouble("criticalArea", 5000);
        if (!double.IsFinite(majorArea) || !double.IsFinite(criticalArea) || majorArea < 0 || criticalArea < majorArea)
            throw new InvalidOperationException("Defect grading requires 0 <= majorArea <= criticalArea.");

        // One reference preserves historical comparison behavior. Extra good
        // references build a per-pixel mean and sample-deviation model after optional
        // registration; ordinary image inputs keep the model portable with the graph.
        VisionImage? preparedReference = null;
        float[]? effectiveReferenceValues = null; float[]? sourceValuesCache = null; float[]? modelStdDev = null;
        var shiftX = 0d; var shiftY = 0d; var gain = 1d; var offset = 0d; double? registrationError = null;
        var referenceAlignmentErrors = new List<double>();
        try
        {
            using var image = DefectNodeHelpers.Gray(source, true);
            using var domain = DefectNodeHelpers.Domain(image, source.Width, source.Height, scoped, context);
            using var domainMaskImage = domain.RegionToBin(255, 0, source.Width, source.Height);
            using var domainMaskMat = JLVisionImages.ToMat(domainMaskImage);
            domainMaskMat.GetArray(out byte[] domainMask);
            var needsPreparedModel = referenceSamples.Count > 1 || registrationTolerance > 0 || compensation != "none" || variationK > 0;
            if (needsPreparedModel)
            {
                var width = source.Width; var height = source.Height; var pixelCount = checked(width * height);
                var sourceValues = GrayValues(image);
                using var firstReferenceGray = DefectNodeHelpers.Gray(referenceSamples[0], true);
                var referenceAnchor = GrayValues(firstReferenceGray);
                var mean = (float[])referenceAnchor.Clone();
                float[]? m2 = referenceSamples.Count > 1 ? new float[pixelCount] : null;
                int[]? sampleCounts = referenceSamples.Count > 1 ? Enumerable.Repeat(1, pixelCount).ToArray() : null;
                for (var sampleIndex = 1; sampleIndex < referenceSamples.Count; sampleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var sampleGray = DefectNodeHelpers.Gray(referenceSamples[sampleIndex], true);
                    var sampleValues = GrayValues(sampleGray);
                    if (compensation != "none")
                    {
                        var (sampleGain, sampleOffset) = ReferenceAlignment.FitGainOffset(referenceAnchor, sampleValues, domainMask, cancellationToken);
                        if (compensation != "gainoffset") sampleGain = 1d;
                        sampleValues = ReferenceAlignment.ApplyGainOffset(sampleValues, width, height, sampleGain, sampleOffset, cancellationToken);
                    }
                    var sampleShiftX = 0d; var sampleShiftY = 0d;
                    if (registrationTolerance > 0)
                    {
                        var sampleShift = ReferenceAlignment.SearchShift(referenceAnchor, sampleValues, width, height,
                            registrationTolerance, domainMask, cancellationToken, subpixelRegistration);
                        sampleShiftX = sampleShift.ShiftX; sampleShiftY = sampleShift.ShiftY;
                        referenceAlignmentErrors.Add(sampleShift.MeanAbsoluteDifference);
                        if (maxRegistrationError > 0 && sampleShift.MeanAbsoluteDifference > maxRegistrationError)
                            throw new InvalidOperationException($"reference{sampleIndex + 1} alignment error {sampleShift.MeanAbsoluteDifference:F3} exceeds maxRegistrationError {maxRegistrationError:F3}.");
                    }
                    // Keep shift-aware accumulation so out-of-frame model pixels stay excluded
                    // (WarpTranslate fill semantics differ from Welford skip-on-border).
                    ReferenceAlignment.AccumulateAlignedSample(mean, m2!, sampleCounts!, sampleValues, width, height,
                        sampleShiftX, sampleShiftY, cancellationToken);
                }
                if (m2 is not null) modelStdDev = ReferenceAlignment.SampleStdDev(m2, sampleCounts!, cancellationToken);
                if (registrationTolerance > 0)
                {
                    var registrationValues = sourceValues; var registrationScale = 1d;
                    if (compensation != "none")
                    {
                        var (fittedGain, fittedOffset) = ReferenceAlignment.FitGainOffset(sourceValues, mean, domainMask, cancellationToken);
                        registrationScale = compensation == "gainoffset" ? fittedGain : 1d;
                        if (Math.Abs(registrationScale) < 1e-9) registrationScale = 1d;
                        // Inverse illumination: (v - offset) / gain via JLVision ScaleImage.
                        registrationValues = ReferenceAlignment.ApplyGainOffset(
                            sourceValues, width, height, 1d / registrationScale, -fittedOffset / registrationScale, cancellationToken);
                    }
                    var shift = ReferenceAlignment.SearchShift(registrationValues, mean, width, height,
                        registrationTolerance, domainMask, cancellationToken, subpixelRegistration);
                    shiftX = shift.ShiftX; shiftY = shift.ShiftY;
                    registrationError = shift.MeanAbsoluteDifference * registrationScale;
                    if (maxRegistrationError > 0 && registrationError > maxRegistrationError)
                        throw new InvalidOperationException($"Source alignment error {registrationError:F3} exceeds maxRegistrationError {maxRegistrationError:F3}.");
                    // JLVision affine_trans_image_size for the model mean/std; out-of-frame
                    // pixels keep the live source (or zero std) so edges do not invent defects.
                    mean = ReferenceAlignment.WarpTranslate(mean, sourceValues, width, height, shiftX, shiftY, cancellationToken);
                    if (modelStdDev is not null)
                    {
                        var zeroFill = new float[pixelCount];
                        modelStdDev = ReferenceAlignment.WarpTranslate(modelStdDev, zeroFill, width, height, shiftX, shiftY, cancellationToken);
                    }
                }
                if (compensation != "none")
                {
                    var (fittedGain, fittedOffset) = ReferenceAlignment.FitGainOffset(sourceValues, mean, domainMask, cancellationToken);
                    gain = compensation == "gainoffset" ? fittedGain : 1d;
                    offset = fittedOffset;
                    mean = ReferenceAlignment.ApplyGainOffset(mean, width, height, gain, offset, cancellationToken);
                    if (modelStdDev is not null)
                        modelStdDev = ReferenceAlignment.ApplyGainOffset(modelStdDev, width, height, gain, 0d, cancellationToken);
                }
                using var alignedMat = new Mat(height, width, MatType.CV_32FC1);
                alignedMat.SetArray(mean);
                preparedReference = VisionImage.Own(alignedMat.Clone());
                effectiveReferenceValues = mean; sourceValuesCache = sourceValues;
            }
            var effectiveReference = preparedReference ?? reference;
            using var effectiveBaseline = DefectNodeHelpers.Gray(effectiveReference, true);
            using var difference = polarity switch
            {
                "both" => image.AbsDiffImage(effectiveBaseline, 1d), "bright" => image.SubImage(effectiveBaseline, 1d, 0d), "dark" => effectiveBaseline.SubImage(image, 1d, 0d),
                _ => throw new InvalidOperationException($"Unsupported defect polarity '{polarity}'.")
            };
            if (domain.AreaCenter(out double _, out double _) > 0) _ = DefectNodeHelpers.Statistics(difference, domain);
            using var limited = difference.ReduceDomain(domain);
            using var detected = variationK > 0
                ? VariationThreshold(effectiveReferenceValues!, sourceValuesCache!, modelStdDev, source.Width, source.Height, domain,
                    threshold, variationK, variationWindow, polarity, cancellationToken)
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
                ["referenceSampleCount"] = VisionValue.Integer(referenceSamples.Count),
                ["grade"] = VisionValue.String(grade)
            };
            return ValueTask.FromResult(result with
            {
                Outputs = outputs,
                Summary = new Dictionary<string, object?>(result.Summary)
                {
                    ["threshold"] = threshold, ["minArea"] = minArea, ["maxArea"] = maxArea,
                    ["polarity"] = polarity,
                    ["registrationTolerance"] = registrationTolerance, ["subpixelRegistration"] = subpixelRegistration,
                    ["shiftX"] = Math.Round(shiftX, 4), ["shiftY"] = Math.Round(shiftY, 4),
                    ["registrationMeanAbsoluteDifference"] = registrationError,
                    ["referenceSampleCount"] = referenceSamples.Count,
                    ["referenceSampleAlignmentErrors"] = referenceAlignmentErrors,
                    ["compensation"] = compensation, ["gain"] = Math.Round(gain, 6), ["offset"] = Math.Round(offset, 4),
                    ["morphologyOpen"] = openSize, ["morphologyClose"] = closeSize,
                    ["variationK"] = variationK, ["variationWindow"] = variationWindow,
                    ["grade"] = grade, ["maxDefectArea"] = maxDefectArea,
                    ["algorithmBackend"] = "hybrid",
                    ["registrationSearchBackend"] = "managed-mad",
                    ["registrationWarpBackend"] = "jlvision-affine",
                    ["illuminationFitBackend"] = "managed-least-squares",
                    ["illuminationApplyBackend"] = "jlvision-scaleimage",
                    ["differenceBackend"] = "jlvision",
                    ["variationModelBackend"] = variationK > 0
                        ? (referenceSamples.Count > 1 ? "managed-sample-stddev" : "managed-local-stddev")
                        : "off",
                    ["honestGaps"] = "No JLVision domain-masked MAD shift search, soft-weighted NCC registration, or learned variation-model API; multi-sample std-dev and local-texture variation remain managed."
                }
            });
        }
        finally { preparedReference?.Dispose(); }
    }

    /// <summary>
    /// A multi-sample model supplies learned per-pixel deviation. One reference keeps
    /// the local-texture estimate as a compatibility fallback.
    /// </summary>
    private static JlRegion VariationThreshold(float[] referenceValues, float[] sourceValues, float[]? modelStdDev,
        int width, int height, JlRegion domain, double threshold, double variationK, int variationWindow, string polarity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var variation = modelStdDev ?? ReferenceAlignment.LocalStdDev(referenceValues, width, height, variationWindow, token);
        var mask = new byte[width * height];
        for (var i = 0; i < mask.Length; i++)
        {
            if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            var diff = polarity switch
            {
                "bright" => (double)sourceValues[i] - referenceValues[i],
                "dark" => (double)referenceValues[i] - sourceValues[i],
                "both" => Math.Abs((double)sourceValues[i] - referenceValues[i]),
                _ => throw new InvalidOperationException("Invalid defect polarity.")
            };
            mask[i] = diff >= threshold + variationK * variation[i] ? (byte)255 : (byte)0;
        }
        using var maskMat = new Mat(height, width, MatType.CV_8UC1);
        maskMat.SetArray(mask);
        using var maskImage = JLVisionImages.FromMat(maskMat);
        using var maskRegion = maskImage.Threshold(1d, 255d);
        return maskRegion.Intersection(domain);
    }

    private static float[] GrayValues(JlImage image)
    {
        using var mat = JLVisionImages.ToMat(image);
        mat.GetArray(out float[] values);
        return values;
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

    private sealed record Settings(double Nominal, double Tolerance, double MinimumMean, double MaximumMean,
        double MinimumArea, double MaximumStd, double MaximumCv, string EmptyPolicy, string DiagnosticsLevel);

    internal static void ValidateParameters(NodeDefinition node) => _ = ReadSettings(node);

    private static Settings ReadSettings(NodeDefinition node)
    {
        double Number(string name, double maximum)
        {
            var value = node.Parameters?.ContainsKey(name) == true ? node.GetDouble(name, double.NaN) : 0;
            if (!double.IsFinite(value) || value < 0 || value > maximum)
                throw new InvalidOperationException($"measure.regionIntensity {name} must be finite and in 0..{maximum} (0 disables the gate).");
            return value;
        }
        var nominal = Number("meanNominal", 65535);
        var tolerance = Number("tolerance", 65535);
        var minimumMean = Number("minMean", 65535);
        var maximumMean = Number("maxMean", 65535);
        var minimumArea = Number("minArea", 1e9);
        var maximumStd = Number("maxStd", 65535);
        var maximumCv = Number("maxCv", 100);
        if (minimumMean > 0 && maximumMean > 0 && minimumMean > maximumMean)
            throw new InvalidOperationException("measure.regionIntensity minMean must not exceed maxMean when both limits are enabled.");
        if (nominal > 0 && tolerance <= 0)
            throw new InvalidOperationException("measure.regionIntensity tolerance must be > 0 when meanNominal is set.");
        if (nominal > 0 && (minimumMean > 0 && nominal + tolerance < minimumMean
            || maximumMean > 0 && nominal - tolerance > maximumMean))
            throw new InvalidOperationException("measure.regionIntensity nominal tolerance and mean limits must have an overlapping acceptance range.");
        // The explicit policy wins over the historical duplicate checkbox.
        var legacyPolicy = node.GetBool("requireNonEmpty", false) ? "fail" : "zeros";
        var emptyPolicy = ResultFileWriter.Choice(node, "emptyPolicy", legacyPolicy, "zeros", "fail");
        return new(nominal, tolerance, minimumMean, maximumMean, minimumArea, maximumStd, maximumCv,
            emptyPolicy, NodeCompletionGuards.DiagnosticsLevel(node));
    }

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();
        var settings = ReadSettings(node);
        var (source, scoped) = RegionNodeHelpers.Source(context);
        if (source.Mat.Empty()) throw new InvalidOperationException("measure.regionIntensity requires a non-empty image.");
        _ = context.Require<VisionRegion>("region");
        using var image = DefectNodeHelpers.Gray(source, true);
        using var domain = DefectNodeHelpers.Domain(image, source.Width, source.Height, scoped, context);
        var region = JLVisionRegions.FromNative(domain, source.Width, source.Height, cancellationToken);
        var pixelCount = checked((long)JLVisionRegions.PixelArea(region));
        var found = pixelCount > 0;
        if (!found && settings.EmptyPolicy == "fail")
            throw new InvalidOperationException("measure.regionIntensity effective region is empty and emptyPolicy=fail.");

        var stats = found ? DefectNodeHelpers.Statistics(image, domain) : (Mean: 0d, Deviation: 0d, Min: 0d, Max: 0d);
        // CV is undefined for a zero-mean signal with nonzero variation; never label that signal as CV=0.
        double? cv = !found || stats.Mean == 0 && stats.Deviation == 0 ? 0
            : stats.Mean == 0 ? null : stats.Deviation / Math.Abs(stats.Mean);
        if (cv is { } coefficient && !double.IsFinite(coefficient)) cv = null;
        var failedGates = new List<string>();
        if (!found) failedGates.Add("emptyRegion");
        else
        {
            if (settings.Nominal > 0 && Math.Abs(stats.Mean - settings.Nominal) > settings.Tolerance) failedGates.Add("meanNominal");
            if (settings.MinimumMean > 0 && stats.Mean < settings.MinimumMean) failedGates.Add("minMean");
            if (settings.MaximumMean > 0 && stats.Mean > settings.MaximumMean) failedGates.Add("maxMean");
            if (settings.MaximumStd > 0 && stats.Deviation > settings.MaximumStd) failedGates.Add("maxStd");
            if (settings.MaximumCv > 0 && (cv is null || cv > settings.MaximumCv)) failedGates.Add("maxCv");
        }
        if (settings.MinimumArea > 0 && pixelCount < settings.MinimumArea) failedGates.Add("minArea");
        var within = failedGates.Count == 0;
        var cvDefined = found && cv is not null;
        var diagnostics = NodeCompletionGuards.BuildDiagnostics(Type, settings.DiagnosticsLevel, found && !cvDefined ? 1 : 0, 1,
            ("found", found), ("withinTolerance", within), ("failedGates", failedGates), ("pixelCount", pixelCount),
            ("samplingDomain", "region-intersect-image-domain"), ("emptyPolicy", settings.EmptyPolicy),
            ("mean", stats.Mean), ("stdDev", stats.Deviation), ("min", stats.Min), ("max", stats.Max),
            ("range", stats.Max - stats.Min), ("cv", cv), ("cvDefined", cvDefined),
            ("meanNominal", settings.Nominal), ("tolerance", settings.Tolerance), ("minMean", settings.MinimumMean),
            ("maxMean", settings.MaximumMean), ("minArea", settings.MinimumArea), ("maxStd", settings.MaximumStd), ("maxCv", settings.MaximumCv),
            ("width", source.Width), ("height", source.Height));
        cancellationToken.ThrowIfCancellationRequested();
        var result = RegionNodeHelpers.Result(region, cancellationToken);
        try
        {
            var values = new Dictionary<string, double>
            {
                ["mean"] = stats.Mean, ["stdDev"] = stats.Deviation, ["min"] = stats.Min, ["max"] = stats.Max,
                ["range"] = stats.Max - stats.Min
            };
            if (cv is { } cvValue) values["cv"] = cvValue;
            var outputs = new Dictionary<string, VisionValue>(result.Outputs)
            {
                ["pixelCount"] = VisionValue.Integer(pixelCount), ["withinTolerance"] = VisionValue.Boolean(within),
                ["cvDefined"] = VisionValue.Boolean(cvDefined), ["warningCount"] = VisionValue.Integer(found && !cvDefined ? 1 : 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            };
            var summary = new Dictionary<string, object?>(result.Summary)
            {
                ["pixelCount"] = pixelCount, ["withinTolerance"] = within, ["cvDefined"] = cvDefined,
                ["meanNominal"] = settings.Nominal, ["failedGates"] = failedGates, ["diagnostics"] = diagnostics,
                ["samplingDomain"] = "region-intersect-image-domain", ["warningCount"] = found && !cvDefined ? 1 : 0
            };
            foreach (var (key, value) in values) { outputs[key] = VisionValue.Double(value); summary[key] = value; }
            return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
        }
        catch { result.Outputs["image"].Require<VisionImage>("image").Dispose(); throw; }
    }
}
