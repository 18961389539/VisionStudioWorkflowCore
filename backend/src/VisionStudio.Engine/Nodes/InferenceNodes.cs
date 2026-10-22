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
                ["result"] = VisionValue.String(JsonSerializer.Serialize(new { detections = payload, outputShape = shape, tensorOrder })),
                ["maxScore"] = VisionValue.Double(detections.Length == 0 ? 0 : detections.Max(d => d.Confidence ?? 0)),
                ["diagnostics"] = VisionValue.String(CoordinateValidation.Diagnostics(Type,
                    ("count", detections.Length), ("maxScore", detections.Length == 0 ? 0 : detections.Max(d => d.Confidence ?? 0)),
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
        foreach (var candidate in candidates.OrderByDescending(x => x.Score))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (applyNms && kept.Any(x => x.ClassId == candidate.ClassId && BoundsOverlap(x, candidate) &&
                InferenceStats.RotatedBoxIoU(x.CenterX, x.CenterY, x.Width, x.Height, x.AngleRad,
                    candidate.CenterX, candidate.CenterY, candidate.Width, candidate.Height, candidate.AngleRad) > nmsThreshold))
                continue;
            kept.Add(candidate);
            if (kept.Count == maxCount) break;
        }

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
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("layout", layout), ("count", detections.Count), ("nmsMode", nmsMode),
            ("letterbox", letterbox), ("coordinates", coordinates), ("outputShape", shape));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["detections"] = VisionValue.Detections(detections),
                ["count"] = VisionValue.Integer(detections.Count),
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

/// <summary>ONNX semantic segmentation. layout=logits expects [1,C,H,W]; layout=yolo26-semantic expects YOLO26 class map [1,H,W]/[H,W]/[1,1,H,W].</summary>
public sealed class OnnxSegmentationNode : IVisionNodeExecutor
{
    public string Type => "inference.segmentation";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
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
        var layout = node.GetString("layout", "logits").Trim().ToLowerInvariant();
        if (layout is not ("logits" or "yolo26-semantic" or "classmap"))
            throw new InvalidOperationException($"Unsupported segmentation layout '{layout}'. Supported values are 'logits' and 'yolo26-semantic'.");
        if (layout == "classmap") layout = "yolo26-semantic";

        byte[] labels;
        float[] bestScores;
        int height, width, channels;
        var values = OnnxInferenceHelpers.ReadFloatTensor(output, "segmentation");
        var threshold = node.GetDouble("binaryThreshold", 0.5);
        if (!double.IsFinite(threshold)) throw new InvalidOperationException("binaryThreshold must be finite.");

