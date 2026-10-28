using System.Diagnostics;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 图像几何/梯度/距离变换的共用校验与数值辅助（image.flip / image.gradient / image.distanceTransform，2026-10-28）。
/// 三个节点都是像素级一一映射：输出尺寸与输入一致，因此不需要重采样质量参数，也不改变坐标帧。
/// </summary>
internal static class ImageGeometryMath
{
    internal const int MaxAxis = 16384;
    internal const long MaxPixels = 64_000_000;

    /// <summary>镜像只需要搬运像素，int16 也可原样翻转。</summary>
    internal static void ValidateGeometryDepth(Mat source, string nodeType)
    {
        if (source.Empty() || source.Width < 1 || source.Height < 1)
            throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        if (source.Depth() is not (MatType.CV_8U or MatType.CV_16U or MatType.CV_16S or MatType.CV_32F))
            throw new InvalidOperationException(
                $"{nodeType} supports 8-bit, 16-bit or float32 images; received {source.Depth()}.");
        if (source.Channels() is < 1 or > 4)
            throw new InvalidOperationException($"{nodeType} supports 1..4 channels; received {source.Channels()}.");
        ValidateSize(source, nodeType);
    }

    /// <summary>导数与距离场要求数值域，int16 由调用方先转换。</summary>
    internal static void ValidateNumericDepth(Mat source, string nodeType)
    {
        if (source.Empty() || source.Width < 1 || source.Height < 1)
            throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        if (source.Depth() is not (MatType.CV_8U or MatType.CV_16U or MatType.CV_32F))
            throw new InvalidOperationException(
                $"{nodeType} supports 8-bit, 16-bit or float32 images; received {source.Depth()}.");
        if (source.Channels() is < 1 or > 4)
            throw new InvalidOperationException($"{nodeType} supports 1..4 channels; received {source.Channels()}.");
        ValidateSize(source, nodeType);
    }

    private static void ValidateSize(Mat source, string nodeType)
    {
        if (source.Width > MaxAxis || source.Height > MaxAxis || source.Total() > MaxPixels)
            throw new InvalidOperationException(
                $"{nodeType} image must be at most {MaxAxis} per axis and {MaxPixels} pixels.");
    }

    /// <summary>彩色转单通道灰度（保持源位深），单通道直接复制以便调用方统一拥有所有权。</summary>
    internal static Mat ToGrayCopy(Mat source)
    {
        if (source.Channels() == 1) return source.Clone();
        var gray = new Mat();
        var code = source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY;
        Cv2.CvtColor(source, gray, code);
        return gray;
    }

    /// <summary>
    /// 256 桶直方图近似百分位：60MP 图像上排序真值要几百毫秒，而 stretch 只用于预览，
    /// 直方图误差（一个桶宽）对显示没有影响；数值指标仍用原始极值，不走这里。
    /// </summary>
    internal static double HistogramPercentile(Mat single32, double fraction, double knownMin, double knownMax)
    {
        if (single32.Empty() || !(knownMax > knownMin)) return knownMax;
        var range = new Rangef((float)knownMin, (float)knownMax);
        using var hist = new Mat();
        using var noMask = new Mat();
        Cv2.CalcHist([single32], [0], noMask, hist, 1, [256], [range]);
        var target = single32.Total() * fraction;
        var accumulated = 0d;
        for (var bin = 0; bin < 256; bin++)
        {
            accumulated += hist.At<float>(bin);
            if (accumulated >= target)
                return knownMin + (knownMax - knownMin) * (bin + 1) / 256d;
        }
        return knownMax;
    }

    /// <summary>两张输出图像的共同所有权：结果构造失败时一次释放，成功后交给各自的 VisionImage。</summary>
    internal sealed class ImagePairOwner(VisionImage first, VisionImage? second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second?.Dispose();
        }
    }
}

/// <summary>
/// 镜像翻转（image.flip）：成熟视觉库的 mirror_image 等价物。
/// 现场用途：相机倒装/镜像安装、镜像工件与模板同向对齐、左右工位共用一套模板。
/// </summary>
public sealed class ImageFlipNode : IVisionNodeExecutor
{
    public string Type => "image.flip";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var mode = (node.GetString("mode", "horizontal") ?? "horizontal").Trim().ToLowerInvariant();
        var flip = mode switch
        {
            "horizontal" => FlipMode.Y,
            "vertical" => FlipMode.X,
            "both" => FlipMode.XY,
            _ => throw new InvalidOperationException(
                "image.flip mode must be horizontal, vertical or both.")
        };
        var flipCode = (int)flip;

        var emptyPolicy = (node.GetString("emptyPolicy", "fail") ?? "fail").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("fail" or "passthrough"))
            throw new InvalidOperationException("image.flip emptyPolicy must be fail or passthrough.");

        var source = context.RequireImage("image");
        ImageGeometryMath.ValidateGeometryDepth(source.Mat, Type);

        var flipped = new Mat();
        Cv2.Flip(source.Mat, flipped, flip);
        var image = VisionImage.Own(flipped);
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("mode", mode), ("flipCode", flipCode),
                ("width", image.Width), ("height", image.Height), ("channels", image.Mat.Channels()),
                ("sourceType", source.Mat.Depth().ToString()),
                ("elapsedMs", timer.Elapsed.TotalMilliseconds));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = VisionValue.Image(image),
                    ["width"] = VisionValue.Integer(image.Width),
                    ["height"] = VisionValue.Integer(image.Height),
                    ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = mode, ["flipCode"] = flipCode,
                    ["width"] = image.Width, ["height"] = image.Height,
                    ["channels"] = image.Mat.Channels(),
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }
}

