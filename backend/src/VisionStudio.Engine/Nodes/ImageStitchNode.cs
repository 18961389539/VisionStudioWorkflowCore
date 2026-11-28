using System.Diagnostics;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 多图拼接（image.stitch，2026-10-27）。
///
/// 采集端只提供单帧或多个独立相机帧；大视野/长工件需要把有重叠的图像拼成一张。
/// 本节点直接使用 OpenCV 5 Stitcher（特征匹配 → 单应估计 → 束调整 → 接缝/混合），
/// 固定 8 路可选输入，最少两路。输入必须同为 8 位 1/3 通道；stitching 失败时不返回
/// 半成品，而是带 OpenCV 状态码明确报错。
///
/// 2026-11-02：补 `sourceMode=frameSet`，可直接吃 `camera.syncCapture` 的帧组；
/// 全景对顺序敏感，因此新增 `cameraOrder`（逗号分隔相机 id）显式指定拼接顺序，留空按相机 id 排序。
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
        var sourceMode = node.GetString("sourceMode", "images").Trim().ToLowerInvariant();
        if (sourceMode is not ("images" or "frameset"))
            throw new InvalidOperationException("image.stitch sourceMode must be images or frameSet.");
        var maxInputPixels = node.GetInt("maxInputPixels", 64_000_000);
        if (maxInputPixels is < 1 or > 500_000_000)
            throw new InvalidOperationException("image.stitch maxInputPixels must be within 1..500000000.");
        var portPolicy = node.GetString("portPolicy", "reject").Trim().ToLowerInvariant();
        if (portPolicy is not ("reject" or "compact"))
            throw new InvalidOperationException("image.stitch portPolicy must be reject or compact.");
        var cameraOrderMode = node.GetString("cameraOrderMode", "all").Trim().ToLowerInvariant();
        if (cameraOrderMode is not ("all" or "subset"))
            throw new InvalidOperationException("image.stitch cameraOrderMode must be all or subset.");

        var images = CollectImages(context, node, sourceMode, portPolicy, cameraOrderMode,
            out var inputSources, out var cameraIds, out var cameraOrder);
        if (images.Count < 2)
            throw new InvalidOperationException(sourceMode == "frameset"
                ? "image.stitch requires at least two camera images in the FrameSet."
                : "image.stitch requires at least two connected images (image1 and image2).");

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
        var inputContracts = images.Select((image, index) => new
        {
            source = inputSources[index],
            cameraId = cameraIds.ElementAtOrDefault(index),
            order = index,
            width = image.Width,
            height = image.Height,
            channels = image.Mat.Channels(),
            depth = image.Mat.Depth().ToString(),
            pixels = (long)image.Width * image.Height
        }).ToArray();

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
                var pairDiagnostics = new List<object>();
                for (var index = 0; index + 1 < copies.Count; index++)
                {
                    using var pairStitcher = Stitcher.Create(mode == "scans" ? Stitcher.Mode.Scans : Stitcher.Mode.Panorama);
                    using var pairOutput = new Mat();
                    var pairStatus = pairStitcher.Stitch([copies[index], copies[index + 1]], pairOutput);
                    pairDiagnostics.Add(new
                    {
                        first = inputSources[index],
                        second = inputSources[index + 1],
                        firstCameraId = cameraIds.ElementAtOrDefault(index),
                        secondCameraId = cameraIds.ElementAtOrDefault(index + 1),
                        status = pairStatus.ToString(),
                        overlapOrHomography = pairStatus == Stitcher.Status.OK ? "estimated" : "failed"
                    });
                }
                var pairJson = System.Text.Json.JsonSerializer.Serialize(pairDiagnostics);
                throw new InvalidOperationException(
                    $"image.stitch failed with {status}; check overlap, texture and ordering, or switch mode between panorama and scans. Pair diagnostics: {pairJson}");
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
                var inputContractsJson = System.Text.Json.JsonSerializer.Serialize(inputContracts);
                var cameraOrderJson = System.Text.Json.JsonSerializer.Serialize(cameraOrder);
                var pairDiagnosticsJson = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, Math.Max(0, images.Count - 1)).Select(index => new
                {
                    first = inputSources[index],
                    second = inputSources[index + 1],
                    firstCameraId = cameraIds.ElementAtOrDefault(index),
                    secondCameraId = cameraIds.ElementAtOrDefault(index + 1),
                    overlapOrHomography = "estimated-by-opencv-stitcher"
                }).ToArray());
            timer.Stop();
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(result, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("mode", mode),
                    ("sourceMode", sourceMode),
                    ("inputCount", images.Count),
                    ("inputPixels", totalPixels),
                    ("inputSources", inputSources), ("cameraIds", cameraIds), ("cameraOrder", cameraOrder),
                    ("inputContracts", inputContracts),
                    ("portPolicy", portPolicy), ("cameraOrderMode", cameraOrderMode),
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
                        ["inputContractsJson"] = VisionValue.String(inputContractsJson),
                        ["cameraOrderJson"] = VisionValue.String(cameraOrderJson),
                        ["pairDiagnosticsJson"] = VisionValue.String(pairDiagnosticsJson),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["mode"] = mode,
                        ["inputCount"] = images.Count,
                        ["inputPixels"] = totalPixels,
                        ["inputSources"] = inputSources, ["cameraIds"] = cameraIds, ["cameraOrder"] = cameraOrder,
                        ["inputContractsJson"] = inputContractsJson, ["pairDiagnosticsJson"] = pairDiagnosticsJson,
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

    private static List<VisionImage> CollectImages(NodeExecutionContext context, NodeDefinition node, string sourceMode,
        string portPolicy, string cameraOrderMode, out string[] inputSources, out string[] cameraIds, out string[] cameraOrder)
    {
        if (sourceMode == "images")
        {
            if (context.TryGetValue("frameSet", out _))
                throw new InvalidOperationException("image.stitch sourceMode=images cannot also receive frameSet; choose one source mode.");
            var wired = new List<VisionImage>(8);
            var sources = new List<string>(8);
            var emptyBefore = false;
            for (var index = 1; index <= 8; index++)
            {
                var port = $"image{index}";
                if (!context.TryGetValue(port, out var value))
                {
                    emptyBefore = true;
                    continue;
                }
                if (emptyBefore && portPolicy == "reject")
                    throw new InvalidOperationException($"image.stitch port '{port}' leaves an empty image slot; connect ports contiguously or select compact.");
                var image = value.Require<IVisionImage>(port);
                if (image is not VisionImage engineImage)
                    throw new InvalidOperationException($"Input '{port}' must be an engine VisionImage; got '{image.GetType().FullName}'.");
                wired.Add(engineImage);
                sources.Add(port);
            }
            inputSources = sources.ToArray();
            cameraIds = [];
            cameraOrder = inputSources;
            return wired;
        }

        for (var index = 1; index <= 8; index++)
        {
            if (context.TryGetValue($"image{index}", out _))
                throw new InvalidOperationException(
                    "image.stitch sourceMode=frameSet ignores image1..image8; disconnect them or switch sourceMode=images.");
        }
        var frameSet = context.TryGetValue("frameSet", out var frameSetValue)
            ? frameSetValue.Require<IVisionFrameSet>("frameSet")
            : throw new InvalidOperationException("image.stitch sourceMode=frameSet requires a frameSet input.");

        var available = frameSet.Images.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal).ToArray();
        var requested = (node.GetString("cameraOrder", "") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            throw new InvalidOperationException("image.stitch cameraOrder contains duplicate camera ids.");
        foreach (var cameraId in requested)
        {
            if (!frameSet.Images.ContainsKey(cameraId))
                throw new InvalidOperationException(
                    $"image.stitch cameraOrder id '{cameraId}' is not in the FrameSet. Available: {string.Join(", ", available)}.");
        }
        if (requested.Length > 0 && cameraOrderMode == "all")
        {
            var missing = available.Except(requested, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"image.stitch cameraOrder omits FrameSet cameras [{string.Join(", ", missing)}]; include every id or select subset mode.");
        }
        var order = requested.Length > 0 ? requested : available;

        var images = new List<VisionImage>(order.Length);
        foreach (var cameraId in order)
        {
            if (frameSet.Images[cameraId] is not VisionImage frame)
                throw new InvalidOperationException($"image.stitch frameSet image '{cameraId}' is not a VisionImage.");
            images.Add(frame);
        }
        inputSources = order.ToArray();
        cameraIds = order.ToArray();
        cameraOrder = order.ToArray();
        return images;
    }
}
