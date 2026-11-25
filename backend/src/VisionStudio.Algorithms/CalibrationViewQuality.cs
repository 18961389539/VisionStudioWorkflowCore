using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Per-view capture quality used by the intrinsics wizard and detectBoard:
/// blur (Laplacian variance), board coverage, and corner completeness.
/// Mature calibrators gate acceptance on these before solving.
/// </summary>
public static class CalibrationViewQuality
{
    public enum Verdict { Fail, Warn, Pass }

    public sealed record Report(
        int CornerCount,
        int ExpectedCorners,
        double CoverageX,
        double CoverageY,
        double BlurVariance,
        Verdict Verdict,
        string Reason,
        string Guidance);

    public static double LaplacianVariance(Mat gray)
    {
        if (gray.Empty()) throw new InvalidOperationException("Blur analysis requires a non-empty grayscale image.");
        using var lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var std);
        var s = std.Val0;
        return s * s;
    }

    public static Report Evaluate(
        IReadOnlyList<IReadOnlyList<double>>? imagePoints,
        int imageWidth,
        int imageHeight,
        int expectedCorners,
        double blurVariance,
        double minBlurVariance = 0,
        double minCoverage = 0,
        double warnCoverage = 0.35)
    {
        if (imageWidth < 16 || imageHeight < 16) throw new InvalidOperationException("Image size must be at least 16x16.");
        if (expectedCorners < 1) throw new InvalidOperationException("Expected corner count must be positive.");
        if (!double.IsFinite(blurVariance) || blurVariance < 0) throw new InvalidOperationException("Blur variance must be finite and non-negative.");
        if (!double.IsFinite(minBlurVariance) || minBlurVariance < 0) throw new InvalidOperationException("minBlurVariance must be finite and non-negative (0 disables).");
        if (!double.IsFinite(minCoverage) || minCoverage is < 0 or > 1) throw new InvalidOperationException("minCoverage must be in 0..1 (0 disables).");
        if (!double.IsFinite(warnCoverage) || warnCoverage is < 0 or > 1) throw new InvalidOperationException("warnCoverage must be in 0..1.");

        var count = imagePoints?.Count ?? 0;
        double covX = 0, covY = 0;
        if (count > 0 && imagePoints is not null)
        {
            covX = (imagePoints.Max(p => p[0]) - imagePoints.Min(p => p[0])) / Math.Max(1, imageWidth - 1);
            covY = (imagePoints.Max(p => p[1]) - imagePoints.Min(p => p[1])) / Math.Max(1, imageHeight - 1);
        }

        if (count == 0)
            return new(0, expectedCorners, 0, 0, blurVariance, Verdict.Fail, "board-not-found",
                "Move the board fully into view, improve lighting contrast, and keep the plane mostly flat to the camera.");
        if (count != expectedCorners)
            return new(count, expectedCorners, covX, covY, blurVariance, Verdict.Fail, "incomplete-grid",
                $"Detected {count}/{expectedCorners} corners. Fill the frame with a complete isolated board and avoid clutter.");
        if (minBlurVariance > 0 && blurVariance < minBlurVariance)
            return new(count, expectedCorners, covX, covY, blurVariance, Verdict.Fail, "too-blurry",
                "Sharpen focus or reduce motion blur; Laplacian variance is below the configured gate.");
        if (minCoverage > 0 && (covX < minCoverage || covY < minCoverage))
            return new(count, expectedCorners, covX, covY, blurVariance, Verdict.Fail, "low-coverage",
                "Move the board closer or tilt so corners cover more of the image; avoid tiny distant boards.");

        var coverage = Math.Min(covX, covY);
        if (coverage < warnCoverage)
            return new(count, expectedCorners, covX, covY, blurVariance, Verdict.Warn, "marginal-coverage",
                "Accepted with a warning: board covers little of the FOV. Capture additional views near the edges and corners.");
        if (blurVariance < 80)
            return new(count, expectedCorners, covX, covY, blurVariance, Verdict.Warn, "soft-focus",
                "Accepted with a warning: image looks soft. Prefer sharper views for lower RMS.");

        return new(count, expectedCorners, covX, covY, blurVariance, Verdict.Pass, "ok",
            "Good view. Vary tilt and position across the FOV; aim for 8–20 well-distributed views.");
    }
}
