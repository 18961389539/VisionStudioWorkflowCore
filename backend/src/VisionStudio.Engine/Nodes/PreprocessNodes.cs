using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 批次一（2026-10-13 节点补齐）· 图像预处理族：灰度/通道 → 滤波 → 形态学 → 自动阈值。
/// 设计约定（与既有 image.threshold 一致）：
///   · 输出图像一律由节点持有（VisionImage.Own），临时 Mat 就地释放；
///   · 彩色输入在需要单通道的环节自动转灰度（summary 记录 autoGray=true），
///     避免用户必须显式前置灰度转换才能使用二值化/Blob；
///   · 支持 ROI 的节点采用"输出掩膜"语义：ROI 外清零（与 image.threshold 相同）。
/// </summary>
internal static class PreprocessImageHelpers
{
    /// <summary>
    /// 把输入规范化为单通道 CV_8U（3/4 通道转灰度；UInt16 按 <paramref name="inputBitDepth"/> 缩放到 8 位）。
    /// <paramref name="inputBitDepth"/> 为 null（auto）时沿用满量程 1/257；指定位深时按 255/(2^bits-1) 映射，
    /// 否则 10/12 位相机的有效灰度会被压进 0..16 / 0..4，阈值几乎无法使用。
    /// </summary>
    public static Mat ToGray8(Mat source, out bool converted, out Mat? owned, int? inputBitDepth = null)
    {
        converted = false;
        owned = null;
        var gray = source;
        if (source.Channels() != 1)
        {
            if (source.Channels() is not (3 or 4))
                throw new InvalidOperationException($"Grayscale conversion supports 1, 3, or 4 channels; received {source.Channels()}.");
            var convertedGray = new Mat();
            try
            {
                JLVisionOperators.CvtColor(source, convertedGray, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            }
            catch
            {
                convertedGray.Dispose();
                throw;
            }
            gray = convertedGray;
            converted = true;
        }

        if (gray.Depth() == MatType.CV_8U)
        {
            if (converted) owned = gray;
            return gray;
        }

        var gray8 = new Mat();
        try
        {
            // auto 保持历史口径：把 uint16 满量程映射到 byte（1/257）；指定位深时按实际有效位深映射。
            // 其他数值深度用 OpenCV 的饱和转换，不做与图像相关的隐式归一化——那会让边缘/阈值含义逐帧漂移。
            var scale = gray.Depth() == MatType.CV_16U ? ByteScale(inputBitDepth) : 1d;
            gray.ConvertTo(gray8, MatType.CV_8UC1, scale);
        }
        catch
        {
            gray8.Dispose();
            if (converted) gray.Dispose();
            throw;
        }

        if (converted) gray.Dispose();
        owned = gray8;
        return gray8;
    }

    /// <summary>uint16 → byte 的缩放系数：auto（null）= 1/257 满量程，指定 b 位 = 255/(2^b-1)。</summary>
    public static double ByteScale(int? inputBitDepth) => inputBitDepth is { } bits ? 255d / ((1 << bits) - 1) : 1d / 257d;

    /// <summary>读取 inputBitDepth 参数：auto/空 → null（满量程），8..16 → 位深；其他值报错。</summary>
    public static int? InputBitDepth(NodeDefinition node, string nodeType)
    {
        var text = node.GetString("inputBitDepth", "auto").Trim();
        if (text.Length == 0 || text.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bits) && bits is >= 8 and <= 16)
            return bits;
        throw new InvalidOperationException($"{nodeType} inputBitDepth '{text}' is unsupported; use auto or 8..16.");
    }

    /// <summary>ROI 掩膜：ROI 外清零（调用方交出 processed 的所有权；返回可继续持有的新 Mat）。</summary>
    public static Mat ApplyRoiMask(Mat processed, VisionRoi? roi, CancellationToken cancellationToken = default)
    {
        if (roi is null) return processed;
        Mat mask;
        try
        {
            mask = roi.BuildMask(processed.Size(), cancellationToken);
        }
        catch
        {
            processed.Dispose();
            throw;
        }

        using (mask) return ApplyMask(processed, mask, cancellationToken);
    }

