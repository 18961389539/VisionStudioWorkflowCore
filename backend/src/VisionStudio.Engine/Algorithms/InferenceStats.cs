namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure post-processing statistics for ONNX inference nodes. Managed-only so
/// heatmap/class/YOLO logic is unit-testable without ONNX or OpenCV natives.</summary>
public static class InferenceStats
{
    public sealed record ClassStat(int ClassId, int PixelCount, double Fraction, double MeanScore);
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
    public static YoloCandidate[] DecodeYolo(float[] values, IReadOnlyList<int> shape)
    {
        int channels, count; bool channelsFirst;
        if (shape.Count == 3 && shape[0] == 1 && shape[1] >= 5 && shape[2] > 0) { channels = (int)shape[1]; count = (int)shape[2]; channelsFirst = true; }
        else if (shape.Count == 3 && shape[0] == 1 && shape[2] >= 5 && shape[1] > 0) { channels = (int)shape[2]; count = (int)shape[1]; channelsFirst = false; }
        else if (shape.Count == 2 && shape[1] >= 5 && shape[0] > 0) { channels = (int)shape[1]; count = (int)shape[0]; channelsFirst = false; }
        else throw new InvalidOperationException($"YOLO detection output must have shape [1,C,N], [1,N,C] or [N,C] with C>=5; received [{string.Join(',', shape)}].");
        if (values.Length != channels * count) throw new InvalidOperationException("YOLO tensor element count does not match its shape.");
        float At(int n, int c) => channelsFirst ? values[c * count + n] : values[n * channels + c];
        var result = new YoloCandidate[count];
        for (var n = 0; n < count; n++)
        {
            var bestClass = 0; var bestScore = double.NegativeInfinity;
            for (var c = 4; c < channels; c++)
            {
                var score = At(n, c);
                if (score > bestScore) { bestScore = score; bestClass = c - 4; }
            }
            result[n] = new(At(n, 0), At(n, 1), At(n, 2), At(n, 3), bestScore, bestClass);
        }
        return result;
    }
}
