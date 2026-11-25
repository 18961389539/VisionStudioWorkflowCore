using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>Golden-template registration helpers for defect.referenceCompare.
/// Translation search (MAD) and least-squares gain/offset fitting remain managed C# —
/// JLVision has no domain-masked MAD shift search or soft-weighted NCC registration API.
/// Applying a found translation uses JLVision affine_trans_image_size via WarpAffine;
/// applying gain/offset uses JLVision ScaleImage. There is no learned variation-model
/// operator in the JLVision package used here; multi-sample std-dev stays managed.</summary>
public static class ReferenceAlignment
{
    public sealed record ShiftResult(double ShiftX, double ShiftY, double MeanAbsoluteDifference);

    /// <summary>Translate a float image with JLVision affine_trans_image_size (via WarpAffine).
    /// Pixels that leave the frame are filled from <paramref name="outsideFill"/> (typically the
    /// live source) so difference edges do not invent zero-valued false defects.</summary>
    public static float[] WarpTranslate(float[] values, float[] outsideFill, int width, int height, double shiftX, double shiftY, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width < 1 || height < 1) throw new InvalidOperationException("Warp dimensions must be positive.");
        var length = checked(width * height);
        if (values.Length != length || outsideFill.Length != length)
            throw new InvalidOperationException("Warp buffers must match image dimensions.");
        if (shiftX == 0 && shiftY == 0) return (float[])values.Clone();