    /// <summary>按已栅格化的掩膜清零（掩膜为 null 时原样返回，便于和 <see cref="ThresholdStatisticsDomain"/> 复用同一份掩膜）。</summary>
    public static Mat ApplyMask(Mat processed, Mat? mask, CancellationToken cancellationToken = default)
    {
        if (mask is null) return processed;
        Mat? masked = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            masked = new Mat(processed.Rows, processed.Cols, processed.Type(), Scalar.All(0));
            processed.CopyTo(masked, mask);
            cancellationToken.ThrowIfCancellationRequested();
            processed.Dispose();
            return masked;
        }
        catch
        {
            masked?.Dispose();
            processed.Dispose();
            throw;
        }
    }

    /// <summary>把域内处理结果贴回全画布（域未裁剪、即整图时直接返回原图）。</summary>
    public static Mat RestoreToCanvas(Mat patch, Rect rect, Size canvas, CancellationToken cancellationToken = default)
    {
        if (rect.X == 0 && rect.Y == 0 && rect.Width == canvas.Width && rect.Height == canvas.Height) return patch;
        Mat? full = null;
        try
        {
            full = new Mat(canvas.Height, canvas.Width, patch.Type(), Scalar.All(0));
            using (var view = new Mat(full, rect)) patch.CopyTo(view);
            cancellationToken.ThrowIfCancellationRequested();
            patch.Dispose();
            return full;
        }
        catch
        {
            full?.Dispose();
            patch.Dispose();
            throw;
        }
    }

    public static int OddKernel(int value, int min, int max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped % 2 == 1) return clamped;
        return clamped + 1 <= max ? clamped + 1 : clamped - 1;
    }
}

/// <summary>
/// 阈值统计域。ROI 存在时把 8 位灰度图裁剪到 ROI 包围盒，让 Otsu / 自适应阈值只统计 ROI 内的像素
/// （此前 ROI 只在输出阶段清零，阈值本身仍由全图直方图决定，ROI 外的高亮会把阈值拉偏）。
/// 同时给出 ROI 覆盖的像素数，作为前景占比的分母——用整图面积做分母会让 ROI 越小、占比越接近 0。
///
/// 裁剪而非掩膜的原因：JLVision 的 Otsu / 局部均值都按直方图或邻域计算，掩膜清零会引入一个
/// 巨大的 0 值尖峰并污染邻域。矩形 ROI 裁剪即精确；圆形/多边形/画笔取包围盒，统计会含少量框外像素。
/// <c>padding</c> 用于自适应阈值：外扩半个窗口，保证 ROI 内每个像素的邻域与整图计算时完全相同。
/// </summary>
internal sealed class ThresholdStatisticsDomain : IDisposable
{
    private readonly Mat? _cropped;
    private readonly Mat? _mask;

    private ThresholdStatisticsDomain(Mat gray, Rect rect, Mat? cropped, long pixelCount, Mat? mask)
    {
        Gray = gray;
        Rect = rect;
        _cropped = cropped;
        PixelCount = pixelCount;
        _mask = mask;
    }

    /// <summary>统计/处理用的灰度图；未裁剪时就是调用方的原图，不要释放。</summary>
    public Mat Gray { get; }

    /// <summary><see cref="Gray"/> 在整图中的位置，用于把结果贴回全画布。</summary>
    public Rect Rect { get; }

    /// <summary>ROI 覆盖的像素数（未设置 ROI 时为整图像素数），前景占比的分母。</summary>
    public long PixelCount { get; }

    /// <summary>ROI 掩膜；未设置 ROI 时为 null。交给 <see cref="PreprocessImageHelpers.ApplyMask"/> 以免重复栅格化。</summary>
    public Mat? Mask => _mask;

