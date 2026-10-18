namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure post-processing statistics for ONNX inference nodes. Managed-only so
/// heatmap/class/YOLO logic is unit-testable without ONNX or OpenCV natives.</summary>
public static class InferenceStats
{
    public sealed record ClassStat(int ClassId, int PixelCount, double Fraction, double MeanScore);
    public sealed record ClassBoxStat(int ClassId, int PixelCount, double Fraction, double MeanScore, int X, int Y, int Width, int Height, double CentroidX, double CentroidY);

    /// <summary>Per-class area/score plus bounding box and centroid (in label-map pixels).
    /// Segmentation inspection needs to know where a class is, not only how much of it
    /// exists (HALCON/ViDi segmentation reports region geometry per class).</summary>
    public static ClassBoxStat[] SegmentationClassBoxes(byte[] labels, float[] bestScores, int classCount, int width, int height)
    {
        if (labels.Length != bestScores.Length || labels.Length != width * height)
            throw new InvalidOperationException("Segmentation label/score buffers must match width*height.");
        var stats = SegmentationClasses(labels, bestScores, classCount);
        var minX = new int[classCount]; var minY = new int[classCount];
        var maxX = new int[classCount]; var maxY = new int[classCount];
        var sumX = new double[classCount]; var sumY = new double[classCount];
        Array.Fill(minX, int.MaxValue); Array.Fill(minY, int.MaxValue);
        Array.Fill(maxX, -1); Array.Fill(maxY, -1);
        for (var i = 0; i < labels.Length; i++)
        {
            var c = labels[i];
            if (c >= classCount) continue;
            var x = i % width; var y = i / width;
            if (x < minX[c]) minX[c] = x;
            if (y < minY[c]) minY[c] = y;
            if (x > maxX[c]) maxX[c] = x;
            if (y > maxY[c]) maxY[c] = y;
            sumX[c] += x; sumY[c] += y;
        }
        return stats.Select(s => s.PixelCount == 0
            ? new ClassBoxStat(s.ClassId, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            : new ClassBoxStat(s.ClassId, s.PixelCount, s.Fraction, s.MeanScore,
                minX[s.ClassId], minY[s.ClassId], maxX[s.ClassId] - minX[s.ClassId] + 1, maxY[s.ClassId] - minY[s.ClassId] + 1,
                sumX[s.ClassId] / s.PixelCount, sumY[s.ClassId] / s.PixelCount)).ToArray();
    }

    /// <summary>Deterministic BGR palette for segmentation overlays. Class 0 is the
    /// background and is never tinted; other classes cycle a fixed colour table.</summary>
    public static (byte B, byte G, byte R) SegmentationColorBgr(int classId)
    {
        (byte B, byte G, byte R)[] palette =
        [
            (0, 0, 0), (0, 0, 255), (0, 200, 0), (255, 0, 0), (0, 220, 255),
            (255, 0, 200), (255, 200, 0), (180, 180, 180), (80, 120, 255), (60, 255, 160),
            (200, 80, 255), (120, 255, 255)
        ];
        if (classId <= 0) return palette[0];
        return palette[1 + (classId - 1) % (palette.Length - 1)];
    }
    public sealed record RankedClass(int Index, string Label, double Score);
    public sealed record ClassificationRank(RankedClass[] Top, double Margin, double Entropy, double ProbabilitySum, int ClassCount);

    /// <summary>Rank a [C] classification head deterministically. layout=yolo26 means the
    /// tensor is already a probability distribution (validated, never re-softmaxed);
    /// layout=scores optionally applies a numerically stable softmax. Unknown layouts,
    /// non-finite scores, label-count mismatches and invalid topK fail loudly instead of
    /// silently producing a plausible-looking label.</summary>
    public static ClassificationRank RankClassification(float[] scores, string layout, bool applySoftmax, int topK, string[] labels)
    {
        if (scores is null || scores.Length == 0) throw new InvalidOperationException("Classification scores must be a non-empty [C] tensor.");
        if (scores.Any(s => !float.IsFinite(s))) throw new InvalidOperationException("Classification scores must all be finite.");
        var normalizedLayout = (layout ?? "scores").Trim().ToLowerInvariant();
        if (normalizedLayout is not ("scores" or "yolo26")) throw new InvalidOperationException($"Unsupported classification layout '{layout}'. Supported values are 'scores' and 'yolo26'.");
        if (topK < 1) throw new InvalidOperationException("Classification topK must be at least 1.");
        var hasLabels = labels is not null && labels.Any(l => !string.IsNullOrWhiteSpace(l));
        if (hasLabels && labels!.Length != scores.Length) throw new InvalidOperationException($"Classification labels count ({labels.Length}) must match the model class count ({scores.Length}), or be left empty.");
        double[] probabilities;
        if (normalizedLayout == "yolo26")
        {
            if (scores.Any(s => s < -0.001f || s > 1.001f)) throw new InvalidOperationException("YOLO26 classification output must already be softmax probabilities in [0, 1]; a value outside that range means layout=yolo26 does not match the exported model (use layout=scores for logits).");
            var sum = scores.Sum(s => (double)s);
            if (Math.Abs(sum - 1) > 0.02) throw new InvalidOperationException($"YOLO26 classification probabilities must sum to ~1, got {sum:0.####}; use layout=scores if the model emits logits.");
            probabilities = scores.Select(s => Math.Clamp((double)s, 0, 1)).ToArray();
        }
        else if (applySoftmax)
        {
            var max = scores.Max();
            var exp = scores.Select(s => Math.Exp(s - max)).ToArray();
            var sum = exp.Sum();
            if (!double.IsFinite(sum) || sum <= 0) throw new InvalidOperationException("Softmax produced a non-finite normalizer.");
            probabilities = exp.Select(e => e / sum).ToArray();
        }
        else
        {
            probabilities = scores.Select(s => (double)s).ToArray();
        }
        var effectiveTopK = Math.Min(topK, scores.Length);
        var top = probabilities.Select((score, index) => new RankedClass(index, hasLabels && !string.IsNullOrWhiteSpace(labels![index]) ? labels[index].Trim() : index.ToString(System.Globalization.CultureInfo.InvariantCulture), score))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Index).Take(effectiveTopK).ToArray();
        var margin = top.Length > 1 ? top[0].Score - top[1].Score : top[0].Score;
        var entropyBase = probabilities.Where(p => p > 0).ToArray();
        var entropySum = entropyBase.Sum();
        var entropy = entropySum > 0 ? -entropyBase.Sum(p => { var q = p / entropySum; return q * Math.Log2(q); }) : 0;
        if (!double.IsFinite(margin) || !double.IsFinite(entropy)) throw new InvalidOperationException("Classification ranking produced non-finite margin/entropy.");
        return new ClassificationRank(top, margin, entropy, probabilities.Sum(), scores.Length);
    }

