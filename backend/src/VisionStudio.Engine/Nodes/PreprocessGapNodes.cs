using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 图像预处理补全批的公共算量：掩膜合成、掩膜统计、直方图分位、通道增益。
/// 这一批用 OpenCV / 纯托管实现，不走 JLVision（CLAHE、极坐标重映射、双边滤波在 JLVision 1.0.0 里没有）。
/// </summary>
internal static class PreprocessGapMath
{
    /// <summary>
    /// ROI 语义与 image.denoise 一致：只替换选区内像素，选区外保持原图。
    /// **所有权约定**：本方法消费 processed（无掩膜时原样返回，否则克隆后释放入参），
    /// 返回值由调用方持有，调用点不能再对 processed 写 using。
    /// </summary>
    public static Mat Composite(Mat processed, Mat source, Mat? mask)
    {
        if (mask is null || mask.Empty()) return processed;
        if (source.Size() != processed.Size() || source.Type() != processed.Type())
            throw new InvalidOperationException("ROI compositing requires matching size and type.");
        var output = processed.Clone();
        processed.Dispose();
        using var inverse = new Mat();
        JLVisionOperators.BitwiseNot(mask, inverse);
        source.CopyTo(output, inverse);
        return output;
    }

    /// <summary>带掩膜的均值；空掩膜等于"无掩膜"，直接走单参数重载（OpenCV 不接受 null InputArray）。</summary>
    public static double MeanOf(Mat source, Mat? mask)
        => mask is null || mask.Empty() ? Cv2.Mean(source).Val0 : Cv2.Mean(source, mask).Val0;

    public static long MaskedCount(Mat? mask, long fallback)
        => mask is null || mask.Empty() ? fallback : Cv2.CountNonZero(mask);

    /// <summary>在掩膜内统计 "gray >= threshold" 的像素数。</summary>
    public static long CountAtLeast(Mat gray, Mat? mask, double threshold)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, threshold - 1, 255, ThresholdTypes.Binary);
        return CountMasked(binary, mask);
    }

    public static long CountAtMost(Mat gray, Mat? mask, double threshold)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, threshold, 255, ThresholdTypes.BinaryInv);
        return CountMasked(binary, mask);
    }

    private static long CountMasked(Mat binary, Mat? mask)
    {
        if (mask is null || mask.Empty()) return Cv2.CountNonZero(binary);
        using var both = new Mat();
        Cv2.BitwiseAnd(binary, mask, both);
        return Cv2.CountNonZero(both);
    }

    /// <summary>8 位直方图（可带掩膜），返回 256 个计数。</summary>
    public static long[] Histogram(Mat gray, Mat? mask)
    {
        using var hist = new Mat();
        using var empty = new Mat();
        Cv2.CalcHist([gray], [0], mask is not null && !mask.Empty() ? mask : empty, hist, 1, [256], [new Rangef(0, 256)]);
        var counts = new long[256];
        for (var i = 0; i < 256; i++) counts[i] = (long)Math.Round(hist.At<float>(i));
        return counts;
    }

    /// <summary>按直方图求分位灰度（0..255，线性插值在箱内）。</summary>
    public static double Percentile(long[] histogram, long total, double percent)
    {
        if (total <= 0) return 0;
        var target = total * Math.Clamp(percent, 0, 100) / 100.0;
        long running = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            if (running >= target) return i;
        }
        return histogram.Length - 1;
    }

    /// <summary>香农熵（bit/像素），用 8 位直方图近似。</summary>
    public static double Entropy(long[] histogram, long total)
    {
        if (total <= 0) return 0;
        var entropy = 0.0;
        foreach (var count in histogram)
        {
            if (count == 0) continue;
            var p = count / (double)total;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    /// <summary>8 位单通道图的"最短距离到边缘"上限，用于自动外半径。</summary>
    public static double AutoOuterRadius(double x, double y, int width, int height)
        => Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y));
}

