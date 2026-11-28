using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

public sealed class WhiteBalanceNode : IVisionNodeExecutor
{
    public string Type => "image.whiteBalance";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.whiteBalance requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U || sourceMat.Channels() != 3)
            throw new InvalidOperationException("image.whiteBalance requires an 8-bit 3-channel (BGR) image.");

        var mode = node.GetString("mode", "grayWorld").Trim().ToLowerInvariant();
        if (mode is not ("grayworld" or "whitepatch" or "manual"))
            throw new InvalidOperationException($"image.whiteBalance mode '{mode}' is not supported (grayWorld | whitePatch | manual).");
        var percentile = node.GetDouble("percentile", 99);
        if (!double.IsFinite(percentile) || percentile is < 50 or > 100)
            throw new InvalidOperationException("image.whiteBalance percentile must be within 50..100.");
        var preserve = node.GetBool("preserveBrightness", true);
        var roiMode = node.GetString("roiMode", "estimateAndApply").Trim().ToLowerInvariant();
        if (roiMode is not ("estimateandapply" or "estimateonly"))
            throw new InvalidOperationException("image.whiteBalance roiMode must be estimateAndApply or estimateOnly.");
        var minRoiPixels = node.GetInt("minRoiPixels", 16);
        if (minRoiPixels is < 1 or > 100000000)
            throw new InvalidOperationException("image.whiteBalance minRoiPixels must be within 1..100000000.");

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);
        var roiStats = PreprocessP1Diagnostics.Roi(mask, sourceMat.Size());
        if (roi is not null && roiStats.Count < minRoiPixels)
            throw new InvalidOperationException($"image.whiteBalance ROI has {roiStats.Count} pixels; at least {minRoiPixels} are required for stable gains.");

        var manualGains = mode == "manual"
            ? new[] { node.GetDouble("manualB", 1), node.GetDouble("manualG", 1), node.GetDouble("manualR", 1) }
            : Array.Empty<double>();
        var balanced = PreprocessEnhancementAlgorithms.WhiteBalance(sourceMat, mask, mode, percentile, preserve, manualGains, roiMode == "estimateandapply", cancellationToken);
        var means = balanced.Means;
        var gains = balanced.Gains;
        Mat? output = balanced.Image;
        try
        {
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    mode,
                    sourceMeans = new { b = Math.Round(means[0], 3), g = Math.Round(means[1], 3), r = Math.Round(means[2], 3) },
                    gains = new { b = Math.Round(gains[0], 4), g = Math.Round(gains[1], 4), r = Math.Round(gains[2], 4) },
                    preserveBrightness = preserve,
                    roiMode,
                    estimatePixels = roiStats.Count
                });
                var saturation = PreprocessP1Diagnostics.ByteSaturation(image.Mat, 255);
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("mode", mode),
                    ("gainB", gains[0]), ("gainG", gains[1]), ("gainR", gains[2]),
                    ("gainMin", gains.Min()), ("gainMax", gains.Max()),
                    ("gainClampCount", gains.Count(gain => gain <= .0100000001 || gain >= 19.999999999)),
                    ("zeroMeanChannels", means.Select((mean, index) => (mean, index)).Where(pair => pair.mean <= 0).Select(pair => pair.index).ToArray()),
                    ("preserveBrightness", preserve), ("roiApplied", roi is not null),
                    ("roiMode", roiMode), ("estimatePixels", roiStats.Count),
                    ("roiBounds", $"{roiStats.Bounds.X},{roiStats.Bounds.Y},{roiStats.Bounds.Width},{roiStats.Bounds.Height}"),
                    ("clippedHighPixels", saturation.High), ("clippedHighRatio", saturation.Ratio));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["gainR"] = VisionValue.Double(gains[2]),
                        ["gainG"] = VisionValue.Double(gains[1]),
                        ["gainB"] = VisionValue.Double(gains[0]),
                        ["mode"] = VisionValue.String(mode),
                        ["channelMeansJson"] = VisionValue.String(payload),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["mode"] = mode, ["gainR"] = gains[2], ["gainG"] = gains[1], ["gainB"] = gains[0],
                        ["preserveBrightness"] = preserve, ["roiApplied"] = roi is not null,
                        ["roiMode"] = roiMode, ["estimatePixels"] = roiStats.Count,
                        ["clippedHighRatio"] = saturation.Ratio,
                        ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            output?.Dispose();
        }
    }
}

