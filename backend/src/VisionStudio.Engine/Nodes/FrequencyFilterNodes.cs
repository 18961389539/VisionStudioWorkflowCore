using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

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

    /// <summary>把图像按其原位深重新落地；32F 结果走饱和截断，不绕回。</summary>
    internal static Mat ToSourceDepth(Mat source32, MatType sourceType)
    {
        var result = new Mat();
        source32.ConvertTo(result, sourceType);
        return result;
    }

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

    /// <summary>频率方向按 180° 折叠：FFT 共轭对称，(θ) 与 (θ+180°) 是同一个纹理方向。</summary>
    internal static double FoldDirectionDeg(double degrees)
    {
        var folded = degrees % 180.0;
        if (folded < 0) folded += 180.0;
        return folded;
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
        if (normalize)
        {
            if (!double.IsFinite(kernelSum) || Math.Abs(kernelSum) < 1e-12)
                throw new InvalidOperationException(
                    "image.convolve cannot normalize a kernel whose sum is zero (edge/difference kernels). Turn Normalize off.");
        }

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

        using var kernelMat = BuildKernelMat(kernel, normalize ? kernelSum : 1);
        using var source32 = new Mat();
        source.Mat.ConvertTo(source32, MatType.CV_32F);
        using var filtered32 = new Mat();
        Cv2.Filter2D(source32, filtered32, MatType.CV_32F, kernelMat, anchor, delta, border);
        cancellationToken.ThrowIfCancellationRequested();

        var image = VisionImage.Own(TextureFilterMath.ToSourceDepth(filtered32, source.Mat.Type()));
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("kernelWidth", cols), ("kernelHeight", rows), ("kernelSum", kernelSum),
                ("normalized", normalize), ("delta", delta), ("anchorX", anchorX), ("anchorY", anchorY),
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

    private static Mat BuildKernelMat(double[][] kernel, double divisor)
    {
        var rows = kernel.Length;
        var cols = kernel[0].Length;
        var buffer = new float[rows * cols];
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < cols; column++)
                buffer[row * cols + column] = (float)(kernel[row][column] / divisor);

        var mat = new Mat(rows, cols, MatType.CV_32FC1);
        try
        {
            mat.SetArray(buffer);
            return mat;
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }
}

/// <summary>
/// 频域滤波（image.fftFilter）：按"周期（像素）"在频谱上挖带或保留带，用于周期性纹理与摩尔纹。
/// 支持低通/高通/带通/带阻 + 方向窗口（梳状/拉丝纹理只在一个方向上出现）。
/// </summary>
public sealed class FftFilterNode : IVisionNodeExecutor
{
    public string Type => "image.fftFilter";

    private sealed record Options(
        string Filter, double WavelengthMinPx, double WavelengthMaxPx,
        double DirectionDeg, double DirectionWidthDeg, double MaskBlurPx,
        bool KeepDc, string OutputMode, int MaxPixels);

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

        var rows = source.Mat.Rows;
        var cols = source.Mat.Cols;
        var paddedWidth = Cv2.GetOptimalDFTSize(cols);
        var paddedHeight = Cv2.GetOptimalDFTSize(rows);
        var paddedPixels = (long)paddedWidth * paddedHeight;
        if (paddedPixels > options.MaxPixels)
            throw new InvalidOperationException(
                $"image.fftFilter would pad to {paddedWidth}x{paddedHeight} ({paddedPixels} px) above maxPixels={options.MaxPixels}; " +
                "downscale upstream (image.resize) or raise maxPixels — FFT memory grows with the padded area.");

        using var mask = BuildMask(paddedWidth, paddedHeight, options, out var passRatio);
        if (passRatio <= 0)
            throw new InvalidOperationException(
                "image.fftFilter mask removes every frequency; widen the wavelength band or the direction window.");

