using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class ImageGeometryAlgorithms
{
    internal sealed record GradientResult(
        Mat Magnitude, Mat? Direction, double Mean, double Maximum, double Percentile99);

    internal sealed record DistanceResult(Mat Image, int ForegroundCount, double Maximum, double Mean);

    internal static Mat Flip(Mat source, FlipMode mode)
    {
        var result = new Mat();
        try
        {
            Cv2.Flip(source, result, mode);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal static GradientResult Gradient(
        Mat source, string op, int kernelSize, string scaleMode, bool emitDirection, CancellationToken cancellationToken)
    {
        using var gray = ToGrayCopy(source);
        using var gray32 = new Mat();
        gray.ConvertTo(gray32, MatType.CV_32F, 1, 0);
        using var dx = new Mat();
        using var dy = new Mat();
        if (op == "scharr")
        {
            Cv2.Scharr(gray32, dx, MatType.CV_32F, 1, 0);
            Cv2.Scharr(gray32, dy, MatType.CV_32F, 0, 1);
        }
        else
        {
            Cv2.Sobel(gray32, dx, MatType.CV_32F, 1, 0, kernelSize);
            Cv2.Sobel(gray32, dy, MatType.CV_32F, 0, 1, kernelSize);
        }

        using var magnitude = new Mat();
        Cv2.Magnitude(dx, dy, magnitude);
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.MinMaxLoc(magnitude, out var minimum, out var maximum, out _, out _);
        var mean = Cv2.Mean(magnitude).Val0;
        var percentile = HistogramPercentile(magnitude, 0.99, minimum, maximum);

        var magnitudeOut = new Mat();
        Mat? directionOut = null;
        try
        {
            if (scaleMode == "stretch")
            {
                var divisor = percentile > 1e-9 ? percentile : 1d;
                using var scaled = new Mat();
                magnitude.ConvertTo(scaled, MatType.CV_32F, 255d / divisor, 0);
                scaled.ConvertTo(magnitudeOut, MatType.CV_8UC1);
            }
            else
            {
                magnitude.ConvertTo(magnitudeOut, source.Depth() == MatType.CV_32F ? MatType.CV_32F : source.Depth());
            }

            if (emitDirection)
            {
                directionOut = new Mat();
                Cv2.Phase(dx, dy, directionOut, angleInDegrees: true);
                using var negativeMask = new Mat();
                Cv2.Compare(directionOut, Scalar.All(0), negativeMask, CmpTypes.LT);
                using var shifted = new Mat();
                Cv2.Add(directionOut, Scalar.All(360), shifted);
                shifted.CopyTo(directionOut, negativeMask);
            }
            return new GradientResult(magnitudeOut, directionOut, mean, maximum, percentile);
        }
        catch
        {
            magnitudeOut.Dispose();
            directionOut?.Dispose();
            throw;
        }
    }

    internal static DistanceResult DistanceTransform(
        Mat source, string mode, DistanceTypes metric, int maskSize, CancellationToken cancellationToken)
    {
        using var mask = new Mat();
        if (mode == "background") Cv2.BitwiseNot(source, mask);
        else source.CopyTo(mask);
        using var distance = new Mat();
        Cv2.DistanceTransform(mask, distance, metric, (DistanceTransformMasks)maskSize);
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.MinMaxLoc(distance, out _, out var maximum, out _, out _);
        var foregroundCount = Cv2.CountNonZero(mask);
        var mean = foregroundCount > 0 ? Cv2.Mean(distance, mask).Val0 : 0d;
        return new DistanceResult(distance.Clone(), foregroundCount, maximum, mean);
    }

    private static Mat ToGrayCopy(Mat source)
    {
        if (source.Channels() == 1) return source.Clone();
        var gray = new Mat();
        try
        {
            var code = source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY;
            Cv2.CvtColor(source, gray, code);
            return gray;
        }
        catch
        {
            gray.Dispose();
            throw;
        }
    }

    private static double HistogramPercentile(Mat single32, double fraction, double knownMin, double knownMax)
    {
        if (single32.Empty() || !(knownMax > knownMin)) return knownMax;
        var range = new Rangef((float)knownMin, (float)knownMax);
        using var hist = new Mat();
        using var noMask = new Mat();
        Cv2.CalcHist([single32], [0], noMask, hist, 1, [256], [range]);
        var target = single32.Total() * fraction;
        var accumulated = 0d;
        for (var bin = 0; bin < 256; bin++)
        {
            accumulated += hist.At<float>(bin);
            if (accumulated >= target)
                return knownMin + (knownMax - knownMin) * (bin + 1) / 256d;
        }
        return knownMax;
    }
}