    public static ThresholdStatisticsDomain Create(Mat gray8, VisionRoi? roi, int padding, bool crop, CancellationToken cancellationToken)
    {
        var whole = new Rect(0, 0, gray8.Width, gray8.Height);
        if (roi is null) return new ThresholdStatisticsDomain(gray8, whole, null, gray8.Total(), null);

        var mask = roi.BuildMask(gray8.Size(), cancellationToken);
        try
        {
            var pixelCount = (long)JLVisionOperators.CountNonZero(mask);
            if (pixelCount <= 0)
                throw new InvalidOperationException(
                    $"{roi.Type} ROI covers no pixels in the {gray8.Width}×{gray8.Height} image; threshold statistics would be empty. Move or enlarge the ROI.");
            if (!crop) return new ThresholdStatisticsDomain(gray8, whole, null, pixelCount, mask);

            var rect = Inflate(VisionRoiOpenCvExtensions.BoundsOf(mask), padding, gray8.Size());
            using var view = new Mat(gray8, rect);
            var cropped = view.Clone();
            return new ThresholdStatisticsDomain(cropped, rect, cropped, pixelCount, mask);
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    private static Rect Inflate(Rect bounds, int padding, Size canvas)
    {
        var left = Math.Max(0, bounds.X - padding);
        var top = Math.Max(0, bounds.Y - padding);
        var right = Math.Min(canvas.Width, bounds.X + bounds.Width + padding);
        var bottom = Math.Min(canvas.Height, bounds.Y + bounds.Height + padding);
        return new Rect(left, top, right - left, bottom - top);
    }

    public void Dispose()
    {
        _cropped?.Dispose();
        _mask?.Dispose();
    }
}

public sealed class ChannelExtractNode : IVisionNodeExecutor
{
    public string Type => "image.channel";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException("image.channel requires a non-empty image.");
        var source = image.Mat;
        var channel = node.GetString("channel", "gray").Trim().ToLowerInvariant();
        if (channel is not ("gray" or "luminance" or "blue" or "green" or "red" or "hue" or "saturation" or "value" or "average" or "mean" or "max")) throw new InvalidOperationException($"image.channel channel '{channel}' is unsupported; use gray, blue, green, red, hue, saturation, value, average or max.");
        // gray/luminance 的亮度权重：bt601 走原生 CvtColor，bt709 / avg 走逐通道加权（原 image.grayscale 的 weights 能力）。
        var weights = node.GetString("weights", "bt601").Trim().ToLowerInvariant();
        if (weights is not ("bt601" or "bt709" or "avg")) throw new InvalidOperationException("image.channel weights must be bt601, bt709 or avg.");
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var mat = new Mat();

        if (channel is "hue" or "saturation" or "value")
        {
            if (source.Channels() == 1)
                throw new InvalidOperationException($"Channel '{channel}' requires a color image; the input is single-channel (insert a color source or use 'gray').");
            using var bgr = new Mat();
            var color = source.Channels() == 4 ? Bgr(source, bgr) : source;
            using var hsv = new Mat();
            JLVisionOperators.CvtColor(color, hsv, ColorConversionCodes.BGR2HSV);
            JLVisionOperators.ExtractChannel(hsv, mat, channel switch { "hue" => 0, "saturation" => 1, _ => 2 });
        }
        else if (channel is "average" or "mean" or "max")
        {
            if (source.Channels() == 1)
            {
                source.CopyTo(mat);
            }
            else
            {
                using var bgr = new Mat();
                var color = source.Channels() == 4 ? Bgr(source, bgr) : source;
                JLVisionOperators.Split(color, out var channels);
                try
                {
                    if (channel is "average" or "mean")
                    {
                        using var average = new Mat();
                        JLVisionOperators.AddWeighted(channels[0], 1d / 3, channels[1], 1d / 3, 0, average);
                        JLVisionOperators.AddWeighted(average, 1, channels[2], 1d / 3, 0, mat);
                    }
                    else
                    {
                        JLVisionOperators.Max(channels[0], channels[1], mat);
                        JLVisionOperators.Max(mat, channels[2], mat);
                    }
                }
                finally
                {
                    foreach (var component in channels) component.Dispose();
                }
            }
        }
        else if (channel is "blue" or "green" or "red")
        {
            if (source.Channels() == 1)
                throw new InvalidOperationException($"Channel '{channel}' requires a color image; the input is single-channel (use 'gray' for the only channel).");
            using var bgr = new Mat();
            var color = source.Channels() == 4 ? Bgr(source, bgr) : source;
            JLVisionOperators.ExtractChannel(color, mat, channel switch { "blue" => 0, "green" => 1, _ => 2 });
        }
        else
        {
            // gray/luminance：bt601 与 image.grayscale 的默认路径一致；bt709/avg 按通道权重求和。
            if (source.Channels() == 1) source.CopyTo(mat);
            else if (weights == "bt601")
                JLVisionOperators.CvtColor(source, mat, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            else
            {
                using var bgr = new Mat();
                var color = source.Channels() == 4 ? Bgr(source, bgr) : source;
                JLVisionOperators.Split(color, out var channels);
                try
                {
                    var (bw, gw, rw) = weights switch
                    {
                        "bt709" => (0.0722, 0.7152, 0.2126),
                        _ => (1d / 3, 1d / 3, 1d / 3)
                    };
                    using var sum = new Mat();
                    JLVisionOperators.AddWeighted(channels[0], bw, channels[1], gw, 0, sum);
                    JLVisionOperators.AddWeighted(sum, 1, channels[2], rw, 0, mat);
                }
                finally
                {
                    foreach (var component in channels) component.Dispose();
                }
            }
        }

        JLVisionOperators.MinMax(mat, out double minIntensity, out double maxIntensity);
        var meanIntensity = JLVisionOperators.Mean(mat, out _);
        var diag = CoordinateValidation.Diagnostics(Type, ("channel", channel), ("weights", weights), ("inputChannels", source.Channels()), ("outputChannels", mat.Channels()),
            ("meanIntensity", meanIntensity), ("minIntensity", minIntensity), ("maxIntensity", maxIntensity),
            ("width", mat.Width), ("height", mat.Height));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(mat)),
                ["meanIntensity"] = VisionValue.Double(meanIntensity),
                ["minIntensity"] = VisionValue.Double(minIntensity),
                ["maxIntensity"] = VisionValue.Double(maxIntensity),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["channel"] = channel, ["weights"] = weights, ["channels"] = mat.Channels(), ["meanIntensity"] = meanIntensity, ["diagnostics"] = diag }));

        static Mat Bgr(Mat source, Mat scratch)
        {
            JLVisionOperators.CvtColor(source, scratch, ColorConversionCodes.BGRA2BGR);
            return scratch;
        }
    }
}

