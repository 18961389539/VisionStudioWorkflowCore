using System.Text.Json;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// YOLO26 / YOLOv8 pose ONNX.
/// layout=yolo: raw head [1,4+nc+K*D,N] (typical COCO [1,56,8400]).
/// layout=xyxy: YOLO26 end-to-end NMS-free rows [1,N,6+K*D] (typical [1,300,57] = box+score+class+17*(x,y,conf)).
/// Emits detections, keypoints JSON, and an overlay (boxes + keypoints + optional COCO skeleton).
/// </summary>
public sealed class OnnxPoseNode : IVisionNodeExecutor
{
    public string Type => "inference.pose";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var source = context.RequireImage("image").Mat;
        if (node.GetBool("crop", false))
            throw new InvalidOperationException("Pose with crop=true is unsupported. Set crop=false.");
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var shape = OnnxInferenceHelpers.Shape(output);
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "pose");
        var layout = node.GetString("layout", "yolo").Trim().ToLowerInvariant();
        if (layout is "yolo26") layout = "yolo";
        if (layout is "yolo26-e2e" or "end2end") layout = "xyxy";
        if (layout is not ("yolo" or "xyxy"))
            throw new InvalidOperationException($"Unsupported pose layout '{layout}'. Supported values are 'yolo' (raw) and 'xyxy' (YOLO26 end-to-end).");
        var numKeypoints = node.GetInt("numKeypoints", 17);
        var keypointDims = node.GetInt("keypointDims", 3);
        if (numKeypoints is < 1 or > 128) throw new InvalidOperationException("numKeypoints must be between 1 and 128.");
        if (keypointDims is < 2 or > 3) throw new InvalidOperationException("keypointDims must be 2 or 3.");
        var threshold = node.GetDouble("confidenceThreshold", 0.25);
        var nmsThreshold = node.GetDouble("nmsThreshold", 0.45);
        var keypointThreshold = node.GetDouble("keypointThreshold", 0.5);
        var maxCount = node.GetInt("maxCount", 100);
        if (maxCount is < 1 or > 1000) throw new InvalidOperationException("maxCount must be between 1 and 1000.");
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || !double.IsFinite(nmsThreshold) || nmsThreshold is < 0 or > 1 || !double.IsFinite(keypointThreshold) || keypointThreshold is < 0 or > 1)
            throw new InvalidOperationException("Pose thresholds must be finite values from 0 to 1.");
        var coordinates = node.GetString("coordinates", "pixels").Trim().ToLowerInvariant();
        if (coordinates is not ("normalized" or "pixels"))
            throw new InvalidOperationException($"Unsupported coordinates '{coordinates}'. Supported values are 'normalized' and 'pixels'.");
        var letterbox = node.GetBool("letterbox", true);
        var drawSkeleton = node.GetBool("drawSkeleton", true);
        var inputWidth = node.GetInt("inputWidth", 640);
        var inputHeight = node.GetInt("inputHeight", 640);
        if (inputWidth < 1 || inputHeight < 1) throw new InvalidOperationException("inputWidth/inputHeight must be positive.");

        var tensorOrder = node.GetString("tensorOrder", "auto").Trim().ToLowerInvariant();
        if (tensorOrder is not ("auto" or "channelsfirst" or "channelslast"))
            throw new InvalidOperationException("Pose tensorOrder must be auto, channelsFirst, or channelsLast.");
        InferenceStats.YoloPoseCandidate[] decoded = layout == "yolo"
            ? InferenceStats.DecodeYoloPose(values, shape, numKeypoints, keypointDims, tensorOrder)
            : InferenceStats.DecodeYoloPoseXyxy(values, shape, numKeypoints, keypointDims);

        double MapX(double inputX)
        {
            if (!letterbox) return inputX * source.Width / inputWidth;
            var scale = Math.Min((double)inputWidth / source.Width, (double)inputHeight / source.Height);
            return (inputX - (inputWidth - source.Width * scale) / 2) / scale;
        }
        double MapY(double inputY)
        {
            if (!letterbox) return inputY * source.Height / inputHeight;
            var scale = Math.Min((double)inputWidth / source.Width, (double)inputHeight / source.Height);
            return (inputY - (inputHeight - source.Height * scale) / 2) / scale;
        }

        var scored = new List<(double Left, double Top, double Right, double Bottom, double Score, int ClassId, double[] Kpts)>();
        for (var i = 0; i < decoded.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var c = decoded[i];
            if (!double.IsFinite(c.Score) || c.Score < threshold) continue;
            if (!double.IsFinite(c.CenterX) || !double.IsFinite(c.CenterY) || !double.IsFinite(c.Width) || !double.IsFinite(c.Height) || c.Width < 0 || c.Height < 0)
                throw new InvalidOperationException($"Pose row {i} has non-finite or negative box geometry.");
            if (c.ClassId < 0) throw new InvalidOperationException($"Pose row {i} has a negative classId.");
            double x1 = c.CenterX - c.Width / 2, y1 = c.CenterY - c.Height / 2;
            double x2 = c.CenterX + c.Width / 2, y2 = c.CenterY + c.Height / 2;
            if (coordinates == "normalized")
            {
                x1 *= inputWidth; x2 *= inputWidth; y1 *= inputHeight; y2 *= inputHeight;
            }
            var left = Math.Clamp(Math.Min(MapX(x1), MapX(x2)), 0, source.Width);
            var top = Math.Clamp(Math.Min(MapY(y1), MapY(y2)), 0, source.Height);
            var right = Math.Clamp(Math.Max(MapX(x1), MapX(x2)), 0, source.Width);
            var bottom = Math.Clamp(Math.Max(MapY(y1), MapY(y2)), 0, source.Height);
            if (right <= left || bottom <= top) continue;

            var mappedKpts = new double[c.Keypoints.Length];
            for (var k = 0; k < numKeypoints; k++)
            {
                var baseIdx = k * keypointDims;
                var kx = c.Keypoints[baseIdx];
                var ky = c.Keypoints[baseIdx + 1];
                var kc = keypointDims == 3 ? c.Keypoints[baseIdx + 2] : 1.0;
                if (coordinates == "normalized") { kx *= inputWidth; ky *= inputHeight; }
                mappedKpts[baseIdx] = MapX(kx);
                mappedKpts[baseIdx + 1] = MapY(ky);
                if (keypointDims == 3) mappedKpts[baseIdx + 2] = kc;
            }
            scored.Add((left, top, right, bottom, c.Score, c.ClassId, mappedKpts));
        }

        var applyNms = layout == "yolo";
        var kept = new List<(double Left, double Top, double Right, double Bottom, double Score, int ClassId, double[] Kpts)>();
        var truncated = InferenceNmsCap.Fill(scored.OrderByDescending(x => x.Score), maxCount, kept,
            (existing, candidate) => applyNms && existing.Any(x => x.ClassId == candidate.ClassId && BoxIoU(x, candidate) > nmsThreshold),
            cancellationToken);

        var labels = OnnxInferenceHelpers.ResolveLabels(node);
        var detections = new List<VisionDetection>();
        var posePayload = new List<object>();
        for (var i = 0; i < kept.Count; i++)
        {
            var (left, top, right, bottom, score, classId, kpts) = kept[i];
            var w = right - left; var h = bottom - top;
            detections.Add(new VisionDetection(i, left, top, w, h, w * h, 0, 0,
                new VisionCircle((left + right) / 2, (top + bottom) / 2, Math.Sqrt(w * w + h * h) / 2),
                new VisionRectangle2D(left, top, w, h), score, classId));
            var points = new List<object>(numKeypoints);
            for (var k = 0; k < numKeypoints; k++)
            {
                var baseIdx = k * keypointDims;
                points.Add(new
                {
                    index = k,
                    x = kpts[baseIdx],
                    y = kpts[baseIdx + 1],
                    conf = keypointDims == 3 ? kpts[baseIdx + 2] : 1.0
                });
            }
            posePayload.Add(new { index = i, classId, label = labels.Length > classId ? labels[classId] : $"class_{classId}", score, x1 = left, y1 = top, x2 = right, y2 = bottom, keypoints = points });
        }

        var overlay = BuildPoseOverlay(source, kept, numKeypoints, keypointDims, keypointThreshold, drawSkeleton);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["detections"] = VisionValue.Detections(detections.ToArray()),
                ["count"] = VisionValue.Integer(detections.Count),
                ["truncated"] = VisionValue.Boolean(truncated),
                ["keypoints"] = VisionValue.String(JsonSerializer.Serialize(posePayload)),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlay)),
                ["result"] = VisionValue.String(JsonSerializer.Serialize(new { count = detections.Count, layout, tensorOrder, numKeypoints, keypointDims, outputShape = shape, nmsApplied = applyNms, poses = posePayload }))
            },
            new Dictionary<string, object?> { ["count"] = detections.Count, ["layout"] = layout, ["tensorOrder"] = tensorOrder, ["letterbox"] = letterbox, ["nmsApplied"] = applyNms, ["numKeypoints"] = numKeypoints, ["keypointDims"] = keypointDims, ["outputShape"] = shape }));
    }

    private static double BoxIoU(
        (double Left, double Top, double Right, double Bottom, double Score, int ClassId, double[] Kpts) a,
        (double Left, double Top, double Right, double Bottom, double Score, int ClassId, double[] Kpts) b)
    {
        var iw = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var ih = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var intersection = iw * ih;
        var areaA = (a.Right - a.Left) * (a.Bottom - a.Top);
        var areaB = (b.Right - b.Left) * (b.Bottom - b.Top);
        return intersection / (areaA + areaB - intersection + 1e-9);
    }

    private static Mat BuildPoseOverlay(
        Mat source,
        List<(double Left, double Top, double Right, double Bottom, double Score, int ClassId, double[] Kpts)> poses,
        int numKeypoints, int keypointDims, double keypointThreshold, bool drawSkeleton)
    {
        var overlay = new Mat();
        try
        {
            if (source.Channels() == 1) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
            else if (source.Channels() == 3) source.CopyTo(overlay);
            else if (source.Channels() == 4) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.BGRA2BGR);
            else throw new InvalidOperationException($"Pose overlay supports 1, 3, or 4 channel images; received {source.Channels()}.");

            foreach (var pose in poses)
            {
                var color = InferenceStats.SegmentationColorBgr(pose.ClassId + 1);
                var scalar = new Scalar(color.B, color.G, color.R);
                Cv2.Rectangle(overlay,
                    new Point((int)Math.Round(pose.Left), (int)Math.Round(pose.Top)),
                    new Point((int)Math.Round(pose.Right), (int)Math.Round(pose.Bottom)),
                    scalar, 2, LineTypes.AntiAlias);

                Point?[] pts = new Point?[numKeypoints];
                for (var k = 0; k < numKeypoints; k++)
                {
                    var baseIdx = k * keypointDims;
                    var conf = keypointDims == 3 ? pose.Kpts[baseIdx + 2] : 1.0;
                    if (conf < keypointThreshold || !double.IsFinite(pose.Kpts[baseIdx]) || !double.IsFinite(pose.Kpts[baseIdx + 1])) continue;
                    var pt = new Point((int)Math.Round(pose.Kpts[baseIdx]), (int)Math.Round(pose.Kpts[baseIdx + 1]));
                    pts[k] = pt;
                    Cv2.Circle(overlay, pt, 3, scalar, -1, LineTypes.AntiAlias);
                }

                if (drawSkeleton && numKeypoints == 17)
                {
                    foreach (var (a, b) in InferenceStats.Coco17Skeleton)
                    {
                        if (a >= numKeypoints || b >= numKeypoints) continue;
                        if (pts[a] is not { } pa || pts[b] is not { } pb) continue;
                        Cv2.Line(overlay, pa, pb, scalar, 2, LineTypes.AntiAlias);
                    }
                }
            }
            return overlay;
        }
        catch { overlay.Dispose(); throw; }
    }
}

