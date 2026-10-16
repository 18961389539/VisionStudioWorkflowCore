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
    public static Mat ApplyRoiMask(Mat processed, VisionRoi? roi)
    {
        if (roi is null) return processed;
        using var mask = roi.BuildMask(processed.Size());
        var masked = new Mat(processed.Rows, processed.Cols, processed.Type(), Scalar.All(0));
        processed.CopyTo(masked, mask);
        processed.Dispose();
        return masked;
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
        var source = context.RequireImage("image").Mat;
        var mode = node.GetString("mode", "luminance").Trim().ToLowerInvariant();
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
        else
        {
            // luminance：BT.601 加权（OpenCV 默认 BGR2GRAY）。
            JLVisionOperators.CvtColor(source, mat, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        }

        converted = source.Channels() != 1;
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(mat)) },
            new Dictionary<string, object?>
            {
                ["mode"] = source.Channels() == 1 ? "passthrough" : mode,
                ["autoGray"] = converted,
                ["channels"] = mat.Channels()
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
        var source = context.RequireImage("image").Mat;
        var channel = node.GetString("channel", "gray").Trim().ToLowerInvariant();
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

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(mat)) },
            new Dictionary<string, object?> { ["channel"] = channel, ["channels"] = mat.Channels() }));

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
        var source = context.RequireImage("image").Mat;
        var kernel = PreprocessImageHelpers.OddKernel(node.GetInt("kernelSize", 5), 1, 31);
        var sigmaX = node.GetDouble("sigmaX", 0);
        var sigmaY = node.GetDouble("sigmaY", 0);
        var roi = node.GetRoi();

        var blurred = new Mat();
        JLVisionOperators.GaussianBlur(source, blurred, new Size(kernel, kernel), sigmaX, sigmaY);
        var output = PreprocessImageHelpers.ApplyRoiMask(blurred, roi);

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) },
            new Dictionary<string, object?>
            {
                ["kernelSize"] = kernel,
                ["sigmaX"] = sigmaX <= 0 ? "derived" : sigmaX,
                ["sigmaY"] = sigmaY <= 0 ? "derived" : sigmaY,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            }));
    }
}

public sealed class MedianBlurNode : IVisionNodeExecutor
{
    public string Type => "image.blur.median";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var source = context.RequireImage("image").Mat;
        // 中值滤波要求奇数核（OpenCV 对偶数核直接抛错）——声明范围 + 运行时奇偶收敛双重保证。
        var kernel = PreprocessImageHelpers.OddKernel(node.GetInt("kernelSize", 5), 3, 31);
        var roi = node.GetRoi();

        var blurred = new Mat();
        JLVisionOperators.MedianBlur(source, blurred, kernel);
        var output = PreprocessImageHelpers.ApplyRoiMask(blurred, roi);

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) },
            new Dictionary<string, object?> { ["kernelSize"] = kernel, ["roi"] = roi?.Type.ToString() ?? "FullImage" }));
    }
}

public sealed class ImageFilterNode : IVisionNodeExecutor
{
    public string Type => "image.filter";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var method = node.GetString("method", "gaussian").Trim().ToLowerInvariant();
        IVisionNodeExecutor executor = method switch
        {
            "gaussian" => new GaussianBlurNode(),
            "median" => new MedianBlurNode(),
            _ => throw new InvalidOperationException("Filter method must be gaussian or median.")
        };
        var result = await executor.ExecuteAsync(context, node with { Type = executor.Type }, cancellationToken).ConfigureAwait(false);
        var summary = new Dictionary<string, object?>(result.Summary) { ["method"] = method };
        return result with { Summary = summary };
    }
}

public sealed class MorphologyNode : IVisionNodeExecutor
{
    public string Type => "image.morphology";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
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
            return ValueTask.FromResult(GrayMorphology.Execute(context, node, grayOperation, cancellationToken, applyRoi: true));
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
            _ => MorphShapes.Rect
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
            default:
                operation = "open";
                JLVisionOperators.MorphologyEx(source, morphed, MorphTypes.Open, element, iterations: iterations);
                break;
        }
        var output = PreprocessImageHelpers.ApplyRoiMask(morphed, roi);

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) },
            new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["kernelSize"] = kernel,
                ["iterations"] = iterations,
                ["shape"] = shape,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            }));
    }
}

public sealed class AdaptiveThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.threshold.adaptive";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var source = context.RequireImage("image").Mat;
        // 光照变化场景的二值化主入口：局部窗口均值/高斯加权阈值（块大小必须为奇数）。
        var blockSize = PreprocessImageHelpers.OddKernel(node.GetInt("blockSize", 31), 3, 99);
        var c = node.GetDouble("c", 5);
        var method = node.GetString("method", "mean").Trim().ToLowerInvariant();
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();

        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray);
        try
        {
            var binary = new Mat();
            JLVisionOperators.AdaptiveThreshold(
                gray, binary, 255,
                method == "gaussian" ? AdaptiveThresholdTypes.GaussianC : AdaptiveThresholdTypes.MeanC,
                invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary,
                blockSize, c);
            var output = PreprocessImageHelpers.ApplyRoiMask(binary, roi);

            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) },
                new Dictionary<string, object?>
                {
                    ["blockSize"] = blockSize,
                    ["c"] = c,
                    ["method"] = method == "gaussian" ? "gaussian" : "mean",
                    ["invert"] = invert,
                    ["autoGray"] = converted,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage"
                }));
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
        var source = context.RequireImage("image").Mat;
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();

        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray);
        try
        {
            var binary = new Mat();
            // Otsu：由直方图自动求全局阈值——输出阈值供追溯与"光照漂移"监控。
            var computed = JLVisionOperators.Threshold(gray, binary, 0, 255,
                ThresholdTypes.Otsu | (invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary));
            var output = PreprocessImageHelpers.ApplyRoiMask(binary, roi);

            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(VisionImage.Own(output)),
                    ["threshold"] = VisionValue.Double(computed)
                },
                new Dictionary<string, object?>
                {
                    ["threshold"] = computed,
                    ["invert"] = invert,
                    ["autoGray"] = converted,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage"
                }));
        }
        finally
        {
            ownedGray?.Dispose();
        }
    }
}
