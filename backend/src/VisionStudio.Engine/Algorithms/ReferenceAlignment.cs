namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure golden-template helpers: exhaustive small-shift registration search and
/// least-squares illumination (gain/offset) compensation, HALCON variation-model style.
/// Inputs are row-major 8-bit grayscale buffers of identical size.</summary>
public static class ReferenceAlignment
{
    public sealed record ShiftResult(int ShiftX, int ShiftY, double MeanAbsoluteDifference);

    public static ShiftResult SearchShift(byte[] image, byte[] reference, int width, int height, int maxShift)
    {
        if (image.Length != width * height || reference.Length != width * height) throw new InvalidOperationException("Registration buffers must match image dimensions.");
        if (maxShift < 0) throw new InvalidOperationException("Registration tolerance must be non-negative.");
        var best = new ShiftResult(0, 0, double.MaxValue);
        for (var dy = -maxShift; dy <= maxShift; dy++)
        for (var dx = -maxShift; dx <= maxShift; dx++)
        {
            long sum = 0; long count = 0;
            var y0 = Math.Max(0, dy); var y1 = Math.Min(height, height + dy);
            var x0 = Math.Max(0, dx); var x1 = Math.Min(width, width + dx);
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                sum += Math.Abs(image[y * width + x] - reference[(y - dy) * width + (x - dx)]);
                count++;
            }
            if (count == 0) continue;
            var mean = (double)sum / count;
            var better = mean < best.MeanAbsoluteDifference - 1e-12 ||
                         Math.Abs(mean - best.MeanAbsoluteDifference) <= 1e-12 &&
                         Math.Abs(dx) + Math.Abs(dy) < Math.Abs(best.ShiftX) + Math.Abs(best.ShiftY);
            if (better) best = new(dx, dy, mean);
        }
        return best;
    }

    /// <summary>
    /// Per-pixel local standard deviation over an odd square window, computed with
    /// integral images (O(n) regardless of window size). Used as the variation term
    /// of a golden-template model: textured areas tolerate larger differences.
    /// </summary>
    public static double[] LocalStdDev(byte[] image, int width, int height, int window)
    {
        if (image.Length != width * height) throw new InvalidOperationException("Variation buffer must match image dimensions.");
        if (window < 3 || window % 2 == 0) throw new InvalidOperationException("Variation window must be odd and at least 3.");
        var integral = new double[(height + 1) * (width + 1)];
        var integralSq = new double[(height + 1) * (width + 1)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var v = image[y * width + x];
            var i = (y + 1) * (width + 1) + x + 1;
            integral[i] = v + integral[i - 1] + integral[i - (width + 1)] - integral[i - (width + 1) - 1];
            integralSq[i] = (double)v * v + integralSq[i - 1] + integralSq[i - (width + 1)] - integralSq[i - (width + 1) - 1];
        }
        double Sum(double[] table, int x0, int y0, int x1, int y1) =>
            table[y1 * (width + 1) + x1] - table[y0 * (width + 1) + x1] - table[y1 * (width + 1) + x0] + table[y0 * (width + 1) + x0];
        var half = window / 2;
        var result = new double[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var x0 = Math.Max(0, x - half); var y0 = Math.Max(0, y - half);
            var x1 = Math.Min(width, x + half + 1); var y1 = Math.Min(height, y + half + 1);
            var count = (x1 - x0) * (y1 - y0);
            var mean = Sum(integral, x0, y0, x1, y1) / count;
            var variance = Math.Max(0, Sum(integralSq, x0, y0, x1, y1) / count - mean * mean);
            result[y * width + x] = Math.Sqrt(variance);
        }
        return result;
    }

    /// <summary>Least squares fit of image ≈ gain * reference + offset.</summary>
    public static (double Gain, double Offset) FitGainOffset(byte[] image, byte[] reference, byte[]? domainMask = null)
    {
        if (image.Length != reference.Length) throw new InvalidOperationException("Compensation buffers must have identical size.");
        double sx = 0, sy = 0, sxx = 0, sxy = 0; long n = 0;
        for (var i = 0; i < image.Length; i++)
        {
            if (domainMask is not null && domainMask[i] == 0) continue;
            double x = reference[i]; double y = image[i];
            sx += x; sy += y; sxx += x * x; sxy += x * y; n++;
        }
        if (n == 0) return (1, 0);
        var denominator = n * sxx - sx * sx;
        if (Math.Abs(denominator) < 1e-9) return (1, sy / n - sx / n);
        var gain = (n * sxy - sx * sy) / denominator;
        var offset = (sy - gain * sx) / n;
        if (!double.IsFinite(gain) || gain <= 0.05 || gain > 20) return (1, offset);
        return (gain, offset);
    }
}