/// <summary>
/// 一阶梯度幅值 + 方向（image.gradient）。
/// 现场用途：纹理主方向、拉丝/织纹各向异性评估、梯度方向直方图、弱边增强前的方向滤波。
/// 桥里只有 sobelAmp（幅值），方向分量此前只能写脚本。
/// </summary>
public sealed class ImageGradientNode : IVisionNodeExecutor
{
    public string Type => "image.gradient";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        ImageGeometryMath.ValidateNumericDepth(source.Mat, Type);

        var op = (node.GetString("operator", "sobel") ?? "sobel").Trim().ToLowerInvariant();
        if (op is not ("sobel" or "scharr"))
            throw new InvalidOperationException("image.gradient operator must be sobel or scharr.");

        var kernelSize = node.GetInt("kernelSize", 3);
        if (op == "scharr" && kernelSize != 3)
        {
            // Scharr 只有 3x3 定义，静默忽略比报错更容易让人误以为换了尺寸。
            throw new InvalidOperationException(
                "image.gradient kernelSize must be 3 when operator=scharr (Scharr has a single 3x3 form).");
        }
        if (kernelSize is not (3 or 5 or 7))
            throw new InvalidOperationException("image.gradient kernelSize must be 3, 5 or 7.");

        var scaleMode = (node.GetString("scaleMode", "none") ?? "none").Trim().ToLowerInvariant();
        if (scaleMode is not ("none" or "stretch"))
            throw new InvalidOperationException("image.gradient scaleMode must be none or stretch.");

        var emitDirection = node.GetBool("emitDirection", true);

        using var gray = ImageGeometryMath.ToGrayCopy(source.Mat);
        using var gray32 = new Mat();
        gray.ConvertTo(gray32, MatType.CV_32F, 1, 0);

        using var dx = new Mat();
        using var dy = new Mat();
        if (op == "scharr")
        {
            Cv2.Scharr(gray32, dx, MatType.CV_32F, 1, 0);
            Cv2.Scharr(gray32, dy, MatType.CV_32F, 0, 1);
        }
        else
        {
            Cv2.Sobel(gray32, dx, MatType.CV_32F, 1, 0, kernelSize);
            Cv2.Sobel(gray32, dy, MatType.CV_32F, 0, 1, kernelSize);
        }

        using var magnitude32 = new Mat();
        Cv2.Magnitude(dx, dy, magnitude32);
        cancellationToken.ThrowIfCancellationRequested();

        Cv2.MinMaxLoc(magnitude32, out var minMagnitude, out var maxMagnitude, out _, out _);
        var meanMagnitude = Cv2.Mean(magnitude32).Val0;
        var p99Magnitude = ImageGeometryMath.HistogramPercentile(magnitude32, 0.99, minMagnitude, maxMagnitude);

        // 幅值按源位深落地：8/16 位走饱和截断，负旁瓣不会被绕回成亮边。
        // 这张图的所有权直接交给 VisionImage，因此不能用 using（否则 return 时把结果一起释放）。
        var magnitudeOut = new Mat();
        if (scaleMode == "stretch")
        {
            var divisor = p99Magnitude > 1e-9 ? p99Magnitude : 1d;
            using var scaled = new Mat();
            magnitude32.ConvertTo(scaled, MatType.CV_32F, 255d / divisor, 0);
            scaled.ConvertTo(magnitudeOut, MatType.CV_8UC1);
        }
        else
        {
            magnitude32.ConvertTo(magnitudeOut, source.Mat.Depth() == MatType.CV_32F ? MatType.CV_32F : source.Mat.Depth());
        }

        Mat? directionOut = null;
        if (emitDirection)
        {
            directionOut = new Mat();
            Cv2.Phase(dx, dy, directionOut, angleInDegrees: true);
            // OpenCV 给出 (-180,180]：负值整体平移 +360，得到 [0,360) 便于直方图与阈值使用。
            using var negativeMask = new Mat();
            Cv2.Compare(directionOut, Scalar.All(0), negativeMask, CmpTypes.LT);
            using var shifted = new Mat();
            Cv2.Add(directionOut, Scalar.All(360), shifted);
            shifted.CopyTo(directionOut, negativeMask);
        }

