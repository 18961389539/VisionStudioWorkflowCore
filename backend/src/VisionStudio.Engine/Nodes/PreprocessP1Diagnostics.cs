using OpenCvSharp;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

/// <summary>Shared, deterministic diagnostics for the Preprocess P1 reliability contracts.</summary>
internal static class PreprocessP1Diagnostics
{
    public static (long Count, Rect Bounds) Roi(Mat? mask, Size canvas)
    {
        if (mask is null || mask.Empty()) return ((long)canvas.Width * canvas.Height, new Rect(0, 0, canvas.Width, canvas.Height));
        var count = JLVisionOperators.CountNonZero(mask);
        var bounds = VisionRoiOpenCvExtensions.BoundsOf(mask);
        return (count, bounds);
    }

    public static string NonZeroValues(Mat source)
    {
        if (source.Empty() || source.Channels() != 1) return "[]";
        if (source.Depth() == MatType.CV_8U)
        {
            source.GetArray(out byte[] values);
            return System.Text.Json.JsonSerializer.Serialize(values.Where(value => value != 0).Distinct().OrderBy(value => value).ToArray());
        }
        if (source.Depth() == MatType.CV_16U)
        {
            source.GetArray(out ushort[] values);
            return System.Text.Json.JsonSerializer.Serialize(values.Where(value => value != 0).Distinct().OrderBy(value => value).ToArray());
        }
        return "[]";
    }

    public static bool IsBinary(Mat source)
    {
        if (source.Empty()) return false;
        using var values = new Mat();
        using var equal = new Mat();
        for (var channel = 0; channel < source.Channels(); channel++)
        {
            if (source.Channels() == 1) source.CopyTo(values);
            else Cv2.ExtractChannel(source, values, channel);
            Cv2.MinMaxLoc(values, out double min, out double max);
            if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0) return false;
            if (min == max) continue;
            Cv2.Compare(values, min, equal, CmpTypes.EQ);
            var atMin = Cv2.CountNonZero(equal);
            Cv2.Compare(values, max, equal, CmpTypes.EQ);
            if (atMin + Cv2.CountNonZero(equal) != values.Rows * values.Cols) return false;
        }
        return true;
    }

    public static (int Low, int High, double Ratio) ByteSaturation(Mat mat, byte maximum)
    {
        if (mat.Empty() || mat.Depth() != MatType.CV_8U) return (0, 0, 0);
        using var channel = new Mat();
        using var low = new Mat();
        using var high = new Mat();
        var lowCount = 0;
        var highCount = 0;
        for (var index = 0; index < mat.Channels(); index++)
        {
            // CountNonZero requires a single-channel input; count scalar samples so the
            // numerator and denominator both include every channel of a color image.
            if (mat.Channels() == 1) mat.CopyTo(channel);
            else Cv2.ExtractChannel(mat, channel, index);
            Cv2.Compare(channel, Scalar.All(0), low, CmpTypes.LE);
            Cv2.Compare(channel, Scalar.All(maximum), high, CmpTypes.GE);
            lowCount += Cv2.CountNonZero(low);
            highCount += Cv2.CountNonZero(high);
        }
        var samples = (long)mat.Total() * mat.Channels();
        return (lowCount, highCount, samples == 0 ? 0 : (double)(lowCount + highCount) / samples);
    }

    public static (double Min, double Max) Range(Mat mat)
    {
        if (mat.Empty()) return (0, 0);
        Cv2.MinMaxLoc(mat, out double min, out double max);
        return (min, max);
    }

    public static int FiniteCount(Mat mat)
    {
        if (mat.Empty()) return 0;
        if (mat.Depth() == MatType.CV_32F)
        {
            using var copy = mat.IsContinuous() ? null : mat.Clone();
            var source = copy ?? mat;
            source.GetArray(out float[] values);
            return values.Count(float.IsFinite);
        }
        if (mat.Depth() == MatType.CV_64F)
        {
            using var copy = mat.IsContinuous() ? null : mat.Clone();
            var source = copy ?? mat;
            source.GetArray(out double[] values);
            return values.Count(double.IsFinite);
        }
        return checked((int)mat.Total() * mat.Channels());
    }
}
