using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 多帧融合（image.fuse，2026-10-27 · 缺口分析 P1-13）。
///
/// 现场缺口：`camera.syncCapture` 能出 FrameSet，但只有一个 `frameset.image` 能拆一帧，
/// 没有任何节点能把多帧叠起来。于是"多帧平均降噪（把随机噪声压到 1/√N）"
/// "多曝光融合（暗部用长曝、亮部用短曝）"在图上完全做不到。
///
/// 为什么不做 `image.stack`（帧组构建节点）：库里没有 ImageList 端口类型，硬塞进
/// DetectionList/PointList 都得先降维丢数据。fuse 直接吃 8 个图像端口，反而少一跳、
/// 参数面板也更直白。要真正的帧组请等 ImageList 类型随 3D/多相机批次一起落。
///
/// 口径：
/// - 所有输入必须同尺寸、同通道数、同位深，否则直接失败（尺寸不一致的"平均"是没意义的）；
/// - 平均与 min/max 走 OpenCV 累加器，任意帧数都很快；中值按像素排序，帧数 ≤8 时用插入排序，
///   代价是 O(N·W·H)，大图上比平均慢一个数量级——目录描述里写清楚。
/// - 输出位深跟随输入，浮点累加后饱和回写，不产生绕回。
/// </summary>
public sealed class ImageFuseNode : IVisionNodeExecutor
{
    public string Type => "image.fuse";

    private const int MaxInputs = 8;

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var mode = node.GetString("mode", "mean").Trim().ToLowerInvariant();
        if (mode is not ("mean" or "median" or "min" or "max"))
            throw new InvalidOperationException("image.fuse mode must be mean, median, min or max.");

        var images = new List<VisionImage>(MaxInputs);
        for (var index = 1; index <= MaxInputs; index++)
        {
            var port = $"image{index}";
            if (!context.TryGetValue(port, out _)) continue;
            images.Add(context.RequireImage(port));
        }
        if (images.Count < 2)
            throw new InvalidOperationException(
                $"image.fuse needs at least two images (image1..image{MaxInputs}); got {images.Count}.");

        var maxPixels = node.GetInt("maxPixels", 16_000_000);
        if (maxPixels is < 1000 or > 200_000_000)
            throw new InvalidOperationException("image.fuse maxPixels must be within 1000..200000000.");

        var reference = images[0].Mat;
        if (reference.Empty())
            throw new InvalidOperationException("image.fuse received an empty image.");
        var pixels = (long)reference.Rows * reference.Cols;
        if (pixels > maxPixels)
            throw new InvalidOperationException(
                $"image.fuse input is {reference.Cols}x{reference.Rows} ({pixels} px) above maxPixels={maxPixels}.");

        for (var index = 1; index < images.Count; index++)
        {
            var candidate = images[index].Mat;
            if (candidate.Empty())
                throw new InvalidOperationException($"image.fuse input image{index + 1} is empty.");
            if (candidate.Rows != reference.Rows || candidate.Cols != reference.Cols)
                throw new InvalidOperationException(
                    $"image.fuse inputs must share the same size: image1 is {reference.Cols}x{reference.Rows}, " +
                    $"image{index + 1} is {candidate.Cols}x{candidate.Rows}.");
            if (candidate.Channels() != reference.Channels())
                throw new InvalidOperationException(
                    $"image.fuse inputs must share the same channel count: image1 has {reference.Channels()}, " +
                    $"image{index + 1} has {candidate.Channels()}.");
            if (candidate.Depth() != reference.Depth())
                throw new InvalidOperationException(
                    $"image.fuse inputs must share the same depth: image1 is {reference.Depth()}, " +
                    $"image{index + 1} is {candidate.Depth()}.");
        }

        using var fused = mode switch
        {
            "min" => Fold(images, (left, right, output) => Cv2.Min(left, right, output)),
            "max" => Fold(images, (left, right, output) => Cv2.Max(left, right, output)),
            "median" => Median(images, cancellationToken),
            _ => Mean(images)
        };