public sealed class ClaheNode : IVisionNodeExecutor
{
    public string Type => "image.clahe";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var clipLimit = node.GetDouble("clipLimit", 2);
        if (!double.IsFinite(clipLimit) || clipLimit is < .1 or > 40)
            throw new InvalidOperationException("image.clahe clipLimit must be within 0.1..40.");
        var tilesX = node.GetInt("tilesX", 8);
        var tilesY = node.GetInt("tilesY", 8);
        if (tilesX is < 1 or > 64 || tilesY is < 1 or > 64)
            throw new InvalidOperationException("image.clahe tiles must be within 1..64.");
        var colorMode = node.GetString("colorMode", "luminance").Trim().ToLowerInvariant();
        if (colorMode is not ("luminance" or "gray"))
            throw new InvalidOperationException($"image.clahe colorMode '{colorMode}' is not supported (luminance | gray).");

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.clahe requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("image.clahe requires an 8-bit image; convert 16-bit sources with image.depthScale first.");
        if (sourceMat.Channels() is not (1 or 3))
            throw new InvalidOperationException($"image.clahe supports 1 or 3 channel images; received {sourceMat.Channels()}.");

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);

        using var clahe = CLAHE.Create(clipLimit, new Size(tilesX, tilesY));
        Mat? output = null;
        try
        {
            if (sourceMat.Channels() == 1)
            {
                var enhanced = new Mat();
                try
                {
                    clahe.Apply(sourceMat, enhanced);
                    output = PreprocessGapMath.Composite(enhanced, sourceMat, mask);
                    enhanced = null!;
                }
                finally { enhanced?.Dispose(); }
            }
            else if (colorMode == "gray")
            {
                // 输出单通道：下游（阈值/卡尺）多数只吃灰度，通道数跟着 colorMode 走才不误导
                using var gray = new Mat();
                Cv2.CvtColor(sourceMat, gray, ColorConversionCodes.BGR2GRAY);
                var enhanced = new Mat();
                try
                {
                    clahe.Apply(gray, enhanced);
                    output = PreprocessGapMath.Composite(enhanced, gray, mask);
                    enhanced = null!;
                }
                finally { enhanced?.Dispose(); }
            }
            else
            {
                // Lab 的 L 通道均衡后再转回 BGR：颜色不漂移，这是彩色缺陷件的要求
                using var lab = new Mat();
                Cv2.CvtColor(sourceMat, lab, ColorConversionCodes.BGR2Lab);
                Cv2.Split(lab, out var channels);
                using var restored = new Mat();
                try
                {
                    clahe.Apply(channels[0], channels[0]);
                    Cv2.Merge(channels, restored);
                }
                finally { foreach (var channel in channels) channel.Dispose(); }
                var bgr = new Mat();
                try
                {
                    Cv2.CvtColor(restored, bgr, ColorConversionCodes.Lab2BGR);
                    output = PreprocessGapMath.Composite(bgr, sourceMat, mask);
                    bgr = null!;
                }
                finally { bgr?.Dispose(); }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("clipLimit", clipLimit),
                    ("tilesX", tilesX), ("tilesY", tilesY), ("colorMode", colorMode),
                    ("roiApplied", roi is not null), ("width", image.Width), ("height", image.Height),
                    ("channels", image.Mat.Channels()));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["width"] = VisionValue.Integer(image.Width),
                        ["height"] = VisionValue.Integer(image.Height),
                        ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                        ["clipLimit"] = VisionValue.Double(clipLimit),
                        ["tilesX"] = VisionValue.Integer(tilesX),
                        ["tilesY"] = VisionValue.Integer(tilesY),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["clipLimit"] = clipLimit, ["tilesX"] = tilesX, ["tilesY"] = tilesY,
                        ["colorMode"] = colorMode, ["roiApplied"] = roi is not null,
                        ["width"] = image.Width, ["height"] = image.Height,
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

public sealed class PolarUnwrapNode : IVisionNodeExecutor
{
    public string Type => "image.polarUnwrap";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.polarUnwrap requires a non-empty image.");
        if (sourceMat.Channels() is < 1 or > 4)
            throw new InvalidOperationException("image.polarUnwrap supports 1..4 channel images.");

        var width = sourceMat.Width; var height = sourceMat.Height;
        var cx = node.GetDouble("centerX", 0); var cy = node.GetDouble("centerY", 0);
        var hasCircle = context.TryGetValue("circle", out var circleValue);
        var circle = hasCircle ? circleValue.Require<VisionCircle>("circle") : default;
        if (hasCircle)
        {
            if (!double.IsFinite(circle.X) || !double.IsFinite(circle.Y) || !double.IsFinite(circle.Radius) || circle.Radius <= 0)
                throw new InvalidOperationException("image.polarUnwrap circle must have finite center and a positive radius.");
            cx = circle.X; cy = circle.Y;
        }
        if (!double.IsFinite(cx) || !double.IsFinite(cy))
            throw new InvalidOperationException("image.polarUnwrap center must be finite.");

        var inner = node.GetDouble("innerRadius", 0);
        var outer = node.GetDouble("outerRadius", 0);
        if (!double.IsFinite(inner) || inner < 0) throw new InvalidOperationException("image.polarUnwrap innerRadius must be finite and >= 0.");
        if (!double.IsFinite(outer) || outer < 0) throw new InvalidOperationException("image.polarUnwrap outerRadius must be finite and >= 0 (0 derives it).");
        if (outer <= 0) outer = hasCircle ? circle.Radius : PreprocessGapMath.AutoOuterRadius(cx, cy, width, height);
        if (outer <= inner + 1e-6) throw new InvalidOperationException("image.polarUnwrap requires outerRadius > innerRadius.");
        if (outer > Math.Max(width, height) * 4.0)
            throw new InvalidOperationException("image.polarUnwrap outerRadius is far outside the image; check the center.");

        var startAngle = node.GetDouble("angleStartDeg", 0);
        var sweep = node.GetDouble("angleSweepDeg", 360);
        if (!double.IsFinite(startAngle) || startAngle is < -3600 or > 3600)
            throw new InvalidOperationException("image.polarUnwrap angleStartDeg must be within -3600..3600.");
        if (!double.IsFinite(sweep) || sweep is < 1 or > 360)
            throw new InvalidOperationException("image.polarUnwrap angleSweepDeg must be within 1..360.");
        var direction = node.GetString("direction", "counterClockwise").Trim().ToLowerInvariant();
        if (direction is not ("counterclockwise" or "clockwise"))
            throw new InvalidOperationException($"image.polarUnwrap direction '{direction}' is not supported (counterClockwise | clockwise).");
        var interpolation = node.GetString("interpolation", "linear").Trim().ToLowerInvariant();
        var inter = interpolation switch
        {
            "nearest" => InterpolationFlags.Nearest,
            "linear" => InterpolationFlags.Linear,
            "cubic" => InterpolationFlags.Cubic,
            _ => throw new InvalidOperationException($"image.polarUnwrap interpolation '{interpolation}' is not supported (nearest | linear | cubic).")
        };

        var band = outer - inner;
        var radial = node.GetInt("radialSamples", 0);
        if (radial <= 0) radial = (int)Math.Clamp(Math.Round(band), 2, 4096);
        if (radial is < 2 or > 4096) throw new InvalidOperationException("image.polarUnwrap radialSamples must be within 2..4096.");
        var midRadius = inner + band / 2;
        var angular = node.GetInt("angularSamples", 0);
        if (angular <= 0) angular = (int)Math.Clamp(Math.Round(2 * Math.PI * midRadius * (sweep / 360.0)), 16, 8192);
        if (angular is < 8 or > 8192) throw new InvalidOperationException("image.polarUnwrap angularSamples must be within 8..8192.");

        var total = (long)angular * radial;
        if (total > 16_000_000) throw new InvalidOperationException($"image.polarUnwrap would allocate {total} pixels; lower the sample counts.");

        var sign = direction == "clockwise" ? -1 : 1;
        var mapX = new float[total]; var mapY = new float[total];
        for (var y = 0; y < radial; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var radius = inner + band * (y + .5) / radial;
            for (var x = 0; x < angular; x++)
            {
                var angle = (startAngle + sign * sweep * (x + .5) / angular) * Math.PI / 180;
                mapX[y * angular + x] = (float)(cx + radius * Math.Cos(angle));
                mapY[y * angular + x] = (float)(cy + radius * Math.Sin(angle));
            }
        }

        using var mapMatX = new Mat(radial, angular, MatType.CV_32FC1);
        using var mapMatY = new Mat(radial, angular, MatType.CV_32FC1);
        Marshal.Copy(mapX, 0, mapMatX.Data, mapX.Length);
        Marshal.Copy(mapY, 0, mapMatY.Data, mapY.Length);
        Mat? output = null;
        try
        {
            output = new Mat();
            Cv2.Remap(sourceMat, output, mapMatX, mapMatY, inter, BorderTypes.Replicate);
            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("centerX", cx), ("centerY", cy),
                    ("innerRadius", inner), ("outerRadius", outer), ("angleStartDeg", startAngle),
                    ("angleSweepDeg", sweep), ("direction", direction), ("interpolation", interpolation),
                    ("width", angular), ("height", radial));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["width"] = VisionValue.Integer(angular),
                        ["height"] = VisionValue.Integer(radial),
                        ["centerX"] = VisionValue.Double(cx), ["centerY"] = VisionValue.Double(cy),
                        ["innerRadius"] = VisionValue.Double(inner), ["outerRadius"] = VisionValue.Double(outer),
                        ["angleStartDeg"] = VisionValue.Double(startAngle), ["angleSweepDeg"] = VisionValue.Double(sweep),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["centerX"] = cx, ["centerY"] = cy, ["innerRadius"] = inner, ["outerRadius"] = outer,
                        ["angleStartDeg"] = startAngle, ["angleSweepDeg"] = sweep, ["direction"] = direction,
                        ["interpolation"] = interpolation, ["width"] = angular, ["height"] = radial,
                        ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            output?.Dispose();
        }
    }
}