    public sealed record AnomalyMapStat(double Min, double Max, double Mean, double Peak, int PeakX, int PeakY, double AreaFraction);
    public sealed record YoloCandidate(double CenterX, double CenterY, double Width, double Height, double Score, int ClassId);

    public static ClassStat[] SegmentationClasses(byte[] labels, float[] bestScores, int classCount)
    {
        if (labels.Length != bestScores.Length) throw new InvalidOperationException("Segmentation label/score buffers must have identical length.");
        var counts = new int[classCount]; var sums = new double[classCount];
        for (var i = 0; i < labels.Length; i++)
        {
            var c = labels[i];
            if (c >= classCount) continue;
            counts[c]++; sums[c] += bestScores[i];
        }
        return Enumerable.Range(0, classCount)
            .Select(c => new ClassStat(c, counts[c], labels.Length == 0 ? 0 : (double)counts[c] / labels.Length, counts[c] == 0 ? 0 : sums[c] / counts[c]))
            .ToArray();
    }

    public static AnomalyMapStat AnomalyMap(float[] values, int width, int height, double threshold)
    {
        if (values.Length != width * height || values.Length == 0) throw new InvalidOperationException("Anomaly map buffer must match width*height and be non-empty.");
        double min = double.MaxValue, max = double.MinValue, sum = 0; var peakIndex = 0; var above = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (!float.IsFinite(v)) throw new InvalidOperationException("Anomaly map contains non-finite values.");
            if (v < min) min = v;
            if (v > max) { max = v; peakIndex = i; }
            sum += v;
            if (v >= threshold) above++;
        }
        return new(min, max, sum / values.Length, max, peakIndex % width, peakIndex / width, (double)above / values.Length);
    }

    /// <summary>
    /// Otsu threshold of a score map, in the map's own units. The map range is binned
    /// into 256 levels; a constant map yields its only value. This is a starting-point
    /// suggestion (as in HALCON DL threshold aids), not a trained operating point.
    /// </summary>
    public static double OtsuThreshold(float[] values)
    {
        if (values.Length == 0) throw new InvalidOperationException("Cannot threshold an empty anomaly map.");
        var min = values.Min(); var max = values.Max();
        if (max - min < 1e-12) return min;
        var histogram = new long[256];
        foreach (var v in values) histogram[Math.Clamp((int)((v - min) / (max - min) * 255), 0, 255)]++;
        var total = values.Length;
        var sumTotal = histogram.Select((count, i) => (double)count * i).Sum();
        double sumBackground = 0; long weightBackground = 0; var bestVariance = -1d; var bestBin = 0;
        for (var bin = 0; bin < 256; bin++)
        {
            weightBackground += histogram[bin];
            if (weightBackground == 0) continue;
            var weightForeground = total - weightBackground;
            if (weightForeground == 0) break;
            sumBackground += bin * histogram[bin];
            var meanBackground = sumBackground / weightBackground;
            var meanForeground = (sumTotal - sumBackground) / weightForeground;
            var variance = (double)weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);
            if (variance > bestVariance) { bestVariance = variance; bestBin = bin; }
        }
        return min + (bestBin + 0.5) / 256d * (max - min);
    }

    /// <summary>Normalize a float map to 0-255 (min-max). Constant maps become all-zero.</summary>
    public static byte[] NormalizeMap(float[] values)
    {
        if (values.Length == 0) return [];
        var min = values.Min(); var max = values.Max();
        var range = max - min;
        return values.Select(v => range <= 1e-12 ? (byte)0 : (byte)Math.Clamp(Math.Round((v - min) / range * 255), 0, 255)).ToArray();
    }

    /// <summary>
    /// Decode YOLO-style output (YOLOv8 convention: cx, cy, w, h followed by per-class
    /// scores, no separate objectness). Accepts [1,C,N], [1,N,C] and [N,C] shapes.
    /// Coordinates are returned in the same units as the tensor (normalization is the caller's job).
    /// </summary>
    public static YoloCandidate[] DecodeYolo(float[] values, IReadOnlyList<int> shape, bool useObjectness = false)
    {
        int channels, count; bool channelsFirst;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] >= 5 && shape[2] > 0) { channels = (int)shape[1]; count = (int)shape[2]; channelsFirst = true; }
        else if (shape.Count == 3 && shape[0] == 1 && shape[2] >= 5 && shape[1] > 0) { channels = (int)shape[2]; count = (int)shape[1]; channelsFirst = false; }
        else if (shape.Count == 2 && shape[1] >= 5 && shape[0] > 0) { channels = (int)shape[1]; count = (int)shape[0]; channelsFirst = false; }
        else throw new InvalidOperationException($"YOLO detection output must have shape [1,C,N], [1,N,C] or [N,C] with C>=5; received [{string.Join(',', shape)}].");
        if (values.Length != channels * count) throw new InvalidOperationException("YOLO tensor element count does not match its shape.");
        // YOLOv5 heads carry an objectness channel at index 4 and class scores after it;
        // the effective score is objectness * best class score. YOLOv8 heads have no
        // objectness and class scores start at index 4.
        var firstClass = useObjectness ? 5 : 4;
        if (useObjectness && channels < 6) throw new InvalidOperationException("YOLOv5 detection output must have at least 6 channels (cx, cy, w, h, objectness, one or more class scores).");
        float At(int n, int c) => channelsFirst ? values[c * count + n] : values[n * channels + c];
        var result = new YoloCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var bestClass = 0; var bestScore = double.NegativeInfinity;
            for (var c = firstClass; c < channels; c++)
            {
                var score = At(n, c);
                if (score > bestScore) { bestScore = score; bestClass = c - firstClass; }
            }
            var objectness = useObjectness ? At(n, 4) : 1d;
            result[n] = new(At(n, 0), At(n, 1), At(n, 2), At(n, 3), objectness * bestScore, bestClass);
        }
        return result;
    }

    public sealed record YoloObbCandidate(double CenterX, double CenterY, double Width, double Height, double Score, int ClassId, double AngleRad);

    /// <summary>
    /// Decode Ultralytics raw oriented-box heads. Channels are cx,cy,w,h, class scores, angle;
    /// angles are radians in both YOLOv8 (already transformed by its head) and YOLO26 (direct regression).
    /// Accepts [1,C,N], [1,N,C], and [N,C].
    /// </summary>
    public static YoloObbCandidate[] DecodeYoloObb(float[] values, IReadOnlyList<int> shape, string tensorOrder = "auto")
    {
        var normalizedOrder = (tensorOrder ?? "auto").Trim().ToLowerInvariant();
        if (normalizedOrder is not ("auto" or "channelsfirst" or "channelslast"))
            throw new InvalidOperationException("YOLO-OBB tensorOrder must be auto, channelsFirst, or channelsLast.");
        int channels, count; bool channelsFirst;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] > 0 && shape[2] > 0 && shape[1] >= 6 && shape[2] >= 6)
        {
            channelsFirst = normalizedOrder switch
            {
                "channelsfirst" => true,
                "channelslast" => false,
                _ when shape[1] < shape[2] => true,
                _ when shape[2] < shape[1] => false,
                _ => throw new InvalidOperationException("YOLO-OBB raw tensor order is ambiguous because both dimensions are equal; set tensorOrder explicitly.")
            };
            channels = channelsFirst ? shape[1] : shape[2];
            count = channelsFirst ? shape[2] : shape[1];
        }
        else if (shape.Count == 3 && shape[0] == 1 && shape[1] >= 6 && shape[2] > 0 && normalizedOrder != "channelslast") { channels = shape[1]; count = shape[2]; channelsFirst = true; }
        else if (shape.Count == 3 && shape[0] == 1 && shape[2] >= 6 && shape[1] > 0 && normalizedOrder != "channelsfirst") { channels = shape[2]; count = shape[1]; channelsFirst = false; }
        else if (shape.Count == 2 && shape[1] >= 6 && shape[0] > 0) { channels = shape[1]; count = shape[0]; channelsFirst = false; }
        else throw new InvalidOperationException($"YOLO-OBB raw output must have shape [1,C,N], [1,N,C] or [N,C] with C>=6; received [{string.Join(',', shape)}].");
        if (values.Length != channels * count) throw new InvalidOperationException("YOLO-OBB tensor element count does not match its shape.");
        var classCount = channels - 5;
        float At(int n, int c) => channelsFirst ? values[c * count + n] : values[n * channels + c];
        var result = new YoloObbCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var bestClass = 0; var bestScore = double.NegativeInfinity;
            for (var c = 0; c < classCount; c++)
            {
                var score = At(n, 4 + c);
                if (score > bestScore) { bestScore = score; bestClass = c; }
            }
            result[n] = new(At(n, 0), At(n, 1), At(n, 2), At(n, 3), bestScore, bestClass, At(n, channels - 1));
        }
        return result;
    }

    /// <summary>Decode YOLO26 end-to-end OBB rows [cx,cy,w,h,score,classId,angleRad].</summary>
    public static YoloObbCandidate[] DecodeYoloObbXywhr(float[] values, IReadOnlyList<int> shape)
    {
        const int expected = 7;
        int count, channels;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] > 0 && shape[2] >= expected) { count = shape[1]; channels = shape[2]; }
        else if (shape.Count == 2 && shape[0] > 0 && shape[1] >= expected) { count = shape[0]; channels = shape[1]; }
        else throw new InvalidOperationException($"YOLO26-OBB end-to-end output must have shape [1,N,{expected}+] or [N,{expected}+]; received [{string.Join(',', shape)}].");
        if (values.Length != count * channels) throw new InvalidOperationException("YOLO26-OBB tensor element count does not match its shape.");
        var result = new YoloObbCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var offset = n * channels;
            var classValue = values[offset + 5];
            if (!float.IsFinite(classValue) || classValue < 0 || classValue > int.MaxValue || Math.Abs(classValue - MathF.Round(classValue)) > 1e-3f)
                throw new InvalidOperationException($"YOLO26-OBB row {n} has a non-integer or negative class id.");
            result[n] = new(values[offset], values[offset + 1], values[offset + 2], values[offset + 3], values[offset + 4], (int)MathF.Round(classValue), values[offset + 6]);
        }
        return result;
    }

    /// <summary>Exact convex-polygon IoU for two oriented boxes, used by class-aware rotated NMS.</summary>
    public static double RotatedBoxIoU(double centerAX, double centerAY, double widthA, double heightA, double angleARad,
        double centerBX, double centerBY, double widthB, double heightB, double angleBRad)
    {
        if (widthA <= 0 || heightA <= 0 || widthB <= 0 || heightB <= 0) return 0;
        var polygonA = OrientedBoxCorners(centerAX, centerAY, widthA, heightA, angleARad);
        var polygonB = OrientedBoxCorners(centerBX, centerBY, widthB, heightB, angleBRad);
        var intersection = ClipConvexPolygon(polygonA, polygonB);
        if (intersection.Count < 3) return 0;
        var intersectionArea = PolygonArea(intersection);
        var union = widthA * heightA + widthB * heightB - intersectionArea;
        return union > 0 ? Math.Clamp(intersectionArea / union, 0, 1) : 0;
    }

    /// <summary>Return rectangle corners in Ultralytics xywhr angle convention (radians).</summary>
    public static (double X, double Y)[] OrientedBoxCorners(double centerX, double centerY, double width, double height, double angleRad)
    {
        var cos = Math.Cos(angleRad); var sin = Math.Sin(angleRad);
        var halfW = width / 2; var halfH = height / 2;
        return [
            Transform(-halfW, -halfH), Transform(halfW, -halfH),
            Transform(halfW, halfH), Transform(-halfW, halfH)
        ];

        (double X, double Y) Transform(double x, double y) => (centerX + x * cos - y * sin, centerY + x * sin + y * cos);
    }

    private static List<(double X, double Y)> ClipConvexPolygon((double X, double Y)[] subject, (double X, double Y)[] clip)
    {
        var output = subject.ToList();
        for (var edge = 0; edge < clip.Length && output.Count > 0; edge++)
        {
            var a = clip[edge]; var b = clip[(edge + 1) % clip.Length];
            var input = output; output = [];
            var previous = input[^1]; var previousSide = Cross(a, b, previous);
            foreach (var current in input)
            {
                var currentSide = Cross(a, b, current);
                var previousInside = previousSide >= -1e-9; var currentInside = currentSide >= -1e-9;
                if (currentInside != previousInside)
                {
                    var denominator = previousSide - currentSide;
                    if (Math.Abs(denominator) > 1e-12)
                    {
                        var t = previousSide / denominator;
                        output.Add((previous.X + t * (current.X - previous.X), previous.Y + t * (current.Y - previous.Y)));
                    }
                }
                if (currentInside) output.Add(current);
                previous = current; previousSide = currentSide;
            }
        }
        return output;

        static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) p) =>
            (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
    }

    private static double PolygonArea(IReadOnlyList<(double X, double Y)> polygon)
    {
        var twiceArea = 0d;
        for (var i = 0; i < polygon.Count; i++)
        {
            var next = polygon[(i + 1) % polygon.Count];
            twiceArea += polygon[i].X * next.Y - next.X * polygon[i].Y;
        }
        return Math.Abs(twiceArea) / 2;
    }

    public sealed record YoloSegCandidate(double CenterX, double CenterY, double Width, double Height, double Score, int ClassId, float[] MaskCoeffs);

    /// <summary>
    /// Decode YOLO instance-seg detection head: channels are cx,cy,w,h + class scores + mask_dim coefficients.
    /// Accepts [1,C,N], [1,N,C], [N,C]. Coordinates stay in the tensor's own units.
    /// </summary>
    public static YoloSegCandidate[] DecodeYoloSeg(float[] values, IReadOnlyList<int> shape, int maskDim = 32)
    {
        if (maskDim < 1) throw new InvalidOperationException("YOLO-Seg maskDim must be positive.");
        int channels, count; bool channelsFirst;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] >= 4 + 1 + maskDim && shape[2] > 0) { channels = shape[1]; count = shape[2]; channelsFirst = true; }
        else if (shape.Count == 3 && shape[0] == 1 && shape[2] >= 4 + 1 + maskDim && shape[1] > 0) { channels = shape[2]; count = shape[1]; channelsFirst = false; }
        else if (shape.Count == 2 && shape[1] >= 4 + 1 + maskDim && shape[0] > 0) { channels = shape[1]; count = shape[0]; channelsFirst = false; }
        else throw new InvalidOperationException($"YOLO-Seg detection output must have shape [1,C,N], [1,N,C] or [N,C] with C>=4+classes+maskDim; received [{string.Join(',', shape)}].");
        if (values.Length != channels * count) throw new InvalidOperationException("YOLO-Seg tensor element count does not match its shape.");
        var classCount = channels - 4 - maskDim;
        if (classCount < 1) throw new InvalidOperationException("YOLO-Seg output must include at least one class score channel.");
        float At(int n, int c) => channelsFirst ? values[c * count + n] : values[n * channels + c];
        var result = new YoloSegCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var bestClass = 0; var bestScore = double.NegativeInfinity;
            for (var c = 0; c < classCount; c++)
            {
                var score = At(n, 4 + c);
                if (score > bestScore) { bestScore = score; bestClass = c; }
            }
            var coeffs = new float[maskDim];
            for (var m = 0; m < maskDim; m++) coeffs[m] = At(n, 4 + classCount + m);
            result[n] = new(At(n, 0), At(n, 1), At(n, 2), At(n, 3), bestScore, bestClass, coeffs);
        }
        return result;
    }

    /// <summary>
    /// Decode YOLO26 end-to-end instance-seg rows: [x1,y1,x2,y2,score,class,mask coefficients].
    /// Accepts [1,N,C] and [N,C]; coordinates stay in the tensor's own units.
    /// </summary>
    public static YoloSegCandidate[] DecodeYoloSegXyxy(float[] values, IReadOnlyList<int> shape, int maskDim = 32)
    {
        if (maskDim < 1) throw new InvalidOperationException("YOLO-Seg maskDim must be positive.");
        var expected = 6 + maskDim;
        int count, channels;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] > 0 && shape[2] >= expected) { count = shape[1]; channels = shape[2]; }
        else if (shape.Count == 2 && shape[0] > 0 && shape[1] >= expected) { count = shape[0]; channels = shape[1]; }
        else throw new InvalidOperationException($"YOLO26-Seg end-to-end output must have shape [1,N,{expected}+] or [N,{expected}+]; received [{string.Join(',', shape)}].");
        if (values.Length != count * channels) throw new InvalidOperationException("YOLO26-Seg tensor element count does not match its shape.");
        var result = new YoloSegCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var offset = n * channels;
            var x1 = values[offset]; var y1 = values[offset + 1];
            var x2 = values[offset + 2]; var y2 = values[offset + 3];
            var score = values[offset + 4];
            var classValue = values[offset + 5];
            var classId = (int)MathF.Round(classValue);
            if (score > 0 && (classValue < 0 || Math.Abs(classValue - classId) > 1e-3))
                throw new InvalidOperationException($"YOLO26-Seg end-to-end row {n} has a non-integer or negative class id.");
            var coeffs = new float[maskDim];
            Array.Copy(values, offset + 6, coeffs, 0, maskDim);
            result[n] = new((x1 + x2) / 2d, (y1 + y2) / 2d, x2 - x1, y2 - y1, score, score > 0 ? classId : 0, coeffs);
        }
        return result;
    }

    /// <summary>
    /// Compose one instance mask from prototype tensor [maskDim, protoH, protoW] and coefficients,
    /// then crop to the box (in proto / input pixels) and bilinear-resize into an output buffer of outH×outW.
    /// Returns a binary 0/255 mask. Ultralytics-style: sigmoid(coeffs · prototypes).
    /// </summary>
    public static byte[] ComposeYoloSegMask(float[] prototypes, int maskDim, int protoH, int protoW, float[] coeffs, double boxLeft, double boxTop, double boxRight, double boxBottom, int inputWidth, int inputHeight, int outWidth, int outHeight, float maskThreshold = 0.5f, bool letterbox = false)
    {
        if (prototypes.Length != maskDim * protoH * protoW) throw new InvalidOperationException("Prototype buffer size does not match maskDim*protoH*protoW.");
        if (coeffs.Length != maskDim) throw new InvalidOperationException("Mask coefficient count must equal maskDim.");
        if (inputWidth < 1 || inputHeight < 1 || outWidth < 1 || outHeight < 1) throw new InvalidOperationException("Mask sizes must be positive.");
        // Linear combo at proto resolution
        var proto = new float[protoH * protoW];
        for (var y = 0; y < protoH; y++)
        for (var x = 0; x < protoW; x++)
        {
            var sum = 0f;
            var pix = y * protoW + x;
            for (var m = 0; m < maskDim; m++) sum += coeffs[m] * prototypes[m * protoH * protoW + pix];
            proto[pix] = 1f / (1f + MathF.Exp(-sum)); // sigmoid
        }
        // Map box from input pixels into proto pixels
        var scaleX = (double)protoW / inputWidth; var scaleY = (double)protoH / inputHeight;
        var left = (int)Math.Clamp(Math.Floor(Math.Min(boxLeft, boxRight) * scaleX), 0, protoW - 1);
        var top = (int)Math.Clamp(Math.Floor(Math.Min(boxTop, boxBottom) * scaleY), 0, protoH - 1);
        var right = (int)Math.Clamp(Math.Ceiling(Math.Max(boxLeft, boxRight) * scaleX), left + 1, protoW);
        var bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(boxTop, boxBottom) * scaleY), top + 1, protoH);
        // Zero outside the box in proto space (Ultralytics crop_mask)
        for (var y = 0; y < protoH; y++)
        for (var x = 0; x < protoW; x++)
            if (x < left || x >= right || y < top || y >= bottom) proto[y * protoW + x] = 0;

        // Nearest upsample proto -> input size, then map input -> out (caller may already be source size).
        // When letterbox=true, out is source resolution and samples through Ultralytics-style pad undoing
        // (same math as detection MapX/MapY), not a naive stretch.
        var inputMask = new float[inputHeight * inputWidth];
        for (var y = 0; y < inputHeight; y++)
        for (var x = 0; x < inputWidth; x++)
        {
            var px = Math.Clamp((int)(x * scaleX), 0, protoW - 1);
            var py = Math.Clamp((int)(y * scaleY), 0, protoH - 1);
            inputMask[y * inputWidth + x] = proto[py * protoW + px];
        }
        var result = new byte[outHeight * outWidth];
        if (letterbox)
        {
            var lbScale = Math.Min((double)inputWidth / outWidth, (double)inputHeight / outHeight);
            var padX = (inputWidth - outWidth * lbScale) / 2.0;
            var padY = (inputHeight - outHeight * lbScale) / 2.0;
            for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
            {
                var ix = Math.Clamp((int)(x * lbScale + padX), 0, inputWidth - 1);
                var iy = Math.Clamp((int)(y * lbScale + padY), 0, inputHeight - 1);
                result[y * outWidth + x] = inputMask[iy * inputWidth + ix] >= maskThreshold ? (byte)255 : (byte)0;
            }
        }
        else
        {
            for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
            {
                var ix = Math.Clamp((int)((double)x * inputWidth / outWidth), 0, inputWidth - 1);
                var iy = Math.Clamp((int)((double)y * inputHeight / outHeight), 0, inputHeight - 1);
                result[y * outWidth + x] = inputMask[iy * inputWidth + ix] >= maskThreshold ? (byte)255 : (byte)0;
            }
        }
        return result;
    }


    public sealed record YoloPoseCandidate(
        double CenterX, double CenterY, double Width, double Height,
        double Score, int ClassId, double[] Keypoints);

    /// <summary>
    /// Decode YOLO pose raw head (YOLOv8/YOLO26 with nms=None): channels are
    /// cx,cy,w,h + class scores + numKeypoints*keypointDims (usually x,y,conf).
    /// Typical COCO: [1,56,8400] = 4+1+17*3. Accepts [1,C,N], [1,N,C], [N,C].
    /// </summary>
    public static YoloPoseCandidate[] DecodeYoloPose(float[] values, IReadOnlyList<int> shape, int numKeypoints = 17, int keypointDims = 3)
    {
        if (numKeypoints < 1) throw new InvalidOperationException("numKeypoints must be positive.");
        if (keypointDims is < 2 or > 3) throw new InvalidOperationException("keypointDims must be 2 (x,y) or 3 (x,y,conf).");
        var kptChannels = numKeypoints * keypointDims;
        int channels, count; bool channelsFirst;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] >= 4 + 1 + kptChannels && shape[2] > 0) { channels = shape[1]; count = shape[2]; channelsFirst = true; }
        else if (shape.Count == 3 && shape[0] == 1 && shape[2] >= 4 + 1 + kptChannels && shape[1] > 0) { channels = shape[2]; count = shape[1]; channelsFirst = false; }
        else if (shape.Count == 2 && shape[1] >= 4 + 1 + kptChannels && shape[0] > 0) { channels = shape[1]; count = shape[0]; channelsFirst = false; }
        else throw new InvalidOperationException($"YOLO-Pose raw output must have shape [1,C,N], [1,N,C] or [N,C] with C>=4+classes+{kptChannels}; received [{string.Join(',', shape)}].");
        if (values.Length != channels * count) throw new InvalidOperationException("YOLO-Pose tensor element count does not match its shape.");
        var classCount = channels - 4 - kptChannels;
        if (classCount < 1) throw new InvalidOperationException("YOLO-Pose output must include at least one class score channel.");
        float At(int n, int c) => channelsFirst ? values[c * count + n] : values[n * channels + c];
        var result = new YoloPoseCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var bestClass = 0; var bestScore = double.NegativeInfinity;
            for (var c = 0; c < classCount; c++)
            {
                var score = At(n, 4 + c);
                if (score > bestScore) { bestScore = score; bestClass = c; }
            }
            var kpts = new double[kptChannels];
            for (var k = 0; k < kptChannels; k++) kpts[k] = At(n, 4 + classCount + k);
            result[n] = new(At(n, 0), At(n, 1), At(n, 2), At(n, 3), bestScore, bestClass, kpts);
        }
        return result;
    }

    /// <summary>
    /// Decode YOLO26 pose end-to-end / NMS-free rows: [x1,y1,x2,y2,score,class, kpt...].
    /// Typical COCO: [1,300,57] = 6+17*3. Accepts [1,N,C], [N,C].
    /// </summary>
    public static YoloPoseCandidate[] DecodeYoloPoseXyxy(float[] values, IReadOnlyList<int> shape, int numKeypoints = 17, int keypointDims = 3)
    {
        if (numKeypoints < 1) throw new InvalidOperationException("numKeypoints must be positive.");
        if (keypointDims is < 2 or > 3) throw new InvalidOperationException("keypointDims must be 2 (x,y) or 3 (x,y,conf).");
        var kptChannels = numKeypoints * keypointDims;
        var expected = 6 + kptChannels;
        int count, channels;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] > 0 && shape[2] >= expected) { count = shape[1]; channels = shape[2]; }
        else if (shape.Count == 2 && shape[0] > 0 && shape[1] >= expected) { count = shape[0]; channels = shape[1]; }
        else throw new InvalidOperationException($"YOLO-Pose end-to-end output must have shape [1,N,{expected}+] or [N,{expected}+]; received [{string.Join(',', shape)}].");
        if (values.Length != count * channels) throw new InvalidOperationException("YOLO-Pose tensor element count does not match its shape.");
        var result = new YoloPoseCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var o = n * channels;
            var x1 = values[o]; var y1 = values[o + 1]; var x2 = values[o + 2]; var y2 = values[o + 3];
            var score = values[o + 4];
            var classId = (int)MathF.Round(values[o + 5]);
            var kpts = new double[kptChannels];
            for (var k = 0; k < kptChannels; k++) kpts[k] = values[o + 6 + k];
            var cx = (x1 + x2) / 2.0; var cy = (y1 + y2) / 2.0;
            var w = Math.Abs(x2 - x1); var h = Math.Abs(y2 - y1);
            result[n] = new(cx, cy, w, h, score, classId, kpts);
        }
        return result;
    }

    /// <summary>COCO-17 skeleton edges (0-based indices), Ultralytics order.</summary>
    public static readonly (int A, int B)[] Coco17Skeleton =
    [
        (15, 13), (13, 11), (16, 14), (14, 12), (11, 12), (5, 11), (6, 12),
        (5, 6), (5, 7), (6, 8), (7, 9), (8, 10), (1, 2), (0, 1), (0, 2),
        (1, 3), (2, 4), (3, 5), (4, 6)
    ];

    /// <summary>
    /// Interpret a YOLO26 semantic class-map tensor as per-pixel class IDs.
    /// Accepts [1,H,W], [H,W], or [1,1,H,W]. Values are rounded to nearest integer in 0..255.
    /// </summary>
    public static (byte[] Labels, int Height, int Width) DecodeClassMap(float[] values, IReadOnlyList<int> shape)
    {
        int height, width;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] > 0 && shape[2] > 0) { height = shape[1]; width = shape[2]; }
        else if (shape.Count == 2 && shape[0] > 0 && shape[1] > 0) { height = shape[0]; width = shape[1]; }
        else if (shape.Count == 4 && shape[0] == 1 && shape[1] == 1 && shape[2] > 0 && shape[3] > 0) { height = shape[2]; width = shape[3]; }
        else throw new InvalidOperationException($"YOLO26 semantic class map must have shape [1,H,W], [H,W] or [1,1,H,W]; received [{string.Join(',', shape)}].");
        if (values.Length != height * width) throw new InvalidOperationException("Class-map element count does not match its shape.");
        var labels = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (!float.IsFinite(v)) throw new InvalidOperationException("Class-map contains a non-finite value.");
            var id = (int)Math.Round(v);
            if (id is < 0 or > 255) throw new InvalidOperationException($"Class-map value {v} is outside the supported label range 0..255.");
            labels[i] = (byte)id;
        }
        return (labels, height, width);
    }

}
