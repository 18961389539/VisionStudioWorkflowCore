using System.Text.Json;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>Runs a configured ONNX image classification head: [1,C] or [1,C,1,1] float32 logits/scores.
/// layout=yolo26 treats the tensor as already-softmaxed class probabilities (Ultralytics YOLO26-cls ONNX).</summary>
public sealed class OnnxClassificationNode : IVisionNodeExecutor
{
    public string Type => "inference.classification";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var source = context.RequireImage("image").Mat;
        if (source.Empty()) throw new InvalidOperationException("ONNX inference requires a non-empty image.");
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        var layout = node.GetString("layout", "scores").Trim().ToLowerInvariant();
        // YOLO26-cls ONNX already embeds softmax; applying it again flattens the distribution.
        var applySoftmax = layout == "yolo26" ? false : node.GetBool("applySoftmax", true);
        var multiCrop = node.GetString("multiCrop", "none").Trim().ToLowerInvariant();
        if (multiCrop is not ("none" or "five" or "ten"))
            throw new InvalidOperationException("Classification multiCrop must be none, five, or ten.");
        var viewCropFraction = node.GetDouble("viewCropFraction", 0.9);
        if (multiCrop != "none" && (!double.IsFinite(viewCropFraction) || viewCropFraction is < 0.5 or > 1))
            throw new InvalidOperationException("Classification viewCropFraction must be between 0.5 and 1.");
        var minimumConfidence = node.GetDouble("minimumConfidence", 0);
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1)
            throw new InvalidOperationException("Classification minimumConfidence must be finite and between 0 and 1.");
        if (minimumConfidence > 0 && layout == "scores" && !applySoftmax)
            throw new InvalidOperationException("Classification minimumConfidence requires probabilities; enable applySoftmax or choose layout=yolo26.");
        var topK = node.GetInt("topK", 5);
        if (topK is < 1 or > 100) throw new InvalidOperationException("Classification topK must be between 1 and 100.");
        var labels = OnnxInferenceHelpers.ResolveLabels(node);
        var inputWidth = node.GetInt("inputWidth", 224); var inputHeight = node.GetInt("inputHeight", 224);
        if (inputWidth is < 1 or > 4096 || inputHeight is < 1 or > 4096)
            throw new InvalidOperationException("Classification inputWidth and inputHeight must be between 1 and 4096.");
        var views = multiCrop == "none"
            ? new List<Mat> { source }
            : CreateClassificationViews(source, inputWidth, inputHeight, viewCropFraction, multiCrop == "ten", cancellationToken);
        var ownsViews = multiCrop != "none";
        try
        {
            double[]? aggregate = null;
            int[]? outputShape = null;
            var viewPredictions = new List<object>(views.Count);
            var classCount = 0;
            var batchSize = 1;
            // Test-time augmentation is N views of the same image, so a dynamic-batch model can
            // score them in one run. Models with a fixed batch dimension of one fall back to the
            // per-view loop below; the outputs are identical either way.
            if (multiCrop != "none" && OnnxInferenceHelpers.SupportsBatch(net, views.Count))
            {
                using var batchInput = OnnxInferenceHelpers.PrepareBatchInput(net, views, node);
                using var batchOutput = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
                var batchShape = OnnxInferenceHelpers.Shape(batchOutput);
                var batchValues = OnnxInferenceHelpers.ReadFloatTensor(batchOutput, "classification batch");
                var batchShapeValid = batchShape.Length == 2 && batchShape[0] == views.Count && batchShape[1] > 0
                    || batchShape.Length == 4 && batchShape[0] == views.Count && batchShape[1] > 0 && batchShape[2] == 1 && batchShape[3] == 1;
                if (batchShapeValid && batchValues.Length % views.Count == 0)
                {
                    batchSize = views.Count;
                    var perView = batchValues.Length / views.Count;
                    classCount = perView;
                    outputShape = batchShape;
                    for (var viewIndex = 0; viewIndex < views.Count; viewIndex++)
                    {
                        var scores = batchValues.AsSpan(viewIndex * perView, perView).ToArray();
                        var viewRank = InferenceStats.RankClassification(scores, layout, applySoftmax, 1, labels);
                        viewPredictions.Add(new { view = viewIndex + 1, index = viewRank.Top[0].Index, label = viewRank.Top[0].Label, score = viewRank.Top[0].Score });
                        var distribution = ClassificationDistribution(scores, layout, applySoftmax);
                        aggregate ??= new double[distribution.Length];
                        if (distribution.Length != aggregate.Length) throw new InvalidOperationException($"Classification view {viewIndex + 1} produced {distribution.Length} classes; expected {aggregate.Length}.");
                        for (var classIndex = 0; classIndex < aggregate.Length; classIndex++) aggregate[classIndex] += distribution[classIndex];
                    }
                }
            }

            if (batchSize == 1)
            {
                for (var viewIndex = 0; viewIndex < views.Count; viewIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = OnnxInferenceHelpers.PrepareInput(net, views[viewIndex], node).Blob;
                    using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
                    var shape = OnnxInferenceHelpers.Shape(output);
                    var scores = ReadClassificationScores(output, shape);
                    if (aggregate is not null && scores.Length != classCount)
                        throw new InvalidOperationException($"Classification view {viewIndex + 1} produced {scores.Length} classes; expected {classCount}.");
                    classCount = scores.Length;
                    outputShape ??= shape;
                    var viewRank = InferenceStats.RankClassification(scores, layout, applySoftmax, 1, labels);
                    viewPredictions.Add(new { view = viewIndex + 1, index = viewRank.Top[0].Index, label = viewRank.Top[0].Label, score = viewRank.Top[0].Score });
                    if (multiCrop == "none")
                    {
                        aggregate = scores.Select(value => (double)value).ToArray();
                        break;
                    }

                    var distribution = ClassificationDistribution(scores, layout, applySoftmax);
                    aggregate ??= new double[distribution.Length];
                    for (var classIndex = 0; classIndex < aggregate.Length; classIndex++) aggregate[classIndex] += distribution[classIndex];
                }
            }

            var aggregateIsProbability = layout == "yolo26" || applySoftmax;
            var averaged = aggregate!.Select(value => (float)(value / views.Count)).ToArray();
            var rank = multiCrop == "none"
                ? InferenceStats.RankClassification(averaged, layout, applySoftmax, topK, labels)
                : InferenceStats.RankClassification(averaged, aggregateIsProbability ? "yolo26" : "scores", false, topK, labels);
            var best = rank.Top[0];
            var accepted = minimumConfidence <= 0 || best.Score >= minimumConfidence;
            var diag = CoordinateValidation.Diagnostics(Type, ("layout", layout), ("applySoftmax", applySoftmax), ("classCount", rank.ClassCount), ("bestIndex", best.Index), ("bestLabel", best.Label), ("confidence", best.Score), ("minimumConfidence", minimumConfidence), ("accepted", accepted), ("margin", rank.Margin), ("entropy", rank.Entropy), ("letterbox", node.GetBool("letterbox", false)), ("multiCrop", multiCrop), ("viewCropFraction", viewCropFraction), ("viewCount", views.Count), ("batchSize", batchSize));
            var jsonFields = new Dictionary<string, object?>
            {
                ["classes"] = rank.Top.Select(x => new { index = x.Index, label = x.Label, score = x.Score }),
                ["outputShape"] = outputShape, ["layout"] = layout, ["margin"] = rank.Margin, ["entropy"] = rank.Entropy,
                ["minimumConfidence"] = minimumConfidence, ["accepted"] = accepted
            };
            if (multiCrop != "none")
            {
                jsonFields["multiCrop"] = multiCrop;
                jsonFields["viewCropFraction"] = viewCropFraction;
                jsonFields["views"] = viewPredictions;
                jsonFields["batchSize"] = batchSize;
            }
            var json = JsonSerializer.Serialize(jsonFields);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["result"] = VisionValue.String(json),
                    ["label"] = VisionValue.String(best.Label),
                    ["classIndex"] = VisionValue.Integer(best.Index),
                    ["confidence"] = VisionValue.Double(best.Score),
                    ["accepted"] = VisionValue.Boolean(accepted),
                    ["margin"] = VisionValue.Double(rank.Margin),
                    ["entropy"] = VisionValue.Double(rank.Entropy),
                    ["classCount"] = VisionValue.Integer(rank.ClassCount),
                    ["diagnostics"] = VisionValue.String(diag)
                },
                new Dictionary<string, object?> { ["outputShape"] = outputShape, ["topK"] = rank.Top.Length, ["applySoftmax"] = applySoftmax, ["layout"] = layout, ["minimumConfidence"] = minimumConfidence, ["accepted"] = accepted, ["multiCrop"] = multiCrop, ["viewCropFraction"] = viewCropFraction, ["viewCount"] = views.Count, ["batchSize"] = batchSize, ["diagnostics"] = diag }));
        }
        finally
        {
            if (ownsViews) foreach (var view in views) view.Dispose();
        }
    }

    private static float[] ReadClassificationScores(OnnxOutputTensor output, int[] shape)
    {
        var valid = shape.Length == 2 && shape[0] == 1 && shape[1] > 0
            || shape.Length == 4 && shape[0] == 1 && shape[1] > 0 && shape[2] == 1 && shape[3] == 1;
        if (!valid)
            throw new InvalidOperationException($"Classification output must have shape [1,C] or [1,C,1,1]; received [{string.Join(',', shape)}].");
        return OnnxInferenceHelpers.ReadFloatTensor(output, "classification");
    }

    private static double[] ClassificationDistribution(float[] scores, string layout, bool applySoftmax)
    {
        if (layout == "yolo26" || !applySoftmax) return scores.Select(value => (double)value).ToArray();
        var maximum = scores.Max();
        var exponentials = scores.Select(value => Math.Exp(value - maximum)).ToArray();
        var total = exponentials.Sum();
        if (!double.IsFinite(total) || total <= 0) throw new InvalidOperationException("Classification crop softmax produced an invalid normalizer.");
        return exponentials.Select(value => value / total).ToArray();
    }

    private static List<Mat> CreateClassificationViews(Mat source, int inputWidth, int inputHeight, double cropFraction,
        bool includeHorizontalFlips, CancellationToken cancellationToken)
    {
        var targetAspect = (double)inputWidth / inputHeight;
        var fitWidth = (double)source.Width; var fitHeight = fitWidth / targetAspect;
        if (fitHeight > source.Height) { fitHeight = source.Height; fitWidth = fitHeight * targetAspect; }
        var cropWidth = Math.Clamp((int)Math.Round(fitWidth * cropFraction), 1, source.Width);
        var cropHeight = Math.Clamp((int)Math.Round(fitHeight * cropFraction), 1, source.Height);
        var maxX = source.Width - cropWidth; var maxY = source.Height - cropHeight;
        if (maxX == 0 && maxY == 0)
            throw new InvalidOperationException("Multi-crop aggregation needs a smaller viewCropFraction or a source aspect ratio that yields distinct views.");

        var centerX = maxX / 2; var centerY = maxY / 2;
        var rectangles = new[]
        {
            new Rect(0, 0, cropWidth, cropHeight),
            new Rect(maxX, 0, cropWidth, cropHeight),
            new Rect(0, maxY, cropWidth, cropHeight),
            new Rect(maxX, maxY, cropWidth, cropHeight),
            new Rect(centerX, centerY, cropWidth, cropHeight)
        };
        var views = new List<Mat>(includeHorizontalFlips ? 10 : 5);
        try
        {
            foreach (var rectangle in rectangles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var roi = new Mat(source, rectangle);
                var view = new Mat();
                try { JLVisionOperators.Resize(roi, view, new Size(inputWidth, inputHeight)); }
                catch { view.Dispose(); throw; }
                views.Add(view);
                if (includeHorizontalFlips)
                {
                    var mirrored = new Mat();
                    try { JLVisionOperators.MirrorHorizontal(view, mirrored); views.Add(mirrored); }
                    catch { mirrored.Dispose(); throw; }
                }
            }
            return views;
        }
        catch
        {
            foreach (var view in views) view.Dispose();
            throw;
        }
    }
}

