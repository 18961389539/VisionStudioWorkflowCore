using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

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

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);

        Cv2.Split(sourceMat, out var channels);
        double[] means = new double[3];
        double[] gains = new double[3];
        try
        {
            for (var i = 0; i < 3; i++) means[i] = PreprocessGapMath.MeanOf(channels[i], mask);
            var meanAll = (means[0] + means[1] + means[2]) / 3;
            if (meanAll <= 0)
                throw new InvalidOperationException("image.whiteBalance requires a non-black image.");

            if (mode == "manual")
            {
                gains[0] = node.GetDouble("manualB", 1);
                gains[1] = node.GetDouble("manualG", 1);
                gains[2] = node.GetDouble("manualR", 1);
            }
            else if (mode == "whitepatch")
            {
                var references = new double[3];
                for (var i = 0; i < 3; i++)
                {
                    var histogram = PreprocessGapMath.Histogram(channels[i], mask);
                    var total = histogram.Sum();
                    references[i] = Math.Max(1, PreprocessGapMath.Percentile(histogram, total, percentile));
                }
                var reference = references.Max();
                for (var i = 0; i < 3; i++) gains[i] = reference / references[i];
            }
            else
            {
                for (var i = 0; i < 3; i++) gains[i] = meanAll / Math.Max(1, means[i]);
            }

            for (var i = 0; i < 3; i++)
            {
                if (!double.IsFinite(gains[i]) || gains[i] <= 0)
                    throw new InvalidOperationException($"image.whiteBalance computed an invalid gain for channel {i}.");
                gains[i] = Math.Clamp(gains[i], .01, 20);
            }

            if (preserve)
            {
                var adjusted = (means[0] * gains[0] + means[1] * gains[1] + means[2] * gains[2]) / 3;
                if (adjusted > 1e-9)
                {
                    var k = meanAll / adjusted;
                    for (var i = 0; i < 3; i++) gains[i] = Math.Clamp(gains[i] * k, .01, 20);
                }
            }

            for (var i = 0; i < 3; i++)
            {
                using var scaled = new Mat();
                channels[i].ConvertTo(scaled, MatType.CV_8U, gains[i], 0);
                scaled.CopyTo(channels[i]);
            }
            var merged = new Mat();
            Cv2.Merge(channels, merged);
            Mat? output = merged;
            if (mask is not null)
            {
                var composited = PreprocessGapMath.Composite(merged, sourceMat, mask);
                merged.Dispose();
                output = composited;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    mode,
                    sourceMeans = new { b = Math.Round(means[0], 3), g = Math.Round(means[1], 3), r = Math.Round(means[2], 3) },
                    gains = new { b = Math.Round(gains[0], 4), g = Math.Round(gains[1], 4), r = Math.Round(gains[2], 4) },
                    preserveBrightness = preserve
                });
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("mode", mode),
                    ("gainB", gains[0]), ("gainG", gains[1]), ("gainR", gains[2]),
                    ("preserveBrightness", preserve), ("roiApplied", roi is not null));
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
                        ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            foreach (var channel in channels) channel.Dispose();
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
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("image.qualityMetrics requires an 8-bit image; convert 16-bit sources with image.depthScale first.");
        if (sourceMat.Channels() is not (1 or 3))
            throw new InvalidOperationException($"image.qualityMetrics supports 1 or 3 channel images; received {sourceMat.Channels()}.");

        var saturation = node.GetDouble("saturationThreshold", 254);
        var dark = node.GetDouble("darkThreshold", 1);
        if (!double.IsFinite(saturation) || saturation is < 1 or > 255)
            throw new InvalidOperationException("image.qualityMetrics saturationThreshold must be within 1..255.");
        if (!double.IsFinite(dark) || dark is < 0 or > 254)
            throw new InvalidOperationException("image.qualityMetrics darkThreshold must be within 0..254.");

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);

        using var gray = sourceMat.Channels() == 3 ? new Mat() : sourceMat.Clone();
        if (sourceMat.Channels() == 3) Cv2.CvtColor(sourceMat, gray, ColorConversionCodes.BGR2GRAY);

        var effectiveMask = mask is not null && !mask.Empty() ? mask : null;
        var pixelCount = PreprocessGapMath.MaskedCount(effectiveMask, (long)gray.Rows * gray.Cols);
        if (pixelCount <= 0) throw new InvalidOperationException("image.qualityMetrics ROI selects no pixels.");

        var brightness = PreprocessGapMath.MeanOf(gray, effectiveMask);
        var histogram = PreprocessGapMath.Histogram(gray, effectiveMask);
        var contrast = PreprocessGapMath.Percentile(histogram, pixelCount, 95) - PreprocessGapMath.Percentile(histogram, pixelCount, 5);
        var entropy = PreprocessGapMath.Entropy(histogram, pixelCount);

        using var gx = new Mat(); using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, 3);
        using var gx2 = new Mat(); using var gy2 = new Mat(); using var energy = new Mat();
        Cv2.Multiply(gx, gx, gx2); Cv2.Multiply(gy, gy, gy2); Cv2.Add(gx2, gy2, energy);
        var sharpness = PreprocessGapMath.MeanOf(energy, effectiveMask);

        // 噪声估计：Laplacian 高频残差的平均绝对值。对零均值高斯噪声 E|L| = ||k||₂·σ·sqrt(2/π)，
        // 3×3 Laplacian 的 ||k||₂ = sqrt(20)，所以 σ ≈ mean|L| / 3.5676。
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_32F, 3);
        using var absolute = new Mat();
        Cv2.Absdiff(laplacian, Scalar.All(0), absolute);
        var meanAbs = PreprocessGapMath.MeanOf(absolute, effectiveMask);
        var noiseSigma = meanAbs / (Math.Sqrt(2 / Math.PI) * Math.Sqrt(20));

        var saturated = PreprocessGapMath.CountAtLeast(gray, effectiveMask, saturation);
        var crushed = PreprocessGapMath.CountAtMost(gray, effectiveMask, dark);
        var saturationRatio = saturated / (double)pixelCount;
        var darkRatio = crushed / (double)pixelCount;

        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("brightness", brightness), ("contrast", contrast),
            ("sharpness", sharpness), ("noiseSigma", noiseSigma), ("saturationRatio", saturationRatio),
            ("darkRatio", darkRatio), ("entropy", entropy), ("pixelCount", pixelCount));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true),
                ["brightness"] = VisionValue.Double(brightness), ["contrast"] = VisionValue.Double(contrast),
                ["sharpness"] = VisionValue.Double(sharpness), ["noiseSigma"] = VisionValue.Double(noiseSigma),
                ["saturationRatio"] = VisionValue.Double(saturationRatio), ["darkRatio"] = VisionValue.Double(darkRatio),
                ["entropy"] = VisionValue.Double(entropy), ["pixelCount"] = VisionValue.Integer(pixelCount),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["brightness"] = brightness, ["contrast"] = contrast, ["sharpness"] = sharpness,
                ["noiseSigma"] = noiseSigma, ["saturationRatio"] = saturationRatio, ["darkRatio"] = darkRatio,
                ["entropy"] = entropy, ["pixelCount"] = pixelCount, ["roiApplied"] = roi is not null,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
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
        var autoScale = ((1 << targetBits) - 1.0) / ((1 << inputBits) - 1.0);
        if (scale <= 0) scale = autoScale;

        Mat? output = null;
        try
        {
            output = new Mat();
            sourceMat.ConvertTo(output, targetBits == 8 ? MatType.CV_8U : MatType.CV_16U, scale, offset);
            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("inputBits", inputBits),
                    ("outputBits", targetBits), ("scaleUsed", scale), ("offsetUsed", offset),
                    ("autoScale", autoScale), ("channels", image.Mat.Channels()));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["inputBits"] = VisionValue.Integer(inputBits),
                        ["outputBits"] = VisionValue.Integer(targetBits),
                        ["scaleUsed"] = VisionValue.Double(scale),
                        ["offsetUsed"] = VisionValue.Double(offset),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["inputBits"] = inputBits, ["outputBits"] = targetBits, ["scaleUsed"] = scale,
                        ["offsetUsed"] = offset, ["autoScale"] = autoScale,
                        ["channels"] = image.Mat.Channels(), ["algorithmBackend"] = "opencv",
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
        if (normalize)
        {
            var sum = used.Sum();
            if (Math.Abs(sum) < 1e-12)
                throw new InvalidOperationException("image.channelMix weights sum to zero, so normalize cannot preserve brightness; set normalize=false.");
            used = used.Select(w => w / sum).ToArray();
        }
        var offset = node.GetDouble("offset", 0);
        if (!double.IsFinite(offset)) throw new InvalidOperationException("image.channelMix offset must be finite.");

        Mat? mixed = null; Mat? output = null;
        try
        {
            var kernel = new float[used.Length];
            for (var i = 0; i < used.Length; i++) kernel[i] = (float)used[i];
            using var kernelMat = new Mat(1, used.Length, MatType.CV_32FC1);
            Marshal.Copy(kernel, 0, kernelMat.Data, kernel.Length);
            mixed = new Mat();
            Cv2.Transform(sourceMat, mixed, kernelMat);
            output = new Mat();
            mixed.ConvertTo(output, MatType.CV_8U, 1, offset);
            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    channels = channelCount,
                    weights = used.Select(w => Math.Round(w, 6)).ToArray(),
                    normalize,
                    offset
                });
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("channels", channelCount),
                    ("normalize", normalize), ("offset", offset),
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
                        ["weightsJson"] = payload, ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            mixed?.Dispose();
            output?.Dispose();
        }
    }
}
