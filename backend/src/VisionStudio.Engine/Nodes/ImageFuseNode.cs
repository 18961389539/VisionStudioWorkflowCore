using OpenCvSharp;
using VisionStudio.Abstractions;
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
            for (var index = 1; index <= MaxInputs; index++)
            {
                var port = $"image{index}";
                if (!context.TryGetValue(port, out _)) continue;
                inputSources.Add(port);
                images.Add(context.RequireImage(port));
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
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("mode", mode), ("sourceMode", sourceMode), ("inputCount", images.Count),
            ("width", reference.Cols), ("height", reference.Rows), ("channels", reference.Channels()),
            ("alignmentPolicy", alignmentPolicy), ("alignmentStatus", alignmentStatus),
            ("inputSources", sourceNames), ("inputCoordinateFrames", coordinateFrames),
            ("cameraIds", cameraIds), ("frameSetGroupId", frameSet?.GroupId),
            ("timestampBasis", frameSet?.TimestampBasis), ("triggerSkewUs", frameSet?.TriggerSkewUs),
            ("withinTolerance", frameSet?.WithinTolerance));

        cancellationToken.ThrowIfCancellationRequested();
        using var fused = mode switch
        {
            "min" => Fold(images, (left, right, output) => Cv2.Min(left, right, output)),
            "max" => Fold(images, (left, right, output) => Cv2.Max(left, right, output)),
            "median" => Median(images, cancellationToken),
            _ => Mean(images)
        };

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

    private static Mat Mean(List<VisionImage> images)
    {
        using var accumulator = new Mat();
        if (images[0].Mat.Depth() == MatType.CV_64F || images[0].Mat.Depth() == MatType.CV_32F ||
            images[0].Mat.Depth() == MatType.CV_32S)
        {
            var weight = 1.0 / images.Count;
            images[0].Mat.ConvertTo(accumulator, MatType.CV_64F, weight);
            for (var index = 1; index < images.Count; index++)
            {
                using var weighted = new Mat();
                images[index].Mat.ConvertTo(weighted, MatType.CV_64F, weight);
                Cv2.Add(accumulator, weighted, accumulator);
            }
            return accumulator.Clone();
        }

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
                        outputPlanes[channel].Set(y, x, (byte)median);
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