/// <summary>Runs an ONNX detector. Layouts: xyxy rows, YOLO (YOLOv8/YOLO26 raw), yolov5 (with objectness).
/// YOLO26 end-to-end export (nms=False) uses xyxy with shape [1,300,6].</summary>
public sealed class OnnxDetectionNode : IVisionNodeExecutor
{
    public string Type => "inference.detection";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
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
        var layout = node.GetString("layout", "xyxy").Trim().ToLowerInvariant();
        if (layout is not ("xyxy" or "yolo" or "yolov5"))
            throw new InvalidOperationException($"Unsupported detection layout '{layout}'. Supported values are 'xyxy', 'yolo' and 'yolov5'.");
        var tensorOrder = node.GetString("tensorOrder", "auto").Trim().ToLowerInvariant();
        if (tensorOrder is not ("auto" or "channelsfirst" or "channelslast"))
            throw new InvalidOperationException("Detection tensorOrder must be auto, channelsFirst, or channelsLast.");
        var isYoloLayout = layout is "yolo" or "yolov5";
        if (rows <= 0 && !isYoloLayout)
            throw new InvalidOperationException($"Detection output must have shape [N,6] or [1,N,6] with rows [x1,y1,x2,y2,score,classId]; received [{string.Join(',', shape)}].");
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "detection");
        var threshold = node.GetDouble("confidenceThreshold", 0.25);
        var nmsThreshold = node.GetDouble("nmsThreshold", 0.45);
        var maxCount = node.GetInt("maxCount", 100);
        if (maxCount is < 1 or > 1000) throw new InvalidOperationException("maxCount must be between 1 and 1000.");
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || !double.IsFinite(nmsThreshold) || nmsThreshold is < 0 or > 1)
            throw new InvalidOperationException("Detection confidenceThreshold and nmsThreshold must be finite values from 0 to 1.");
        var coordinates = node.GetString("coordinates", "normalized").Trim().ToLowerInvariant();
        if (coordinates is not ("normalized" or "pixels"))
            throw new InvalidOperationException($"Unsupported detection coordinates '{coordinates}'. Supported values are 'normalized' and 'pixels'.");
        var letterbox = node.GetBool("letterbox", false);

        var candidates = new List<DetectionCandidate>();
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var inputWidth = node.GetInt("inputWidth", 224);
        var inputHeight = node.GetInt("inputHeight", 224);
        if (inputWidth < 1 || inputHeight < 1) throw new InvalidOperationException("Detection inputWidth/inputHeight must be positive.");
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
            if (coordinates == "normalized") { x1 *= inputWidth; x2 *= inputWidth; y1 *= inputHeight; y2 *= inputHeight; }
            var left = Math.Clamp(Math.Min(MapX(x1), MapX(x2)), 0, sourceWidth);
            var top = Math.Clamp(Math.Min(MapY(y1), MapY(y2)), 0, sourceHeight);
            var right = Math.Clamp(Math.Max(MapX(x1), MapX(x2)), 0, sourceWidth);
            var bottom = Math.Clamp(Math.Max(MapY(y1), MapY(y2)), 0, sourceHeight);
            if (score < threshold || right <= left || bottom <= top) return;
            candidates.Add(new DetectionCandidate(left, top, right, bottom, score, classId));
        }

        if (isYoloLayout)
        {
            // YOLOv8 / YOLO26 raw heads: cx, cy, w, h + per-class scores (no objectness).
            // YOLOv5: objectness at channel 4, class scores after.
            var yolo = InferenceStats.DecodeYolo(values, shape, useObjectness: layout == "yolov5", tensorOrder: tensorOrder);
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
            // xyxy / YOLO26 end-to-end (nms=False): [N,6] or [1,N,6]
            for (var i = 0; i < rows; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = i * 6;
                var rawClassId = values[offset + 5];
                if (rawClassId < 0 || rawClassId > int.MaxValue || Math.Abs(rawClassId - MathF.Round(rawClassId)) > 1e-4f)
                    throw new InvalidOperationException($"Detection row {i} has an invalid score or classId; score must be within [0,1] and classId must be a non-negative integer.");
                AddCandidate(values[offset], values[offset + 1], values[offset + 2], values[offset + 3], values[offset + 4], (int)MathF.Round(rawClassId), i);
            }
        }
        var kept = new List<DetectionCandidate>();
        var truncated = InferenceNmsCap.Fill(candidates.OrderByDescending(x => x.Score), maxCount, kept,
            (existing, candidate) => existing.Any(x => x.ClassId == candidate.ClassId && IoU(x, candidate) > nmsThreshold),
            cancellationToken);
        var detections = kept.Select((x, index) => ToVisionDetection(x, index)).ToArray();
        var payload = kept.Select(x => new { x1 = x.Left, y1 = x.Top, x2 = x.Right, y2 = x.Bottom, score = x.Score, classId = x.ClassId }).ToArray();
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["detections"] = VisionValue.Detections(detections),
                ["count"] = VisionValue.Integer(detections.Length),
                ["truncated"] = VisionValue.Boolean(truncated),
                ["result"] = VisionValue.String(JsonSerializer.Serialize(new { detections = payload, outputShape = shape, tensorOrder })),
                ["maxScore"] = VisionValue.Double(detections.Length == 0 ? 0 : detections.Max(d => d.Confidence ?? 0)),
                ["diagnostics"] = VisionValue.String(CoordinateValidation.Diagnostics(Type,
                    ("count", detections.Length), ("truncated", truncated), ("maxScore", detections.Length == 0 ? 0 : detections.Max(d => d.Confidence ?? 0)),
                    ("layout", layout), ("tensorOrder", tensorOrder), ("coordinates", coordinates), ("letterbox", letterbox),
                    ("confidenceThreshold", threshold), ("nmsThreshold", nmsThreshold), ("maxCount", maxCount),
                    ("outputShape", string.Join(',', shape))))
            },
            new Dictionary<string, object?> { ["count"] = detections.Length, ["coordinates"] = coordinates, ["layout"] = layout, ["letterbox"] = letterbox, ["outputShape"] = shape,
                ["maxScore"] = detections.Length == 0 ? 0 : detections.Max(d => d.Confidence ?? 0),
                ["diagnostics"] = CoordinateValidation.Diagnostics(Type, ("count", detections.Length), ("layout", layout)) }));
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

