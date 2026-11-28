using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 频域与自定义卷积（image.convolve / image.fftFilter，2026-10-27）。
///
/// 现场缺口：`jlvision.imageOp` 的 107 个算子全在空间域。两类场景因此只能绕行：
/// 1) 工程师手上有一个从论文/客户规范里抄来的 5×5 核（方向增强、脊线、非对称锐化），
///    现有节点的固定高斯/中值/形态学都表达不了 —— 用 `image.convolve` 直接喂核；
/// 2) 周期性纹理（织造网格、拉丝、喷砂、屏幕摩尔纹）在空间域无论怎么调都清不干净，
///    必须把固定周期对应的频点挖掉 —— 用 `image.fftFilter` 按"波长（像素）"挖带。
///
/// 两个节点都以 32F 中间层计算再回到源位深（8/16 位饱和截断），因此负旁瓣不会被静默绕回成亮边。
/// 参数用工程单位而不是归一化频率：`wavelengthMinPx` / `wavelengthMaxPx` 就是"几像素一个周期"，
/// 与现场"网格间距 3~5 像素"的说法直接对应，避免用户自己换算 cycles/pixel。
/// </summary>
internal static class TextureFilterMath
{
    internal const int MaxKernelSize = 31;
    internal const double MaxKernelValue = 10_000;

    internal static void ValidateDepth(Mat source, string nodeType)
    {
        if (source.Empty())
            throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        if (source.Depth() is not (MatType.CV_8U or MatType.CV_16U or MatType.CV_32F))
            throw new InvalidOperationException(
                $"{nodeType} supports 8-bit, 16-bit or float32 images; received {source.Depth()}.");
        if (source.Channels() is < 1 or > 4)
            throw new InvalidOperationException(
                $"{nodeType} supports 1..4 channels; received {source.Channels()}.");
    }

}

/// <summary>
/// 自定义卷积核（image.convolve）：工程师直接给出核矩阵，支持归一化、偏置、锚点与边界策略。
/// 这是"论文里的核 / 客户规范里的核"落地的最短路径，也是现有固定滤波器的兜底。
/// </summary>
public sealed class CustomConvolutionNode : IVisionNodeExecutor
{
    public string Type => "image.convolve";

    private const string DefaultKernel = "[[0,-1,0],[-1,5,-1],[0,-1,0]]";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        TextureFilterMath.ValidateDepth(source.Mat, Type);

        var kernel = ParseKernel(node.GetString("kernelJson", DefaultKernel));
        var normalize = node.GetBool("normalize", true);
        var kernelSum = kernel.SelectMany(row => row).Sum();
        var normalizeEpsilon = node.GetDouble("normalizeEpsilon", 1e-12);
        if (!double.IsFinite(normalizeEpsilon) || normalizeEpsilon <= 0)
            throw new InvalidOperationException("image.convolve normalizeEpsilon must be finite and > 0.");
        if (normalize)
        {
            if (!double.IsFinite(kernelSum) || Math.Abs(kernelSum) < normalizeEpsilon)
                throw new InvalidOperationException(
                    $"image.convolve cannot normalize a kernel with a zero or near-zero sum {kernelSum:R} (below normalizeEpsilon={normalizeEpsilon:R}); turn Normalize off for edge/difference kernels.");
        }
        var inputRange = PreprocessP1Diagnostics.Range(source.Mat);

        var delta = node.GetDouble("delta", 0);
        if (!double.IsFinite(delta) || Math.Abs(delta) > 100_000)
            throw new InvalidOperationException("image.convolve delta must be finite within +/-100000.");

        var anchorX = node.GetInt("anchorX", -1);
        var anchorY = node.GetInt("anchorY", -1);
        var cols = kernel[0].Length;
        var rows = kernel.Length;
        if (anchorX < -1 || anchorX >= cols)
            throw new InvalidOperationException($"image.convolve anchorX must be -1 (center) or 0..{cols - 1}.");
        if (anchorY < -1 || anchorY >= rows)
            throw new InvalidOperationException($"image.convolve anchorY must be -1 (center) or 0..{rows - 1}.");
        var anchor = anchorX < 0 || anchorY < 0 ? (Point?)null : new Point(anchorX, anchorY);