        if (layout == "yolo26-semantic")
        {
            var decoded = InferenceStats.DecodeClassMap(values, shape);
            labels = decoded.Labels;
            height = decoded.Height;
            width = decoded.Width;
            channels = 0; // class map: no per-pixel logits
            bestScores = Enumerable.Repeat(1f, labels.Length).ToArray();
        }
        else
        {
            if (shape.Length != 4 || shape[0] != 1 || shape[1] is < 1 or > 256 || shape[2] < 1 || shape[3] < 1)
                throw new InvalidOperationException($"Segmentation output must have shape [1,C,H,W] with 1<=C<=256; received [{string.Join(',', shape)}].");
            channels = shape[1];
            height = shape[2];
            width = shape[3];
            labels = new byte[height * width];
            bestScores = new float[height * width];
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
                labels[y * width + x] = label;
                bestScores[y * width + x] = winningScore;
            }
        }

        using var modelMask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        for (var i = 0; i < labels.Length; i++)
            modelMask.Set(i / width, i % width, labels[i]);

        var lowConfidenceThreshold = node.GetDouble("lowConfidenceThreshold", 0.5);
        if (!double.IsFinite(lowConfidenceThreshold)) throw new InvalidOperationException("lowConfidenceThreshold must be finite.");
        var classCountTotal = layout == "yolo26-semantic"
            ? Math.Max(1, labels.Max() + 1)
            : (channels == 1 ? 2 : channels);
        if (classCountTotal > 256) classCountTotal = 256;
        var boxStats = InferenceStats.SegmentationClassBoxes(labels, bestScores, classCountTotal, width, height)
            .Where(s => s.PixelCount > 0).ToArray();
        var classStats = boxStats.Select(s => new InferenceStats.ClassStat(s.ClassId, s.PixelCount, s.Fraction, s.MeanScore)).ToArray();
        var dominant = classStats.OrderByDescending(s => s.PixelCount).FirstOrDefault();
        var scaleX = (double)source.Width / width; var scaleY = (double)source.Height / height;
        var meanConfidence = bestScores.Length == 0 ? 0 : bestScores.Average(v => (double)v);
        var lowConfidenceFraction = bestScores.Length == 0 ? 0 : (double)bestScores.Count(v => v < lowConfidenceThreshold) / bestScores.Length;
        var mask = new Mat();
        try
        {
            if (node.GetBool("letterbox", false))
            {
                var inputWidth = node.GetInt("inputWidth", 224);
                var inputHeight = node.GetInt("inputHeight", 224);
                using var cropped = UnletterboxMask(modelMask, source.Width, source.Height, inputWidth, inputHeight);
                JLVisionOperators.Resize(cropped, mask, new Size(source.Width, source.Height), 0, 0, InterpolationFlags.Nearest);
            }
            else
                JLVisionOperators.Resize(modelMask, mask, new Size(source.Width, source.Height), 0, 0, InterpolationFlags.Nearest);
        }
        catch { mask.Dispose(); throw; }
        var overlay = BuildOverlay(source, mask);
        var segOutputs = new Dictionary<string, VisionValue>
        {
            ["mask"] = VisionValue.Image(VisionImage.Own(mask)),
            ["overlay"] = VisionValue.Image(VisionImage.Own(overlay)),
            ["classes"] = VisionValue.String(JsonSerializer.Serialize(boxStats.Select(s => new
            {
                classId = s.ClassId, pixelCount = s.PixelCount, fraction = s.Fraction, meanScore = s.MeanScore,
                x = Math.Round(s.X * scaleX, 2), y = Math.Round(s.Y * scaleY, 2),
                width = Math.Round(s.Width * scaleX, 2), height = Math.Round(s.Height * scaleY, 2),
                centroidX = Math.Round(s.CentroidX * scaleX, 2), centroidY = Math.Round(s.CentroidY * scaleY, 2)
            }))),
            ["classCount"] = VisionValue.Integer(classStats.Length),
            ["meanConfidence"] = VisionValue.Double(meanConfidence),
            ["lowConfidenceFraction"] = VisionValue.Double(lowConfidenceFraction)
        };
        if (dominant is not null)
        {
            segOutputs["dominantClass"] = VisionValue.Integer(dominant.ClassId);
            segOutputs["dominantFraction"] = VisionValue.Double(dominant.Fraction);
        }
        return ValueTask.FromResult(new NodeExecutorResult(segOutputs,
            new Dictionary<string, object?> { ["outputShape"] = shape, ["layout"] = layout, ["classChannels"] = channels, ["maskWidth"] = source.Width, ["maskHeight"] = source.Height, ["classCount"] = classStats.Length, ["dominantClass"] = dominant?.ClassId, ["dominantFraction"] = dominant?.Fraction, ["meanConfidence"] = meanConfidence, ["lowConfidenceFraction"] = lowConfidenceFraction, ["lowConfidenceThreshold"] = lowConfidenceThreshold }));
    }

    /// <summary>Crop letterbox padding from a model-resolution mask, then the caller resizes to source.</summary>
    internal static Mat UnletterboxMask(Mat modelMask, int sourceWidth, int sourceHeight, int inputWidth, int inputHeight)
    {
        var scale = Math.Min((double)inputWidth / sourceWidth, (double)inputHeight / sourceHeight);
        var newW = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var newH = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var padX = (inputWidth - newW) / 2;
        var padY = (inputHeight - newH) / 2;
        if (padX < 0 || padY < 0 || padX + newW > modelMask.Width || padY + newH > modelMask.Height)
            throw new InvalidOperationException("Letterbox mask crop is outside the model mask bounds; check inputWidth/inputHeight.");
        using var roi = new Mat(modelMask, new Rect(padX, padY, newW, newH));
        var cropped = new Mat();
        roi.CopyTo(cropped);
        return cropped;
    }

    internal static Mat BuildOverlay(Mat source, Mat mask)
    {
        var overlay = new Mat();
        try
        {
            if (source.Channels() == 1) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
            else if (source.Channels() == 3) source.CopyTo(overlay);
            else if (source.Channels() == 4) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.BGRA2BGR);
            else throw new InvalidOperationException($"Segmentation overlay supports 1, 3, or 4 channel images; received {source.Channels()}.");
            mask.GetArray(out byte[] maskBytes);
            overlay.GetArray(out Vec3b[] basePixels);
            for (var i = 0; i < maskBytes.Length; i++)
            {
                var label = maskBytes[i];
                if (label == 0) continue;
                var color = InferenceStats.SegmentationColorBgr(label);
                var px = basePixels[i];
                basePixels[i] = new Vec3b(
                    (byte)Math.Clamp((px.Item0 + color.B) / 2, 0, 255),
                    (byte)Math.Clamp((px.Item1 + color.G) / 2, 0, 255),
                    (byte)Math.Clamp((px.Item2 + color.R) / 2, 0, 255));
            }
            overlay.SetArray(basePixels);
            return overlay;
        }
        catch { overlay.Dispose(); throw; }
    }
}