/// <summary>
/// Runs an ONNX anomaly head. Scalar outputs keep the historical score behaviour;
/// spatial outputs ([1,1,H,W] or [1,H,W], HALCON DL Anomaly style) additionally emit a
/// normalized heatmap image, peak location in source pixels and anomalous-area fraction.
/// </summary>
public sealed class OnnxAnomalyNode : IVisionNodeExecutor
{
    public string Type => "inference.anomaly";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var source = context.RequireImage("image").Mat;
        if (node.GetBool("crop", false))
            throw new InvalidOperationException("Anomaly inference with crop=true is unsupported because map and peak coordinates cannot be mapped through the center crop. Set crop=false.");
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        var threshold = node.GetDouble("threshold", 0.5);
        if (!double.IsFinite(threshold)) throw new InvalidOperationException("Anomaly threshold must be finite.");
        var mapThreshold = node.GetDouble("mapThreshold", 0.5);
        if (!double.IsFinite(mapThreshold)) throw new InvalidOperationException("Anomaly mapThreshold must be finite.");
        var useSeparateMapThreshold = node.GetBool("useSeparateMapThreshold", false);
        var thresholdMode = node.GetString("thresholdMode", "manual").Trim().ToLowerInvariant();
        if (thresholdMode is not ("manual" or "otsu")) throw new InvalidOperationException("Anomaly thresholdMode must be manual or otsu.");
        var mapLayout = node.GetString("mapLayout", "auto").Trim().ToLowerInvariant();
        if (mapLayout is not ("auto" or "channelsfirst" or "channelslast"))
            throw new InvalidOperationException("Anomaly mapLayout must be auto, channelsFirst, or channelsLast.");

