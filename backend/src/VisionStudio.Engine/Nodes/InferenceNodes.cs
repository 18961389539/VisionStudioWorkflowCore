using System.Text.Json;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Shared OpenCV DNN ONNX path. Input is always a single BGR image converted to float NCHW [1,3,H,W].
/// Each invocation owns its Net and temporary Mats, so concurrent node executions do not share mutable DNN state.
/// </summary>
internal static class OnnxInferenceHelpers
{
    public static Net LoadNet(NodeDefinition node)
    {
        var modelPath = node.GetString("modelPath", string.Empty).Trim();
        if (modelPath.Length == 0)
            throw new InvalidOperationException($"Node '{node.Type}' requires modelPath to an ONNX model file.");
        var resolved = Path.GetFullPath(modelPath);
        if (!File.Exists(resolved))
            throw new InvalidOperationException($"ONNX model '{resolved}' does not exist.");
        try
        {
            var net = Cv2.Dnn.ReadNetFromONNX(resolved);
            if (net is null || net.Empty())
            {
                net?.Dispose();
                throw new InvalidOperationException($"ONNX model '{resolved}' could not be loaded or contains no network.");
            }
            return net;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not load ONNX model '{resolved}': {ex.Message}", ex);
        }
    }

    public static (Mat Blob, string OutputName, int Width, int Height) PrepareInput(
        Net net, Mat source, NodeDefinition node)
    {
        if (source.Empty()) throw new InvalidOperationException("ONNX inference requires a non-empty image.");
        var width = node.GetInt("inputWidth", 224);
        var height = node.GetInt("inputHeight", 224);
        if (width is < 1 or > 4096 || height is < 1 or > 4096)
            throw new InvalidOperationException("ONNX inputWidth and inputHeight must be between 1 and 4096.");
        var scale = node.GetDouble("scale", 1d / 255);
        var meanB = node.GetDouble("meanB", 0);
        var meanG = node.GetDouble("meanG", 0);
        var meanR = node.GetDouble("meanR", 0);
        if (!double.IsFinite(scale) || !double.IsFinite(meanB) || !double.IsFinite(meanG) || !double.IsFinite(meanR))
            throw new InvalidOperationException("ONNX normalization scale and mean values must be finite.");

        Mat? bgr = null;
        Mat? blob = null;
        try
        {
            if (source.Channels() == 3)
                bgr = source;
            else
            {
                bgr = new Mat();
                var conversion = source.Channels() switch
                {
                    1 => ColorConversionCodes.GRAY2BGR,
                    4 => ColorConversionCodes.BGRA2BGR,
                    _ => throw new InvalidOperationException($"ONNX inference supports 1, 3, or 4 channel images; received {source.Channels()}.")
                };
                Cv2.CvtColor(source, bgr, conversion);
            }

            blob = Cv2.Dnn.BlobFromImage(
                bgr,
                scale,
                new Size(width, height),
                new Scalar(meanB, meanG, meanR),
                node.GetBool("swapRB", false),
                node.GetBool("crop", false));
            net.SetInput(blob);
            var result = blob;
            blob = null;
            return (result, node.GetString("outputName", string.Empty).Trim(), width, height);
        }
        finally
        {
            if (!ReferenceEquals(bgr, source)) bgr?.Dispose();
            blob?.Dispose();
        }
    }