/// <summary>YOLOv8/YOLO26 ONNX oriented-box inference with exact rotated IoU suppression.</summary>
public sealed class OnnxOrientedDetectionNode : IVisionNodeExecutor
{
    public string Type => "inference.obb";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var source = context.RequireImage("image").Mat;
        if (node.GetBool("crop", false))
            throw new InvalidOperationException("OBB inference with crop=true is unsupported because crop mapping does not preserve box geometry. Set crop=false.");
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        using var output = OnnxInferenceHelpers.Forward(net, node.GetString("outputName", string.Empty).Trim(), modelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var shape = OnnxInferenceHelpers.Shape(output);
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "oriented detection");
        var layout = node.GetString("layout", "yolo").Trim().ToLowerInvariant();
        if (layout is "yolo26" or "raw") layout = "yolo";
        if (layout is "end2end" or "xywhr") layout = "xywhr";
        if (layout is not ("yolo" or "xywhr"))
            throw new InvalidOperationException($"Unsupported OBB layout '{layout}'. Supported values are 'yolo' (raw) and 'xywhr' (YOLO26 end-to-end).");
        var tensorOrder = node.GetString("tensorOrder", "auto").Trim().ToLowerInvariant();
        if (tensorOrder is not ("auto" or "channelsfirst" or "channelslast"))
            throw new InvalidOperationException("OBB tensorOrder must be auto, channelsFirst, or channelsLast.");