        var channels = source.Mat.Channels();
        var split = Cv2.Split(source.Mat);
        var filtered = new Mat[channels];
        try
        {
            for (var index = 0; index < channels; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filtered[index] = FilterChannel(split[index], mask, paddedWidth, paddedHeight,
                    options, cancellationToken);
            }

            using var merged = new Mat();
            Cv2.Merge(filtered, merged);
            var image = VisionImage.Own(TextureFilterMath.ToSourceDepth(merged, source.Mat.Type()));
            timer.Stop();

            var filter = options.Filter;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("filter", filter), ("wavelengthMinPx", options.WavelengthMinPx),
                    ("wavelengthMaxPx", options.WavelengthMaxPx), ("directionDeg", options.DirectionDeg),
                    ("directionWidthDeg", options.DirectionWidthDeg), ("maskBlurPx", options.MaskBlurPx),
                    ("keepDc", options.KeepDc), ("outputMode", options.OutputMode),
                    ("paddedWidth", paddedWidth), ("paddedHeight", paddedHeight),
                    ("maskPassRatio", passRatio), ("channels", channels),
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
                        ["keepDc"] = options.KeepDc,
                        ["outputMode"] = options.OutputMode,
                        ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                        ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            foreach (var mat in split) mat.Dispose();
            foreach (var mat in filtered) mat?.Dispose();
        }
    }

    private static Options ReadOptions(NodeDefinition node)
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

        return new Options(filter, wavelengthMinPx, wavelengthMaxPx, directionDeg, directionWidthDeg,
            maskBlurPx, node.GetBool("keepDc", true), outputMode, maxPixels);
    }

    /// <summary>
    /// 在未搬移的频谱坐标里建增益掩码：DC 在 (0,0)，用"到最近角点的距离"处理环绕，
    /// 因此不需要 fftShift 也不会在换行处出错。
    /// </summary>
    private static Mat BuildMask(int width, int height, Options options, out double passRatio)
    {
        var (fLow, fHigh) = Band(options);
        var wedge = options.DirectionWidthDeg is > 0 and < 360;
        var halfWidthRad = options.DirectionWidthDeg * Math.PI / 360.0;
        var directionRad = TextureFilterMath.FoldDirectionDeg(options.DirectionDeg) * Math.PI / 180.0;
        var keepOnly = options.Filter == "bandpass";

        var buffer = new float[width * height];
        long passed = 0;
        for (var y = 0; y < height; y++)
        {
            var cy = y <= height / 2 ? y : y - height;
            var fy = (double)cy / height;
            for (var x = 0; x < width; x++)
            {
                var cx = x <= width / 2 ? x : x - width;
                var fx = (double)cx / width;
                var frequency = Math.Sqrt(fx * fx + fy * fy);
                var inBand = frequency >= fLow && frequency <= fHigh;

                bool keep = options.Filter switch
                {
                    "lowpass" => frequency <= fHigh,
                    "highpass" => frequency >= fLow,
                    "bandpass" => inBand,
                    _ => !inBand
                };

                if (wedge)
                {
                    var angle = Math.Atan2(fy, fx);
                    var delta = Math.Abs(NormalizeRadians(angle - directionRad));
                    // 频点方向按 180° 折叠，所以只比较 [0, π/2] 内的夹角。
                    delta = Math.Min(delta, Math.PI - delta);
                    var inWedge = delta <= halfWidthRad;
                    // 带通是"只保留"，方向窗口进一步收窄保留区；低通/高通/带阻是"只去掉"，方向窗口只收窄去掉区。
                    keep = keepOnly ? keep && inWedge : !(!keep && inWedge);
                }

                buffer[y * width + x] = keep ? 1f : 0f;
                if (keep) passed++;
            }
        }

        passRatio = (double)passed / (width * height);
        var mask = new Mat(height, width, MatType.CV_32FC1);
        try
        {
            mask.SetArray(buffer);
            if (options.MaskBlurPx > 0)
            {
                // 掩码在频域是周期性的，边界必须用 WRAP，否则 DC 附近会被当成图像边缘而失真。
                Cv2.GaussianBlur(mask, mask, new Size(0, 0), options.MaskBlurPx, options.MaskBlurPx,
                    BorderTypes.Wrap);
            }
            if (options.KeepDc) mask.Set(0, 0, 1f);
            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    private static (double Flow, double Fhigh) Band(Options options)
    {
        // 频率单位 = cycles/pixel；波长 λ 对应的频率是 1/λ，Nyquist 上限 0.5。
        var fMinWavelength = Math.Min(0.5, 1.0 / options.WavelengthMinPx);
        var fMaxWavelength = Math.Min(0.5, 1.0 / options.WavelengthMaxPx);
        return options.Filter switch
        {
            "lowpass" => (0, fMinWavelength),
            "highpass" => (fMaxWavelength, 0.5),
            _ => (fMaxWavelength, fMinWavelength)
        };
    }

    private static double NormalizeRadians(double radians)
    {
        var value = radians % (2 * Math.PI);
        if (value <= -Math.PI) value += 2 * Math.PI;
        if (value > Math.PI) value -= 2 * Math.PI;
        return value;
    }

    private static Mat FilterChannel(Mat channel, Mat mask, int paddedWidth, int paddedHeight,
        Options options, CancellationToken cancellationToken)
    {
        using var source32 = new Mat();
        channel.ConvertTo(source32, MatType.CV_32F);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(source32, padded, 0, paddedHeight - source32.Rows, 0, paddedWidth - source32.Cols,
            BorderTypes.Reflect101, Scalar.All(0));
        cancellationToken.ThrowIfCancellationRequested();

        using var spectrum = new Mat();
        Cv2.Dft(padded, spectrum, DftFlags.ComplexOutput);
        cancellationToken.ThrowIfCancellationRequested();

        var planes = Cv2.Split(spectrum);
        try
        {
            Cv2.Multiply(planes[0], mask, planes[0]);
            Cv2.Multiply(planes[1], mask, planes[1]);
            using var merged = new Mat();
            Cv2.Merge(planes, merged);
            using var inverse = new Mat();
            Cv2.Idft(merged, inverse, DftFlags.Scale | DftFlags.RealOutput);
            using var cropped = new Mat(inverse, new Rect(0, 0, source32.Cols, source32.Rows));
            if (options.OutputMode == "removed")
            {
                // 被滤掉的成分 = 输入 − 滤波结果，是零均值信号。8/16 位输出会把负半周直接截掉，
                // 所以整型输入时把该通道均值加回去（浮点输出保持纯差值），口径写在目录描述里。
                var removed = new Mat();
                Cv2.Subtract(source32, cropped, removed);
                if (channel.Depth() == MatType.CV_32F) return removed;
                using (removed)
                {
                    var centered = new Mat();
                    removed.ConvertTo(centered, MatType.CV_32F, 1.0, Cv2.Mean(channel).Val0);
                    return centered;
                }
            }
            return cropped.Clone();
        }
        finally
        {
            foreach (var plane in planes) plane.Dispose();
        }
    }
}