public sealed class QualityMetricsNode : IVisionNodeExecutor
{
    public string Type => "image.qualityMetrics";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.qualityMetrics requires a non-empty image.");
        if (sourceMat.Depth() is not (MatType.CV_8U or MatType.CV_16U or MatType.CV_32F))
            throw new InvalidOperationException("image.qualityMetrics requires 8-bit, 16-bit or float32 input.");
        if (sourceMat.Channels() is not (1 or 3))
            throw new InvalidOperationException($"image.qualityMetrics supports 1 or 3 channel images; received {sourceMat.Channels()}.");

        var saturation = node.GetDouble("saturationThreshold", 254);
        var dark = node.GetDouble("darkThreshold", 1);
        if (!double.IsFinite(saturation) || saturation is < 1 or > 255)
            throw new InvalidOperationException("image.qualityMetrics saturationThreshold must be within 1..255.");
        if (!double.IsFinite(dark) || dark is < 0 or > 254)
            throw new InvalidOperationException("image.qualityMetrics darkThreshold must be within 0..254.");

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var roiMask = roi?.BuildMask(sourceMat.Size(), cancellationToken);
        var roiStats = PreprocessP1Diagnostics.Roi(roiMask, sourceMat.Size());
        if (roiStats.Count <= 0)
        {
            var emptyDiag = CoordinateValidation.Diagnostics(Type, ("found", false), ("reason", "emptyRoi"),
                ("roiBounds", $"{roiStats.Bounds.X},{roiStats.Bounds.Y},{roiStats.Bounds.Width},{roiStats.Bounds.Height}"),
                ("pixelCount", 0), ("roiClipped", true));
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["found"] = VisionValue.Boolean(false), ["pixelCount"] = VisionValue.Integer(0),
                    ["invalidPixelCount"] = VisionValue.Integer(0), ["diagnostics"] = VisionValue.String(emptyDiag)
                },
                new Dictionary<string, object?> { ["found"] = false, ["reason"] = "emptyRoi", ["pixelCount"] = 0, ["diagnostics"] = emptyDiag }));
        }

        Mat? prepared = null;
        Mat? finiteMask = null;
        var invalidPixels = 0;
        try
        {
            if (sourceMat.Depth() == MatType.CV_8U)
            {
                prepared = sourceMat;
            }
            else if (sourceMat.Depth() == MatType.CV_16U)
            {
                prepared = new Mat();
                sourceMat.ConvertTo(prepared, MatType.CV_8U, 1d / 257d, 0);
            }
            else
            {
                sourceMat.GetArray(out float[] values);
                var bytes = new byte[values.Length];
                var valid = new byte[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    if (!float.IsFinite(values[i])) { invalidPixels++; continue; }
                    valid[i] = 255;
                    bytes[i] = (byte)Math.Clamp((int)Math.Round(values[i]), 0, 255);
                }
                prepared = new Mat(sourceMat.Rows, sourceMat.Cols, MatType.CV_8UC(sourceMat.Channels()));
                prepared.SetArray(bytes);
                finiteMask = new Mat(sourceMat.Rows, sourceMat.Cols, MatType.CV_8UC1);
                finiteMask.SetArray(valid);
            }

            using var combinedMask = finiteMask is null ? null : CombineMasks(roiMask, finiteMask);
            var effectiveMask = combinedMask ?? roiMask;
            var metrics = PreprocessEnhancementAlgorithms.MeasureQuality(prepared, effectiveMask, saturation, dark);
            var (brightness, contrast, sharpness, noiseSigma, saturationRatio, darkRatio, entropy, pixelCount) = metrics;

            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = CoordinateValidation.Diagnostics(Type, ("brightness", brightness), ("contrast", contrast),
                ("sharpness", sharpness), ("noiseSigma", noiseSigma), ("saturationRatio", saturationRatio),
                ("darkRatio", darkRatio), ("entropy", entropy), ("pixelCount", pixelCount),
                ("inputDepth", sourceMat.Depth().ToString()), ("inputChannels", sourceMat.Channels()),
                ("grayRule", sourceMat.Channels() == 3 ? "BGR->BT.601 gray" : "single channel"),
                ("inputScaling", sourceMat.Depth() == MatType.CV_16U ? "UInt16*1/257 saturated to 8-bit" : sourceMat.Depth() == MatType.CV_32F ? "finite float32 rounded/clamped to 0..255" : "none"),
                ("invalidPixelCount", invalidPixels), ("roiClipped", roi is not null && (roiStats.Bounds.X > 0 || roiStats.Bounds.Y > 0 || roiStats.Bounds.Right < sourceMat.Width || roiStats.Bounds.Bottom < sourceMat.Height)),
                ("roiBounds", $"{roiStats.Bounds.X},{roiStats.Bounds.Y},{roiStats.Bounds.Width},{roiStats.Bounds.Height}"),
                ("contrastDefinition", "P95-P5"), ("sharpnessDefinition", "mean((SobelX3^2+SobelY3^2))"),
                ("noiseSigmaDefinition", "mean(abs(Laplacian3))/(sqrt(20)*sqrt(2/pi))"),
                ("ratioDenominator", "valid ROI pixels after NaN/Inf exclusion"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true),
                ["brightness"] = VisionValue.Double(brightness), ["contrast"] = VisionValue.Double(contrast),
                ["sharpness"] = VisionValue.Double(sharpness), ["noiseSigma"] = VisionValue.Double(noiseSigma),
                ["saturationRatio"] = VisionValue.Double(saturationRatio), ["darkRatio"] = VisionValue.Double(darkRatio),
                ["entropy"] = VisionValue.Double(entropy), ["pixelCount"] = VisionValue.Integer(pixelCount),
                ["invalidPixelCount"] = VisionValue.Integer(invalidPixels),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["brightness"] = brightness, ["contrast"] = contrast, ["sharpness"] = sharpness,
                ["noiseSigma"] = noiseSigma, ["saturationRatio"] = saturationRatio, ["darkRatio"] = darkRatio,
                ["entropy"] = entropy, ["pixelCount"] = pixelCount, ["invalidPixelCount"] = invalidPixels, ["roiApplied"] = roi is not null,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
        }
        finally
        {
            if (!ReferenceEquals(prepared, sourceMat)) prepared?.Dispose();
            finiteMask?.Dispose();
        }
    }

    private static Mat CombineMasks(Mat? first, Mat second)
    {
        if (first is null) return second.Clone();
        var combined = new Mat();
        Cv2.BitwiseAnd(first, second, combined);
        return combined;
    }
}