/// <summary>Gaussian 平滑实现：image.filter(method=gaussian) 与历史别名 image.blur.gaussian 共用。</summary>
internal static class GaussianBlurFilter
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string nodeType, CancellationToken cancellationToken)
    {
        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        var source = image.Mat;
        var channels = source.Channels();
        if (channels is < 1 or > 4) throw new InvalidOperationException($"{nodeType} supports 1..4 channel images.");
        var requested = node.GetInt("kernelSize", 5);
        if (requested is < 1 or > 31) throw new InvalidOperationException($"{nodeType} kernelSize must be between 1 and 31.");
        var kernel = PreprocessImageHelpers.OddKernel(requested, 1, 31);
        var sigmaX = node.GetDouble("sigmaX", 0);
        var sigmaY = node.GetDouble("sigmaY", 0);
        if (!double.IsFinite(sigmaX) || !double.IsFinite(sigmaY) || sigmaX < 0 || sigmaY < 0)
            throw new InvalidOperationException($"{nodeType} sigmaX/sigmaY must be finite and >= 0 (0 derives sigma from kernel size).");
        var borderName = node.GetString("borderType", "default").Trim().ToLowerInvariant();
        var border = borderName switch
        {
            "default" => BorderTypes.Default,
            "constant" => BorderTypes.Constant,
            "replicate" => BorderTypes.Replicate,
            "reflect" => BorderTypes.Reflect,
            "wrap" => BorderTypes.Wrap,
            _ => throw new InvalidOperationException($"{nodeType} borderType must be default, constant, replicate, reflect or wrap.")
        };
        cancellationToken.ThrowIfCancellationRequested();
        var roi = node.GetRoi();
        var pad = kernel / 2;
        Mat blurred;
        if (pad == 0 || borderName == "default")
        {
            blurred = new Mat();
            JLVisionOperators.GaussianBlur(source, blurred, new Size(kernel, kernel), sigmaX, sigmaY);
        }
        else
        {
            using var padded = new Mat();
            Cv2.CopyMakeBorder(source, padded, pad, pad, pad, pad, border, Scalar.All(0));
            using var full = new Mat();
            JLVisionOperators.GaussianBlur(padded, full, new Size(kernel, kernel), sigmaX, sigmaY);
            blurred = new Mat(full, new Rect(pad, pad, source.Width, source.Height)).Clone();
        }
        var output = PreprocessImageHelpers.ApplyRoiMask(blurred, roi, cancellationToken);
        JLVisionOperators.MinMax(output, out double minIntensity, out double maxIntensity);
        var meanIntensity = JLVisionOperators.Mean(output);
        var diag = CoordinateValidation.Diagnostics(nodeType, ("kernelSize", kernel), ("requestedKernelSize", requested),
            ("sigmaX", sigmaX), ("sigmaY", sigmaY), ("borderType", borderName),
            ("meanIntensity", meanIntensity), ("minIntensity", minIntensity), ("maxIntensity", maxIntensity),
            ("width", output.Width), ("height", output.Height), ("channels", channels));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(output)),
                ["meanIntensity"] = VisionValue.Double(meanIntensity),
                ["minIntensity"] = VisionValue.Double(minIntensity),
                ["maxIntensity"] = VisionValue.Double(maxIntensity),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["kernelSize"] = kernel,
                ["sigmaX"] = sigmaX <= 0 ? "derived" : sigmaX,
                ["sigmaY"] = sigmaY <= 0 ? "derived" : sigmaY,
                ["borderType"] = borderName,
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["channels"] = channels,
                ["meanIntensity"] = meanIntensity,
                ["minIntensity"] = minIntensity,
                ["maxIntensity"] = maxIntensity,
                ["diagnostics"] = diag
            });
    }
}