        var result = new Mat();
        fused.ConvertTo(result, reference.Type());
        var image = VisionImage.Own(result);

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("mode", mode), ("inputCount", images.Count),
            ("width", reference.Cols), ("height", reference.Rows), ("channels", reference.Channels()));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = VisionValue.Image(image),
                ["width"] = VisionValue.Integer(image.Width),
                ["height"] = VisionValue.Integer(image.Height),
                ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                ["inputCount"] = VisionValue.Integer(images.Count),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["mode"] = mode, ["inputCount"] = images.Count,
                ["width"] = reference.Cols, ["height"] = reference.Rows,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    /// <summary>均值：先在 float32 里累加再除，避免 8 位累加溢出。</summary>
    private static Mat Mean(List<VisionImage> images)
    {
        using var accumulator = new Mat();
        images[0].Mat.ConvertTo(accumulator, MatType.CV_32F);
        for (var index = 1; index < images.Count; index++)
        {
            using var current = new Mat();
            images[index].Mat.ConvertTo(current, MatType.CV_32F);
            Cv2.Add(accumulator, current, accumulator);
        }
        Cv2.Multiply(accumulator, 1.0 / images.Count, accumulator);
        return accumulator.Clone();
    }

    private static Mat Fold(List<VisionImage> images,
        Action<Mat, Mat, Mat> combine)
    {
        var output = images[0].Mat.Clone();
        for (var index = 1; index < images.Count; index++)
        {
            var next = new Mat();
            combine(output, images[index].Mat, next);
            output.Dispose();
            output = next;
        }
        return output;
    }

    /// <summary>
    /// 逐像素中值：先拆通道，再对每个像素的 N 个灰度做插入排序（N≤8 时比堆排序快）。
    /// 只支持 8 位：中值本质是序统计，16 位要另写一套 ushort 版本，先明确拒绝而不是静默降精度。
    /// </summary>
    private static Mat Median(List<VisionImage> images, CancellationToken cancellationToken)
    {
        var reference = images[0].Mat;
        if (reference.Depth() != MatType.CV_8U)
            throw new InvalidOperationException(
                $"image.fuse median supports 8-bit images only; received {reference.Depth()}. Use mean/min/max for 16-bit data.");

        var rows = reference.Rows;
        var cols = reference.Cols;
        var channels = reference.Channels();
        var planes = new List<Mat[]>(images.Count);
        var outputPlanes = new Mat[channels];
        var buffer = new byte[images.Count];
        try
        {
            foreach (var image in images) planes.Add(Cv2.Split(image.Mat));

            for (var channel = 0; channel < channels; channel++)
            {
                outputPlanes[channel] = new Mat(rows, cols, MatType.CV_8UC1);
                for (var y = 0; y < rows; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = 0; x < cols; x++)
                    {
                        for (var index = 0; index < images.Count; index++)
                            buffer[index] = planes[index][channel].At<byte>(y, x);
                        SortBytes(buffer);
                        var median = images.Count % 2 == 1
                            ? buffer[images.Count / 2]
                            : (buffer[images.Count / 2 - 1] + buffer[images.Count / 2]) / 2;
                        outputPlanes[channel].Set(y, x, median);
                    }
                }
            }

            var output = new Mat();
            Cv2.Merge(outputPlanes, output);
            return output;
        }
        finally
        {
            foreach (var plane in planes)
                foreach (var channel in plane) channel.Dispose();
            foreach (var plane in outputPlanes) plane?.Dispose();
        }
    }

    private static void SortBytes(byte[] values)
    {
        for (var index = 1; index < values.Length; index++)
        {
            var current = values[index];
            var cursor = index - 1;
            while (cursor >= 0 && values[cursor] > current)
            {
                values[cursor + 1] = values[cursor];
                cursor--;
            }
            values[cursor + 1] = current;
        }
    }
}
