using System.Text.Json;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>Runs a configured ONNX image classification head: [1,C] or [1,C,1,1] float32 logits/scores.</summary>
public sealed class OnnxClassificationNode : IVisionNodeExecutor
{
    public string Type => "inference.classification";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var shape = OnnxInferenceHelpers.Shape(output);
        var valid = shape.Length == 2 && shape[0] == 1 && shape[1] > 0
            || shape.Length == 4 && shape[0] == 1 && shape[1] > 0 && shape[2] == 1 && shape[3] == 1;
        if (!valid)
            throw new InvalidOperationException($"Classification output must have shape [1,C] or [1,C,1,1]; received [{string.Join(',', shape)}].");
        var scores = OnnxInferenceHelpers.ReadFloatTensor(output, "classification");
        if (node.GetBool("applySoftmax", true))
        {
            var max = scores.Max();
            var exp = scores.Select(x => Math.Exp(x - max)).ToArray();
            var sum = exp.Sum();
            scores = exp.Select(x => (float)(x / sum)).ToArray();
        }
        var labels = node.GetString("labels", string.Empty).Split(',', StringSplitOptions.TrimEntries);
        var topK = Math.Clamp(node.GetInt("topK", 5), 1, scores.Length);
        var top = scores.Select((score, index) => (score, index))
            .OrderByDescending(x => x.score).ThenBy(x => x.index).Take(topK)
            .Select(x => new { index = x.index, label = LabelAt(labels, x.index), score = x.score }).ToArray();
        var best = top[0];
        var json = JsonSerializer.Serialize(new { classes = top, outputShape = shape });
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["result"] = VisionValue.String(json),
                ["label"] = VisionValue.String(best.label),
                ["classIndex"] = VisionValue.Integer(best.index),
                ["confidence"] = VisionValue.Double(best.score)
            },
            new Dictionary<string, object?> { ["outputShape"] = shape, ["topK"] = top.Length, ["applySoftmax"] = node.GetBool("applySoftmax", true) }));
    }

    private static string LabelAt(string[] labels, int index)
        => index < labels.Length && !string.IsNullOrWhiteSpace(labels[index]) ? labels[index] : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Runs an ONNX detector whose output is strictly rows of [x1,y1,x2,y2,score,classId].</summary>
