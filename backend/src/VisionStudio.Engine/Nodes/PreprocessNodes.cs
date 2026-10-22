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
    /// <summary>把输入规范化为单通道 CV_8U（3/4 通道转灰度；UInt16 按全量程缩放到 8 位）。</summary>
    public static Mat ToGray8(Mat source, out bool converted, out Mat? owned)
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
            // Match the recognition pipeline: map the full UInt16 sensor range to byte gray.
            // Other numeric depths use OpenCV's saturating conversion without an implicit
            // image-dependent normalization that would change edge-threshold meaning per frame.
            var scale = gray.Depth() == MatType.CV_16U ? 1d / 257d : 1d;
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

    /// <summary>ROI 掩膜：ROI 外清零（调用方交出 processed 的所有权；返回可继续持有的新 Mat）。</summary>
    public static Mat ApplyRoiMask(Mat processed, VisionRoi? roi, CancellationToken cancellationToken = default)
    {
        Mat? masked = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (roi is null) return processed;
            using var mask = roi.BuildMask(processed.Size(), cancellationToken);
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

    public static int OddKernel(int value, int min, int max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped % 2 == 1) return clamped;
        return clamped + 1 <= max ? clamped + 1 : clamped - 1;
    }
}

public sealed class GrayscaleNode : IVisionNodeExecutor
{
    public string Type => "image.grayscale";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException("image.grayscale requires a non-empty image.");
        var source = image.Mat;
        var mode = node.GetString("mode", "luminance").Trim().ToLowerInvariant();
        if (mode is not ("luminance" or "bt601" or "gray" or "average" or "mean" or "max")) throw new InvalidOperationException($"image.grayscale mode '{mode}' is unsupported; use luminance, average or max.");
        var weights = node.GetString("weights", "bt601").Trim().ToLowerInvariant();
        if (weights is not ("bt601" or "bt709" or "avg")) throw new InvalidOperationException("image.grayscale weights must be bt601, bt709 or avg.");
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var mat = new Mat();
        var converted = false;

        if (source.Channels() == 1)
        {
            // 已是单通道：原样输出（克隆以保持"输出归节点所有"的生命周期约定）。
            source.CopyTo(mat);
        }
        else if (mode is "average" or "mean")
        {
            using var bgr = new Mat();
            var color = source.Channels() == 4 ? Convert(source, bgr, ColorConversionCodes.BGRA2BGR) : source;
            JLVisionOperators.Split(color, out var channels);
            try
            {
                using var sum = new Mat();
                JLVisionOperators.AddWeighted(channels[0], 1d / 3, channels[1], 1d / 3, 0, sum);
                JLVisionOperators.AddWeighted(sum, 1, channels[2], 1d / 3, 0, mat);
            }
            finally
            {
                foreach (var channel in channels) channel.Dispose();
            }
        }
        else if (mode == "max")
        {
            using var bgr = new Mat();
            var color = source.Channels() == 4 ? Convert(source, bgr, ColorConversionCodes.BGRA2BGR) : source;
            JLVisionOperators.Split(color, out var channels);
            try
            {
                JLVisionOperators.Max(channels[0], channels[1], mat);
                JLVisionOperators.Max(mat, channels[2], mat);
            }
            finally
            {
                foreach (var channel in channels) channel.Dispose();
            }
        }
        else if (mode is "luminance" or "bt601" or "gray")
        {
            if (weights == "bt601" && source.Channels() != 1)
                JLVisionOperators.CvtColor(source, mat, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            else
            {
                using var bgr = new Mat();
                var color = source.Channels() == 4 ? Convert(source, bgr, ColorConversionCodes.BGRA2BGR) : source;
                JLVisionOperators.Split(color, out var channels);
                try
                {
                    var (bw, gw, rw) = weights switch
                    {
                        "bt709" => (0.0722, 0.7152, 0.2126),
                        "avg" => (1d / 3, 1d / 3, 1d / 3),
                        _ => (0.114, 0.587, 0.299)
                    };
                    using var sum = new Mat();
                    JLVisionOperators.AddWeighted(channels[0], bw, channels[1], gw, 0, sum);
                    JLVisionOperators.AddWeighted(sum, 1, channels[2], rw, 0, mat);
                }
                finally
                {
                    foreach (var channel in channels) channel.Dispose();
                }
            }
        }

        converted = source.Channels() != 1;
        var effectiveMode = source.Channels() == 1 ? "passthrough" : mode;
        JLVisionOperators.MinMax(mat, out double minIntensity, out double maxIntensity);
        var meanIntensity = JLVisionOperators.Mean(mat, out _);
        var diag = CoordinateValidation.Diagnostics(Type, ("mode", effectiveMode), ("weights", weights), ("inputChannels", source.Channels()), ("outputChannels", mat.Channels()),
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
            new Dictionary<string, object?>
            {
                ["mode"] = effectiveMode,
                ["weights"] = weights,
                ["autoGray"] = converted,
                ["channels"] = mat.Channels(),
                ["meanIntensity"] = meanIntensity,
                ["diagnostics"] = diag
            }));
    }

    private static Mat Convert(Mat source, Mat scratch, ColorConversionCodes code)
    {
        JLVisionOperators.CvtColor(source, scratch, code);
        return scratch;
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
            // gray/luminance uses the same BT.601 conversion as image.grayscale's default mode.
            if (source.Channels() == 1) source.CopyTo(mat);
            else JLVisionOperators.CvtColor(source, mat, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        }

        JLVisionOperators.MinMax(mat, out double minIntensity, out double maxIntensity);
        var meanIntensity = JLVisionOperators.Mean(mat, out _);
        var diag = CoordinateValidation.Diagnostics(Type, ("channel", channel), ("inputChannels", source.Channels()), ("outputChannels", mat.Channels()),
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
            new Dictionary<string, object?> { ["channel"] = channel, ["channels"] = mat.Channels(), ["meanIntensity"] = meanIntensity, ["diagnostics"] = diag }));

        static Mat Bgr(Mat source, Mat scratch)
        {
            JLVisionOperators.CvtColor(source, scratch, ColorConversionCodes.BGRA2BGR);
            return scratch;
        }
    }
}