public sealed class DepthScaleNode : IVisionNodeExecutor
{
    public string Type => "image.depthScale";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.depthScale requires a non-empty image.");
        var sourceBits = sourceMat.Depth() switch
        {
            MatType.CV_8U => 8,
            MatType.CV_16U => 16,
            _ => throw new InvalidOperationException($"image.depthScale supports 8-bit and 16-bit integer images; received depth {sourceMat.Depth()}.")
        };

        var inputBits = node.GetInt("inputBits", 0);
        if (inputBits == 0) inputBits = sourceBits;
        if (inputBits is < 8 or > 16) throw new InvalidOperationException("image.depthScale inputBits must be 0 or within 8..16.");
        if (inputBits > 8 && sourceBits != 16)
            throw new InvalidOperationException($"image.depthScale inputBits={inputBits} requires a 16-bit container; the image is {sourceBits}-bit.");
        var alignment = node.GetString("alignment", "low").Trim().ToLowerInvariant();
        if (alignment is not ("low" or "high"))
            throw new InvalidOperationException("image.depthScale alignment must be low or high.");
        if (alignment == "high" && sourceBits != 16)
            throw new InvalidOperationException("image.depthScale alignment=high requires a 16-bit container.");
        var rightShift = alignment == "high" ? sourceBits - inputBits : 0;
        var effectiveInputMax = ((1 << inputBits) - 1) << rightShift;

