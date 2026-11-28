using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class PreprocessEnhancementAlgorithms
{
    internal sealed record WhiteBalanceResult(Mat Image, double[] Means, double[] Gains);
    internal sealed record QualityMetrics(
        double Brightness, double Contrast, double Sharpness, double NoiseSigma,
        double SaturationRatio, double DarkRatio, double Entropy, long PixelCount);

    internal static Mat Clahe(Mat source, Mat? mask, double clipLimit, int tilesX, int tilesY, string colorMode)
    {
        using var clahe = CLAHE.Create(clipLimit, new Size(tilesX, tilesY));
        if (source.Channels() == 1 || colorMode == "gray")
        {
            using var gray = new Mat();
            var input = source;
            if (source.Channels() != 1)
            {
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                input = gray;
            }
            Mat? enhanced = new Mat();
            try
            {
                clahe.Apply(input, enhanced);
                var result = PreprocessGapMath.Composite(enhanced, input, mask);
                enhanced = null;
                return result;
            }
            finally { enhanced?.Dispose(); }
        }

        using var lab = new Mat();
        Cv2.CvtColor(source, lab, ColorConversionCodes.BGR2Lab);
        Cv2.Split(lab, out var channels);
        using var restored = new Mat();
        try
        {
            clahe.Apply(channels[0], channels[0]);
            Cv2.Merge(channels, restored);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }

        Mat? bgr = new Mat();
        try
        {
            Cv2.CvtColor(restored, bgr, ColorConversionCodes.Lab2BGR);
            var result = PreprocessGapMath.Composite(bgr, source, mask);
            bgr = null;
            return result;
        }
        finally { bgr?.Dispose(); }
    }

    internal static Mat UnwrapPolar(
        Mat source, double centerX, double centerY, double inner, double outer,
        double startAngle, double sweep, string direction, int radial, int angular,
        InterpolationFlags interpolation, CancellationToken cancellationToken)
    {
        var band = outer - inner;
        // Image coordinates are y-down: increasing atan2(y, x) is clockwise.
        var sign = direction == "clockwise" ? 1 : -1;
        var mapX = new float[(long)angular * radial];
        var mapY = new float[mapX.Length];
        for (var y = 0; y < radial; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var radius = inner + band * (y + .5) / radial;
            for (var x = 0; x < angular; x++)
            {
                var angle = (startAngle + sign * sweep * (x + .5) / angular) * Math.PI / 180;
                mapX[y * angular + x] = (float)(centerX + radius * Math.Cos(angle));
                mapY[y * angular + x] = (float)(centerY + radius * Math.Sin(angle));
            }
        }

        using var mapMatX = new Mat(radial, angular, MatType.CV_32FC1);
        using var mapMatY = new Mat(radial, angular, MatType.CV_32FC1);
        Marshal.Copy(mapX, 0, mapMatX.Data, mapX.Length);
        Marshal.Copy(mapY, 0, mapMatY.Data, mapY.Length);
        var output = new Mat();
        try
        {
            Cv2.Remap(source, output, mapMatX, mapMatY, interpolation, BorderTypes.Replicate);
            cancellationToken.ThrowIfCancellationRequested();
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    internal static Mat Bilateral(
        Mat source, Mat? mask, int diameter, double sigmaColor, double sigmaSpace, CancellationToken cancellationToken)
    {
        Mat? filtered = new Mat();
        try
        {
            Cv2.BilateralFilter(source, filtered, diameter, sigmaColor, sigmaSpace);
            cancellationToken.ThrowIfCancellationRequested();
            var output = PreprocessGapMath.Composite(filtered, source, mask);
            filtered = null;
            return output;
        }
        finally { filtered?.Dispose(); }
    }

    internal static WhiteBalanceResult WhiteBalance(
        Mat source, Mat? mask, string mode, double percentile, bool preserve, double[] manualGains,
        bool applyRoi, CancellationToken cancellationToken)
    {
        Cv2.Split(source, out var channels);
        var means = new double[3];
        var gains = new double[3];
        try
        {
            for (var i = 0; i < 3; i++) means[i] = PreprocessGapMath.MeanOf(channels[i], mask);
            var meanAll = (means[0] + means[1] + means[2]) / 3;
            if (meanAll <= 0) throw new InvalidOperationException("image.whiteBalance requires a non-black image.");
            if (mode == "manual")
            {
                Array.Copy(manualGains, gains, gains.Length);
            }
            else if (mode == "whitepatch")
            {
                var references = new double[3];
                for (var i = 0; i < 3; i++)
                {
                    var histogram = PreprocessGapMath.Histogram(channels[i], mask);
                    references[i] = Math.Max(1, PreprocessGapMath.Percentile(histogram, histogram.Sum(), percentile));
                }
                var reference = references.Max();
                for (var i = 0; i < 3; i++) gains[i] = reference / references[i];
            }
            else
            {
                for (var i = 0; i < 3; i++) gains[i] = meanAll / Math.Max(1, means[i]);
            }
            for (var i = 0; i < 3; i++)
            {
                if (!double.IsFinite(gains[i]) || gains[i] <= 0)
                    throw new InvalidOperationException($"image.whiteBalance computed an invalid gain for channel {i}.");
                gains[i] = Math.Clamp(gains[i], .01, 20);
            }
            if (preserve)
            {
                var adjusted = (means[0] * gains[0] + means[1] * gains[1] + means[2] * gains[2]) / 3;
                if (adjusted > 1e-9)
                {
                    var k = meanAll / adjusted;
                    for (var i = 0; i < 3; i++) gains[i] = Math.Clamp(gains[i] * k, .01, 20);
                }
            }
            for (var i = 0; i < 3; i++)
            {
                using var scaled = new Mat();
                channels[i].ConvertTo(scaled, MatType.CV_8U, gains[i], 0);
                scaled.CopyTo(channels[i]);
            }
            Mat? merged = new Mat();
            try
            {
                Cv2.Merge(channels, merged);
                var output = PreprocessGapMath.Composite(merged, source, applyRoi ? mask : null);
                merged = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new WhiteBalanceResult(output, means, gains);
                }
                catch { output.Dispose(); throw; }
            }
            finally { merged?.Dispose(); }
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }

    internal static QualityMetrics MeasureQuality(Mat source, Mat? mask, double saturation, double dark)
    {
        using var gray = source.Channels() == 3 ? new Mat() : source.Clone();
        if (source.Channels() == 3) Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        var effectiveMask = mask is not null && !mask.Empty() ? mask : null;
        var pixelCount = PreprocessGapMath.MaskedCount(effectiveMask, (long)gray.Rows * gray.Cols);
        if (pixelCount <= 0) throw new InvalidOperationException("image.qualityMetrics ROI selects no pixels.");
        var brightness = PreprocessGapMath.MeanOf(gray, effectiveMask);
        var histogram = PreprocessGapMath.Histogram(gray, effectiveMask);
        var contrast = PreprocessGapMath.Percentile(histogram, pixelCount, 95) - PreprocessGapMath.Percentile(histogram, pixelCount, 5);
        var entropy = PreprocessGapMath.Entropy(histogram, pixelCount);

        using var gx = new Mat(); using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, 3);
        using var gx2 = new Mat(); using var gy2 = new Mat(); using var energy = new Mat();
        Cv2.Multiply(gx, gx, gx2); Cv2.Multiply(gy, gy, gy2); Cv2.Add(gx2, gy2, energy);
        var sharpness = PreprocessGapMath.MeanOf(energy, effectiveMask);
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_32F, 3);
        using var absolute = new Mat();
        Cv2.Absdiff(laplacian, Scalar.All(0), absolute);
        // For the 3x3 Laplacian, E|L| = sqrt(20) * sqrt(2/pi) * sigma.
        var noiseSigma = PreprocessGapMath.MeanOf(absolute, effectiveMask) / (Math.Sqrt(2 / Math.PI) * Math.Sqrt(20));
        var saturationRatio = PreprocessGapMath.CountAtLeast(gray, effectiveMask, saturation) / (double)pixelCount;
        var darkRatio = PreprocessGapMath.CountAtMost(gray, effectiveMask, dark) / (double)pixelCount;
        return new QualityMetrics(brightness, contrast, sharpness, noiseSigma, saturationRatio, darkRatio, entropy, pixelCount);
    }

    internal static Mat MixChannels(Mat source, double[] weights, double offset)
    {
        var kernel = new float[weights.Length];
        for (var i = 0; i < weights.Length; i++) kernel[i] = (float)weights[i];
        using var kernelMat = new Mat(1, weights.Length, MatType.CV_32FC1);
        Marshal.Copy(kernel, 0, kernelMat.Data, kernel.Length);
        using var mixed = new Mat();
        Cv2.Transform(source, mixed, kernelMat);
        var output = new Mat();
        try { mixed.ConvertTo(output, MatType.CV_8U, 1, offset); return output; }
        catch { output.Dispose(); throw; }
    }
}