        var scoreOutputName = node.GetString("scoreOutputName", string.Empty).Trim();
        var mapOutputName = node.GetString("mapOutputName", string.Empty).Trim();
        if (scoreOutputName.Length > 0 && mapOutputName.Length > 0 && scoreOutputName.Equals(mapOutputName, StringComparison.Ordinal))
            throw new InvalidOperationException("Anomaly scoreOutputName and mapOutputName must refer to different ONNX outputs.");
        var explicitNames = new[] { scoreOutputName, mapOutputName }.Where(x => x.Length > 0).ToArray();
        var legacyOutputName = node.GetString("outputName", string.Empty).Trim();
        var requested = explicitNames.Length > 0 ? explicitNames : string.IsNullOrEmpty(legacyOutputName) ? null : [legacyOutputName];
        var outputs = OnnxInferenceHelpers.ForwardOutputs(net, requested, modelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        OnnxOutputTensor? scoreTensor = null;
        OnnxOutputTensor? mapTensor = null;
        if (explicitNames.Length > 0)
        {
            if (scoreOutputName.Length > 0) scoreTensor = outputs[Array.IndexOf(explicitNames, scoreOutputName)];
            if (mapOutputName.Length > 0) mapTensor = outputs[Array.IndexOf(explicitNames, mapOutputName)];
        }
        else
        {
            var (scoreIndex, mapIndex) = InferenceStats.SelectAnomalyOutputs(outputs.Select(output => (IReadOnlyList<int>)output.Dimensions).ToArray());
            if (scoreIndex >= 0) scoreTensor = outputs[scoreIndex];
            if (mapIndex >= 0) mapTensor = outputs[mapIndex];
        }

        double? scalarScore = null;
        int[]? scoreShape = null;
        if (scoreTensor is not null)
        {
            scoreShape = scoreTensor.Dimensions;
            if (!IsScalarOutput(scoreShape))
                throw new InvalidOperationException($"Anomaly score output must contain exactly one value; received [{string.Join(',', scoreShape)}]. Use mapOutputName for a spatial output.");
            scalarScore = OnnxInferenceHelpers.ReadFloatTensor(scoreTensor, "anomaly score")[0];
        }

        Mat? heatmap = null;
        double? mapPeak = null, mapMean = null, mapMin = null, mapMax = null;
        double? peakX = null, peakY = null, areaFraction = null, suggestedThreshold = null;
        int[]? mapShape = null;
        double? effectiveMapThreshold = null;
        if (mapTensor is not null)
        {
            mapShape = mapTensor.Dimensions;
            var mapDims = ReadMapDimensions(mapShape, mapLayout);
            var mapWidth = mapDims.Width; var mapHeight = mapDims.Height;
            var mapValues = OnnxInferenceHelpers.ReadFloatTensor(mapTensor, "anomaly map");
            if (mapValues.Length != mapWidth * mapHeight) throw new InvalidOperationException("Anomaly map element count does not match its shape.");
            suggestedThreshold = InferenceStats.OtsuThreshold(mapValues);
            effectiveMapThreshold = thresholdMode == "otsu"
                ? suggestedThreshold
                : useSeparateMapThreshold ? mapThreshold : threshold;
            var stat = InferenceStats.AnomalyMap(mapValues, mapWidth, mapHeight, effectiveMapThreshold.Value);
            mapPeak = stat.Peak; mapMean = stat.Mean; mapMin = stat.Min; mapMax = stat.Max;
            peakX = stat.PeakX * (double)source.Width / mapWidth;
            peakY = stat.PeakY * (double)source.Height / mapHeight;
            areaFraction = stat.AreaFraction;
            var normalized = InferenceStats.NormalizeMap(mapValues);
            using var mapMat = new Mat(mapHeight, mapWidth, MatType.CV_8UC1);
            mapMat.SetArray(normalized);
            heatmap = new Mat();
            try { JLVisionOperators.Resize(mapMat, heatmap, new Size(source.Width, source.Height), 0, 0, InterpolationFlags.Linear); }
            catch { heatmap.Dispose(); heatmap = null; throw; }
        }

        if (scalarScore is null && mapPeak is null)
            throw new InvalidOperationException("Anomaly model outputs must include a scalar score, an anomaly map, or both.");
        var score = scalarScore ?? mapPeak!.Value;
        var isAnomaly = scalarScore is { } imageScore
            ? imageScore >= threshold
            : mapPeak!.Value >= effectiveMapThreshold!.Value;
        var outputShape = explicitNames.Length == 0 ? scoreShape ?? mapShape : null;
        var legacyThreshold = scalarScore is null ? effectiveMapThreshold ?? threshold : threshold;
        var json = JsonSerializer.Serialize(new
        {
            score,
            scoreSource = scalarScore is null ? "mapPeak" : "scalarOutput",
            isAnomaly,
            threshold = legacyThreshold,
            scoreThreshold = threshold,
            mapThreshold = effectiveMapThreshold,
            thresholdMode,
            useSeparateMapThreshold,
            mapLayout,
            scoreOutputName = scoreOutputName.Length > 0 ? scoreOutputName : null,
            mapOutputName = mapOutputName.Length > 0 ? mapOutputName : null,
            scoreOutputShape = scoreShape,
            mapOutputShape = mapShape,
            mapPeak,
            mapMean,
            mapMin,
            mapMax,
            mean = mapMean,
            min = mapMin,
            max = mapMax,
            suggestedThreshold,
            peakX,
            peakY,
            areaFraction,
            outputShape
        });
        var values = new Dictionary<string, VisionValue>
        {
            ["score"] = VisionValue.Double(score),
            ["isAnomaly"] = VisionValue.Boolean(isAnomaly),
            ["result"] = VisionValue.String(json)
        };
        if (heatmap is not null) values["heatmap"] = VisionValue.Image(VisionImage.Own(heatmap));
        if (mapPeak is { } peak) values["mapPeak"] = VisionValue.Double(peak);
        if (mapMean is { } mean) values["mapMean"] = VisionValue.Double(mean);
        if (mapMin is { } min) values["mapMin"] = VisionValue.Double(min);
        if (mapMax is { } max) values["mapMax"] = VisionValue.Double(max);
        if (peakX is { } x) values["peakX"] = VisionValue.Double(x);
        if (peakY is { } y) values["peakY"] = VisionValue.Double(y);
        if (areaFraction is { } fraction) values["areaFraction"] = VisionValue.Double(fraction);
        if (suggestedThreshold is { } suggested) values["suggestedThreshold"] = VisionValue.Double(suggested);

        return ValueTask.FromResult(new NodeExecutorResult(values,
            new Dictionary<string, object?>
            {
                ["score"] = score, ["scoreSource"] = scalarScore is null ? "mapPeak" : "scalarOutput", ["threshold"] = legacyThreshold,
                ["scoreThreshold"] = threshold, ["mean"] = mapMean, ["min"] = mapMin, ["max"] = mapMax,
                ["mapThreshold"] = effectiveMapThreshold, ["useSeparateMapThreshold"] = useSeparateMapThreshold, ["thresholdMode"] = thresholdMode,
                ["mapLayout"] = mapLayout, ["isAnomaly"] = isAnomaly,
                ["scoreOutputName"] = scoreOutputName, ["mapOutputName"] = mapOutputName, ["scoreOutputShape"] = scoreShape,
                ["mapOutputShape"] = mapShape, ["mapPeak"] = mapPeak, ["mapMean"] = mapMean, ["mapMin"] = mapMin,
                ["mapMax"] = mapMax, ["suggestedThreshold"] = suggestedThreshold, ["peakX"] = peakX,
                ["peakY"] = peakY, ["areaFraction"] = areaFraction, ["outputShape"] = outputShape
            }));
    }