/// <summary>
/// YOLO26 / YOLOv8 instance segmentation ONNX: raw or YOLO26 end-to-end detection rows plus mask prototypes.
/// Emits detections, a combined class-id mask, overlay, and per-instance geometry/mask-run JSON.
/// Raw heads use class-aware NMS; end-to-end rows already have NMS-free top detections. OBB heads are not supported.
/// </summary>
public sealed class OnnxInstanceSegmentationNode : IVisionNodeExecutor
{
    public string Type => "inference.instanceSegmentation";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var source = context.RequireImage("image").Mat;
        if (node.GetBool("crop", false))
            throw new InvalidOperationException("Instance segmentation with crop=true is unsupported. Set crop=false.");
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node).Blob;
        cancellationToken.ThrowIfCancellationRequested();
        var detName = node.GetString("outputName", string.Empty).Trim();
        var protoName = node.GetString("protoOutputName", string.Empty).Trim();
        string[]? names = null;
        if (detName.Length > 0 && protoName.Length > 0) names = [detName, protoName];
        else if (detName.Length > 0 || protoName.Length > 0)
            throw new InvalidOperationException("Instance segmentation requires both outputName and protoOutputName, or leave both empty to use the model's first two outputs.");
        var outputs = OnnxInferenceHelpers.ForwardOutputs(net, names, modelPath, cancellationToken);
        if (outputs.Length < 2)
            throw new InvalidOperationException($"YOLO-Seg models must expose at least two outputs (detections + prototypes); received {outputs.Length}.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var det = outputs[0];
            var proto = outputs[1];
            // Prefer the 4D tensor as prototypes if the user ordered them backwards.
            if (det.Dimensions.Length == 4 && proto.Dimensions.Length != 4)
            { var tmp = det; det = proto; proto = tmp; }
            var detShape = OnnxInferenceHelpers.Shape(det);
            var protoShape = OnnxInferenceHelpers.Shape(proto);
            if (protoShape.Length != 4 || protoShape[0] != 1 || protoShape[1] < 1 || protoShape[2] < 1 || protoShape[3] < 1)
                throw new InvalidOperationException($"YOLO-Seg prototype output must have shape [1,maskDim,H,W]; received [{string.Join(',', protoShape)}].");
            var maskDim = protoShape[1];
            var protoH = protoShape[2];
            var protoW = protoShape[3];
            var detValues = OnnxInferenceHelpers.ReadFloatTensor(det, "instance-seg-det");
            var protoValues = OnnxInferenceHelpers.ReadFloatTensor(proto, "instance-seg-proto");
            var layout = node.GetString("layout", "yolo26-seg").Trim().ToLowerInvariant();
            if (layout is "yolo26-e2e" or "end2end") layout = "xyxy";
            if (layout is not ("yolo26-seg" or "xyxy"))
                throw new InvalidOperationException($"Unsupported instance-segmentation layout '{layout}'. Supported values are 'yolo26-seg' (raw head) and 'xyxy' (YOLO26 end-to-end).");
            var tensorOrder = node.GetString("tensorOrder", "auto").Trim().ToLowerInvariant();
            if (tensorOrder is not ("auto" or "channelsfirst" or "channelslast"))
                throw new InvalidOperationException("Instance segmentation tensorOrder must be auto, channelsFirst, or channelsLast.");
            var candidates = layout == "xyxy"
                ? InferenceStats.DecodeYoloSegXyxy(detValues, detShape, maskDim)
                : InferenceStats.DecodeYoloSeg(detValues, detShape, maskDim, tensorOrder);
            var threshold = node.GetDouble("confidenceThreshold", 0.25);
            var nmsThreshold = node.GetDouble("nmsThreshold", 0.45);
            var maskThreshold = (float)node.GetDouble("maskThreshold", 0.5);
            var nmsMode = node.GetString("nmsMode", "auto").Trim().ToLowerInvariant();
            if (nmsMode is not ("auto" or "on" or "off"))
                throw new InvalidOperationException("nmsMode must be auto, on, or off.");
            var applyNms = nmsMode == "on" || (nmsMode == "auto" && layout == "yolo26-seg");
            var includeMaskRuns = node.GetBool("includeMaskRuns", true);
            var maxCount = node.GetInt("maxCount", 100);
            if (maxCount is < 1 or > 1000) throw new InvalidOperationException("maxCount must be between 1 and 1000.");
            if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || !double.IsFinite(nmsThreshold) || nmsThreshold is < 0 or > 1 || !float.IsFinite(maskThreshold) || maskThreshold is < 0 or > 1)
                throw new InvalidOperationException("Instance segmentation thresholds must be finite values from 0 to 1.");
            var coordinates = node.GetString("coordinates", "pixels").Trim().ToLowerInvariant();
            if (coordinates is not ("normalized" or "pixels"))
                throw new InvalidOperationException($"Unsupported coordinates '{coordinates}'. Supported values are 'normalized' and 'pixels'.");
            // YOLO26/v8-seg raw exports use input-pixel xywh; default coordinates=pixels.
            var letterbox = node.GetBool("letterbox", true);
            var inputWidth = node.GetInt("inputWidth", 640);
            var inputHeight = node.GetInt("inputHeight", 640);
            if (inputWidth < 1 || inputHeight < 1) throw new InvalidOperationException("inputWidth/inputHeight must be positive.");
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

            var scored = new List<(InferenceStats.YoloSegCandidate Cand, double Left, double Top, double Right, double Bottom)>();
            for (var i = 0; i < candidates.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = candidates[i];
                if (c.Score < threshold || c.Score <= 0 || !double.IsFinite(c.Score)) continue;
                if (c.ClassId < 0 || c.ClassId > 254) throw new InvalidOperationException($"Instance-seg class id {c.ClassId} is outside the supported 0..254 range for the byte class-mask output.");
                var x1 = c.CenterX - c.Width / 2; var y1 = c.CenterY - c.Height / 2;
                var x2 = c.CenterX + c.Width / 2; var y2 = c.CenterY + c.Height / 2;
                if (coordinates == "normalized") { x1 *= inputWidth; x2 *= inputWidth; y1 *= inputHeight; y2 *= inputHeight; }
                var left = Math.Clamp(Math.Min(MapX(x1), MapX(x2)), 0, source.Width);
                var top = Math.Clamp(Math.Min(MapY(y1), MapY(y2)), 0, source.Height);
                var right = Math.Clamp(Math.Max(MapX(x1), MapX(x2)), 0, source.Width);
                var bottom = Math.Clamp(Math.Max(MapY(y1), MapY(y2)), 0, source.Height);
                if (right <= left || bottom <= top) continue;
                scored.Add((c, left, top, right, bottom));
            }
            var kept = new List<(InferenceStats.YoloSegCandidate Cand, double Left, double Top, double Right, double Bottom)>();
            foreach (var candidate in scored.OrderByDescending(x => x.Cand.Score))
            {
                if (applyNms && kept.Any(x => x.Cand.ClassId == candidate.Cand.ClassId && BoxIoU(x, candidate) > nmsThreshold)) continue;
                kept.Add(candidate);
                if (kept.Count == maxCount) break;
            }

            var combined = new byte[source.Height * source.Width];
            var labels = OnnxInferenceHelpers.ResolveLabels(node);
            var instancePayload = new List<object>();
            var detections = new List<VisionDetection>();
            for (var i = 0; i < kept.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (c, left, top, right, bottom) = kept[i];
                // Mask coeffs are in input-pixel space relative to the letterboxed input.
                double InvMapX(double srcX)
                {
                    if (!letterbox) return srcX * inputWidth / source.Width;
                    var scale = Math.Min((double)inputWidth / source.Width, (double)inputHeight / source.Height);
                    return srcX * scale + (inputWidth - source.Width * scale) / 2;
                }
                double InvMapY(double srcY)
                {
                    if (!letterbox) return srcY * inputHeight / source.Height;
                    var scale = Math.Min((double)inputWidth / source.Width, (double)inputHeight / source.Height);
                    return srcY * scale + (inputHeight - source.Height * scale) / 2;
                }
                var boxInInputL = InvMapX(left); var boxInInputT = InvMapY(top);
                var boxInInputR = InvMapX(right); var boxInInputB = InvMapY(bottom);
                var instanceMask = InferenceStats.ComposeYoloSegMask(
                    protoValues, maskDim, protoH, protoW, c.MaskCoeffs,
                    boxInInputL, boxInInputT, boxInInputR, boxInInputB,
                    inputWidth, inputHeight, source.Width, source.Height, maskThreshold, letterbox);
                var area = 0;
                var maskRuns = includeMaskRuns ? new List<int>() : null;
                var runValue = 0;
                var runLength = 0;
                for (var p = 0; p < instanceMask.Length; p++)
                {
                    var value = instanceMask[p] == 0 ? 0 : 1;
                    if (value != runValue)
                    {
                        maskRuns?.Add(runLength);
                        runValue = value;
                        runLength = 0;
                    }
                    runLength++;
                    if (value == 0) continue;
                    area++;
                    // Store classId+1 so background stays 0. Candidates are score-sorted,
                    // so the first foreground instance owns composite-mask overlaps.
                    if (combined[p] == 0) combined[p] = (byte)(c.ClassId + 1);
                }
                maskRuns?.Add(runLength);
                var w = right - left; var h = bottom - top;
                detections.Add(new VisionDetection(i, left, top, w, h, area, 0, 0,
                    new VisionCircle((left + right) / 2, (top + bottom) / 2, Math.Sqrt(w * w + h * h) / 2),
                    new VisionRectangle2D(left, top, w, h), c.Score, c.ClassId));
                var label = labels.Length > c.ClassId ? labels[c.ClassId] : $"class_{c.ClassId}";
                instancePayload.Add(new { index = i, classId = c.ClassId, label, score = c.Score, x1 = left, y1 = top, x2 = right, y2 = bottom, area,
                    maskSize = includeMaskRuns ? new[] { source.Height, source.Width } : null,
                    maskRuns = maskRuns?.ToArray(), maskRunOrder = includeMaskRuns ? "row-major, alternating background/foreground runs" : null });
            }

            using var maskMat = new Mat(source.Height, source.Width, MatType.CV_8UC1);
            maskMat.SetArray(combined);
            var maskOut = maskMat.Clone();
            var overlay = OnnxSegmentationNode.BuildOverlay(source, maskOut);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["detections"] = VisionValue.Detections(detections.ToArray()),
                    ["count"] = VisionValue.Integer(detections.Count),
                    ["mask"] = VisionValue.Image(VisionImage.Own(maskOut)),
                    ["overlay"] = VisionValue.Image(VisionImage.Own(overlay)),
                    ["instances"] = VisionValue.String(JsonSerializer.Serialize(instancePayload)),
                    ["result"] = VisionValue.String(JsonSerializer.Serialize(new { count = detections.Count, layout, nmsMode, tensorOrder, detShape, protoShape, instances = instancePayload }))
                },
                new Dictionary<string, object?> { ["count"] = detections.Count, ["maskDim"] = maskDim, ["letterbox"] = letterbox, ["layout"] = layout, ["tensorOrder"] = tensorOrder, ["nmsMode"] = nmsMode, ["applyNms"] = applyNms, ["includeMaskRuns"] = includeMaskRuns, ["detShape"] = detShape, ["protoShape"] = protoShape }));
        }
        finally { foreach (var o in outputs) o.Dispose(); }
    }

    private static double BoxIoU(
        (InferenceStats.YoloSegCandidate Cand, double Left, double Top, double Right, double Bottom) a,
        (InferenceStats.YoloSegCandidate Cand, double Left, double Top, double Right, double Bottom) b)
    {
        var iw = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var ih = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var intersection = iw * ih;
        var areaA = (a.Right - a.Left) * (a.Bottom - a.Top);
        var areaB = (b.Right - b.Left) * (b.Bottom - b.Top);
        return intersection / (areaA + areaB - intersection + 1e-9);
    }
}

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
        foreach (var candidate in scored.OrderByDescending(x => x.Score))
        {
            if (applyNms && kept.Any(x => x.ClassId == candidate.ClassId && BoxIoU(x, candidate) > nmsThreshold)) continue;
            kept.Add(candidate);
            if (kept.Count == maxCount) break;
        }

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