public sealed class BilateralFilterNode : IVisionNodeExecutor
{
    public string Type => "image.bilateral";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.bilateral requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("image.bilateral requires an 8-bit image; convert 16-bit sources with image.depthScale first.");
        if (sourceMat.Channels() is not (1 or 3))
            throw new InvalidOperationException($"image.bilateral supports 1 or 3 channel images; received {sourceMat.Channels()}.");

        var diameter = node.GetInt("diameter", 0);
        if (diameter is < 0 or > 31) throw new InvalidOperationException("image.bilateral diameter must be within 0..31 (0=auto).");
        var sigmaColor = node.GetDouble("sigmaColor", 25);
        var sigmaSpace = node.GetDouble("sigmaSpace", 25);
        if (!double.IsFinite(sigmaColor) || sigmaColor is < 1 or > 500)
            throw new InvalidOperationException("image.bilateral sigmaColor must be within 1..500.");
        if (!double.IsFinite(sigmaSpace) || sigmaSpace is < 1 or > 500)
            throw new InvalidOperationException("image.bilateral sigmaSpace must be within 1..500.");
        if (diameter == 0) diameter = sigmaSpace >= 50 ? 9 : 5;

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);

        Mat? filtered = null;
        Mat? output = null;
        try
        {
            filtered = new Mat();
            Cv2.BilateralFilter(sourceMat, filtered, diameter, sigmaColor, sigmaSpace);
            cancellationToken.ThrowIfCancellationRequested();
            output = PreprocessGapMath.Composite(filtered, sourceMat, mask);
            filtered = null;
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("diameter", diameter),
                    ("sigmaColor", sigmaColor), ("sigmaSpace", sigmaSpace), ("roiApplied", roi is not null),
                    ("width", image.Width), ("height", image.Height), ("channels", image.Mat.Channels()));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["width"] = VisionValue.Integer(image.Width),
                        ["height"] = VisionValue.Integer(image.Height),
                        ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                        ["sigmaColor"] = VisionValue.Double(sigmaColor),
                        ["sigmaSpace"] = VisionValue.Double(sigmaSpace),
                        ["diameter"] = VisionValue.Integer(diameter),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["diameter"] = diameter, ["sigmaColor"] = sigmaColor, ["sigmaSpace"] = sigmaSpace,
                        ["roiApplied"] = roi is not null, ["width"] = image.Width, ["height"] = image.Height,
                        ["channels"] = image.Mat.Channels(), ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            filtered?.Dispose();
            output?.Dispose();
        }
    }
}

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