public sealed class OnnxDetectionNode : IVisionNodeExecutor
{
    public string Type => "inference.detection";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        if (node.GetBool("crop", false))
            throw new InvalidOperationException("Detection with crop=true is unsupported because detection coordinates cannot be mapped through the center crop. Set crop=false.");
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var shape = OnnxInferenceHelpers.Shape(output);
        var rows = shape.Length == 2 && shape[1] == 6
            ? shape[0]
            : shape.Length == 3 && shape[0] == 1 && shape[2] == 6 ? shape[1] : 0;
        var isYoloLayout = node.GetString("layout", "xyxy").Trim().Equals("yolo", StringComparison.OrdinalIgnoreCase);
        if (rows <= 0 && !isYoloLayout)
            throw new InvalidOperationException($"Detection output must have shape [N,6] or [1,N,6] with rows [x1,y1,x2,y2,score,classId]; received [{string.Join(',', shape)}].");
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "detection");
        var threshold = node.GetDouble("confidenceThreshold", 0.25);
        var nmsThreshold = node.GetDouble("nmsThreshold", 0.45);
        var maxCount = Math.Clamp(node.GetInt("maxCount", 100), 1, 1000);
        var normalized = node.GetString("coordinates", "normalized").Trim().Equals("normalized", StringComparison.OrdinalIgnoreCase);
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || !double.IsFinite(nmsThreshold) || nmsThreshold is < 0 or > 1)
            throw new InvalidOperationException("Detection confidenceThreshold and nmsThreshold must be finite values from 0 to 1.");
        var coordinates = node.GetString("coordinates", "normalized").Trim().ToLowerInvariant();
        if (coordinates is not ("normalized" or "pixels"))
            throw new InvalidOperationException($"Unsupported detection coordinates '{coordinates}'. Supported values are 'normalized' and 'pixels'.");
        var layout = node.GetString("layout", "xyxy").Trim().ToLowerInvariant();
        if (layout is not ("xyxy" or "yolo"))
            throw new InvalidOperationException($"Unsupported detection layout '{layout}'. Supported values are 'xyxy' and 'yolo'.");
        var letterbox = node.GetBool("letterbox", false);

        var candidates = new List<DetectionCandidate>();
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var inputWidth = node.GetInt("inputWidth", 224);
        var inputHeight = node.GetInt("inputHeight", 224);
        if (inputWidth < 1 || inputHeight < 1) throw new InvalidOperationException("Detection inputWidth/inputHeight must be positive.");
        // Input-pixel -> source-pixel mapping. Letterbox mode inverts the symmetric
        // padding used when the source was resized into the model input preserving aspect.
        double MapX(double inputX)
        {
            if (!letterbox) return inputX * sourceWidth / inputWidth;
            var scale = Math.Min((double)inputWidth / sourceWidth, (double)inputHeight / sourceHeight);
            return (inputX - (inputWidth - sourceWidth * scale) / 2) / scale;
        }
        double MapY(double inputY)
        {
            if (!letterbox) return inputY * sourceHeight / inputHeight;
            var scale = Math.Min((double)inputWidth / sourceWidth, (double)inputHeight / sourceHeight);
            return (inputY - (inputHeight - sourceHeight * scale) / 2) / scale;
        }
        void AddCandidate(double x1, double y1, double x2, double y2, double score, int classId, int rowIndex)
        {
            if (score is < 0 or > 1 || classId < 0)
                throw new InvalidOperationException($"Detection row {rowIndex} has an invalid score or classId; score must be within [0,1] and classId must be a non-negative integer.");
            // Coordinates arrive normalized to the model input or in input pixels; both
            // are converted to input pixels first, then mapped back to the source.
            if (coordinates == "normalized") { x1 *= inputWidth; x2 *= inputWidth; y1 *= inputHeight; y2 *= inputHeight; }
            var left = Math.Clamp(Math.Min(MapX(x1), MapX(x2)), 0, sourceWidth);
            var top = Math.Clamp(Math.Min(MapY(y1), MapY(y2)), 0, sourceHeight);
            var right = Math.Clamp(Math.Max(MapX(x1), MapX(x2)), 0, sourceWidth);
            var bottom = Math.Clamp(Math.Max(MapY(y1), MapY(y2)), 0, sourceHeight);
            if (score < threshold || right <= left || bottom <= top) return;
            candidates.Add(new DetectionCandidate(left, top, right, bottom, score, classId));
        }

        if (layout == "yolo")
        {
            // YOLOv8-style heads: cx, cy, w, h + per-class scores (no objectness).
            var yolo = InferenceStats.DecodeYolo(values, shape);
            for (var i = 0; i < yolo.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = yolo[i];
                if (!double.IsFinite(c.CenterX) || !double.IsFinite(c.CenterY) || !double.IsFinite(c.Width) || !double.IsFinite(c.Height) || c.Width < 0 || c.Height < 0)
                    throw new InvalidOperationException($"YOLO detection row {i} has non-finite or negative box geometry.");
                AddCandidate(c.CenterX - c.Width / 2, c.CenterY - c.Height / 2, c.CenterX + c.Width / 2, c.CenterY + c.Height / 2, c.Score, c.ClassId, i);
            }
        }
        else
        {
            for (var i = 0; i < rows; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = i * 6;
                var rawClassId = values[offset + 5];
                if (rawClassId < 0 || rawClassId > int.MaxValue || Math.Abs(rawClassId - MathF.Round(rawClassId)) > 1e-4f)
                    throw new InvalidOperationException($"Detection row {i} has an invalid score or classId; score must be within [0,1] and classId must be a non-negative integer.");
                AddCandidate(values[offset], values[offset + 1], values[offset + 2], values[offset + 3], values[offset + 4], (int)rawClassId, i);
            }
        }
        var kept = new List<DetectionCandidate>();
        foreach (var candidate in candidates.OrderByDescending(x => x.Score))
        {
            if (kept.Any(x => x.ClassId == candidate.ClassId && IoU(x, candidate) > nmsThreshold)) continue;
            kept.Add(candidate);
            if (kept.Count == maxCount) break;
        }
        var detections = kept.Select((x, index) => ToVisionDetection(x, index)).ToArray();
        var payload = kept.Select(x => new { x1 = x.Left, y1 = x.Top, x2 = x.Right, y2 = x.Bottom, score = x.Score, classId = x.ClassId }).ToArray();
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["detections"] = VisionValue.Detections(detections),
                ["count"] = VisionValue.Integer(detections.Length),
                ["result"] = VisionValue.String(JsonSerializer.Serialize(new { detections = payload, outputShape = shape }))
            },
            new Dictionary<string, object?> { ["count"] = detections.Length, ["coordinates"] = coordinates, ["layout"] = layout, ["letterbox"] = letterbox, ["outputShape"] = shape }));
    }

    private static double IoU(DetectionCandidate a, DetectionCandidate b)
    {
        var iw = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var ih = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var intersection = iw * ih;
        var areaA = (a.Right - a.Left) * (a.Bottom - a.Top);
        var areaB = (b.Right - b.Left) * (b.Bottom - b.Top);
        return intersection / (areaA + areaB - intersection);
    }

    private static VisionDetection ToVisionDetection(DetectionCandidate x, int index)
    {
        var width = x.Right - x.Left;
        var height = x.Bottom - x.Top;
        var cx = (x.Left + x.Right) / 2;
        var cy = (x.Top + x.Bottom) / 2;
        return new VisionDetection(index, x.Left, x.Top, width, height, width * height, 0, 0,
            new VisionCircle(cx, cy, Math.Sqrt(width * width + height * height) / 2),
            new VisionRectangle2D(x.Left, x.Top, width, height), x.Score, x.ClassId);
    }

    private sealed record DetectionCandidate(double Left, double Top, double Right, double Bottom, double Score, int ClassId);
}

