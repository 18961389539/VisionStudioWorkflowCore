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
