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
}
