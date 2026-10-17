namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure golden-template helpers: sampled/coarse-to-fine integer-shift registration and
/// least-squares illumination (gain/offset) compensation, HALCON variation-model style.
/// Inputs are row-major grayscale buffers in source intensity units; optional masks
/// restrict fitting and alignment scoring to the inspection domain.</summary>
public static class ReferenceAlignment
{
    public sealed record ShiftResult(int ShiftX, int ShiftY, double MeanAbsoluteDifference);

    public static ShiftResult SearchShift(byte[] image, byte[] reference, int width, int height, int maxShift, byte[]? domainMask = null, CancellationToken cancellationToken = default)
        => SearchShift(ToFloat(image), ToFloat(reference), width, height, maxShift, domainMask, cancellationToken);

    public static ShiftResult SearchShift(float[] image, float[] reference, int width, int height, int maxShift, byte[]? domainMask = null, CancellationToken cancellationToken = default)
    {
        if (width < 1 || height < 1) throw new InvalidOperationException("Registration dimensions must be positive.");
        var length = checked(width * height);
        if (image.Length != length || reference.Length != length) throw new InvalidOperationException("Registration buffers must match image dimensions.");
        if (domainMask is not null && domainMask.Length != image.Length) throw new InvalidOperationException("Registration mask must match image dimensions.");
        if (maxShift < 0) throw new InvalidOperationException("Registration tolerance must be non-negative.");
        var sampleIndices = RegistrationSampleIndices(image.Length, domainMask, cancellationToken);
        if (sampleIndices.Length == 0) return new(0, 0, 0);
        const int candidateLimit = 5;
        var coarseCandidates = new List<ShiftResult>(candidateLimit);
        for (var dy = -maxShift; dy <= maxShift; dy++)
        for (var dx = -maxShift; dx <= maxShift; dx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ScoreShift(image, reference, width, height, dx, dy, domainMask, sampleIndices, cancellationToken);
            if (candidate.MeanAbsoluteDifference == double.MaxValue) continue;
            var insertAt = 0;
            while (insertAt < coarseCandidates.Count && !IsBetter(candidate, coarseCandidates[insertAt])) insertAt++;
            if (insertAt >= candidateLimit) continue;
            coarseCandidates.Insert(insertAt, candidate);
            if (coarseCandidates.Count > candidateLimit) coarseCandidates.RemoveAt(candidateLimit);
        }
        if (coarseCandidates.Count == 0) return new(0, 0, 0);

        var best = new ShiftResult(0, 0, double.MaxValue);
        foreach (var candidate in coarseCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exact = ScoreShift(image, reference, width, height, candidate.ShiftX, candidate.ShiftY, domainMask, null, cancellationToken);
            if (IsBetter(exact, best)) best = exact;
        }
        var centerX = best.ShiftX; var centerY = best.ShiftY;
        for (var dy = Math.Max(-maxShift, centerY - 1); dy <= Math.Min(maxShift, centerY + 1); dy++)
        for (var dx = Math.Max(-maxShift, centerX - 1); dx <= Math.Min(maxShift, centerX + 1); dx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refined = ScoreShift(image, reference, width, height, dx, dy, domainMask, null, cancellationToken);
            if (IsBetter(refined, best)) best = refined;
        }
        return best.MeanAbsoluteDifference == double.MaxValue ? new(0, 0, 0) : best;
    }

    private static int[] RegistrationSampleIndices(int length, byte[]? domainMask, CancellationToken token)
    {
        var validCount = 0;
        if (domainMask is null) validCount = length;
        else for (var i = 0; i < length; i++)
        {
            if ((i & 0x3FFFF) == 0) token.ThrowIfCancellationRequested();
            if (domainMask[i] != 0) validCount++;
        }
        if (validCount == 0) return [];
        const int targetSamples = 16384;
        var stride = Math.Max(1, (validCount + targetSamples - 1) / targetSamples);
        var result = new List<int>(Math.Min(validCount, targetSamples)); var validIndex = 0;
        for (var i = 0; i < length; i++)
        {
            if ((i & 0x3FFFF) == 0) token.ThrowIfCancellationRequested();
            if (domainMask is not null && domainMask[i] == 0) continue;
            if (validIndex % stride == 0) result.Add(i);
            validIndex++;
        }
        return result.ToArray();
    }