/// <summary>中值滤波实现：image.filter(method=median) 与历史别名 image.blur.median 共用。</summary>
internal static class MedianBlurFilter
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string nodeType, CancellationToken cancellationToken)
    {
        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        var source = image.Mat;
        var channels = source.Channels();
        if (channels is not (1 or 3 or 4)) throw new InvalidOperationException($"{nodeType} supports 1, 3 or 4 channel images only.");
        var requested = node.GetInt("kernelSize", 5);
        if (requested is < 3 or > 31) throw new InvalidOperationException($"{nodeType} kernelSize must be between 3 and 31.");
        var kernel = PreprocessImageHelpers.OddKernel(requested, 3, 31);
        if (kernel > Math.Max(image.Width, image.Height) * 2 + 1)
            throw new InvalidOperationException($"{nodeType} kernelSize is unreasonably large for the image dimensions.");
        var borderName = node.GetString("borderType", "replicate").Trim().ToLowerInvariant();
        var border = borderName switch
        {
            "constant" => BorderTypes.Constant,
            "replicate" => BorderTypes.Replicate,
            "reflect" => BorderTypes.Reflect,
            "wrap" => BorderTypes.Wrap,
            _ => throw new InvalidOperationException($"{nodeType} borderType must be constant, replicate, reflect or wrap.")
        };
        var roi = node.GetRoi();
        cancellationToken.ThrowIfCancellationRequested();
        // OpenCV medianBlur has no borderMode; pad then crop so borderType is a real aperture edge policy.
        var pad = kernel / 2;
        using var padded = new Mat();
        Cv2.CopyMakeBorder(source, padded, pad, pad, pad, pad, border, Scalar.All(0));
        using var blurredFull = new Mat();
        JLVisionOperators.MedianBlur(padded, blurredFull, kernel);
        var cropped = new Mat(blurredFull, new Rect(pad, pad, source.Width, source.Height)).Clone();
        var output = PreprocessImageHelpers.ApplyRoiMask(cropped, roi, cancellationToken);

        JLVisionOperators.MinMax(output, out double minIntensity, out double maxIntensity);
        var meanIntensity = JLVisionOperators.Mean(output);
        var diag = CoordinateValidation.Diagnostics(nodeType, ("kernelSize", kernel), ("requestedKernelSize", requested), ("meanIntensity", meanIntensity), ("minIntensity", minIntensity), ("maxIntensity", maxIntensity),
            ("borderType", borderName), ("pad", pad), ("width", output.Width), ("height", output.Height), ("channels", channels));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)), ["meanIntensity"] = VisionValue.Double(meanIntensity), ["minIntensity"] = VisionValue.Double(minIntensity), ["maxIntensity"] = VisionValue.Double(maxIntensity), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["kernelSize"] = kernel, ["borderType"] = borderName, ["roi"] = roi?.Type.ToString() ?? "FullImage", ["channels"] = channels, ["meanIntensity"] = meanIntensity, ["minIntensity"] = minIntensity, ["maxIntensity"] = maxIntensity, ["diagnostics"] = diag });
    }
}