public sealed class GaussianBlurNode : IVisionNodeExecutor
{
    public string Type => "image.blur.gaussian";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException("image.blur.gaussian requires a non-empty image.");
        var source = image.Mat;
        var channels = source.Channels();
        if (channels is < 1 or > 4) throw new InvalidOperationException("image.blur.gaussian supports 1..4 channel images.");
        var requested = node.GetInt("kernelSize", 5);
        if (requested is < 1 or > 31) throw new InvalidOperationException("image.blur.gaussian kernelSize must be between 1 and 31.");
        var kernel = PreprocessImageHelpers.OddKernel(requested, 1, 31);
        var sigmaX = node.GetDouble("sigmaX", 0);
        var sigmaY = node.GetDouble("sigmaY", 0);
        if (!double.IsFinite(sigmaX) || !double.IsFinite(sigmaY) || sigmaX < 0 || sigmaY < 0)
            throw new InvalidOperationException("image.blur.gaussian sigmaX/sigmaY must be finite and >= 0 (0 derives sigma from kernel size).");
        var borderName = node.GetString("borderType", "default").Trim().ToLowerInvariant();
        var border = borderName switch
        {
            "default" => BorderTypes.Default,
            "constant" => BorderTypes.Constant,
            "replicate" => BorderTypes.Replicate,
            "reflect" => BorderTypes.Reflect,
            "wrap" => BorderTypes.Wrap,
            _ => throw new InvalidOperationException("image.blur.gaussian borderType must be default, constant, replicate, reflect or wrap.")
        };
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
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
        var diag = CoordinateValidation.Diagnostics(Type, ("kernelSize", kernel), ("requestedKernelSize", requested),
            ("sigmaX", sigmaX), ("sigmaY", sigmaY), ("borderType", borderName),
            ("meanIntensity", meanIntensity), ("minIntensity", minIntensity), ("maxIntensity", maxIntensity),
            ("width", output.Width), ("height", output.Height), ("channels", channels));
        return ValueTask.FromResult(new NodeExecutorResult(
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
            }));
    }
}

public sealed class MedianBlurNode : IVisionNodeExecutor
{
    public string Type => "image.blur.median";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException("image.blur.median requires a non-empty image.");
        var source = image.Mat;
        var channels = source.Channels();
        if (channels is not (1 or 3 or 4)) throw new InvalidOperationException("image.blur.median supports 1, 3 or 4 channel images only.");
        var requested = node.GetInt("kernelSize", 5);
        if (requested is < 3 or > 31) throw new InvalidOperationException("image.blur.median kernelSize must be between 3 and 31.");
        var kernel = PreprocessImageHelpers.OddKernel(requested, 3, 31);
        if (kernel > Math.Max(image.Width, image.Height) * 2 + 1)
            throw new InvalidOperationException("image.blur.median kernelSize is unreasonably large for the image dimensions.");
        var borderName = node.GetString("borderType", "replicate").Trim().ToLowerInvariant();
        var border = borderName switch
        {
            "constant" => BorderTypes.Constant,
            "replicate" => BorderTypes.Replicate,
            "reflect" => BorderTypes.Reflect,
            "wrap" => BorderTypes.Wrap,
            _ => throw new InvalidOperationException("image.blur.median borderType must be constant, replicate, reflect or wrap.")
        };
        var roi = node.GetRoi();
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
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
        var diag = CoordinateValidation.Diagnostics(Type, ("kernelSize", kernel), ("requestedKernelSize", requested), ("meanIntensity", meanIntensity), ("minIntensity", minIntensity), ("maxIntensity", maxIntensity),
            ("borderType", borderName), ("pad", pad), ("width", output.Width), ("height", output.Height), ("channels", channels));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)), ["meanIntensity"] = VisionValue.Double(meanIntensity), ["minIntensity"] = VisionValue.Double(minIntensity), ["maxIntensity"] = VisionValue.Double(maxIntensity), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["kernelSize"] = kernel, ["borderType"] = borderName, ["roi"] = roi?.Type.ToString() ?? "FullImage", ["channels"] = channels, ["meanIntensity"] = meanIntensity, ["minIntensity"] = minIntensity, ["maxIntensity"] = maxIntensity, ["diagnostics"] = diag }));
    }
}