    public static Mat Forward(Net net, string outputName, string modelPath)
    {
        try
        {
            return net.Forward(string.IsNullOrEmpty(outputName) ? null : outputName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ONNX model '{modelPath}' inference failed for output '{(string.IsNullOrEmpty(outputName) ? "<default>" : outputName)}': {ex.Message}", ex);
        }
    }

    public static float[] ReadFloatTensor(Mat tensor, string contract)
    {
        if (tensor.Empty()) throw new InvalidOperationException($"ONNX output for {contract} is empty.");
        if (tensor.Type() != MatType.CV_32FC1)
            throw new InvalidOperationException($"ONNX output for {contract} must be float32 single-channel; received {tensor.Type()}.");
        if (!tensor.IsContinuous())
            throw new InvalidOperationException($"ONNX output for {contract} is not contiguous.");
        var values = new float[checked((int)tensor.Total())];
        Marshal.Copy(tensor.Data, values, 0, values.Length);
        if (values.Length != tensor.Total())
            throw new InvalidOperationException($"ONNX output for {contract} could not be read as a contiguous float tensor.");
        if (values.Any(x => !float.IsFinite(x)))
            throw new InvalidOperationException($"ONNX output for {contract} contains a non-finite value.");
        return values;
    }

    public static int[] Shape(Mat tensor)
        => Enumerable.Range(0, tensor.Dims).Select(tensor.Size).ToArray();

    public static string ResolvePath(NodeDefinition node)
        => Path.GetFullPath(node.GetString("modelPath", string.Empty).Trim());
}

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
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath);
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
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath);
        cancellationToken.ThrowIfCancellationRequested();
        var shape = OnnxInferenceHelpers.Shape(output);
        var rows = shape.Length == 2 && shape[1] == 6
            ? shape[0]
            : shape.Length == 3 && shape[0] == 1 && shape[2] == 6 ? shape[1] : 0;
        if (rows <= 0)
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

        var candidates = new List<DetectionCandidate>();
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var inputWidth = node.GetInt("inputWidth", 224);
        var inputHeight = node.GetInt("inputHeight", 224);
        for (var i = 0; i < rows; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = i * 6;
            var x1 = (double)values[offset];
            var y1 = (double)values[offset + 1];
            var x2 = (double)values[offset + 2];
            var y2 = (double)values[offset + 3];
            var score = (double)values[offset + 4];
            var rawClassId = values[offset + 5];
            if (score is < 0 or > 1 || rawClassId < 0 || rawClassId > int.MaxValue || Math.Abs(rawClassId - MathF.Round(rawClassId)) > 1e-4f)
                throw new InvalidOperationException($"Detection row {i} has an invalid score or classId; score must be within [0,1] and classId must be a non-negative integer.");
            var classId = (int)rawClassId;
            if (coordinates == "normalized") { x1 *= inputWidth; x2 *= inputWidth; y1 *= inputHeight; y2 *= inputHeight; }
            x1 *= (double)sourceWidth / inputWidth; x2 *= (double)sourceWidth / inputWidth;
            y1 *= (double)sourceHeight / inputHeight; y2 *= (double)sourceHeight / inputHeight;
            var left = Math.Clamp(Math.Min(x1, x2), 0, sourceWidth);
            var top = Math.Clamp(Math.Min(y1, y2), 0, sourceHeight);
            var right = Math.Clamp(Math.Max(x1, x2), 0, sourceWidth);
            var bottom = Math.Clamp(Math.Max(y1, y2), 0, sourceHeight);
            if (score < threshold || right <= left || bottom <= top) continue;
            candidates.Add(new DetectionCandidate(left, top, right, bottom, score, classId));
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
            new Dictionary<string, object?> { ["count"] = detections.Length, ["coordinates"] = coordinates, ["outputShape"] = shape }));
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
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath);
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
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte label;
            if (channels == 1)
                label = values[y * width + x] >= threshold ? (byte)1 : (byte)0;
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
            }
            modelMask.Set(y, x, label);
        }
        var mask = new Mat();
        try { Cv2.Resize(modelMask, mask, new Size(source.Width, source.Height), 0, 0, InterpolationFlags.Nearest); }
        catch { mask.Dispose(); throw; }
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["mask"] = VisionValue.Image(VisionImage.Own(mask)) },
            new Dictionary<string, object?> { ["outputShape"] = shape, ["classChannels"] = channels, ["maskWidth"] = mask.Width, ["maskHeight"] = mask.Height }));
    }
}

/// <summary>Runs an ONNX anomaly head with one scalar float32 output score.</summary>
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
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath);
        cancellationToken.ThrowIfCancellationRequested();
        var shape = OnnxInferenceHelpers.Shape(output);
        var count = shape.Aggregate(1L, (a, b) => a * b);
        var scalarShape = shape.Length is >= 1 and <= 4 && shape[0] == 1 && count == 1;
        if (!scalarShape)
            throw new InvalidOperationException($"Anomaly output must be a single scalar tensor (for example [1] or [1,1]); received [{string.Join(',', shape)}].");
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "anomaly");
        var threshold = node.GetDouble("threshold", 0.5);
        if (!double.IsFinite(threshold)) throw new InvalidOperationException("Anomaly threshold must be finite.");
        var score = (double)values[0];
        var isAnomaly = score >= threshold;
        var json = JsonSerializer.Serialize(new { score, threshold, isAnomaly, outputShape = shape });
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["score"] = VisionValue.Double(score),
                ["isAnomaly"] = VisionValue.Boolean(isAnomaly),
                ["result"] = VisionValue.String(json)
            },
            new Dictionary<string, object?> { ["score"] = score, ["threshold"] = threshold, ["isAnomaly"] = isAnomaly, ["outputShape"] = shape }));
    }
}