/// <summary>Runs an ONNX semantic segmentation head with output [1,C,H,W] and emits an 8-bit label mask.</summary>
public sealed class OnnxSegmentationNode : IVisionNodeExecutor
{
    public string Type => "inference.segmentation";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        if (node.GetBool("crop", false))
            throw new InvalidOperationException("Segmentation with crop=true is unsupported because mask coordinates cannot be mapped through the center crop. Set crop=false.");
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var shape = OnnxInferenceHelpers.Shape(output);
        if (shape.Length != 4 || shape[0] != 1 || shape[1] is < 1 or > 256 || shape[2] < 1 || shape[3] < 1)
            throw new InvalidOperationException($"Segmentation output must have shape [1,C,H,W] with 1<=C<=256; received [{string.Join(',', shape)}].");
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "segmentation");
        var channels = shape[1];
        var height = shape[2];
        var width = shape[3];
        var threshold = node.GetDouble("binaryThreshold", 0.5);
        if (!double.IsFinite(threshold)) throw new InvalidOperationException("binaryThreshold must be finite.");
        using var modelMask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        var labels = new byte[height * width];
        var bestScores = new float[height * width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte label;
            float winningScore;
            if (channels == 1)
            {
                winningScore = values[y * width + x];
                label = winningScore >= threshold ? (byte)1 : (byte)0;
            }
            else
            {
                var bestClass = 0;
                var bestScore = float.NegativeInfinity;
                for (var c = 0; c < channels; c++)
                {
                    var score = values[(c * height + y) * width + x];
                    if (score > bestScore) { bestScore = score; bestClass = c; }
                }
                label = (byte)bestClass;
                winningScore = bestScore;
            }
            modelMask.Set(y, x, label);
            labels[y * width + x] = label;
            bestScores[y * width + x] = winningScore;
        }
        // Per-class reporting (ViDi/HALCON DL segmentation style): area, image fraction
        // and mean winning score per class actually present in the mask.
        var classStats = InferenceStats.SegmentationClasses(labels, bestScores, channels == 1 ? 2 : channels)
            .Where(s => s.PixelCount > 0).ToArray();
        var dominant = classStats.OrderByDescending(s => s.PixelCount).FirstOrDefault();
        var mask = new Mat();
        try { JLVisionOperators.Resize(modelMask, mask, new Size(source.Width, source.Height), 0, 0, InterpolationFlags.Nearest); }
        catch { mask.Dispose(); throw; }
        var segOutputs = new Dictionary<string, VisionValue>
        {
            ["mask"] = VisionValue.Image(VisionImage.Own(mask)),
            ["classes"] = VisionValue.String(JsonSerializer.Serialize(classStats.Select(s => new { classId = s.ClassId, pixelCount = s.PixelCount, fraction = s.Fraction, meanScore = s.MeanScore }))),
            ["classCount"] = VisionValue.Integer(classStats.Length)
        };
        if (dominant is not null)
        {
            segOutputs["dominantClass"] = VisionValue.Integer(dominant.ClassId);
            segOutputs["dominantFraction"] = VisionValue.Double(dominant.Fraction);
        }
        return ValueTask.FromResult(new NodeExecutorResult(segOutputs,
            new Dictionary<string, object?> { ["outputShape"] = shape, ["classChannels"] = channels, ["maskWidth"] = mask.Width, ["maskHeight"] = mask.Height, ["classCount"] = classStats.Length, ["dominantClass"] = dominant?.ClassId, ["dominantFraction"] = dominant?.Fraction }));
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
        var source = context.RequireImage("image").Mat;
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var shape = OnnxInferenceHelpers.Shape(output);
        var count = shape.Aggregate(1L, (a, b) => a * b);
        var threshold = node.GetDouble("threshold", 0.5);
        if (!double.IsFinite(threshold)) throw new InvalidOperationException("Anomaly threshold must be finite.");
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "anomaly");

        var scalarShape = shape.Length is >= 1 and <= 4 && shape[0] == 1 && count == 1;
        if (scalarShape)
        {
            var scalar = (double)values[0];
            var scalarAnomaly = scalar >= threshold;
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["score"] = VisionValue.Double(scalar),
                    ["isAnomaly"] = VisionValue.Boolean(scalarAnomaly),
                    ["result"] = VisionValue.String(JsonSerializer.Serialize(new { score = scalar, threshold, isAnomaly = scalarAnomaly, outputShape = shape }))
                },
                new Dictionary<string, object?> { ["score"] = scalar, ["threshold"] = threshold, ["isAnomaly"] = scalarAnomaly, ["outputShape"] = shape }));
        }

        int mapWidth, mapHeight;
        if (shape.Length == 4 && shape[0] == 1 && shape[1] == 1 && shape[2] > 0 && shape[3] > 0) { mapHeight = (int)shape[2]; mapWidth = (int)shape[3]; }
        else if (shape.Length == 3 && shape[0] == 1 && shape[1] > 0 && shape[2] > 0) { mapHeight = (int)shape[1]; mapWidth = (int)shape[2]; }
        else throw new InvalidOperationException($"Anomaly output must be a single scalar tensor (for example [1] or [1,1]) or an anomaly map [1,1,H,W] / [1,H,W]; received [{string.Join(',', shape)}].");
        if (values.Length != mapWidth * mapHeight) throw new InvalidOperationException("Anomaly map element count does not match its shape.");
        var stat = InferenceStats.AnomalyMap(values, mapWidth, mapHeight, threshold);
        var normalized = InferenceStats.NormalizeMap(values);
        using var mapMat = new Mat(mapHeight, mapWidth, MatType.CV_8UC1);
        mapMat.SetArray(normalized);
        var heatmap = new Mat();
        try { JLVisionOperators.Resize(mapMat, heatmap, new Size(source.Width, source.Height), 0, 0, InterpolationFlags.Linear); }
        catch { heatmap.Dispose(); throw; }
        var peakX = stat.PeakX * (double)source.Width / mapWidth;
        var peakY = stat.PeakY * (double)source.Height / mapHeight;
        var isAnomaly = stat.Peak >= threshold;
        var json = JsonSerializer.Serialize(new { score = stat.Peak, mean = stat.Mean, min = stat.Min, max = stat.Max, threshold, isAnomaly, peakX, peakY, areaFraction = stat.AreaFraction, outputShape = shape });
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["score"] = VisionValue.Double(stat.Peak),
                ["isAnomaly"] = VisionValue.Boolean(isAnomaly),
                ["heatmap"] = VisionValue.Image(VisionImage.Own(heatmap)),
                ["peakX"] = VisionValue.Double(peakX),
                ["peakY"] = VisionValue.Double(peakY),
                ["areaFraction"] = VisionValue.Double(stat.AreaFraction),
                ["result"] = VisionValue.String(json)
            },
            new Dictionary<string, object?> { ["score"] = stat.Peak, ["mean"] = stat.Mean, ["threshold"] = threshold, ["isAnomaly"] = isAnomaly, ["peakX"] = peakX, ["peakY"] = peakY, ["areaFraction"] = stat.AreaFraction, ["outputShape"] = shape }));
    }
}
