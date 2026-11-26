using System;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Direct unit tests for the managed fitting primitives the caliper pipeline relies on:
    /// exact recovery on clean data, outlier resistance in robust modes, degenerate-set
    /// rejection, and scale invariance of the circle fit.
    /// </summary>
    public class RobustFitAlgorithmTests
    {
        private const int CircleSampleCount = 16;
        private const double CircleCenterX = 100.0;
        private const double CircleCenterY = 100.0;
        private const double CircleRadius = 50.0;

        [Fact]
        public void TryFitCircle_Regression_CleanCircle_RecoversExactGeometry()
        {
            PointD[] points = BuildCirclePoints(CircleCenterX, CircleCenterY, CircleRadius, CircleSampleCount);

            Assert.True(RobustFitAlgorithm.TryFitCircle(points, LineFitMode.Regression, out PointD center, out double radius));

            Assert.Equal(CircleCenterX, center.X, 6);
            Assert.Equal(CircleCenterY, center.Y, 6);
            Assert.Equal(CircleRadius, radius, 6);
        }

        [Fact]
        public void TryFitCircle_RobustModes_SingleOutlier_StayOnTrueCircle()
        {
            PointD[] clean = BuildCirclePoints(CircleCenterX, CircleCenterY, CircleRadius, CircleSampleCount);
            PointD[] withOutlier = [.. clean, new PointD(CircleCenterX + 150.0, CircleCenterY)];

            Assert.True(RobustFitAlgorithm.TryFitCircle(withOutlier, LineFitMode.Regression, out PointD regressionCenter, out double regressionRadius));
            Assert.True(RobustFitAlgorithm.TryFitCircle(withOutlier, LineFitMode.Tukey, out PointD tukeyCenter, out double tukeyRadius));
            Assert.True(RobustFitAlgorithm.TryFitCircle(withOutlier, LineFitMode.Drop, out PointD dropCenter, out double dropRadius));

            double regressionError = CircleError(regressionCenter, regressionRadius);
            double tukeyError = CircleError(tukeyCenter, tukeyRadius);
            double dropError = CircleError(dropCenter, dropRadius);

            Assert.True(regressionError > 1.0, $"Regression should be pulled by the outlier (error {regressionError:F4}).");
            Assert.True(tukeyError < 0.1, $"Tukey should resist the outlier (error {tukeyError:F4}).");
            Assert.True(dropError < 0.1, $"Drop should resist the outlier (error {dropError:F4}).");
        }

        [Fact]
        public void TryFitCircle_TooFewPoints_ReturnsFalse()
        {
            PointD[] twoPoints = [new PointD(0, 0), new PointD(10, 0)];

            Assert.False(RobustFitAlgorithm.TryFitCircle(twoPoints, LineFitMode.Tukey, out _, out _));
        }

        [Fact]
        public void TryFitCircle_CollinearPoints_ReturnsFalse()
        {
            var points = new PointD[10];
            for (int i = 0; i < points.Length; i++)
            {
                double x = i * 5.0;
                points[i] = new PointD(x, (x * 0.5) + 3.0);
            }

            Assert.False(RobustFitAlgorithm.TryFitCircle(points, LineFitMode.Regression, out _, out _));
        }

        [Theory]
        [InlineData(1e-4)]
        [InlineData(1.0)]
        [InlineData(1e4)]
        public void TryFitCircle_ScaleInvariant_RecoversScaledGeometry(double scale)
        {
            PointD[] points = BuildCirclePoints(
                CircleCenterX * scale, CircleCenterY * scale, CircleRadius * scale, CircleSampleCount);

            Assert.True(RobustFitAlgorithm.TryFitCircle(points, LineFitMode.Regression, out PointD center, out double radius));

            AssertRelative(CircleCenterX * scale, center.X);
            AssertRelative(CircleCenterY * scale, center.Y);
            AssertRelative(CircleRadius * scale, radius);
        }

        [Fact]
        public void TryFitLine_Regression_CleanLine_RecoversExactGeometry()
        {
            var points = new PointD[10];
            for (int i = 0; i < points.Length; i++)
            {
                double x = i * 5.0;
                points[i] = new PointD(x, (x * 0.5) + 3.0);
            }

            Assert.True(RobustFitAlgorithm.TryFitLine(points, LineFitMode.Regression, 0, out DetectedLineSegment segment));

            // Every clean point should sit on the fitted infinite line.
            PointD start = segment.Start;
            PointD end = segment.End;
            VectorD direction = end - start;
            Assert.True(direction.LengthSquared > 1e-9);
            foreach (PointD point in points)
            {
                Assert.True(DistanceToLine(point, start, direction) < 1e-6);
            }
        }

        [Theory]
        [InlineData(LineFitMode.Huber)]
        [InlineData(LineFitMode.Tukey)]
        [InlineData(LineFitMode.Drop)]
        [InlineData(LineFitMode.Gauss)]
        public void TryFitLine_RobustModes_SingleOutlier_KeepTrueLine(LineFitMode fitMode)
        {
            PointD[] clean = BuildLinePoints();
            PointD[] withOutlier = [.. clean, new PointD(60.0, 90.0)];

            Assert.True(RobustFitAlgorithm.TryFitLine(withOutlier, fitMode, 0, out DetectedLineSegment segment));

            double maximumResidual = MaximumResidualFromLine(clean, segment);
            Assert.True(maximumResidual < 0.05, $"{fitMode} should keep the clean line (max residual {maximumResidual:F4}).");
        }

        [Fact]
        public void TryFitLine_Regression_SingleOutlier_IsPulledOffCleanLine()
        {
            PointD[] clean = BuildLinePoints();
            PointD[] withOutlier = [.. clean, new PointD(60.0, 90.0)];

            Assert.True(RobustFitAlgorithm.TryFitLine(withOutlier, LineFitMode.Regression, 0, out DetectedLineSegment segment));

            double maximumResidual = MaximumResidualFromLine(clean, segment);
            Assert.True(maximumResidual > 0.5, $"Regression is expected to be pulled by the outlier (max residual {maximumResidual:F4}).");
        }

        private static PointD[] BuildLinePoints()
        {
            var points = new PointD[10];
            for (int i = 0; i < points.Length; i++)
            {
                double x = i * 5.0;
                points[i] = new PointD(x, (x * 0.5) + 3.0);
            }

            return points;
        }

        private static double MaximumResidualFromLine(PointD[] points, DetectedLineSegment segment)
        {
            VectorD direction = segment.End - segment.Start;
            Assert.True(direction.LengthSquared > 1e-9);
            double maximum = 0;
            foreach (PointD point in points)
            {
                maximum = Math.Max(maximum, DistanceToLine(point, segment.Start, direction));
            }

            return maximum;
        }

        private static PointD[] BuildCirclePoints(double centerX, double centerY, double radius, int count)
        {
            var points = new PointD[count];
            for (int i = 0; i < count; i++)
            {
                double angle = 2 * Math.PI * i / count;
                points[i] = new PointD(centerX + (radius * Math.Cos(angle)), centerY + (radius * Math.Sin(angle)));
            }

            return points;
        }

        private static double CircleError(PointD center, double radius)
        {
            return Math.Abs(center.X - CircleCenterX) +
                   Math.Abs(center.Y - CircleCenterY) +
                   Math.Abs(radius - CircleRadius);
        }

        private static void AssertRelative(double expected, double actual)
        {
            double magnitude = Math.Max(1.0, Math.Abs(expected));
            Assert.True(
                Math.Abs(actual - expected) <= 1e-6 * magnitude,
                $"Expected {expected} within relative tolerance, got {actual}.");
        }

        private static double DistanceToLine(PointD point, PointD linePoint, VectorD direction)
        {
            double length = direction.Length;
            if (length <= 1e-12)
            {
                return (point - linePoint).Length;
            }

            VectorD delta = point - linePoint;
            return Math.Abs((delta.X * direction.Y) - (delta.Y * direction.X)) / length;
        }
    }
}