public sealed class ImageFilterNode : IVisionNodeExecutor
{
    public string Type => "image.filter";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException(Type + " requires a non-empty image.");
        var method = node.GetString("method", "gaussian").Trim().ToLowerInvariant();
        if (method is not ("gaussian" or "median")) throw new InvalidOperationException("image.filter method must be gaussian or median.");
        var requested = node.GetInt("kernelSize", 5);
        if (requested is < 1 or > 31) throw new InvalidOperationException("image.filter kernelSize must be between 1 and 31.");
        var inner = method == "gaussian"
            ? GaussianBlurFilter.Execute(context, node, Type, cancellationToken)
            : MedianBlurFilter.Execute(context, node, Type, cancellationToken);
        var diag = CoordinateValidation.Diagnostics(Type, ("method", method), ("kernelSize", requested),
            ("width", image.Width), ("height", image.Height), ("channels", image.Mat.Channels()));
        var outputs = new Dictionary<string, VisionValue>(inner.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
        var filteredMat = outputs["image"].Require<VisionImage>("image").Mat;
        JLVisionOperators.MinMax(filteredMat, out double filterMin, out double filterMax);
        var filterMean = JLVisionOperators.Mean(filteredMat);
        outputs["meanIntensity"] = VisionValue.Double(filterMean);
        outputs["minIntensity"] = VisionValue.Double(filterMin);
        outputs["maxIntensity"] = VisionValue.Double(filterMax);
        var summary = new Dictionary<string, object?>(inner.Summary) { ["method"] = method, ["diagnostics"] = diag };
        return ValueTask.FromResult(inner with { Outputs = outputs, Summary = summary });
    }
}

public sealed class MorphologyNode : IVisionNodeExecutor
{
    public string Type => "image.morphology";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var operation = node.GetString("operation", "open").Trim();
        var normalizedOperation = operation.ToLowerInvariant();
        if (normalizedOperation is "whitetophat" or "blackbottomhat" or "gradient")
        {
            var grayOperation = normalizedOperation switch
            {
                "whitetophat" => "topHat",
                "blackbottomhat" => "bottomHat",
                _ => "gradient"
            };
            return ValueTask.FromResult(GrayMorphology.Execute(Type, context, node, grayOperation, cancellationToken, applyRoi: true));
        }

        var source = context.RequireImage("image").Mat;
        operation = normalizedOperation;
        var kernel = PreprocessImageHelpers.OddKernel(node.GetInt("kernelSize", 3), 1, 31);
        var iterations = Math.Clamp(node.GetInt("iterations", 1), 1, 10);
        var shape = node.GetString("shape", "rect").Trim().ToLowerInvariant();
        var roi = node.GetRoi();