        var outputBits = node.GetString("outputBits", "8").Trim();
        var targetBits = outputBits switch
        {
            "8" => 8,
            "16" => 16,
            _ => throw new InvalidOperationException($"image.depthScale outputBits '{outputBits}' is not supported (8 | 16).")
        };
        var scale = node.GetDouble("scale", 0);
        var offset = node.GetDouble("offset", 0);
        if (!double.IsFinite(offset)) throw new InvalidOperationException("image.depthScale offset must be finite.");
        if (!double.IsFinite(scale) || scale < 0) throw new InvalidOperationException("image.depthScale scale must be finite and >= 0 (0=auto).");
        var overflowPolicy = ResultFileWriter.Choice(node, "overflowPolicy", "fail", "fail", "saturate");
        var autoScale = ((1 << targetBits) - 1.0) / ((1 << inputBits) - 1.0);
        if (scale <= 0) scale = autoScale;

        var targetMax = (1 << targetBits) - 1;
        var containerScale = scale / (1 << rightShift);
        var channels = sourceMat.Channels();
        var sampleCount = (long)sourceMat.Width * sourceMat.Height;
        long overflowPixels = 0;
        double maxClipAmount = 0;
        if (sourceBits == 8)
        {
            sourceMat.GetArray(out byte[] values);
            CountOverflow(values, channels, scale, offset, targetMax, rightShift, ref overflowPixels, ref maxClipAmount);
        }
        else
        {
            sourceMat.GetArray(out ushort[] values);
            CountOverflow(values, channels, scale, offset, targetMax, rightShift, ref overflowPixels, ref maxClipAmount);
        }
        var overflowRatio = sampleCount == 0 ? 0 : (double)overflowPixels / sampleCount;
        if (targetBits < inputBits && overflowPolicy == "fail" && overflowPixels > 0)
            throw new InvalidOperationException(
                $"image.depthScale would clip {overflowPixels} pixels (ratio {overflowRatio:P2}, max clip {maxClipAmount:0.####}); set overflowPolicy=saturate to acknowledge the loss.");

        Mat? output = null;
        try
        {
            output = new Mat();
            sourceMat.ConvertTo(output, targetBits == 8 ? MatType.CV_8U : MatType.CV_16U, containerScale, offset);
            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("inputBits", inputBits),
                    ("outputBits", targetBits), ("containerBits", sourceBits), ("alignment", alignment),
                    ("rightShift", rightShift), ("effectiveInputMax", effectiveInputMax),
                    ("logicalScale", scale), ("scaleUsed", containerScale), ("offsetUsed", offset),
                    ("autoScale", autoScale), ("channels", image.Mat.Channels()),
                    ("overflowPolicy", overflowPolicy), ("overflowPixelCount", overflowPixels),
                    ("overflowRatio", overflowRatio), ("maxClipAmount", maxClipAmount));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["inputBits"] = VisionValue.Integer(inputBits),
                        ["outputBits"] = VisionValue.Integer(targetBits),
                        ["scaleUsed"] = VisionValue.Double(containerScale),
                        ["offsetUsed"] = VisionValue.Double(offset),
                        ["overflowPolicy"] = VisionValue.String(overflowPolicy),
                        ["overflowPixelCount"] = VisionValue.Integer(overflowPixels),
                        ["overflowRatio"] = VisionValue.Double(overflowRatio),
                        ["maxClipAmount"] = VisionValue.Double(maxClipAmount),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["inputBits"] = inputBits, ["outputBits"] = targetBits, ["scaleUsed"] = containerScale,
                        ["offsetUsed"] = offset, ["autoScale"] = autoScale, ["alignment"] = alignment,
                        ["rightShift"] = rightShift, ["effectiveInputMax"] = effectiveInputMax,
                        ["channels"] = image.Mat.Channels(), ["overflowPolicy"] = overflowPolicy,
                        ["overflowPixelCount"] = overflowPixels, ["overflowRatio"] = overflowRatio,
                        ["maxClipAmount"] = maxClipAmount, ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            output?.Dispose();
        }
    }
    private static void CountOverflow<T>(T[] values, int channels, double scale, double offset,
        double targetMax, int rightShift, ref long overflowPixels, ref double maxClipAmount) where T : unmanaged
    {
        for (var index = 0; index < values.Length; index += channels)
        {
            var clipped = false;
            for (var channel = 0; channel < channels && index + channel < values.Length; channel++)
            {
                var raw = Convert.ToDouble(values[index + channel]) / (1 << rightShift);
                var mapped = raw * scale + offset;
                var clip = Math.Max(0d, Math.Max(-mapped, mapped - targetMax));
                if (clip <= 0) continue;
                clipped = true;
                maxClipAmount = Math.Max(maxClipAmount, clip);
            }
            if (clipped) overflowPixels++;
        }
    }
}