        var threshold = node.GetDouble("confidenceThreshold", 0.25);
        var nmsThreshold = node.GetDouble("nmsThreshold", 0.45);
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || !double.IsFinite(nmsThreshold) || nmsThreshold is < 0 or > 1)
            throw new InvalidOperationException("OBB confidenceThreshold and nmsThreshold must be finite values from 0 to 1.");
        var maxCount = node.GetInt("maxCount", 100);
        if (maxCount is < 1 or > 1000) throw new InvalidOperationException("maxCount must be between 1 and 1000.");
        var coordinates = node.GetString("coordinates", "pixels").Trim().ToLowerInvariant();
        if (coordinates is not ("pixels" or "normalized"))
            throw new InvalidOperationException($"Unsupported OBB coordinates '{coordinates}'. Supported values are 'pixels' and 'normalized'.");
        var letterbox = node.GetBool("letterbox", true);
        var nmsMode = node.GetString("nmsMode", "auto").Trim().ToLowerInvariant();
        if (nmsMode is not ("auto" or "on" or "off"))
            throw new InvalidOperationException("OBB nmsMode must be auto, on, or off.");
        var applyNms = nmsMode == "on" || nmsMode == "auto" && layout == "yolo";
        var inputWidth = node.GetInt("inputWidth", 640);
        var inputHeight = node.GetInt("inputHeight", 640);
        if (inputWidth < 1 || inputHeight < 1) throw new InvalidOperationException("OBB inputWidth/inputHeight must be positive.");

        // A rotated rectangle remains a rotated rectangle through letterbox (uniform scale).
        // A stretch with different X/Y scales turns it into a parallelogram, which Rectangle2D cannot represent.
        var stretchScaleX = (double)inputWidth / source.Width;
        var stretchScaleY = (double)inputHeight / source.Height;
        if (!letterbox && Math.Abs(stretchScaleX - stretchScaleY) > 1e-6 * Math.Max(stretchScaleX, stretchScaleY))
            throw new InvalidOperationException("OBB letterbox=false requires source and model input to have the same aspect ratio; enable letterbox to preserve rotated-box geometry.");
        var letterboxScale = Math.Min(stretchScaleX, stretchScaleY);
        var padX = letterbox ? (inputWidth - source.Width * letterboxScale) / 2 : 0;
        var padY = letterbox ? (inputHeight - source.Height * letterboxScale) / 2 : 0;
        var scaleX = letterbox ? letterboxScale : stretchScaleX;
        var scaleY = letterbox ? letterboxScale : stretchScaleY;
        double MapX(double x) => (x - padX) / scaleX;
        double MapY(double y) => (y - padY) / scaleY;

        var decoded = layout == "yolo"
            ? InferenceStats.DecodeYoloObb(values, shape, tensorOrder)
            : InferenceStats.DecodeYoloObbXywhr(values, shape);
        var candidates = new List<ObbCandidate>();
        for (var i = 0; i < decoded.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = decoded[i];
            if (!double.IsFinite(item.Score) || item.Score is < 0 or > 1)
                throw new InvalidOperationException($"OBB row {i} has a non-finite score or a score outside [0,1].");
            if (item.ClassId < 0) throw new InvalidOperationException($"OBB row {i} has a negative classId.");
            if (item.Score == 0 || item.Score < threshold) continue; // zero-score padding rows are normal in end-to-end exports
            if (!double.IsFinite(item.CenterX) || !double.IsFinite(item.CenterY) || !double.IsFinite(item.Width) || !double.IsFinite(item.Height) ||
                !double.IsFinite(item.AngleRad) || item.Width <= 0 || item.Height <= 0)
                throw new InvalidOperationException($"OBB row {i} has non-finite geometry, angle, or a non-positive width/height.");

            var cx = item.CenterX; var cy = item.CenterY; var width = item.Width; var height = item.Height;
            if (coordinates == "normalized")
            {
                cx *= inputWidth; cy *= inputHeight; width *= inputWidth; height *= inputHeight;
            }
            cx = MapX(cx); cy = MapY(cy);
            width /= scaleX; height /= scaleY;
            var corners = InferenceStats.OrientedBoxCorners(cx, cy, width, height, item.AngleRad);
            candidates.Add(new ObbCandidate(cx, cy, width, height, item.AngleRad, item.Score, item.ClassId, corners));
        }

        var kept = new List<ObbCandidate>();
        var truncated = InferenceNmsCap.Fill(candidates.OrderByDescending(x => x.Score), maxCount, kept,
            (existing, candidate) => applyNms && existing.Any(x => x.ClassId == candidate.ClassId && BoundsOverlap(x, candidate) &&
                InferenceStats.RotatedBoxIoU(x.CenterX, x.CenterY, x.Width, x.Height, x.AngleRad,
                    candidate.CenterX, candidate.CenterY, candidate.Width, candidate.Height, candidate.AngleRad) > nmsThreshold),
            cancellationToken);

        var labels = OnnxInferenceHelpers.ResolveLabels(node);
        var detections = new List<VisionDetection>(kept.Count);
        var payload = new List<object>(kept.Count);
        for (var i = 0; i < kept.Count; i++)
        {
            var item = kept[i];
            if (labels.Length > 0 && item.ClassId >= labels.Length)
                throw new InvalidOperationException($"OBB classId {item.ClassId} is outside the configured labels list ({labels.Length} labels).");
            var minX = item.Corners.Min(p => p.X); var maxX = item.Corners.Max(p => p.X);
            var minY = item.Corners.Min(p => p.Y); var maxY = item.Corners.Max(p => p.Y);
            var perimeter = 2 * (item.Width + item.Height);
            detections.Add(new VisionDetection(i, minX, minY, maxX - minX, maxY - minY,
                item.Width * item.Height, perimeter, perimeter > 0 ? 4 * Math.PI * item.Width * item.Height / (perimeter * perimeter) : 0,
                new VisionCircle(item.CenterX, item.CenterY, Math.Sqrt(item.Width * item.Width + item.Height * item.Height) / 2),
                new VisionRectangle2D(item.CenterX, item.CenterY, item.Width, item.Height, item.AngleRad * 180 / Math.PI), item.Score, item.ClassId));
            payload.Add(new
            {
                index = i,
                classId = item.ClassId,
                label = labels.Length > 0 ? labels[item.ClassId] : $"class_{item.ClassId}",
                score = item.Score,
                centerX = item.CenterX,
                centerY = item.CenterY,
                width = item.Width,
                height = item.Height,
                angleRad = item.AngleRad,
                angleDeg = item.AngleRad * 180 / Math.PI,
                bounds = new { x = minX, y = minY, width = maxX - minX, height = maxY - minY },
                polygon = item.Corners.Select(p => new { x = p.X, y = p.Y }).ToArray()
            });
        }

        var overlay = BuildObbOverlay(source, kept, labels);
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("layout", layout), ("count", detections.Count), ("truncated", truncated), ("nmsMode", nmsMode),
            ("letterbox", letterbox), ("coordinates", coordinates), ("outputShape", shape));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["detections"] = VisionValue.Detections(detections),
                ["count"] = VisionValue.Integer(detections.Count),
                ["truncated"] = VisionValue.Boolean(truncated),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlay)),
                ["result"] = VisionValue.String(JsonSerializer.Serialize(new { count = detections.Count, layout, outputShape = shape, nmsApplied = applyNms, detections = payload })),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?> { ["count"] = detections.Count, ["layout"] = layout, ["nmsApplied"] = applyNms,
                ["nmsMode"] = nmsMode, ["letterbox"] = letterbox, ["coordinates"] = coordinates, ["outputShape"] = shape, ["diagnostics"] = diagnostics }));
    }

    private static bool BoundsOverlap(ObbCandidate a, ObbCandidate b) =>
        a.Right > b.Left && b.Right > a.Left && a.Bottom > b.Top && b.Bottom > a.Top;

    private static Mat BuildObbOverlay(Mat source, IReadOnlyList<ObbCandidate> boxes, string[] labels)
    {
        var overlay = new Mat();
        try
        {
            if (source.Channels() == 1) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
            else if (source.Channels() == 3) source.CopyTo(overlay);
            else if (source.Channels() == 4) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.BGRA2BGR);
            else throw new InvalidOperationException($"OBB overlay supports 1, 3, or 4 channel images; received {source.Channels()}.");

            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                var color = InferenceStats.SegmentationColorBgr(box.ClassId + 1);
                var scalar = new Scalar(color.B, color.G, color.R);
                var points = box.Corners.Select(p => new Point(
                    (int)Math.Round(Math.Clamp(p.X, -2d * source.Width, 3d * source.Width)),
                    (int)Math.Round(Math.Clamp(p.Y, -2d * source.Height, 3d * source.Height)))).ToArray();
                for (var edge = 0; edge < points.Length; edge++)
                    Cv2.Line(overlay, points[edge], points[(edge + 1) % points.Length], scalar, 2, LineTypes.AntiAlias);
                var label = labels.Length > box.ClassId ? labels[box.ClassId] : $"class_{box.ClassId}";
                if (label.Any(character => character > 127)) label = $"class_{box.ClassId}"; // OpenCV Hershey fonts are ASCII-only.
                var text = $"{label} {box.Score:0.00} {box.AngleRad * 180 / Math.PI:0.0}deg";
                var textX = (int)Math.Round(Math.Clamp(box.Left, 0, source.Width - 1));
                var textY = (int)Math.Round(Math.Clamp(box.Top - 5, 16, Math.Max(16, source.Height - 1)));
                Cv2.PutText(overlay, text, new Point(textX, textY),
                    HersheyFonts.HersheySimplex, 0.45, scalar, 1, LineTypes.AntiAlias);
            }
            return overlay;
        }
        catch { overlay.Dispose(); throw; }
    }

    private sealed record ObbCandidate(double CenterX, double CenterY, double Width, double Height, double AngleRad, double Score, int ClassId, (double X, double Y)[] Corners)
    {
        public double Left => Corners.Min(p => p.X);
        public double Right => Corners.Max(p => p.X);
        public double Top => Corners.Min(p => p.Y);
        public double Bottom => Corners.Max(p => p.Y);
    }
}