    private static bool IsScalarOutput(IReadOnlyList<int> shape) =>
        shape.Count == 0 || shape[0] == 1 && shape.Aggregate(1L, (product, dimension) => product * dimension) == 1;

    private static (int Width, int Height) ReadMapDimensions(IReadOnlyList<int> shape, string mapLayout)
    {
        if (shape.Count == 4 && shape[0] == 1)
        {
            var channelsFirst = shape[1] == 1 && shape[2] > 0 && shape[3] > 0;
            var channelsLast = shape[1] > 0 && shape[2] > 0 && shape[3] == 1;
            if (mapLayout == "channelsfirst" && channelsFirst) return (shape[3], shape[2]);
            if (mapLayout == "channelslast" && channelsLast) return (shape[2], shape[1]);
            if (mapLayout == "auto")
            {
                if (channelsFirst && channelsLast)
                    throw new InvalidOperationException("Anomaly map layout is ambiguous for this 4D shape; set mapLayout to channelsFirst or channelsLast.");
                if (channelsFirst) return (shape[3], shape[2]);
                if (channelsLast) return (shape[2], shape[1]);
            }
        }
        if (shape.Count == 3)
        {
            var channelsFirst = shape[0] == 1 && shape[1] > 0 && shape[2] > 0;
            var channelsLast = shape[0] > 0 && shape[1] > 0 && shape[2] == 1;
            if (mapLayout == "channelsfirst" && channelsFirst) return (shape[2], shape[1]);
            if (mapLayout == "channelslast" && channelsLast) return (shape[1], shape[0]);
            if (mapLayout == "auto")
            {
                if (channelsFirst && channelsLast)
                    throw new InvalidOperationException("Anomaly map layout is ambiguous for this 3D shape; set mapLayout to channelsFirst or channelsLast.");
                if (channelsFirst) return (shape[2], shape[1]);
                if (channelsLast) return (shape[1], shape[0]);
            }
        }
        if (shape.Count == 2 && shape[0] > 0 && shape[1] > 0)
            return (shape[1], shape[0]);
        throw new InvalidOperationException($"Anomaly map must have shape [1,1,H,W], [1,H,W], [H,W], channels-last [1,H,W,1], or [H,W,1]; received [{string.Join(',', shape)}] for mapLayout={mapLayout}.");
    }
}
