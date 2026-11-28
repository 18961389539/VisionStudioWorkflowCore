using System.Diagnostics;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

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
        if (source.Mat.Empty())
        {
            if (emptyPolicy == "fail")
                throw new InvalidOperationException("image.flip requires a non-empty image.");
            var sentinel = new Mat(1, 1, source.Mat.Type(), Scalar.All(0));
            var emptyDiag = CoordinateValidation.Diagnostics(Type, ("mode", mode), ("emptyPassthrough", true),
                ("sourceWidth", source.Width), ("sourceHeight", source.Height), ("sourceChannels", source.Mat.Channels()),
                ("sourceType", source.Mat.Type().ToString()), ("sentinelWidth", 1), ("sentinelHeight", 1));
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["image"] = VisionValue.Image(VisionImage.Own(sentinel)), ["width"] = VisionValue.Integer(source.Width),
                    ["height"] = VisionValue.Integer(source.Height), ["channels"] = VisionValue.Integer(source.Mat.Channels()),
                    ["diagnostics"] = VisionValue.String(emptyDiag)
                },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = mode, ["emptyPassthrough"] = true, ["sourceWidth"] = source.Width, ["sourceHeight"] = source.Height,
                    ["sourceChannels"] = source.Mat.Channels(), ["sentinelWidth"] = 1, ["sentinelHeight"] = 1, ["diagnostics"] = emptyDiag
                }));
        }
        ImageGeometryMath.ValidateGeometryDepth(source.Mat, Type);

        var flipped = ImageGeometryAlgorithms.Flip(source.Mat, flip);
        var image = VisionImage.Own(flipped);
        var sx = mode is "horizontal" or "both" ? -1d : 1d;
        var sy = mode is "vertical" or "both" ? -1d : 1d;
        var tx = sx < 0 ? source.Width - 1d : 0d;
        var ty = sy < 0 ? source.Height - 1d : 0d;
        var transform = new VisionTransform2D("CurrentImage", "FlippedImage", "px", sx, 0, tx, 0, sy, ty, 0, 0, 1);
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("mode", mode), ("flipCode", flipCode),
                ("coordinateMapping", mode switch
                {
                    "horizontal" => "x'=width-1-x; y'=y",
                    "vertical" => "x'=x; y'=height-1-y",
                    _ => "x'=width-1-x; y'=height-1-y"
                }),
                ("transformSource", "CurrentImage"), ("transformTarget", "FlippedImage"),
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
                    ["transform"] = VisionValue.Transform(transform),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = mode, ["flipCode"] = flipCode,
                    ["width"] = image.Width, ["height"] = image.Height,
                    ["channels"] = image.Mat.Channels(), ["transform"] = transform,
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

        var gradient = ImageGeometryAlgorithms.Gradient(source.Mat, op, kernelSize, scaleMode, emitDirection, cancellationToken);
        var meanMagnitude = gradient.Mean;
        var maxMagnitude = gradient.Maximum;
        var p99Magnitude = gradient.Percentile99;
        var image = VisionImage.Own(gradient.Magnitude);
        var direction = gradient.Direction is null ? null : VisionImage.Own(gradient.Direction);
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(
            new ImageGeometryMath.ImagePairOwner(image, direction), () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("operator", op), ("kernelSize", op == "scharr" ? 3 : kernelSize), ("scaleMode", scaleMode),
                    ("emitDirection", emitDirection), ("inputDepth", source.Mat.Depth().ToString()),
                    ("inputChannels", source.Mat.Channels()), ("grayWeights", "BGR(A)->BT.601; alpha ignored"),
                    ("intermediateDepth", "CV_32F"), ("outputScale", gradient.OutputScale),
                    ("zeroGradientPixels", gradient.ZeroGradientPixels), ("zeroGradientDirection", 0),
                    ("directionUnit", "degree"), ("directionConvention", "atan2(dy,dx), +x=0deg, +y=90deg, y-down"),
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
        var paddingName = (node.GetString("padding", "background") ?? "background").Trim().ToLowerInvariant();
        var padding = paddingName switch
        {
            "background" => BorderTypes.Constant,
            "reflect" => BorderTypes.Reflect,
            "replicate" => BorderTypes.Replicate,
            _ => throw new InvalidOperationException("image.distanceTransform padding must be background, reflect or replicate.")
        };
        var maskPolicy = (node.GetString("maskPolicy", "nonzero") ?? "nonzero").Trim().ToLowerInvariant();
        if (maskPolicy is not ("nonzero" or "binaryonly"))
            throw new InvalidOperationException("image.distanceTransform maskPolicy must be nonzero or binaryOnly.");
        if (maskPolicy == "binaryonly" && !PreprocessP1Diagnostics.IsBinary(source.Mat))
            throw new InvalidOperationException("image.distanceTransform maskPolicy=binaryOnly requires at most one nonzero value; threshold first or choose nonzero.");

        var distance = ImageGeometryAlgorithms.DistanceTransform(source.Mat, mode, metric, maskSize, padding, cancellationToken);
        var maxDistance = distance.Maximum;
        var foregroundCount = distance.ForegroundCount;
        var meanDistance = distance.Mean;
        var image = VisionImage.Own(distance.Image);
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("distanceType", distanceType), ("maskSize", maskSize), ("mode", mode),
                ("padding", paddingName), ("maskPolicy", maskPolicy),
                ("nonzeroValues", PreprocessP1Diagnostics.NonZeroValues(source.Mat)),
                ("foregroundCount", foregroundCount), ("backgroundCount", source.Mat.Total() - foregroundCount),
                ("borderTouchingCount", distance.BorderTouchingCount), ("borderAffectedRatio", distance.BorderAffectedRatio),
                ("maxDistance", maxDistance), ("meanDistance", meanDistance),
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
                    ["padding"] = paddingName, ["maskPolicy"] = maskPolicy,
                    ["foregroundCount"] = foregroundCount, ["maxDistance"] = maxDistance,
                    ["borderAffectedRatio"] = distance.BorderAffectedRatio,
                    ["meanDistance"] = meanDistance,
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }
}