public sealed class ChannelMixNode : IVisionNodeExecutor
{
    public string Type => "image.channelMix";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.channelMix requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("image.channelMix requires an 8-bit image; convert 16-bit sources with image.depthScale first.");
        var channelCount = sourceMat.Channels();
        if (channelCount is < 1 or > 4)
            throw new InvalidOperationException($"image.channelMix supports 1..4 channel images; received {channelCount}.");

        var weights = new[]
        {
            node.GetDouble("weight1", .114), node.GetDouble("weight2", .587),
            node.GetDouble("weight3", .299), node.GetDouble("weight4", 0)
        };
        for (var i = 0; i < channelCount; i++)
            if (!double.IsFinite(weights[i])) throw new InvalidOperationException($"image.channelMix weight{i + 1} must be finite.");
        var used = weights.Take(channelCount).ToArray();
        if (used.All(w => Math.Abs(w) < 1e-12))
            throw new InvalidOperationException("image.channelMix all weights are zero; a mix needs at least one nonzero weight.");
        var normalize = node.GetBool("normalize", true);
        var normalizeEpsilon = node.GetDouble("normalizeEpsilon", 1e-9);
        if (!double.IsFinite(normalizeEpsilon) || normalizeEpsilon <= 0)
            throw new InvalidOperationException("image.channelMix normalizeEpsilon must be finite and > 0.");
        var normalizationFactor = 1d;
        if (normalize)
        {
            var sum = used.Sum();
            if (Math.Abs(sum) < normalizeEpsilon)
                throw new InvalidOperationException($"image.channelMix weights sum to {sum:R}, below normalizeEpsilon={normalizeEpsilon:R}; set normalize=false for an explicitly signed mix.");
            normalizationFactor = sum;
            used = used.Select(w => w / sum).ToArray();
        }
        var offset = node.GetDouble("offset", 0);
        if (!double.IsFinite(offset)) throw new InvalidOperationException("image.channelMix offset must be finite.");

        Mat? output = null;
        try
        {
            output = PreprocessEnhancementAlgorithms.MixChannels(sourceMat, used, offset);
            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    channelOrder = channelCount == 4 ? "BGRA" : channelCount == 3 ? "BGR" : "gray",
                    channels = channelCount,
                    channelIndices = Enumerable.Range(0, channelCount).ToArray(),
                    weights = used.Select(w => Math.Round(w, 6)).ToArray(),
                    normalizationFactor,
                    normalize,
                    normalizeEpsilon,
                    offset,
                    alphaWeight = channelCount == 4 ? (double?)used[3] : null,
                    outputAlpha = "opaque gray"
                });
                var saturation = PreprocessP1Diagnostics.ByteSaturation(image.Mat, 255);
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("channels", channelCount),
                    ("channelOrder", channelCount == 4 ? "BGRA" : channelCount == 3 ? "BGR" : "gray"),
                    ("channelIndices", Enumerable.Range(0, channelCount).ToArray()),
                    ("weight4Meaning", channelCount == 4 ? "alpha" : "unused"),
                    ("normalize", normalize), ("normalizeEpsilon", normalizeEpsilon),
                    ("normalizationFactor", normalizationFactor), ("offset", offset),
                    ("saturatedLowPixels", saturation.Low), ("saturatedHighPixels", saturation.High),
                    ("saturatedRatio", saturation.Ratio),
                    ("weights", string.Join(",", used.Select(w => w.ToString("0.####")))));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["channels"] = VisionValue.Integer(channelCount),
                        ["weightsJson"] = VisionValue.String(payload),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["channels"] = channelCount, ["normalize"] = normalize, ["offset"] = offset,
                        ["normalizationFactor"] = normalizationFactor, ["saturatedRatio"] = saturation.Ratio,
                        ["weightsJson"] = payload, ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            output?.Dispose();
        }
    }
}