        var image = VisionImage.Own(magnitudeOut);
        var direction = directionOut is null ? null : VisionImage.Own(directionOut);
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(
            new ImageGeometryMath.ImagePairOwner(image, direction), () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("operator", op), ("kernelSize", op == "scharr" ? 3 : kernelSize), ("scaleMode", scaleMode),
                    ("emitDirection", emitDirection),
                    ("meanMagnitude", meanMagnitude), ("maxMagnitude", maxMagnitude), ("p99Magnitude", p99Magnitude),
                    ("width", image.Width), ("height", image.Height),
                    ("elapsedMs", timer.Elapsed.TotalMilliseconds));
                var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = VisionValue.Image(image),
                    ["meanMagnitude"] = VisionValue.Double(meanMagnitude),
                    ["maxMagnitude"] = VisionValue.Double(maxMagnitude),
                    ["p99Magnitude"] = VisionValue.Double(p99Magnitude),
                    ["directionUnit"] = VisionValue.String("degree"),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                };
                if (direction is not null) outputs["direction"] = VisionValue.Image(direction);
                return new NodeExecutorResult(
                    outputs,
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["operator"] = op, ["kernelSize"] = op == "scharr" ? 3 : kernelSize,
                        ["scaleMode"] = scaleMode, ["emitDirection"] = emitDirection,
                        ["meanMagnitude"] = meanMagnitude, ["maxMagnitude"] = maxMagnitude,
                        ["p99Magnitude"] = p99Magnitude,
                        ["directionUnit"] = "degree",
                        ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                        ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
    }
}

/// <summary>
/// 距离变换（image.distanceTransform）：掩膜内（或外）每个像素到最近零像素的距离。
/// 现场用途：壁厚与通道宽度、骨架剪枝前的距离场、分水岭标记、居中度评估。
/// </summary>
public sealed class ImageDistanceTransformNode : IVisionNodeExecutor
{
    public string Type => "image.distanceTransform";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        if (source.Mat.Empty() || source.Mat.Width < 1 || source.Mat.Height < 1)
            throw new InvalidOperationException("image.distanceTransform requires a non-empty image.");
        if (source.Mat.Depth() != MatType.CV_8U || source.Mat.Channels() != 1)
            throw new InvalidOperationException(
                $"image.distanceTransform requires an 8-bit single-channel mask; received {source.Mat.Depth()} with {source.Mat.Channels()} channel(s). Threshold or convert first.");
        if (source.Mat.Width > ImageGeometryMath.MaxAxis || source.Mat.Height > ImageGeometryMath.MaxAxis
            || source.Mat.Total() > ImageGeometryMath.MaxPixels)
            throw new InvalidOperationException(
                $"image.distanceTransform image must be at most {ImageGeometryMath.MaxAxis} per axis and {ImageGeometryMath.MaxPixels} pixels.");

        var distanceType = (node.GetString("distanceType", "l2") ?? "l2").Trim().ToLowerInvariant();
        var metric = distanceType switch
        {
            "l2" => DistanceTypes.L2,
            "l1" => DistanceTypes.L1,
            _ => throw new InvalidOperationException("image.distanceTransform distanceType must be l2 or l1.")
        };

        var maskSize = node.GetInt("maskSize", 3);
        if (maskSize is not (3 or 5 or 7))
            throw new InvalidOperationException("image.distanceTransform maskSize must be 3, 5 or 7.");
        if (distanceType == "l1" && maskSize != 3)
            throw new InvalidOperationException(
                "image.distanceTransform maskSize must be 3 for the L1 metric (city-block has one exact form).");

        var mode = (node.GetString("mode", "foreground") ?? "foreground").Trim().ToLowerInvariant();
        if (mode is not ("foreground" or "background"))
            throw new InvalidOperationException("image.distanceTransform mode must be foreground or background.");

        using var mask = new Mat();
        if (mode == "background") Cv2.BitwiseNot(source.Mat, mask);
        else source.Mat.CopyTo(mask);

        using var distance32 = new Mat();
        // OpenCV 的 DIST_MASK_3=3 / DIST_MASK_5=5 / DIST_MASK_PRECISE=7，与参数取值一一对应。
        var maskCode = (DistanceTransformMasks)maskSize;
        Cv2.DistanceTransform(mask, distance32, metric, maskCode);
        cancellationToken.ThrowIfCancellationRequested();

        Cv2.MinMaxLoc(distance32, out _, out var maxDistance, out _, out _);
        var foregroundCount = Cv2.CountNonZero(mask);
        var meanDistance = foregroundCount > 0 ? Cv2.Mean(distance32, mask).Val0 : 0d;

        var image = VisionImage.Own(distance32.Clone());
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("distanceType", distanceType), ("maskSize", maskSize), ("mode", mode),
                ("foregroundCount", foregroundCount), ("maxDistance", maxDistance), ("meanDistance", meanDistance),
                ("width", image.Width), ("height", image.Height),
                ("elapsedMs", timer.Elapsed.TotalMilliseconds));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = VisionValue.Image(image),
                    ["maxDistance"] = VisionValue.Double(maxDistance),
                    ["meanDistance"] = VisionValue.Double(meanDistance),
                    ["foregroundCount"] = VisionValue.Integer(foregroundCount),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["distanceType"] = distanceType, ["maskSize"] = maskSize, ["mode"] = mode,
                    ["foregroundCount"] = foregroundCount, ["maxDistance"] = maxDistance,
                    ["meanDistance"] = meanDistance,
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }
}
