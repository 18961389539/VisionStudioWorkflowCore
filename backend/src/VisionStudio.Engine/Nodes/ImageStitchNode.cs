using System.Diagnostics;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 多图拼接（image.stitch，2026-10-27）。
///
/// 采集端只提供单帧或多个独立相机帧；大视野/长工件需要把有重叠的图像拼成一张。
/// 本节点直接使用 OpenCV 5 Stitcher（特征匹配 → 单应估计 → 束调整 → 接缝/混合），
/// 固定 8 路可选输入，最少两路。输入必须同为 8 位 1/3 通道；stitching 失败时不返回
/// 半成品，而是带 OpenCV 状态码明确报错。
/// </summary>
public sealed class ImageStitchNode : IVisionNodeExecutor
{
    public string Type => "image.stitch";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var mode = node.GetString("mode", "panorama").Trim().ToLowerInvariant();
        if (mode is not ("panorama" or "scans"))
            throw new InvalidOperationException("image.stitch mode must be panorama or scans.");
        var maxInputPixels = node.GetInt("maxInputPixels", 64_000_000);
        if (maxInputPixels is < 1 or > 500_000_000)
            throw new InvalidOperationException("image.stitch maxInputPixels must be within 1..500000000.");

        var images = CollectImages(context);
        if (images.Count < 2)
            throw new InvalidOperationException("image.stitch requires at least two connected images (image1 and image2).");

        var channels = images[0].Mat.Channels();
        if (channels is not (1 or 3))
            throw new InvalidOperationException($"image.stitch supports 1 or 3 channel images; received {channels}.");
        long totalPixels = 0;
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (image.Mat.Empty())
                throw new InvalidOperationException("image.stitch does not accept empty input images.");
            if (image.Mat.Depth() != MatType.CV_8U)
                throw new InvalidOperationException("image.stitch requires 8-bit images; convert 16-bit or float sources first.");
            if (image.Mat.Channels() != channels)
                throw new InvalidOperationException("image.stitch inputs must all use the same channel count.");
            totalPixels += (long)image.Width * image.Height;
        }
        if (totalPixels > maxInputPixels)
            throw new InvalidOperationException(
                $"image.stitch inputs total {totalPixels} pixels above maxInputPixels={maxInputPixels}; downscale upstream or raise the budget.");

        var copies = new List<Mat>(images.Count);
        Mat? panorama = null;
        try
        {
            // OpenCV Stitcher requires 3-channel 8-bit inputs. Convert grayscale scans to BGR
            // for stitching, then convert the panorama back so the node keeps its input type.
            var singleChannel = channels == 1;
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copy = image.Mat.Clone();
                if (singleChannel)
                {
                    using var gray = copy;
                    copy = new Mat();
                    JLVisionOperators.CvtColor(gray, copy, ColorConversionCodes.GRAY2BGR);
                }
                copies.Add(copy);
            }

            using var stitcher = Stitcher.Create(mode == "scans" ? Stitcher.Mode.Scans : Stitcher.Mode.Panorama);
            panorama = new Mat();
            var status = stitcher.Stitch(copies, panorama);
            cancellationToken.ThrowIfCancellationRequested();
            if (status != Stitcher.Status.OK)
            {
                throw new InvalidOperationException(
                    $"image.stitch failed with {status}; check overlap, texture and ordering, or switch mode between panorama and scans.");
            }
            if (panorama.Empty())
                throw new InvalidOperationException("image.stitch produced an empty panorama.");

            Mat? stitched = panorama;
            panorama = null;
            try
            {
                if (singleChannel)
                {
                    var gray = new Mat();
                    try { JLVisionOperators.CvtColor(stitched!, gray, ColorConversionCodes.BGR2GRAY); }
                    catch { gray.Dispose(); throw; }
                    stitched.Dispose();
                    stitched = gray;
                }
                var result = VisionImage.Own(stitched!);
                stitched = null;
            timer.Stop();
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(result, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("mode", mode),
                    ("inputCount", images.Count),
                    ("inputPixels", totalPixels),
                    ("width", result.Width),
                    ("height", result.Height),
                    ("channels", result.Mat.Channels()),
                    ("elapsedMs", timer.Elapsed.TotalMilliseconds));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(result),
                        ["width"] = VisionValue.Integer(result.Width),
                        ["height"] = VisionValue.Integer(result.Height),
                        ["inputCount"] = VisionValue.Integer(images.Count),
                        ["found"] = VisionValue.Boolean(true),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["mode"] = mode,
                        ["inputCount"] = images.Count,
                        ["inputPixels"] = totalPixels,
                        ["width"] = result.Width,
                        ["height"] = result.Height,
                        ["channels"] = result.Mat.Channels(),
                        ["stitchStatus"] = status.ToString(),
                        ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                        ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
            }
            finally
            {
                stitched?.Dispose();
            }
        }
        finally
        {
            panorama?.Dispose();
            foreach (var copy in copies) copy.Dispose();
        }
    }

    private static List<VisionImage> CollectImages(NodeExecutionContext context)
    {
        var images = new List<VisionImage>(8);
        for (var index = 1; index <= 8; index++)
        {
            var port = $"image{index}";
            if (!context.TryGetValue(port, out var value)) continue;
            var image = value.Require<IVisionImage>(port);
            if (image is not VisionImage engineImage)
                throw new InvalidOperationException($"Input '{port}' must be an engine VisionImage; got '{image.GetType().FullName}'.");
            images.Add(engineImage);
        }
        return images;
    }
}
