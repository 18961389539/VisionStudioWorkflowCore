using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    /// <summary>
    /// Managed geometry fitting and residual calculations.
    /// </summary>
    internal static partial class ImageAnalysisService
    {
        private static List<CaliperEdgeSample> FilterInlierSamples(
            List<CaliperEdgeSample> samples,
            Vector preferredDirection,
            double fallbackHalfLength,
            double configuredThreshold,
            int minimumRequired,
            LineFitMode fitMode = LineFitMode.Tukey,
            int clippingEndPoints = 0)
        {
            if (samples.Count <= minimumRequired)
            {
                return samples;
            }

            Point[] points = [.. samples.Select(sample => sample.Point)];
            if (!TryFitLine(points, fitMode, clippingEndPoints, out LineSegmentOverlay provisionalFit))
            {
                return samples;
            }

            Vector fitDirection = (provisionalFit.End - provisionalFit.Start).ToWpfVector();
            if (fitDirection.LengthSquared < 1e-6)
            {
                return samples;
            }

            List<(CaliperEdgeSample Sample, double Distance)> distances = new(samples.Count);
            foreach (CaliperEdgeSample sample in samples)
            {
                distances.Add((sample, DistanceToLine(sample.Point, provisionalFit.Start.ToWpfPoint(), fitDirection)));
            }

            double[] distanceValues = distances.Select(item => item.Distance).ToArray();
            double distanceMedian = MedianOf(distanceValues);
            double distanceScale = 1.4826 * MedianOf(distanceValues.Select(value => Math.Abs(value - distanceMedian)).ToArray());
            double threshold = configuredThreshold > 0
                ? configuredThreshold
                : Math.Clamp(Math.Max(0.75, distanceMedian + 3.0 * distanceScale), 0.75, 8.0);

            List<CaliperEdgeSample> filtered = distances
                .Where(item => item.Distance <= threshold)
                .Select(item => item.Sample)
                .ToList();

            return filtered.Count >= minimumRequired ? filtered : samples;
        }

        private static List<CaliperEdgeSample> FilterCircularInlierSamples(
            List<CaliperEdgeSample> samples,
            double configuredThreshold,
            int minimumRequired,
            Point? referenceCenter = null,
            double referenceRadius = 0,
            LineFitMode fitMode = LineFitMode.Tukey)
        {
            if (samples.Count <= minimumRequired)
            {
                return samples;
            }

            Point[] points = [.. samples.Select(sample => sample.Point)];
            var candidates = new List<(List<CaliperEdgeSample> Inliers, double Residual, bool IsReference)>(2);
            if (TryFitCircle(points, fitMode, out Point center, out double radius) && radius > 0)
            {
                (List<CaliperEdgeSample> inliers, double residual) = BuildCircularInliers(samples, center, radius, configuredThreshold);
                candidates.Add((inliers, residual, false));
            }

            // A partial arc can contain more invalid samples than the all-point
            // fit can tolerate. When the ROI supplies a nominal center and radius,
            // use their radial consensus as a fit seed and let the final fit run
            // only on the retained arc points.
            if (referenceCenter.HasValue)
            {
                // Automatic-circle search uses a deliberately broad nominal
                // radius. Infer the dominant radial distance from the samples
                // before filtering so gap rays cannot pull the fit toward the
                // outer search boundary.
                double[] referenceDistances = samples
                    .Select(sample => GeometryUtils.Distance(sample.Point, referenceCenter.Value))
                    .Where(double.IsFinite)
                    .ToArray();
                double inferredRadius = PercentileOf(referenceDistances, 0.30);
                double radialSeed = inferredRadius > 0
                    ? inferredRadius
                    : referenceRadius;
                if (double.IsFinite(radialSeed) && radialSeed > 0)
                {
                    (List<CaliperEdgeSample> inliers, double residual) = BuildCircularInliers(samples, referenceCenter.Value, radialSeed, configuredThreshold);
                    candidates.Add((inliers, residual, true));
                }
            }

            if (candidates.Count == 0)
            {
                return samples;
            }

            List<CaliperEdgeSample> selectedInliers;
            (List<CaliperEdgeSample> Inliers, double Residual, bool IsReference)? referenceCandidate = candidates
                .FirstOrDefault(candidate => candidate.IsReference);
            (List<CaliperEdgeSample> Inliers, double Residual, bool IsReference)? nativeCandidate = candidates
                .FirstOrDefault(candidate => !candidate.IsReference);
            if (referenceCandidate.HasValue && referenceCandidate.Value.Inliers.Count >= minimumRequired &&
                referenceCandidate.Value.Residual <= 1.5)
            {
                selectedInliers = referenceCandidate.Value.Inliers;
            }
            else
            {
                selectedInliers = candidates
                    .Where(candidate => !candidate.IsReference || candidate.Inliers.Count >= minimumRequired)
                    .OrderByDescending(candidate => candidate.Inliers.Count)
                    .ThenBy(candidate => candidate.Residual)
                    .First()
                    .Inliers;
            }
            return selectedInliers.Count >= minimumRequired ? selectedInliers : samples;
        }

        private static (List<CaliperEdgeSample> Inliers, double Residual) BuildCircularInliers(
            IReadOnlyList<CaliperEdgeSample> samples,
            Point center,
            double radius,
            double configuredThreshold)
        {
            List<(CaliperEdgeSample Sample, double Distance)> distances = samples
                .Select(sample => (sample, Math.Abs(GeometryUtils.Distance(sample.Point, center) - radius)))
                .ToList();
            double[] distanceValues = distances.Select(item => item.Distance).ToArray();
            double distanceMedian = MedianOf(distanceValues);
            double distanceScale = 1.4826 * MedianOf(distanceValues.Select(value => Math.Abs(value - distanceMedian)).ToArray());
            double threshold = configuredThreshold > 0
                ? configuredThreshold
                : Math.Clamp(Math.Max(0.75, distanceMedian + 3.0 * distanceScale), 0.75, 8.0);
            List<CaliperEdgeSample> filtered = distances
                .Where(item => item.Distance <= threshold)
                .Select(item => item.Sample)
                .ToList();
            double residual = filtered.Count == 0
                ? double.PositiveInfinity
                : filtered.Average(item => Math.Abs(GeometryUtils.Distance(item.Point, center) - radius));
            return (filtered, residual);
        }

        private static (double Rms, double Max) ComputeResidualMetrics(Point[] points, LineSegmentOverlay fittedLine)
        {
            if (points.Length == 0)
            {
                return (0, 0);
            }

            Vector direction = (fittedLine.End - fittedLine.Start).ToWpfVector();
            double sumSquares = 0;
            double maxResidual = 0;
            foreach (Point point in points)
            {
                double distance = DistanceToLine(point, fittedLine.Start.ToWpfPoint(), direction);
                sumSquares += distance * distance;
                maxResidual = Math.Max(maxResidual, distance);
            }

            return (Math.Sqrt(sumSquares / points.Length), maxResidual);
        }

        private static (double Rms, double Max) ComputeCircularResidualMetrics(Point[] points, Point center, double radius)
        {
            if (points.Length == 0)
            {
                return (0, 0);
            }

            double sumSquares = 0;
            double maxResidual = 0;
            foreach (Point point in points)
            {
                double distance = Math.Abs(GeometryUtils.Distance(point, center) - radius);
                sumSquares += distance * distance;
                maxResidual = Math.Max(maxResidual, distance);
            }

            return (Math.Sqrt(sumSquares / points.Length), maxResidual);
        }

        /// <summary>
        /// Single canonical line fitting entry point. The managed fitter owns the
        /// fit mode, iteration count and clipping factor; ImageViewer only maps the
        /// point coordinates and forwards the endpoint clipping count.
        /// </summary>
        private static bool TryFitLine(
            Point[] points,
            LineFitMode fitMode,
            int clippingEndPoints,
            out LineSegmentOverlay segment)
        {
            return VisionAlgorithmEntryPoint.TryFitLine(points, fitMode, clippingEndPoints, out segment);
        }

        private static bool TryFitCircle(Point[] points, LineFitMode fitMode, out Point center, out double radius)
        {
            return VisionAlgorithmEntryPoint.TryFitCircle(points, fitMode, out center, out radius);
        }

        /// <summary>
        /// Fits a line through the managed robust fitter. The fitted result defines
        /// the segment endpoints.
        /// </summary>
        internal static LineSegmentOverlay FitLine(
            Point[] points,
            Vector preferredDirection,
            double fallbackHalfLength,
            double[]? weights = null,
            LineFitMode fitMode = LineFitMode.Tukey,
            int clippingEndPoints = 0)
        {
            _ = weights;
            if (points.Length < 2 || !TryFitLine(points, fitMode, clippingEndPoints, out LineSegmentOverlay segment))
            {
                throw new InvalidOperationException("The robust fitter failed to fit a line for the supplied points.");
            }

            return segment;
        }

        private static double DistanceToLine(Point point, Point linePoint, Vector lineDirection)
        {
            if (lineDirection.LengthSquared < 1e-6)
            {
                return GeometryUtils.Distance(point, linePoint);
            }

            Vector delta = point - linePoint;
            double cross = Math.Abs(delta.X * lineDirection.Y - delta.Y * lineDirection.X);
            return cross / lineDirection.Length;
        }

        private static double MedianOf(double[] values)
        {
            if (values.Length == 0)
            {
                return 0;
            }

            double[] sorted = [.. values];
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        private static double PercentileOf(double[] values, double percentile)
        {
            if (values.Length == 0)
            {
                return 0;
            }

            double[] sorted = [.. values];
            Array.Sort(sorted);
            double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
            {
                return sorted[lower];
            }

            double fraction = position - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
        }
    }
}