        var borderType = node.GetString("borderType", "reflect101").Trim().ToLowerInvariant();
        var border = borderType switch
        {
            "reflect101" => BorderTypes.Reflect101,
            "reflect" => BorderTypes.Reflect,
            "replicate" => BorderTypes.Replicate,
            "constant" => BorderTypes.Constant,
            _ => throw new InvalidOperationException(
                "image.convolve borderType must be reflect101, reflect, replicate or constant.")
        };

        var image = VisionImage.Own(FrequencyFilterAlgorithms.Convolve(
            source.Mat, kernel, normalize ? kernelSum : 1, anchor, delta, border, cancellationToken));
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            (int Low, int High, double Ratio) saturation = source.Mat.Depth() == MatType.CV_8U
                ? PreprocessP1Diagnostics.ByteSaturation(image.Mat, 255) : (0, 0, 0);
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("kernelWidth", cols), ("kernelHeight", rows), ("kernelSum", kernelSum),
                ("normalized", normalize), ("normalizeEpsilon", normalizeEpsilon),
                ("normalizationFactor", normalize ? kernelSum : 1d), ("delta", delta),
                ("anchorX", anchor?.X ?? cols / 2), ("anchorY", anchor?.Y ?? rows / 2),
                ("anchorMode", anchor is null ? "center" : "explicit"),
                ("inputMin", inputRange.Min), ("inputMax", inputRange.Max),
                ("saturatedLowPixels", saturation.Low), ("saturatedHighPixels", saturation.High), ("saturatedRatio", saturation.Ratio),
                ("borderType", borderType), ("elapsedMs", timer.Elapsed.TotalMilliseconds));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = VisionValue.Image(image),
                    ["width"] = VisionValue.Integer(image.Width),
                    ["height"] = VisionValue.Integer(image.Height),
                    ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                    ["kernelWidth"] = VisionValue.Integer(cols),
                    ["kernelHeight"] = VisionValue.Integer(rows),
                    ["kernelSum"] = VisionValue.Double(kernelSum),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>
                {
                    ["kernelWidth"] = cols, ["kernelHeight"] = rows, ["kernelSum"] = kernelSum,
                    ["normalized"] = normalize, ["delta"] = delta, ["borderType"] = borderType,
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }

    private static double[][] ParseKernel(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("image.convolve kernelJson is required.");

        double[][]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<double[][]>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"image.convolve kernelJson is invalid: {ex.Message}", ex);
        }

        if (parsed is null || parsed.Length == 0)
            throw new InvalidOperationException("image.convolve kernelJson must contain at least one row.");
        if (parsed.Length > TextureFilterMath.MaxKernelSize)
            throw new InvalidOperationException(
                $"image.convolve kernel has {parsed.Length} rows; the limit is {TextureFilterMath.MaxKernelSize}.");

        var cols = parsed[0].Length;
        if (cols == 0)
            throw new InvalidOperationException("image.convolve kernel rows must not be empty.");
        if (cols > TextureFilterMath.MaxKernelSize)
            throw new InvalidOperationException(
                $"image.convolve kernel has {cols} columns; the limit is {TextureFilterMath.MaxKernelSize}.");

        for (var row = 0; row < parsed.Length; row++)
        {
            if (parsed[row] is null || parsed[row].Length != cols)
                throw new InvalidOperationException(
                    $"image.convolve kernel is not rectangular: row {row} has {parsed[row]?.Length ?? 0} values, expected {cols}.");
            foreach (var value in parsed[row])
            {
                if (!double.IsFinite(value) || Math.Abs(value) > TextureFilterMath.MaxKernelValue)
                    throw new InvalidOperationException(
                        $"image.convolve kernel values must be finite within +/-{TextureFilterMath.MaxKernelValue}.");
            }
        }

        return parsed;
    }

}