        using var source = new Mat(height, width, MatType.CV_32FC1);
        source.SetArray(values);
        using var ones = new Mat(height, width, MatType.CV_32FC1, new Scalar(1));
        // dst(x,y) = src(x - shiftX, y - shiftY)  =>  M = [1 0 -shiftX; 0 1 -shiftY]
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, 1d); matrix.Set(0, 1, 0d); matrix.Set(0, 2, -shiftX);
        matrix.Set(1, 0, 0d); matrix.Set(1, 1, 1d); matrix.Set(1, 2, -shiftY);
        using var warped = new Mat();
        using var validity = new Mat();
        JLVisionOperators.WarpAffine(source, warped, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        JLVisionOperators.WarpAffine(ones, validity, matrix, new Size(width, height), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        warped.GetArray(out float[] result);
        validity.GetArray(out float[] mask);
        for (var i = 0; i < length; i++)
        {
            if ((i & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (mask[i] < 0.5f) result[i] = outsideFill[i];
        }
        return result;
    }

    /// <summary>Apply g' = gain * g + offset with JLVision ScaleImage.</summary>
    public static float[] ApplyGainOffset(float[] values, int width, int height, double gain, double offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width < 1 || height < 1) throw new InvalidOperationException("ScaleImage dimensions must be positive.");
        if (values.Length != checked(width * height)) throw new InvalidOperationException("ScaleImage buffer must match image dimensions.");
        if (gain == 1d && offset == 0d) return (float[])values.Clone();
        using var mat = new Mat(height, width, MatType.CV_32FC1);
        mat.SetArray(values);
        using var image = JLVisionImages.FromMat(mat);
        using var scaled = image.ScaleImage(gain, offset);
        using var outMat = JLVisionImages.ToMat(scaled);
        outMat.GetArray(out float[] result);
        return result;
    }

    public static ShiftResult SearchShift(byte[] image, byte[] reference, int width, int height, int maxShift, byte[]? domainMask = null, CancellationToken cancellationToken = default, bool subpixel = false)
        => SearchShift(ToFloat(image), ToFloat(reference), width, height, maxShift, domainMask, cancellationToken, subpixel);

    public static ShiftResult SearchShift(float[] image, float[] reference, int width, int height, int maxShift, byte[]? domainMask = null, CancellationToken cancellationToken = default, bool subpixel = false)
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
            var exact = ScoreShift(image, reference, width, height, (int)candidate.ShiftX, (int)candidate.ShiftY, domainMask, null, cancellationToken);
            if (IsBetter(exact, best)) best = exact;
        }
        var centerX = (int)best.ShiftX; var centerY = (int)best.ShiftY;
        for (var dy = Math.Max(-maxShift, centerY - 1); dy <= Math.Min(maxShift, centerY + 1); dy++)
        for (var dx = Math.Max(-maxShift, centerX - 1); dx <= Math.Min(maxShift, centerX + 1); dx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refined = ScoreShift(image, reference, width, height, dx, dy, domainMask, null, cancellationToken);
            if (IsBetter(refined, best)) best = refined;
        }
        if (best.MeanAbsoluteDifference == double.MaxValue) return new(0, 0, 0);
        if (!subpixel) return best;

        // Estimate the fractional peak from normalized cross-correlation around the
        // integer solution. The final reported registration error remains the same
        // domain-restricted mean absolute difference used by the integer search.
        var refinedX = RefineSubpixelAxis(best.ShiftX, best.ShiftY, axisX: true);
        var refinedY = RefineSubpixelAxis(best.ShiftX, best.ShiftY, axisX: false);
        if (!TryScoreShift(image, reference, width, height, refinedX, refinedY, domainMask, null, cancellationToken, out var error))
            return best;
        if (error > best.MeanAbsoluteDifference + 1e-9) return best;
        return new(refinedX, refinedY, error);

        double RefineSubpixelAxis(double centerX, double centerY, bool axisX)
        {
            var center = axisX ? centerX : centerY;
            if (center - 0.5 < -maxShift || center + 0.5 > maxShift) return center;
            const double probe = 0.5;
            var left = axisX
                ? ScoreCorrelation(image, reference, width, height, centerX - probe, centerY, sampleIndices, cancellationToken)
                : ScoreCorrelation(image, reference, width, height, centerX, centerY - probe, sampleIndices, cancellationToken);
            var middle = ScoreCorrelation(image, reference, width, height, centerX, centerY, sampleIndices, cancellationToken);
            var right = axisX
                ? ScoreCorrelation(image, reference, width, height, centerX + probe, centerY, sampleIndices, cancellationToken)
                : ScoreCorrelation(image, reference, width, height, centerX, centerY + probe, sampleIndices, cancellationToken);
            if (!double.IsFinite(left) || !double.IsFinite(middle) || !double.IsFinite(right)) return center;
            var curvature = left - 2 * middle + right;
            if (curvature >= -1e-12) return center;
            var offset = 0.5 * probe * (left - right) / curvature;
            offset = Math.Clamp(offset, -probe, probe);
            return Math.Clamp(center + offset, -maxShift, maxShift);
        }
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
        TryScoreShift(image, reference, width, height, dx, dy, domainMask, sampleIndices, token, out var error);
        return new(dx, dy, error);
    }

    private static bool TryScoreShift(float[] image, float[] reference, int width, int height, double dx, double dy,
        byte[]? domainMask, int[]? sampleIndices, CancellationToken token, out double meanAbsoluteDifference)
    {
        double sum = 0; long count = 0;
        if (dx == Math.Truncate(dx) && dy == Math.Truncate(dy))
        {
            var shiftX = (int)dx; var shiftY = (int)dy;
            if (sampleIndices is not null)
            {
                for (var i = 0; i < sampleIndices.Length; i++)
                {
                    if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                    var index = sampleIndices[i]; var x = index % width; var y = index / width;
                    var referenceX = x - shiftX; var referenceY = y - shiftY;
                    if (referenceX < 0 || referenceX >= width || referenceY < 0 || referenceY >= height) continue;
                    sum += Math.Abs((double)image[index] - reference[referenceY * width + referenceX]); count++;
                }
            }
            else
            {
                for (var y = Math.Max(0, shiftY); y < Math.Min(height, height + shiftY); y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (var x = Math.Max(0, shiftX); x < Math.Min(width, width + shiftX); x++)
                    {
                        var index = y * width + x;
                        if (domainMask is not null && domainMask[index] == 0) continue;
                        sum += Math.Abs((double)image[index] - reference[(y - shiftY) * width + (x - shiftX)]); count++;
                    }
                }
            }
            meanAbsoluteDifference = count == 0 ? double.MaxValue : sum / count;
            return count != 0;
        }
        if (sampleIndices is not null)
        {
            for (var i = 0; i < sampleIndices.Length; i++)
            {
                if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                var index = sampleIndices[i]; var x = index % width; var y = index / width;
                if (!TrySampleBilinear(reference, width, height, x - dx, y - dy, out var referenceValue)) continue;
                sum += Math.Abs((double)image[index] - referenceValue); count++;
            }
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    if (domainMask is not null && domainMask[index] == 0) continue;
                    if (!TrySampleBilinear(reference, width, height, x - dx, y - dy, out var referenceValue)) continue;
                    sum += Math.Abs((double)image[index] - referenceValue); count++;
                }
            }
        }
        meanAbsoluteDifference = count == 0 ? double.MaxValue : sum / count;
        return count != 0;
    }

    private static double ScoreCorrelation(float[] image, float[] reference, int width, int height, double dx, double dy,
        int[] sampleIndices, CancellationToken token)
    {
        double imageSum = 0, referenceSum = 0; long count = 0;
        for (var i = 0; i < sampleIndices.Length; i++)
        {
            if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            var index = sampleIndices[i]; var x = index % width; var y = index / width;
            if (!TrySampleBilinear(reference, width, height, x - dx, y - dy, out var referenceValue)) continue;
            imageSum += image[index]; referenceSum += referenceValue; count++;
        }
        if (count < 2) return double.NaN;
        var imageMean = imageSum / count; var referenceMean = referenceSum / count;
        double covariance = 0, imageVariance = 0, referenceVariance = 0;
        for (var i = 0; i < sampleIndices.Length; i++)
        {
            if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            var index = sampleIndices[i]; var x = index % width; var y = index / width;
            if (!TrySampleBilinear(reference, width, height, x - dx, y - dy, out var referenceValue)) continue;
            var imageDelta = image[index] - imageMean; var referenceDelta = referenceValue - referenceMean;
            covariance += imageDelta * referenceDelta;
            imageVariance += imageDelta * imageDelta;
            referenceVariance += referenceDelta * referenceDelta;
        }
        var denominator = Math.Sqrt(imageVariance * referenceVariance);
        return denominator > 1e-12 ? covariance / denominator : double.NaN;
    }

    public static bool TrySampleBilinear(float[] values, int width, int height, double x, double y, out float value)
    {
        if (width < 1 || height < 1 || values.Length != checked(width * height))
            throw new InvalidOperationException("Bilinear sampling buffer must match positive image dimensions.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > width - 1 || y < 0 || y > height - 1)
        {
            value = 0;
            return false;
        }
        if (x == Math.Truncate(x) && y == Math.Truncate(y))
        {
            value = values[(int)y * width + (int)x];
            return true;
        }
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(width - 1, x0 + 1); var y1 = Math.Min(height - 1, y0 + 1);
        var fx = x - x0; var fy = y - y0;
        var top = values[y0 * width + x0] * (1d - fx) + values[y0 * width + x1] * fx;
        var bottom = values[y1 * width + x0] * (1d - fx) + values[y1 * width + x1] * fx;
        value = (float)(top * (1d - fy) + bottom * fy);
        return true;
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
        int width, int height, double shiftX, double shiftY, CancellationToken cancellationToken = default)
    {
        if (width < 1 || height < 1) throw new InvalidOperationException("Variation model dimensions must be positive.");
        var length = checked(width * height);
        if (mean.Length != length || m2.Length != length || sampleCounts.Length != length || sample.Length != length)
            throw new InvalidOperationException("Variation model buffers must match image dimensions.");
        var integerShift = shiftX == Math.Truncate(shiftX) && shiftY == Math.Truncate(shiftY);
        var integralShiftX = integerShift ? (int)shiftX : 0; var integralShiftY = integerShift ? (int)shiftY : 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                float value;
                if (integerShift)
                {
                    var sampleX = x - integralShiftX; var sampleY = y - integralShiftY;
                    if (sampleX < 0 || sampleX >= width || sampleY < 0 || sampleY >= height) continue;
                    value = sample[sampleY * width + sampleX];
                }
                else if (!TrySampleBilinear(sample, width, height, x - shiftX, y - shiftY, out value)) continue;
                var index = y * width + x; var count = ++sampleCounts[index];
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
