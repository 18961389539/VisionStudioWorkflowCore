using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

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
        modelMask.SetArray(labels);

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
        var overlay = BuildOverlay(source, mask, maskClassOffset: 0);
        var segOutputs = new Dictionary<string, VisionValue>
        {
            ["mask"] = VisionValue.Image(VisionImage.Own(mask)),
            ["maskClassOffset"] = VisionValue.Integer(0),
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
            new Dictionary<string, object?> { ["outputShape"] = shape, ["layout"] = layout, ["classChannels"] = channels, ["maskWidth"] = source.Width, ["maskHeight"] = source.Height, ["maskClassOffset"] = 0, ["classCount"] = classStats.Length, ["dominantClass"] = dominant?.ClassId, ["dominantFraction"] = dominant?.Fraction, ["meanConfidence"] = meanConfidence, ["lowConfidenceFraction"] = lowConfidenceFraction, ["lowConfidenceThreshold"] = lowConfidenceThreshold }));
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

    /// <summary>
    /// Blends every non-background mask pixel with its class colour. <paramref name="maskClassOffset"/>
    /// is the encoding the producer used (maskValue = classId + offset): semantic masks store the class
    /// id directly (0), instance masks reserve 0 for background (1). Colours are keyed by class id, so
    /// the same class gets the same colour in every inference overlay.
    /// </summary>
    internal static Mat BuildOverlay(Mat source, Mat mask, int maskClassOffset)
    {
        var overlay = new Mat();
        try
        {
            if (source.Channels() == 1) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
            else if (source.Channels() == 3) source.CopyTo(overlay);
            else if (source.Channels() == 4) JLVisionOperators.CvtColor(source, overlay, ColorConversionCodes.BGRA2BGR);
            else throw new InvalidOperationException($"Segmentation overlay supports 1, 3, or 4 channel images; received {source.Channels()}.");
            // Zero-copy row spans: the old GetArray/SetArray round trip copied the whole frame
            // through managed memory just to blend the labelled pixels in place.
            var maskRows = mask.AsRows<byte>();
            var overlayRows = overlay.AsRows<Vec3b>();
            for (var y = 0; y < maskRows.Count; y++)
            {
                var maskRow = maskRows[y];
                var pixelRow = overlayRows[y];
                for (var x = 0; x < maskRow.Length; x++)
                {
                    var label = maskRow[x];
                    if (label == 0) continue;
                    // Palette slot = classId + 1; slot 0 is reserved for background so class 0 stays drawable.
                    var color = InferenceStats.SegmentationColorBgr(label - maskClassOffset + 1);
                    var px = pixelRow[x];
                    pixelRow[x] = new Vec3b(
                        (byte)Math.Clamp((px.Item0 + color.B) / 2, 0, 255),
                        (byte)Math.Clamp((px.Item1 + color.G) / 2, 0, 255),
                        (byte)Math.Clamp((px.Item2 + color.R) / 2, 0, 255));
                }
            }
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

        var inputWidth = node.GetInt("inputWidth", 640);
        var inputHeight = node.GetInt("inputHeight", 640);
        var letterbox = node.GetBool("letterbox", true);
        var swapRB = node.GetBool("swapRB", true);
        using var net = OnnxInferenceHelpers.LoadNet(node);
        var modelPath = OnnxInferenceHelpers.ResolvePath(node);
        var (inputName, metadata) = OnnxInferenceHelpers.RequireSingleInput(net);
        if (!metadata.IsTensor || metadata.ElementType != typeof(float))
            throw new InvalidOperationException($"Instance segmentation requires a float32 NCHW input tensor. ONNX input '{inputName}' has a different type.");
        var modelInputShape = metadata.Dimensions;
        if (modelInputShape.Length != 4 || modelInputShape[0] > 1 || (modelInputShape[1] > 0 && modelInputShape[1] != 3))
            throw new InvalidOperationException($"Instance segmentation requires [1,3,H,W] input. ONNX input '{inputName}' declares [{string.Join(',', modelInputShape)}].");
        var inputShape = new[] { 1, 3, inputHeight, inputWidth };
        if ((modelInputShape[2] > 0 && modelInputShape[2] != inputHeight) || (modelInputShape[3] > 0 && modelInputShape[3] != inputWidth))
            throw new InvalidOperationException($"ONNX input '{inputName}' declares [{string.Join(',', modelInputShape)}] but the node is configured for [{string.Join(',', inputShape)}]. Set inputWidth and inputHeight to match the model.");

        var detName = node.GetString("outputName", string.Empty).Trim();
        var protoName = node.GetString("protoOutputName", string.Empty).Trim();
        if ((detName.Length > 0) != (protoName.Length > 0))
            throw new InvalidOperationException("Instance segmentation requires both outputName and protoOutputName, or leave both empty to use the model's first two outputs.");
        if (detName.Length > 0 && detName == protoName)
            throw new InvalidOperationException("Instance segmentation requires distinct detection and prototype output names.");
        var names = detName.Length > 0 ? [detName, protoName] : net.Session.OutputMetadata.Keys.Take(2).ToArray();
        if (names.Length < 2)
            throw new InvalidOperationException($"YOLO-Seg models must expose at least two outputs (detections + prototypes); received {names.Length}.");

        cancellationToken.ThrowIfCancellationRequested();
        using var input = OnnxInferenceHelpers.PrepareInput(net, source, node, (inputWidth, inputHeight), letterbox, swapRB).Blob;
        var outputs = OnnxInferenceHelpers.ForwardOutputs(net, names, modelPath, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var det = outputs[0];
            var proto = outputs[1];
            // Prefer the 4D tensor as prototypes if the user ordered them backwards.
            if (det.Dimensions.Length == 4 && proto.Dimensions.Length != 4)
                (det, proto) = (proto, det);
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
            var includeMaskRuns = node.GetBool("includeMaskRuns", false);
            var maxCount = node.GetInt("maxCount", 100);
            if (maxCount is < 1 or > 1000) throw new InvalidOperationException("maxCount must be between 1 and 1000.");
            if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || !double.IsFinite(nmsThreshold) || nmsThreshold is < 0 or > 1 || !float.IsFinite(maskThreshold) || maskThreshold is < 0 or > 1)
                throw new InvalidOperationException("Instance segmentation thresholds must be finite values from 0 to 1.");
            var coordinates = node.GetString("coordinates", "pixels").Trim().ToLowerInvariant();
            if (coordinates is not ("normalized" or "pixels"))
                throw new InvalidOperationException($"Unsupported coordinates '{coordinates}'. Supported values are 'normalized' and 'pixels'.");
            // YOLO26/v8-seg raw exports use input-pixel xywh; default coordinates=pixels.
            var mapping = new LetterboxMapping(source.Width, source.Height, inputWidth, inputHeight, letterbox);
            var scored = new List<InstanceCandidate>();
            for (var i = 0; i < candidates.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = candidates[i];
                if (c.Score < threshold || c.Score <= 0 || !double.IsFinite(c.Score)) continue;
                if (c.ClassId < 0 || c.ClassId > 254) throw new InvalidOperationException($"Instance-seg class id {c.ClassId} is outside the supported 0..254 range for the byte class-mask output.");
                var x1 = c.CenterX - c.Width / 2; var y1 = c.CenterY - c.Height / 2;
                var x2 = c.CenterX + c.Width / 2; var y2 = c.CenterY + c.Height / 2;
                if (coordinates == "normalized") { x1 *= inputWidth; x2 *= inputWidth; y1 *= inputHeight; y2 *= inputHeight; }
                var inputLeft = Math.Min(x1, x2);
                var inputTop = Math.Min(y1, y2);
                var inputRight = Math.Max(x1, x2);
                var inputBottom = Math.Max(y1, y2);
                var left = Math.Clamp(mapping.ToSourceX(inputLeft), 0, source.Width);
                var top = Math.Clamp(mapping.ToSourceY(inputTop), 0, source.Height);
                var right = Math.Clamp(mapping.ToSourceX(inputRight), 0, source.Width);
                var bottom = Math.Clamp(mapping.ToSourceY(inputBottom), 0, source.Height);
                if (right <= left || bottom <= top) continue;
                // NMS runs on input-space boxes; the clamped source box is only for output and mask composition.
                scored.Add(new InstanceCandidate(c, left, top, right, bottom, inputLeft, inputTop, inputRight, inputBottom));
            }
            var kept = new List<InstanceCandidate>();
            var truncated = InferenceNmsCap.Fill(scored.OrderByDescending(x => x.Cand.Score), maxCount, kept,
                (existing, candidate) => applyNms && existing.Any(x => x.Cand.ClassId == candidate.Cand.ClassId && BoxIoU(x, candidate) > nmsThreshold),
                cancellationToken);

            var combined = new byte[source.Height * source.Width];
            var labels = OnnxInferenceHelpers.ResolveLabels(node);
            var instancePayload = new List<object>();
            var detections = new List<VisionDetection>();
            var contours = new List<VisionContour>(kept.Count);
            for (var i = 0; i < kept.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instance = kept[i];
                var c = instance.Cand;
                var (left, top, right, bottom) = (instance.Left, instance.Top, instance.Right, instance.Bottom);
                // Mask coeffs are in input-pixel space. The clamped source box is mapped back so the
                // proto crop matches the visible part of the box, as before.
                var instanceMask = InferenceStats.ComposeYoloSegMask(
                    protoValues, maskDim, protoH, protoW, c.MaskCoeffs,
                    mapping.ToInputX(left), mapping.ToInputY(top), mapping.ToInputX(right), mapping.ToInputY(bottom),
                    mapping, maskThreshold);
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
                contours.Add(ExtractInstanceContour(instanceMask, source.Width, source.Height, left, top, right, bottom, i));
                var w = right - left; var h = bottom - top;
                detections.Add(new VisionDetection(i, left, top, w, h, area, 0, 0,
                    new VisionCircle((left + right) / 2, (top + bottom) / 2, Math.Sqrt(w * w + h * h) / 2),
                    new VisionRectangle2D(left, top, w, h), c.Score, c.ClassId));
                var label = labels.Length > c.ClassId ? labels[c.ClassId] : $"class_{c.ClassId}";
                instancePayload.Add(new { index = i, classId = c.ClassId, label, score = c.Score, x1 = left, y1 = top, x2 = right, y2 = bottom, area,
                    maskRuns = maskRuns?.ToArray() });
            }

            var summary = new Dictionary<string, object?>
            {
                ["count"] = detections.Count, ["truncated"] = truncated, ["maskDim"] = maskDim, ["maskClassOffset"] = 1,
                ["contourCount"] = contours.Count, ["contourPoints"] = contours.Sum(c => c.Points.Count),
                ["letterbox"] = letterbox, ["layout"] = layout, ["tensorOrder"] = tensorOrder,
                ["nmsMode"] = nmsMode, ["applyNms"] = applyNms, ["includeMaskRuns"] = includeMaskRuns,
                ["inputName"] = inputName, ["modelInputShape"] = modelInputShape, ["inputShape"] = inputShape,
                ["detectionOutputName"] = det.Name, ["detShape"] = detShape,
                ["prototypeOutputName"] = proto.Name, ["protoShape"] = protoShape
            };
            var diagnostics = CoordinateValidation.Diagnostics(Type, summary.Select(pair => (pair.Key, pair.Value)).ToArray());
            var instancesDocument = new Dictionary<string, object?>
            {
                ["maskWidth"] = source.Width,
                ["maskHeight"] = source.Height
            };
            if (includeMaskRuns)
                instancesDocument["maskRunOrder"] = "row-major, alternating background/foreground runs";
            instancesDocument["instances"] = instancePayload;
            var instancesJson = JsonSerializer.Serialize(instancesDocument, InstanceJsonOptions);
            // Summary only: the instance payload is emitted once, on the instances port.
            var resultJson = JsonSerializer.Serialize(new { count = detections.Count, maskClassOffset = 1, layout, nmsMode, tensorOrder, detShape, protoShape });
            // Ownership transfers straight to the outputs; the old Own(Clone()) + CloneOwned()
            // chain copied the full mask twice for nothing.
            var maskMat = new Mat(source.Height, source.Width, MatType.CV_8UC1);
            Mat overlay;
            try
            {
                maskMat.SetArray(combined);
                overlay = OnnxSegmentationNode.BuildOverlay(source, maskMat, maskClassOffset: 1);
            }
            catch { maskMat.Dispose(); throw; }
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["detections"] = VisionValue.Detections(detections.ToArray()),
                    ["contours"] = VisionValue.Contours(contours),
                    ["count"] = VisionValue.Integer(detections.Count),
                    ["truncated"] = VisionValue.Boolean(truncated),
                    ["mask"] = VisionValue.Image(VisionImage.Own(maskMat)),
                    ["maskClassOffset"] = VisionValue.Integer(1),
                    ["overlay"] = VisionValue.Image(VisionImage.Own(overlay)),
                    ["instances"] = VisionValue.Json(new VisionJson(instancesJson)),
                    ["result"] = VisionValue.String(resultJson),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                summary));
        }
        finally { foreach (var o in outputs) o.Dispose(); }
    }

    // Omit disabled per-instance fields entirely so consumers can test for presence.
    private static readonly JsonSerializerOptions InstanceJsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private const int ContourPointBudget = 2000;

    /// <summary>
    /// Largest external ring of one instance mask, indexed like the matching detection so the two
    /// lists stay aligned. A mask split into several components contributes only its largest ring.
    /// </summary>
    private static VisionContour ExtractInstanceContour(byte[] mask, int width, int height, double left, double top, double right, double bottom, int index)
    {
        // Header over the managed buffer; the array outlives the Mat.
        using var maskMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, mask);
        var x0 = Math.Clamp((int)Math.Floor(left), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(top), 0, height - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(right), x0 + 1, width);
        var y1 = Math.Clamp((int)Math.Ceiling(bottom), y0 + 1, height);
        using var roi = new Mat(maskMat, new Rect(x0, y0, x1 - x0, y1 - y0));
        JLVisionOperators.FindContours(roi, out Point[][] rings, out double[] ringAreas, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var best = -1;
        var bestArea = 0d;
        for (var i = 0; i < rings.Length; i++)
        {
            if (best < 0 || ringAreas[i] > bestArea) { best = i; bestArea = ringAreas[i]; }
        }
        if (best < 0)
            return new VisionContour(index, Array.Empty<VisionPoint2D>(), -1, false, 0, 0, 0,
                new VisionRectangle2D(0, 0, 0, 0), new VisionCircle(0, 0, 0));
        var points = rings[best];
        for (var i = 0; i < points.Length; i++) points[i] = new Point(points[i].X + x0, points[i].Y + y0);
        return ContourNodesHelper.ToContour(points, index, -1, false, ContourPointBudget);
    }

    private static double BoxIoU(InstanceCandidate a, InstanceCandidate b)
    {
        var iw = Math.Max(0, Math.Min(a.InputRight, b.InputRight) - Math.Max(a.InputLeft, b.InputLeft));
        var ih = Math.Max(0, Math.Min(a.InputBottom, b.InputBottom) - Math.Max(a.InputTop, b.InputTop));
        var intersection = iw * ih;
        var areaA = (a.InputRight - a.InputLeft) * (a.InputBottom - a.InputTop);
        var areaB = (b.InputRight - b.InputLeft) * (b.InputBottom - b.InputTop);
        return intersection / (areaA + areaB - intersection + 1e-9);
    }

    private sealed record InstanceCandidate(InferenceStats.YoloSegCandidate Cand, double Left, double Top, double Right, double Bottom,
        double InputLeft, double InputTop, double InputRight, double InputBottom);
}