public sealed class ImageFilterNode : IVisionNodeExecutor
{
    public string Type => "image.filter";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));


        var image = context.RequireImage("image");
        if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException("image.filter requires a non-empty image.");
        var method = node.GetString("method", "gaussian").Trim().ToLowerInvariant();
        if (method is not ("gaussian" or "median")) throw new InvalidOperationException("image.filter method must be gaussian or median.");
        var requested = node.GetInt("kernelSize", 5);
        if (requested is < 3 or > 31) throw new InvalidOperationException("image.filter kernelSize must be between 3 and 31.");
        IVisionNodeExecutor executor = method switch
        {
            "gaussian" => new GaussianBlurNode(),
            "median" => new MedianBlurNode(),
            _ => throw new InvalidOperationException("Filter method must be gaussian or median.")
        };
        var result = await executor.ExecuteAsync(context, node with { Type = executor.Type }, cancellationToken).ConfigureAwait(false);
        var diag = CoordinateValidation.Diagnostics(Type, ("method", method), ("kernelSize", requested),
            ("width", image.Width), ("height", image.Height), ("channels", image.Mat.Channels()));
        var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
        var filteredMat = outputs["image"].Require<VisionImage>("image").Mat;
        JLVisionOperators.MinMax(filteredMat, out double filterMin, out double filterMax);
        var filterMean = JLVisionOperators.Mean(filteredMat);
        outputs["meanIntensity"] = VisionValue.Double(filterMean);
        outputs["minIntensity"] = VisionValue.Double(filterMin);
        outputs["maxIntensity"] = VisionValue.Double(filterMax);
        var summary = new Dictionary<string, object?>(result.Summary) { ["method"] = method, ["diagnostics"] = diag };
        return result with { Outputs = outputs, Summary = summary };
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

public sealed class AdaptiveThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.threshold.adaptive";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var source = context.RequireImage("image").Mat;
        if (source.Empty()) throw new InvalidOperationException(Type + " requires a non-empty image.");
        // 光照变化场景的二值化主入口：局部窗口均值/高斯加权阈值（块大小必须为奇数）。
        var blockSize = PreprocessImageHelpers.OddKernel(node.GetInt("blockSize", 31), 3, 99);
        var c = node.GetDouble("c", 5);
        var method = node.GetString("method", "mean").Trim().ToLowerInvariant();
        if (method is not ("mean" or "gaussian")) throw new InvalidOperationException($"image.threshold.adaptive method '{method}' is unsupported; use mean or gaussian.");
        if (!double.IsFinite(c)) throw new InvalidOperationException("image.threshold.adaptive c must be finite.");
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();
        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray);
        Mat? output = null;
        try
        {
            var binary = ImageTransformOutput.Create(target => JLVisionOperators.AdaptiveThreshold(
                gray, target, 255,
                method == "gaussian" ? AdaptiveThresholdTypes.GaussianC : AdaptiveThresholdTypes.MeanC,
                invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary,
                blockSize, c));
            output = PreprocessImageHelpers.ApplyRoiMask(binary, roi, cancellationToken);

            var fg = output.Total() > 0 ? (double)JLVisionOperators.CountNonZero(output) / output.Total() : 0;
            var diag = CoordinateValidation.Diagnostics(Type, ("blockSize", blockSize), ("c", c), ("method", method), ("invert", invert), ("foregroundRatio", fg), ("autoGray", converted));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)), ["foregroundRatio"] = VisionValue.Double(fg), ["diagnostics"] = VisionValue.String(diag) },
                new Dictionary<string, object?>
                {
                    ["blockSize"] = blockSize,
                    ["c"] = c,
                    ["method"] = method,
                    ["invert"] = invert,
                    ["autoGray"] = converted,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage",
                    ["foregroundRatio"] = Math.Round(fg, 6),
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

public sealed class OtsuThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.threshold.otsu";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var source = context.RequireImage("image").Mat;
        if (source.Empty()) throw new InvalidOperationException(Type + " requires a non-empty image.");
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();

        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray);
        Mat? output = null;
        try
        {
            // Otsu：由直方图自动求全局阈值——输出阈值供追溯与"光照漂移"监控。
            var computed = 0d;
            var binary = ImageTransformOutput.Create(target => computed = JLVisionOperators.Threshold(gray, target, 0, 255,
                ThresholdTypes.Otsu | (invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary)));
            output = PreprocessImageHelpers.ApplyRoiMask(binary, roi, cancellationToken);

            var fg2 = output.Total() > 0 ? (double)JLVisionOperators.CountNonZero(output) / output.Total() : 0;
            var diag2 = CoordinateValidation.Diagnostics(Type, ("threshold", computed), ("invert", invert), ("foregroundRatio", fg2), ("autoGray", converted));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NodeExecutorResult(
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
                    ["invert"] = invert,
                    ["autoGray"] = converted,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage"
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