/// <summary>
/// 频域滤波（image.fftFilter）：按"周期（像素）"在频谱上挖带或保留带，用于周期性纹理与摩尔纹。
/// 支持低通/高通/带通/带阻 + 方向窗口（梳状/拉丝纹理只在一个方向上出现）。
/// </summary>
public sealed class FftFilterNode : IVisionNodeExecutor
{
    public string Type => "image.fftFilter";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        TextureFilterMath.ValidateDepth(source.Mat, Type);
        var options = ReadOptions(node);

        var result = FrequencyFilterAlgorithms.Filter(source.Mat, options, cancellationToken);
        var paddedWidth = result.PaddedWidth;
        var paddedHeight = result.PaddedHeight;
        var passRatio = result.PassRatio;
        var channels = source.Mat.Channels();
        var image = VisionImage.Own(result.Image);
        timer.Stop();

        var filter = options.Filter;
        var channelMeans = Enumerable.Range(0, channels).Select(index =>
        {
            using var channel = new Mat();
            Cv2.ExtractChannel(source.Mat, channel, index);
            return Cv2.Mean(channel).Val0;
        }).ToArray();
        var channelMeansJson = System.Text.Json.JsonSerializer.Serialize(channelMeans);
        var outputRange = PreprocessP1Diagnostics.Range(image.Mat);
        var saturatedHigh = 0;
        var saturatedLow = 0;
        if (source.Mat.Depth() == MatType.CV_8U)
        {
            var saturation = PreprocessP1Diagnostics.ByteSaturation(image.Mat, 255);
            saturatedLow = saturation.Low; saturatedHigh = saturation.High;
        }
        else if (source.Mat.Depth() == MatType.CV_16U)
        {
            using var low = new Mat(); using var high = new Mat();
            Cv2.Compare(image.Mat, Scalar.All(0), low, CmpTypes.LE);
            Cv2.Compare(image.Mat, Scalar.All(65535), high, CmpTypes.GE);
            saturatedLow = Cv2.CountNonZero(low); saturatedHigh = Cv2.CountNonZero(high);
        }
        var saturationRatio = (double)(saturatedLow + saturatedHigh) / Math.Max(1L, (long)image.Mat.Total() * channels);
        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("filter", filter), ("wavelengthMinPx", options.WavelengthMinPx),
                ("wavelengthMaxPx", options.WavelengthMaxPx), ("directionDeg", options.DirectionDeg),
                ("directionWidthDeg", options.DirectionWidthDeg), ("maskBlurPx", options.MaskBlurPx),
                ("keepDc", options.KeepDc), ("outputMode", options.OutputMode),
                ("sourceWidth", source.Mat.Width), ("sourceHeight", source.Mat.Height),
                ("paddedWidth", paddedWidth), ("paddedHeight", paddedHeight),
                ("paddingPosition", "bottom-right; reflect101"), ("paddingContract", "zero-padded size with reflect101 extension; periodic wrap-around at padded border"),
                ("frequencyUnit", "cycles/pixel"), ("directionBasis", "+x=0deg, +y=90deg, y-down"),
                ("maskPassRatio", passRatio), ("nearEmptyMask", passRatio < 1e-6),
                ("meanRestore", options.OutputMode == "removed" && source.Mat.Depth() != MatType.CV_32F ? "per-channel input mean" : "none (pure difference)"),
                ("channelMeans", channelMeans), ("outputMin", outputRange.Min), ("outputMax", outputRange.Max),
                ("saturatedLowPixels", saturatedLow), ("saturatedHighPixels", saturatedHigh), ("saturatedRatio", saturationRatio),
                ("channels", channels),
                ("elapsedMs", timer.Elapsed.TotalMilliseconds));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = VisionValue.Image(image),
                    ["width"] = VisionValue.Integer(image.Width),
                    ["height"] = VisionValue.Integer(image.Height),
                    ["channels"] = VisionValue.Integer(channels),
                    ["maskPassRatio"] = VisionValue.Double(passRatio),
                    ["paddedWidth"] = VisionValue.Integer(paddedWidth),
                    ["paddedHeight"] = VisionValue.Integer(paddedHeight),
                    ["channelMeansJson"] = VisionValue.String(channelMeansJson),
                    ["saturatedPixelCount"] = VisionValue.Integer(saturatedLow + saturatedHigh),
                    ["saturatedRatio"] = VisionValue.Double(saturationRatio),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>
                {
                    ["filter"] = filter,
                    ["wavelengthMinPx"] = options.WavelengthMinPx,
                    ["wavelengthMaxPx"] = options.WavelengthMaxPx,
                    ["directionDeg"] = options.DirectionDeg,
                    ["directionWidthDeg"] = options.DirectionWidthDeg,
                    ["maskPassRatio"] = passRatio,
                    ["paddedWidth"] = paddedWidth,
                    ["paddedHeight"] = paddedHeight,
                    ["channelMeansJson"] = channelMeansJson,
                    ["saturatedRatio"] = saturationRatio,
                    ["keepDc"] = options.KeepDc,
                    ["outputMode"] = options.OutputMode,
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }

    private static FrequencyFilterAlgorithms.Options ReadOptions(NodeDefinition node)
    {
        var filter = node.GetString("filter", "bandstop").Trim().ToLowerInvariant();
        if (filter is not ("lowpass" or "highpass" or "bandpass" or "bandstop"))
            throw new InvalidOperationException("image.fftFilter filter must be lowpass, highpass, bandpass or bandstop.");

        var wavelengthMinPx = node.GetDouble("wavelengthMinPx", 3);
        var wavelengthMaxPx = node.GetDouble("wavelengthMaxPx", 30);
        foreach (var (name, value) in new[] { ("wavelengthMinPx", wavelengthMinPx), ("wavelengthMaxPx", wavelengthMaxPx) })
        {
            if (!double.IsFinite(value) || value is < 1 or > 100_000)
                throw new InvalidOperationException($"image.fftFilter {name} must be finite within 1..100000 pixels.");
        }
        if (filter is "bandpass" or "bandstop" && wavelengthMinPx > wavelengthMaxPx)
            throw new InvalidOperationException(
                $"image.fftFilter wavelengthMinPx ({wavelengthMinPx}) must not exceed wavelengthMaxPx ({wavelengthMaxPx}).");

        var directionDeg = node.GetDouble("directionDeg", 0);
        if (!double.IsFinite(directionDeg) || directionDeg is < -360 or > 360)
            throw new InvalidOperationException("image.fftFilter directionDeg must be finite within -360..360.");
        var directionWidthDeg = node.GetDouble("directionWidthDeg", 0);
        if (!double.IsFinite(directionWidthDeg) || directionWidthDeg is < 0 or > 360)
            throw new InvalidOperationException("image.fftFilter directionWidthDeg must be finite within 0..360 (0 disables the window).");

        var maskBlurPx = node.GetDouble("maskBlurPx", 0);
        if (!double.IsFinite(maskBlurPx) || maskBlurPx is < 0 or > 8)
            throw new InvalidOperationException("image.fftFilter maskBlurPx must be finite within 0..8.");

        var outputMode = node.GetString("outputMode", "filtered").Trim().ToLowerInvariant();
        if (outputMode is not ("filtered" or "removed"))
            throw new InvalidOperationException("image.fftFilter outputMode must be filtered or removed.");

        var maxPixels = node.GetInt("maxPixels", 16_000_000);
        if (maxPixels is < 1000 or > 64_000_000)
            throw new InvalidOperationException("image.fftFilter maxPixels must be within 1000..64000000.");

        return new FrequencyFilterAlgorithms.Options(filter, wavelengthMinPx, wavelengthMaxPx, directionDeg, directionWidthDeg,
            maskBlurPx, node.GetBool("keepDc", true), outputMode, maxPixels);
    }
}