        var morphShape = shape switch
        {
            "ellipse" => MorphShapes.Ellipse,
            "cross" => MorphShapes.Cross,
            "rect" => MorphShapes.Rect,
            _ => throw new InvalidOperationException("image.morphology shape must be rect, ellipse or cross.")
        };
        using var element = JLVisionOperators.GetStructuringElement(morphShape, new Size(kernel, kernel));
        var morphed = new Mat();
        switch (operation)
        {
            case "erode":
                JLVisionOperators.Erode(source, morphed, element, iterations: iterations);
                break;
            case "dilate":
                JLVisionOperators.Dilate(source, morphed, element, iterations: iterations);
                break;
            case "close":
                JLVisionOperators.MorphologyEx(source, morphed, MorphTypes.Close, element, iterations: iterations);
                break;
            case "open":
                JLVisionOperators.MorphologyEx(source, morphed, MorphTypes.Open, element, iterations: iterations);
                break;
            default:
                throw new InvalidOperationException("image.morphology operation must be erode, dilate, open, close, whiteTopHat, blackBottomHat or gradient.");
        }
        var output = PreprocessImageHelpers.ApplyRoiMask(morphed, roi, cancellationToken);
        if (output.Empty()) throw new InvalidOperationException("image.morphology produced an empty image.");
        JLVisionOperators.MinMax(output, out double minIntensity, out double maxIntensity);
        var meanIntensity = JLVisionOperators.Mean(output, out double stdIntensity);
        long nonZeroCount;
        if (output.Channels() == 1) nonZeroCount = JLVisionOperators.CountNonZero(output);
        else
        {
            using var nzGray = new Mat();
            JLVisionOperators.CvtColor(output, nzGray, ColorConversionCodes.BGR2GRAY);
            nonZeroCount = JLVisionOperators.CountNonZero(nzGray);
        }
        var diag = CoordinateValidation.Diagnostics(Type, ("operation", operation), ("kernelSize", kernel), ("iterations", iterations), ("shape", shape),
            ("meanIntensity", meanIntensity), ("minIntensity", minIntensity), ("maxIntensity", maxIntensity), ("stdIntensity", stdIntensity), ("nonZeroCount", nonZeroCount),
            ("width", output.Width), ("height", output.Height));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)), ["meanIntensity"] = VisionValue.Double(meanIntensity), ["minIntensity"] = VisionValue.Double(minIntensity), ["maxIntensity"] = VisionValue.Double(maxIntensity), ["stdIntensity"] = VisionValue.Double(stdIntensity), ["nonZeroCount"] = VisionValue.Integer(nonZeroCount), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["kernelSize"] = kernel,
                ["iterations"] = iterations,
                ["shape"] = shape,
                ["meanIntensity"] = meanIntensity, ["minIntensity"] = minIntensity, ["maxIntensity"] = maxIntensity, ["stdIntensity"] = stdIntensity, ["nonZeroCount"] = nonZeroCount,
                ["roi"] = roi?.Type.ToString() ?? "FullImage", ["diagnostics"] = diag
            }));
    }
}

internal static class AdaptiveThresholdCore
{

    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string nodeType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(nodeType + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var source = context.RequireImage("image").Mat;
        if (source.Empty()) throw new InvalidOperationException(nodeType + " requires a non-empty image.");
        // 光照变化场景的二值化主入口：局部窗口均值/高斯加权阈值（块大小必须为奇数）。
        var blockSize = PreprocessImageHelpers.OddKernel(node.GetInt("blockSize", 31), 3, 99);
        var c = node.GetDouble("c", 5);
        var method = node.GetString("method", "mean").Trim().ToLowerInvariant();
        if (method is not ("mean" or "gaussian")) throw new InvalidOperationException($"{nodeType} method '{method}' is unsupported; use mean or gaussian.");
        if (!double.IsFinite(c)) throw new InvalidOperationException($"{nodeType} c must be finite.");
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();
        var inputBitDepth = PreprocessImageHelpers.InputBitDepth(node, nodeType);
        var maxValue = node.GetDouble("maxValue", 255);
        if (!double.IsFinite(maxValue) || maxValue is < 1 or > 255)
            throw new InvalidOperationException($"{nodeType} maxValue (foreground value) must be finite in 1..255.");
        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray, inputBitDepth);
        Mat? output = null;
        try
        {
            // 自适应阈值取局部窗口：外扩半个窗口，ROI 内每个像素的邻域才与整图计算时一致。
            using var domain = ThresholdStatisticsDomain.Create(gray, roi, blockSize / 2, crop: true, cancellationToken);
            var binary = ImageTransformOutput.Create(target => JLVisionOperators.AdaptiveThreshold(
                domain.Gray, target, maxValue,
                method == "gaussian" ? AdaptiveThresholdTypes.GaussianC : AdaptiveThresholdTypes.MeanC,
                invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary,
                blockSize, c));
            // 域内结果贴回全画布，再按 ROI 精确清零（圆形/多边形/画笔的包围盒内也有框外像素）。
            output = PreprocessImageHelpers.ApplyMask(
                PreprocessImageHelpers.RestoreToCanvas(binary, domain.Rect, gray.Size(), cancellationToken),
                domain.Mask, cancellationToken);

            var fg = domain.PixelCount > 0 ? (double)JLVisionOperators.CountNonZero(output) / domain.PixelCount : 0;
            var diag = CoordinateValidation.Diagnostics(nodeType, ("blockSize", blockSize), ("c", c), ("method", method), ("invert", invert), ("foregroundRatio", fg), ("domainPixels", domain.PixelCount), ("autoGray", converted));
            cancellationToken.ThrowIfCancellationRequested();
            return (new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)), ["foregroundRatio"] = VisionValue.Double(fg), ["diagnostics"] = VisionValue.String(diag) },
                new Dictionary<string, object?>
                {
                    ["blockSize"] = blockSize,
                    ["maxValue"] = maxValue,
                    ["c"] = c,
                    ["method"] = method,
                    ["invert"] = invert,
                    ["autoGray"] = converted,
                    ["inputBitDepth"] = inputBitDepth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto",
                    ["byteScale"] = source.Depth() == MatType.CV_16U ? PreprocessImageHelpers.ByteScale(inputBitDepth) : 1d,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage",
                    ["foregroundRatio"] = Math.Round(fg, 6),
                    ["domainPixels"] = domain.PixelCount,
                    ["diagnostics"] = diag
                }));
        }
        catch
        {
            output?.Dispose();
            throw;
        }
        finally
        {
            ownedGray?.Dispose();
        }
    }
}

