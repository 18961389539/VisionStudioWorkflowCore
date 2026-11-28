using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 逐像素融合图像端口或同步帧组。使用共同的像素网格，并要求尺寸、通道数和位深一致。
/// 帧组的采集时序和图像的坐标系分别记录在诊断里。
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

        var sourceMode = node.GetString("sourceMode", "images").Trim().ToLowerInvariant();
        if (sourceMode is not ("images" or "frameset"))
            throw new InvalidOperationException("image.fuse sourceMode must be images or frameSet.");

        var (alignmentPolicy, alignmentStatus) = node.GetString("alignmentPolicy", "legacy").Trim().ToLowerInvariant() switch
        {
            "sameframe" => ("sameFrame", "frameVerified"),
            "prealigned" => ("prealigned", "operatorConfirmed"),
            "legacy" => ("legacy", "unchecked"),
            _ => throw new InvalidOperationException("image.fuse alignmentPolicy must be sameFrame, prealigned or legacy.")
        };
        var referenceGrid = node.GetString("referenceGrid", "").Trim();
        if (alignmentPolicy == "prealigned" && referenceGrid.Length == 0)
            throw new InvalidOperationException("image.fuse alignmentPolicy=prealigned requires referenceGrid to name the shared reference grid.");

        var images = new List<VisionImage>(MaxInputs);
        var inputSources = new List<string>(MaxInputs);
        IVisionFrameSet? frameSet = null;
        if (sourceMode == "frameset")
        {
            for (var index = 1; index <= MaxInputs; index++)
            {
                if (context.TryGetValue($"image{index}", out _))
                    throw new InvalidOperationException(
                        $"image.fuse sourceMode=frameSet ignores image1..image{MaxInputs}; disconnect them or switch sourceMode=images.");
            }
            frameSet = context.TryGetValue("frameSet", out var frameSetValue)
                ? frameSetValue.Require<IVisionFrameSet>("frameSet")
                : throw new InvalidOperationException("image.fuse sourceMode=frameSet requires a frameSet input.");
            // 按相机 id 排序：mean/median/min/max 与顺序无关，但跨运行顺序必须稳定，诊断也要可复现。
            foreach (var pair in frameSet.Images.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (pair.Value is not VisionImage frame)
                    throw new InvalidOperationException($"image.fuse frameSet image '{pair.Key}' is not a VisionImage.");
                inputSources.Add(pair.Key);
                images.Add(frame);
            }
        }
        else
        {
            if (context.TryGetValue("frameSet", out _))
                throw new InvalidOperationException("image.fuse sourceMode=images cannot also receive frameSet; choose one source mode.");
            var seen = new HashSet<VisionImage>(ReferenceEqualityComparer.Instance);
            var emptyBefore = false;
            for (var index = 1; index <= MaxInputs; index++)
            {
                var port = $"image{index}";
                if (!context.TryGetValue(port, out _))
                {
                    emptyBefore = true;
                    continue;
                }
                if (emptyBefore)
                    throw new InvalidOperationException($"image.fuse image ports must be contiguous from image1; '{port}' leaves an empty slot.");
                var input = context.RequireImage(port);
                if (!seen.Add(input))
                    throw new InvalidOperationException($"image.fuse port '{port}' duplicates an earlier image reference; connect distinct frames.");
                inputSources.Add(port);
                images.Add(input);
            }
        }
        if (images.Count < 2)
            throw new InvalidOperationException(
                sourceMode == "frameset"
                    ? $"image.fuse sourceMode=frameSet needs at least two camera images in the FrameSet; got {images.Count}."
                    : $"image.fuse needs at least two images (image1..image{MaxInputs}); got {images.Count}.");

        var maxPixels = node.GetInt("maxPixels", 16_000_000);
        if (maxPixels is < 1000 or > 200_000_000)
            throw new InvalidOperationException("image.fuse maxPixels must be within 1000..200000000.");

        var reference = images[0].Mat;
        var referenceSource = inputSources[0];
        if (reference.Empty())
            throw new InvalidOperationException($"image.fuse input '{referenceSource}' is empty.");
        var pixels = (long)reference.Rows * reference.Cols;
        if (pixels > maxPixels)
            throw new InvalidOperationException(
                $"image.fuse input '{referenceSource}' is {reference.Cols}x{reference.Rows} ({pixels} px) above maxPixels={maxPixels}.");

        for (var index = 1; index < images.Count; index++)
        {
            var candidate = images[index].Mat;
            var candidateSource = inputSources[index];
            if (candidate.Empty())
                throw new InvalidOperationException($"image.fuse input '{candidateSource}' is empty.");
            if (candidate.Rows != reference.Rows || candidate.Cols != reference.Cols)
                throw new InvalidOperationException(
                    $"image.fuse inputs must share the same size: '{referenceSource}' is {reference.Cols}x{reference.Rows}, " +
                    $"'{candidateSource}' is {candidate.Cols}x{candidate.Rows}.");
            if (candidate.Channels() != reference.Channels())
                throw new InvalidOperationException(
                    $"image.fuse inputs must share the same channel count: '{referenceSource}' has {reference.Channels()}, " +
                    $"'{candidateSource}' has {candidate.Channels()}.");
            if (candidate.Depth() != reference.Depth())
                throw new InvalidOperationException(
                    $"image.fuse inputs must share the same depth: '{referenceSource}' is {reference.Depth()}, " +
                    $"'{candidateSource}' is {candidate.Depth()}.");
        }

        var coordinateFrames = images.Select(image => image.CoordinateFrame).ToArray();
        if (alignmentPolicy == "sameFrame" && (!IsKnownFrame(coordinateFrames[0]) ||
            coordinateFrames.Any(frame => !string.Equals(frame, coordinateFrames[0], StringComparison.OrdinalIgnoreCase))))
        {
            var details = string.Join(", ", inputSources.Zip(coordinateFrames, (source, frame) => $"'{source}'={frame}"));
            throw new InvalidOperationException(
                $"image.fuse alignmentPolicy=sameFrame requires a shared known coordinate frame. Inputs: {details}. " +
                "Register and warp the inputs onto one reference image, or select prealigned after verifying their alignment.");
        }

        var sourceNames = inputSources.ToArray();
        var cameraIds = frameSet is null ? Array.Empty<string>() : sourceNames;
        var inputContracts = images.Select((input, index) => new
        {
            source = sourceNames[index],
            cameraId = frameSet is null ? null : sourceNames[index],
            width = input.Width,
            height = input.Height,
            depth = input.Mat.Depth().ToString(),
            channels = input.Mat.Channels(),
            coordinateFrame = input.CoordinateFrame
        }).ToArray();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("mode", mode), ("sourceMode", sourceMode), ("inputCount", images.Count),
            ("width", reference.Cols), ("height", reference.Rows), ("channels", reference.Channels()),
            ("alignmentPolicy", alignmentPolicy), ("alignmentStatus", alignmentStatus),
            ("referenceGrid", referenceGrid.Length == 0 ? null : referenceGrid),
            ("inputSources", sourceNames), ("inputContracts", inputContracts), ("inputCoordinateFrames", coordinateFrames),
            ("cameraIds", cameraIds), ("frameSetGroupId", frameSet?.GroupId),
            ("timestampBasis", frameSet?.TimestampBasis), ("triggerSkewUs", frameSet?.TriggerSkewUs),
            ("withinTolerance", frameSet?.WithinTolerance));

        cancellationToken.ThrowIfCancellationRequested();
        using var fused = ImageFusionAlgorithms.Fuse(images, mode, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var image = VisionImage.Own(ImageTransformOutput.Create(result => fused.ConvertTo(result, reference.Type())));

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
                ["sourceMode"] = sourceMode, ["alignmentPolicy"] = alignmentPolicy, ["alignmentStatus"] = alignmentStatus,
                ["inputSources"] = sourceNames, ["inputCoordinateFrames"] = coordinateFrames, ["cameraIds"] = cameraIds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    private static bool IsKnownFrame(string frame)
    {
        var name = frame.Trim();
        return name.Length > 0 && !name.Equals("CurrentImage", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("ReferenceImage", StringComparison.OrdinalIgnoreCase);
    }

}
