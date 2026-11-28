using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class FeatureDetectionAlgorithms
{
    internal sealed record CornerResult(VisionPoint2D[] Points, double MeanResponse, double PeakResponse,
        int Requested, int Refined, int Discarded);
    internal sealed record EllipseCandidate(
        double CenterX, double CenterY, double SemiMajor, double SemiMinor, double AngleDeg,
        double Rms, double Coverage, int SupportPoints, double Area, double Confidence);
    internal sealed record EllipseResult(EllipseCandidate[] Ordered, int InputCount, int ContourCount, int CandidateCount);
    internal sealed record GaborResult(
        Mat Image, Mat? Direction, double[] Energies, double Mean, double Maximum,
        double DominantDegrees, double Anisotropy, double Sigma, int KernelSize,
        bool GainNormalized, double KernelGain);

    internal static CornerResult Corners(
        Mat analysis, string method, int fastThreshold, int maxCorners, double qualityLevel,
        double minDistance, int blockSize, double harrisK, bool subpixel, CancellationToken cancellationToken)
    {
        var points = new List<VisionPoint2D>();
        using var mask = new Mat();
        if (method == "fast")
        {
            foreach (var keyPoint in Cv2.FAST(analysis, fastThreshold, true))
                points.Add(new VisionPoint2D(keyPoint.Pt.X, keyPoint.Pt.Y));
        }
        else
        {
            var corners = Cv2.GoodFeaturesToTrack(
                analysis, maxCorners, qualityLevel, minDistance, mask, blockSize,
                useHarrisDetector: method == "harris", k: harrisK) ?? [];
            foreach (var corner in corners) points.Add(new VisionPoint2D(corner.X, corner.Y));
        }

        var requested = points.Count;
        var refinedCount = 0;
        var discarded = 0;
        if (subpixel && points.Count > 0)
        {
            var refinedPoints = new List<VisionPoint2D>(points.Count);
            foreach (var point in points)
            {
                var refined = new[] { new Point2f((float)point.X, (float)point.Y) };
                Cv2.CornerSubPix(analysis, refined, new Size(5, 5), new Size(-1, -1),
                    new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.01));
                var candidate = refined[0];
                var moved = Distance(point, new VisionPoint2D(candidate.X, candidate.Y));
                if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y) ||
                    candidate.X < 0 || candidate.Y < 0 || candidate.X >= analysis.Width || candidate.Y >= analysis.Height ||
                    !double.IsFinite(moved) || moved > 1.5)
                {
                    discarded++;
                    continue;
                }
                refinedPoints.Add(new VisionPoint2D(candidate.X, candidate.Y));
                refinedCount++;
            }
            points = refinedPoints;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var response = new Mat();
        Cv2.CornerMinEigenVal(analysis, response, blockSize);
        var ranked = points.Select(point => (Point: point, Response: SampleResponse(response, point)))
            .OrderByDescending(entry => entry.Response);
        var selected = new List<(VisionPoint2D Point, double Response)>();
        foreach (var candidate in ranked)
        {
            if (method == "fast" && selected.Any(entry => Distance(entry.Point, candidate.Point) < minDistance)) continue;
            selected.Add(candidate);
            if (selected.Count >= maxCorners) break;
        }
        return new CornerResult(
            selected.Select(entry => entry.Point).ToArray(),
            selected.Count > 0 ? selected.Average(entry => entry.Response) : 0d,
            selected.Count > 0 ? selected.Max(entry => entry.Response) : 0d,
            requested, refinedCount, discarded);
    }

    internal static EllipseResult Ellipses(
        Mat analysis, double cannyLow, double cannyHigh, int minPoints, double minArea, double maxArea,
        double minAspect, double maxAspect, double minCoverage, double maxRms, int maxEllipses,
        CancellationToken cancellationToken)
    {
        const int coverageBins = 72;
        using var smoothed = new Mat();
        Cv2.GaussianBlur(analysis, smoothed, new Size(3, 3), 0);
        using var edges = new Mat();
        Cv2.Canny(smoothed, edges, cannyLow, cannyHigh);
        cancellationToken.ThrowIfCancellationRequested();
        Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
        var candidates = new List<EllipseCandidate>();
        foreach (var contour in contours)
        {
            if (contour.Length < minPoints) continue;
            RotatedRect fit;
            try
            {
                using var mat = Mat.FromArray(contour);
                fit = Cv2.FitEllipse((InputArray)mat);
            }
            catch (OpenCVException) { continue; }

            var halfWidth = fit.Size.Width / 2.0;
            var halfHeight = fit.Size.Height / 2.0;
            if (halfWidth <= 0.5 || halfHeight <= 0.5) continue;
            var semiMajor = Math.Max(halfWidth, halfHeight);
            var semiMinor = Math.Min(halfWidth, halfHeight);
            var area = Math.PI * semiMajor * semiMinor;
            if (area < minArea || area > maxArea) continue;
            var aspect = semiMinor / semiMajor;
            if (aspect < minAspect || aspect > maxAspect) continue;

            var majorAngle = halfWidth >= halfHeight ? fit.Angle : fit.Angle + 90.0;
            majorAngle %= 180.0;
            if (majorAngle < 0) majorAngle += 180.0;
            var radians = majorAngle * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            var bins = new bool[coverageBins];
            var sumSquared = 0d;
            foreach (var point in contour)
            {
                var dx = point.X - fit.Center.X;
                var dy = point.Y - fit.Center.Y;
                var localX = dx * cos + dy * sin;
                var localY = -dx * sin + dy * cos;
                var normalized = Math.Sqrt((localX / semiMajor) * (localX / semiMajor) + (localY / semiMinor) * (localY / semiMinor));
                var radius = Math.Sqrt(localX * localX + localY * localY);
                var residual = (normalized - 1.0) * radius;
                sumSquared += residual * residual;
                var parametric = Math.Atan2(localY / semiMinor, localX / semiMajor);
                var bin = (int)Math.Floor((parametric + Math.PI) / (2 * Math.PI) * coverageBins) % coverageBins;
                bins[bin] = true;
            }
            var rms = Math.Sqrt(sumSquared / contour.Length);
            if (maxRms > 0 && rms > maxRms) continue;
            var coverage = bins.Count(occupied => occupied) * 360.0 / coverageBins;
            if (coverage < minCoverage) continue;
            candidates.Add(new EllipseCandidate(
                fit.Center.X, fit.Center.Y, semiMajor, semiMinor, majorAngle,
                rms, coverage, contour.Length, area,
                Math.Clamp(1.0 - rms / Math.Max(1e-6, semiMajor), 0, 1) * (coverage / 360.0)));
        }

        var deduped = new List<EllipseCandidate>();
        foreach (var candidate in candidates.OrderByDescending(item => item.SupportPoints))
        {
            var tolerance = Math.Max(2.0, 0.1 * candidate.SemiMinor);
            var duplicate = deduped.Any(existing =>
                Math.Abs(existing.CenterX - candidate.CenterX) <= tolerance &&
                Math.Abs(existing.CenterY - candidate.CenterY) <= tolerance &&
                Math.Abs(existing.SemiMajor - candidate.SemiMajor) <= 0.2 * candidate.SemiMajor &&
                Math.Abs(existing.SemiMinor - candidate.SemiMinor) <= 0.2 * candidate.SemiMinor);
            if (!duplicate) deduped.Add(candidate);
        }
        return new EllipseResult(deduped.Take(maxEllipses).ToArray(), deduped.Count, contours.Length, candidates.Count);
    }

    internal static GaborResult GaborBank(
        Mat analysis, double wavelength, int orientations, double sigmaRatio, double gamma,
        string energyMode, bool emitDirection, bool gainNormalized, CancellationToken cancellationToken)
    {
        var sigma = sigmaRatio * wavelength;
        var kernelSize = (int)Math.Round(6 * sigma) | 1;
        if (kernelSize < 3) kernelSize = 3;
        using var gray32 = new Mat();
        analysis.ConvertTo(gray32, MatType.CV_32F);
        using var accumulated = new Mat(analysis.Rows, analysis.Cols, MatType.CV_32FC1, Scalar.All(0));
        using var best = new Mat(analysis.Rows, analysis.Cols, MatType.CV_32FC1, Scalar.All(0));
        using var bestDirection = new Mat(analysis.Rows, analysis.Cols, MatType.CV_32FC1, Scalar.All(0));
        var energies = new double[orientations];
        var kernelGain = 0d;
        var degreesStep = 180.0 / orientations;
        for (var index = 0; index < orientations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var degrees = degreesStep * index;
            using var kernel = Cv2.GetGaborKernel(
                new Size(kernelSize, kernelSize), sigma, degrees * Math.PI / 180.0, wavelength, gamma, 0, MatType.CV_32F);
            if (gainNormalized)
            {
                using var kernelValues = new Mat();
                Cv2.Absdiff(kernel, kernel, kernelValues);
                kernelGain = Math.Max(1e-12, Cv2.Sum(kernelValues).Val0);
                using var normalizedKernel = new Mat();
                kernel.ConvertTo(normalizedKernel, MatType.CV_32F, 1.0 / kernelGain, 0);
                using var responseNormalized = new Mat();
                Cv2.Filter2D(gray32, responseNormalized, MatType.CV_32F, normalizedKernel);
                using var squaredNormalized = new Mat();
                Cv2.Multiply(responseNormalized, responseNormalized, squaredNormalized);
                energies[index] = Cv2.Mean(squaredNormalized).Val0;
                if (energyMode == "sum") Cv2.Add(accumulated, squaredNormalized, accumulated);
                else
                {
                    using var improved = new Mat();
                    Cv2.Compare(squaredNormalized, best, improved, CmpTypes.GT);
                    squaredNormalized.CopyTo(best, improved);
                    bestDirection.SetTo(Scalar.All((float)degrees), improved);
                }
                continue;
            }
            using var response = new Mat();
            Cv2.Filter2D(gray32, response, MatType.CV_32F, kernel);
            using var squared = new Mat();
            Cv2.Multiply(response, response, squared);
            energies[index] = Cv2.Mean(squared).Val0;
            if (energyMode == "sum") Cv2.Add(accumulated, squared, accumulated);
            else
            {
                using var improved = new Mat();
                Cv2.Compare(squared, best, improved, CmpTypes.GT);
                squared.CopyTo(best, improved);
                bestDirection.SetTo(Scalar.All((float)degrees), improved);
            }
        }

        using var combined = energyMode == "sum" ? accumulated.Clone() : best.Clone();
        if (gainNormalized && energyMode == "sum")
            combined.ConvertTo(combined, MatType.CV_32FC1, 1.0 / orientations, 0);
        Cv2.MinMaxLoc(combined, out _, out var maximum, out _, out _);
        var mean = Cv2.Mean(combined).Val0;
        var dominant = 0d;
        if (energies.Sum() > 1e-12)
        {
            var bestIndex = 0;
            for (var index = 1; index < orientations; index++)
                if (energies[index] > energies[bestIndex]) bestIndex = index;
            dominant = degreesStep * bestIndex;
        }
        var minOrientation = energies.Length > 0 ? Math.Max(energies.Min(), 1e-12) : 1e-12;
        var maxOrientation = energies.Length > 0 ? energies.Max() : 0;
        var anisotropy = minOrientation > 0 ? maxOrientation / minOrientation : 0;

        var normalized = new Mat();
        Mat? direction = null;
        try
        {
            if (maximum > 1e-9) combined.ConvertTo(normalized, MatType.CV_8UC1, 255.0 / maximum, 0);
            else combined.ConvertTo(normalized, MatType.CV_8UC1);
            if (emitDirection)
            {
                direction = new Mat();
                if (energyMode == "sum")
                {
                    direction.Create(analysis.Rows, analysis.Cols, MatType.CV_8UC1);
                    direction.SetTo(Scalar.All(0));
                }
                else bestDirection.ConvertTo(direction, MatType.CV_8UC1, 255.0 / 180.0, 0);
            }
            return new GaborResult(normalized, direction, energies, mean, maximum, dominant, anisotropy, sigma, kernelSize, gainNormalized, kernelGain);
        }
        catch
        {
            normalized.Dispose();
            direction?.Dispose();
            throw;
        }
    }

    private static double Distance(VisionPoint2D a, VisionPoint2D b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double SampleResponse(Mat response, VisionPoint2D point)
    {
        var x = (int)Math.Round(point.X);
        var y = (int)Math.Round(point.Y);
        if (x < 0 || y < 0 || x >= response.Cols || y >= response.Rows) return 0;
        return response.At<float>(y, x);
    }
}
