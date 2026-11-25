using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>图像预处理的掩膜合成、统计与直方图计算。</summary>
internal static class PreprocessGapMath
{
    /// <summary>成功时消费 processed；失败时仍由调用方释放。</summary>
    public static Mat Composite(Mat processed, Mat source, Mat? mask)
    {
        if (mask is null || mask.Empty()) return processed;
        if (source.Size() != processed.Size() || source.Type() != processed.Type())
            throw new InvalidOperationException("ROI compositing requires matching size and type.");
        var output = processed.Clone();
        try
        {
            using var inverse = new Mat();
            JLVisionOperators.BitwiseNot(mask, inverse);
            source.CopyTo(output, inverse);
            processed.Dispose();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public static double MeanOf(Mat source, Mat? mask)
        => mask is null || mask.Empty() ? Cv2.Mean(source).Val0 : Cv2.Mean(source, mask).Val0;

    public static long MaskedCount(Mat? mask, long fallback)
        => mask is null || mask.Empty() ? fallback : Cv2.CountNonZero(mask);

    public static long CountAtLeast(Mat gray, Mat? mask, double threshold)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, threshold - 1, 255, ThresholdTypes.Binary);
        return CountMasked(binary, mask);
    }

    public static long CountAtMost(Mat gray, Mat? mask, double threshold)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, threshold, 255, ThresholdTypes.BinaryInv);
        return CountMasked(binary, mask);
    }

    private static long CountMasked(Mat binary, Mat? mask)
    {
        if (mask is null || mask.Empty()) return Cv2.CountNonZero(binary);
        using var both = new Mat();
        Cv2.BitwiseAnd(binary, mask, both);
        return Cv2.CountNonZero(both);
    }

    public static long[] Histogram(Mat gray, Mat? mask)
    {
        using var hist = new Mat();
        using var empty = new Mat();
        Cv2.CalcHist([gray], [0], mask is not null && !mask.Empty() ? mask : empty, hist, 1, [256], [new Rangef(0, 256)]);
        var counts = new long[256];
        for (var i = 0; i < 256; i++) counts[i] = (long)Math.Round(hist.At<float>(i));
        return counts;
    }

    public static double Percentile(long[] histogram, long total, double percent)
    {
        if (total <= 0) return 0;
        var target = total * Math.Clamp(percent, 0, 100) / 100.0;
        long running = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            if (running >= target) return i;
        }
        return histogram.Length - 1;
    }

    public static double Entropy(long[] histogram, long total)
    {
        if (total <= 0) return 0;
        var entropy = 0.0;
        foreach (var count in histogram)
        {
            if (count == 0) continue;
            var p = count / (double)total;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    public static double AutoOuterRadius(double x, double y, int width, int height)
        => Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y));
}
