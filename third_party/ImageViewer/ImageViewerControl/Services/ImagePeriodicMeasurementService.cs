using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>Periodic line-profile extraction result in raw image pixel coordinates.</summary>
public sealed record ImagePeriodicMeasurementResult(
    bool Success,
    string Error,
    PointD[] Centers,
    double[] Intensities,
    double Threshold);

/// <summary>Extracts complete bright or dark bar centers from a sampled image line profile.</summary>
public static class ImagePeriodicMeasurementService
{
    private const int MaximumSamples = 65_536;
    private const int MaximumSearchPixels = 4_000_000;

    /// <summary>
    /// Samples a line at no more than one-pixel spacing, finds an Otsu threshold and returns
    /// the centers of complete thresholded runs in the directed line's order.
    /// </summary>
    /// <param name="bitmap">Source image.</param>
    /// <param name="line">Line ROI whose endpoints define the profile direction.</param>
    /// <param name="detectDark">When true, extracts dark runs; otherwise extracts bright runs.</param>
    /// <returns>Raw pixel-coordinate centers, their interpolated intensities, and the Otsu threshold.</returns>
    public static ImagePeriodicMeasurementResult Extract(BitmapSource bitmap, LineMeasureRoi line, bool detectDark = false)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(line);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || bitmap.IsDownloading)
            return Failure("The source bitmap is unavailable.");

        PointD start = line.P1, end = line.P2;
        if (!Finite(start) || !Finite(end)) return Failure("The line endpoints must be finite.");
        if (start.X < 0 || start.Y < 0 || end.X < 0 || end.Y < 0 ||
            start.X > bitmap.PixelWidth - 1 || end.X > bitmap.PixelWidth - 1 ||
            start.Y > bitmap.PixelHeight - 1 || end.Y > bitmap.PixelHeight - 1)
            return Failure("The line must lie inside the image bounds.");

        double dx = end.X - start.X, dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length <= 1e-12) return Failure("The line is degenerate.");
        if (length > MaximumSamples - 1) return Failure($"The line exceeds the {MaximumSamples:N0}-sample limit.");
        int intervals = Math.Max(1, (int)Math.Ceiling(length));
        int sampleCount = intervals + 1;

        int left = Math.Max(0, (int)Math.Floor(Math.Min(start.X, end.X)));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(start.Y, end.Y)));
        int right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(Math.Max(start.X, end.X)) + 1);
        int bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(Math.Max(start.Y, end.Y)) + 1);
        int cropWidth = right - left, cropHeight = bottom - top;
        long cropPixels = (long)cropWidth * cropHeight;
        if (cropWidth <= 0 || cropHeight <= 0 || cropPixels > MaximumSearchPixels)
            return Failure($"The line search area exceeds the {MaximumSearchPixels:N0}-pixel memory limit.");

        try
        {
            BitmapSource normalized = ImageViewerPixelAccess.NormalizeForIntensity(bitmap);
            int bytesPerPixel = ImageViewerPixelAccess.GetBytesPerPixel(normalized);
            int stride = checked(cropWidth * bytesPerPixel);
            byte[] pixels = new byte[checked(stride * cropHeight)];
            normalized.CopyPixels(new Int32Rect(left, top, cropWidth, cropHeight), pixels, stride, 0);

            double[] profile = new double[sampleCount];
            var histogram = new int[ImageViewerPixelAccess.GetIntensityMaximum(normalized.Format) + 1];
            double step = length / intervals;
            for (int i = 0; i < sampleCount; i++)
            {
                double distance = i * step;
                double x = start.X + (dx / length) * distance;
                double y = start.Y + (dy / length) * distance;
                double intensity = ImageViewerPixelAccess.ReadBilinearIntensity(
                    pixels, stride, cropWidth, cropHeight, bytesPerPixel, normalized.Format, x - left, y - top);
                if (!double.IsFinite(intensity)) return Failure("The sampled line profile contains a non-finite intensity.");
                profile[i] = intensity;
                histogram[Math.Clamp((int)Math.Round(intensity), 0, histogram.Length - 1)]++;
            }

            double minimum = profile.Min(), maximum = profile.Max();
            if (maximum <= minimum) return Failure("The sampled line has zero intensity contrast.");
            int thresholdValue = Otsu(histogram);
            if (histogram.Count(count => count > 0) < 2) return Failure("The sampled line has insufficient contrast for Otsu thresholding.");
            double threshold = thresholdValue;

            var centers = new List<PointD>();
            var intensities = new List<double>();
            bool IsTarget(double value) => detectDark ? value <= threshold : value > threshold;
            int runStart = 0;
            bool runValue = IsTarget(profile[0]);
            for (int i = 1; i <= sampleCount; i++)
            {
                bool atEnd = i == sampleCount;
                bool value = atEnd ? !runValue : IsTarget(profile[i]);
                if (!atEnd && value == runValue) continue;

                int runEnd = i - 1;
                if (runValue && runStart > 0 && runEnd < sampleCount - 1)
                {
                    double leftCrossing = InterpolateCrossing(profile[runStart - 1], profile[runStart], runStart - 1, threshold);
                    double rightCrossing = InterpolateCrossing(profile[runEnd], profile[runEnd + 1], runEnd, threshold);
                    double centerIndex = (leftCrossing + rightCrossing) / 2;
                    double centerDistance = centerIndex * step;
                    double centerX = start.X + (dx / length) * centerDistance;
                    double centerY = start.Y + (dy / length) * centerDistance;
                    centers.Add(new PointD(centerX, centerY));
                    intensities.Add(InterpolateProfile(profile, centerIndex));
                }

                if (atEnd) break;
                runStart = i;
                runValue = value;
            }

            if (centers.Count < 2) return Failure("At least two complete periodic runs are required.", threshold);
            return new ImagePeriodicMeasurementResult(true, string.Empty, centers.ToArray(), intensities.ToArray(), threshold);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        {
            return Failure($"Could not sample the source image: {exception.Message}");
        }
    }

    private static int Otsu(int[] histogram)
    {
        long total = histogram.Sum(value => (long)value);
        double weightedTotal = 0;
        for (int value = 0; value < histogram.Length; value++) weightedTotal += (double)value * histogram[value];
        long backgroundCount = 0;
        double backgroundWeighted = 0, bestVariance = -1;
        int threshold = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            backgroundCount += histogram[value];
            backgroundWeighted += (double)value * histogram[value];
            long foregroundCount = total - backgroundCount;
            if (backgroundCount == 0 || foregroundCount == 0) continue;
            double meanDifference = backgroundWeighted / backgroundCount - (weightedTotal - backgroundWeighted) / foregroundCount;
            double variance = (double)backgroundCount * foregroundCount * meanDifference * meanDifference;
            if (variance > bestVariance)
            {
                bestVariance = variance;
                threshold = value;
            }
        }

        return threshold;
    }

    private static double InterpolateCrossing(double first, double second, int firstIndex, double threshold)
    {
        double difference = second - first;
        if (difference == 0) return firstIndex + 0.5;
        return firstIndex + Math.Clamp((threshold - first) / difference, 0, 1);
    }

    private static double InterpolateProfile(double[] profile, double index)
    {
        int first = Math.Clamp((int)Math.Floor(index), 0, profile.Length - 1);
        int second = Math.Min(first + 1, profile.Length - 1);
        double fraction = Math.Clamp(index - first, 0, 1);
        return profile[first] * (1 - fraction) + profile[second] * fraction;
    }

    private static bool Finite(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static ImagePeriodicMeasurementResult Failure(string error, double threshold = 0) =>
        new(false, error, [], [], threshold);
}