internal static class OtsuThresholdCore
{

    /// <summary>在给定灰度图上求 Otsu 全局阈值。Otsu 模式与手动模式的建议值共用，避免两套算法口径。</summary>
    public static double Level(Mat gray)
    {
        using var discard = new Mat();
        return JLVisionOperators.Threshold(gray, discard, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
    }

    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string nodeType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(nodeType + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var source = context.RequireImage("image").Mat;
        if (source.Empty()) throw new InvalidOperationException(nodeType + " requires a non-empty image.");
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();
        var inputBitDepth = PreprocessImageHelpers.InputBitDepth(node, nodeType);
        var maxValue = node.GetDouble("maxValue", 255);
        if (!double.IsFinite(maxValue) || maxValue is < 1 or > 255)
            throw new InvalidOperationException($"{nodeType} maxValue (foreground value) must be finite in 1..255.");

        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray, inputBitDepth);
        Mat? output = null;
        try
        {
            // 阈值只由统计域决定：ROI 存在时只用包围盒内的像素求 Otsu，ROI 外的高亮不再参与直方图。
            using var domain = ThresholdStatisticsDomain.Create(gray, roi, 0, crop: true, cancellationToken);
            // Otsu：由直方图自动求全局阈值——输出阈值供追溯与"光照漂移"监控。
            var computed = OtsuThresholdCore.Level(domain.Gray);
            var binary = ImageTransformOutput.Create(target => JLVisionOperators.Threshold(gray, target, computed, maxValue,
                invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary));
            output = PreprocessImageHelpers.ApplyMask(binary, domain.Mask, cancellationToken);

            var fg2 = domain.PixelCount > 0 ? (double)JLVisionOperators.CountNonZero(output) / domain.PixelCount : 0;
            var diag2 = CoordinateValidation.Diagnostics(nodeType, ("threshold", computed), ("invert", invert), ("foregroundRatio", fg2), ("domainPixels", domain.PixelCount), ("autoGray", converted));
            cancellationToken.ThrowIfCancellationRequested();
            return (new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(VisionImage.Own(output)),
                    ["threshold"] = VisionValue.Double(computed),
                    ["foregroundRatio"] = VisionValue.Double(fg2),
                    ["diagnostics"] = VisionValue.String(diag2)
                },
                new Dictionary<string, object?>
                {
                    ["threshold"] = computed,
                    ["maxValue"] = maxValue,
                    ["invert"] = invert,
                    ["autoGray"] = converted,
                    ["inputBitDepth"] = inputBitDepth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto",
                    ["byteScale"] = source.Depth() == MatType.CV_16U ? PreprocessImageHelpers.ByteScale(inputBitDepth) : 1d,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage",
                    ["foregroundRatio"] = Math.Round(fg2, 6),
                    ["domainPixels"] = domain.PixelCount
                }));
        }
        catch
        {
            output?.Dispose();
            throw;
        }
        finally
        {
            ownedGray?.Dispose();
        }
    }
}