    private static ShiftResult ScoreShift(float[] image, float[] reference, int width, int height, int dx, int dy,
        byte[]? domainMask, int[]? sampleIndices, CancellationToken token)
    {
        double sum = 0; long count = 0;
        if (sampleIndices is not null)
        {
            for (var i = 0; i < sampleIndices.Length; i++)
            {
                if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                var index = sampleIndices[i]; var x = index % width; var y = index / width;
                var referenceX = x - dx; var referenceY = y - dy;
                if (referenceX < 0 || referenceX >= width || referenceY < 0 || referenceY >= height) continue;
                sum += Math.Abs((double)image[index] - reference[referenceY * width + referenceX]); count++;
            }
        }
        else
        {
            for (var y = Math.Max(0, dy); y < Math.Min(height, height + dy); y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = Math.Max(0, dx); x < Math.Min(width, width + dx); x++)
                {
                    var index = y * width + x;
                    if (domainMask is not null && domainMask[index] == 0) continue;
                    sum += Math.Abs((double)image[index] - reference[(y - dy) * width + (x - dx)]); count++;
                }
            }
        }
        return count == 0 ? new(dx, dy, double.MaxValue) : new(dx, dy, sum / count);
    }

    private static bool IsBetter(ShiftResult candidate, ShiftResult current)
    {
        if (candidate.MeanAbsoluteDifference == double.MaxValue) return false;
        return candidate.MeanAbsoluteDifference < current.MeanAbsoluteDifference - 1e-12 ||
               Math.Abs(candidate.MeanAbsoluteDifference - current.MeanAbsoluteDifference) <= 1e-12 &&
               Math.Abs(candidate.ShiftX) + Math.Abs(candidate.ShiftY) < Math.Abs(current.ShiftX) + Math.Abs(current.ShiftY);
    }

    /// <summary>
    /// Add a translated sample to per-pixel Welford accumulators. Shift values map
    /// source sample coordinates into the reference model's image coordinates.
    /// Border pixels without a corresponding sample are excluded from their model.
    /// </summary>
    public static void AccumulateAlignedSample(float[] mean, float[] m2, int[] sampleCounts, float[] sample,
        int width, int height, int shiftX, int shiftY, CancellationToken cancellationToken = default)
    {
        if (width < 1 || height < 1) throw new InvalidOperationException("Variation model dimensions must be positive.");
        var length = checked(width * height);
        if (mean.Length != length || m2.Length != length || sampleCounts.Length != length || sample.Length != length)
            throw new InvalidOperationException("Variation model buffers must match image dimensions.");
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var sampleX = x - shiftX; var sampleY = y - shiftY;
                if (sampleX < 0 || sampleX >= width || sampleY < 0 || sampleY >= height) continue;
                var index = y * width + x; var count = ++sampleCounts[index];
                var value = sample[sampleY * width + sampleX];
                var delta = value - mean[index];
                mean[index] += delta / count;
                m2[index] += delta * (value - mean[index]);
            }
        }
    }

    public static float[] SampleStdDev(float[] m2, int[] sampleCounts, CancellationToken cancellationToken = default)
    {
        if (m2.Length != sampleCounts.Length) throw new InvalidOperationException("Variation model buffers must have equal size.");
        var result = new float[m2.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if ((i & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            result[i] = sampleCounts[i] > 1 ? (float)Math.Sqrt(Math.Max(0d, m2[i] / (sampleCounts[i] - 1d))) : 0f;
        }
        return result;
    }

    /// <summary>
    /// Per-pixel local standard deviation over an odd square window, computed with
    /// integral images (O(n) regardless of window size). Used for single-reference
    /// textured areas; multi-reference models use the learned sample deviation.
    /// </summary>
    public static float[] LocalStdDev(float[] image, int width, int height, int window, CancellationToken cancellationToken = default)
    {
        if (width < 1 || height < 1 || image.Length != checked(width * height)) throw new InvalidOperationException("Variation buffer must match image dimensions.");
        if (window is < 3 or > 101 || window % 2 == 0) throw new InvalidOperationException("Variation window must be odd and between 3 and 101.");
        var half = window / 2;
        var result = new float[image.Length];
        var verticalSum = new double[width]; var verticalSquareSum = new double[width];
        var horizontalPrefix = new double[width + 1]; var horizontalSquarePrefix = new double[width + 1];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y0 = Math.Max(0, y - half); var y1 = Math.Min(height, y + half + 1);
            if (y == 0)
            {
                for (var row = y0; row < y1; row++) AddRow(row, 1d);
            }
            else
            {
                var previousY0 = Math.Max(0, y - 1 - half); var previousY1 = Math.Min(height, y - 1 + half + 1);
                if (y0 > previousY0) AddRow(previousY0, -1d);
                if (y1 > previousY1) AddRow(previousY1, 1d);
            }
            horizontalPrefix[0] = 0; horizontalSquarePrefix[0] = 0;
            for (var x = 0; x < width; x++)
            {
                horizontalPrefix[x + 1] = horizontalPrefix[x] + verticalSum[x];
                horizontalSquarePrefix[x + 1] = horizontalSquarePrefix[x] + verticalSquareSum[x];
            }
            for (var x = 0; x < width; x++)
            {
                var x0 = Math.Max(0, x - half); var x1 = Math.Min(width, x + half + 1);
                var count = (x1 - x0) * (y1 - y0);
                var sum = horizontalPrefix[x1] - horizontalPrefix[x0];
                var sumSq = horizontalSquarePrefix[x1] - horizontalSquarePrefix[x0];
                var mean = sum / count;
                var variance = Math.Max(0, sumSq / count - mean * mean);
                result[y * width + x] = (float)Math.Sqrt(variance);
            }
        }
        return result;

        void AddRow(int row, double sign)
        {
            var offset = row * width;
            for (var x = 0; x < width; x++)
            {
                var value = image[offset + x];
                verticalSum[x] += sign * value;
                verticalSquareSum[x] += sign * value * value;
            }
        }
    }

    public static double[] LocalStdDev(byte[] image, int width, int height, int window, CancellationToken cancellationToken = default)
        => LocalStdDev(ToFloat(image), width, height, window, cancellationToken).Select(value => (double)value).ToArray();

    /// <summary>Least squares fit of image ≈ gain * reference + offset.</summary>
    public static (double Gain, double Offset) FitGainOffset(byte[] image, byte[] reference, byte[]? domainMask = null, CancellationToken cancellationToken = default)
        => FitGainOffset(ToFloat(image), ToFloat(reference), domainMask, cancellationToken);

    public static (double Gain, double Offset) FitGainOffset(float[] image, float[] reference, byte[]? domainMask = null, CancellationToken cancellationToken = default)
    {
        if (image.Length != reference.Length) throw new InvalidOperationException("Compensation buffers must have identical size.");
        if (domainMask is not null && domainMask.Length != image.Length) throw new InvalidOperationException("Compensation mask must match image dimensions.");
        double sx = 0, sy = 0, sxx = 0, sxy = 0; long n = 0;
        for (var i = 0; i < image.Length; i++)
        {
            if ((i & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
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

    private static float[] ToFloat(byte[] values)
    {
        var result = new float[values.Length];
        for (var i = 0; i < values.Length; i++) result[i] = values[i];
        return result;
    }
}
