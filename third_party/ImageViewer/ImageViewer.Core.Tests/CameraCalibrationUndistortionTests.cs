using System;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Core-level Brown-Conrady inversion tests: exact round trips for the extended model,
    /// convergence reporting, and the uncertainty linkage for non-converged points.
    /// </summary>
    public class CameraCalibrationUndistortionTests
    {
        [Theory]
        [InlineData(320.0, 240.0)]
        [InlineData(410.0, 285.0)]
        [InlineData(120.0, 400.0)]
        [InlineData(600.0, 60.0)]
        public void Undistort_ExtendedModel_RecoversIdealPoint(double x, double y)
        {
            CameraCalibration calibration = CreateExtendedCalibration();
            var ideal = new PointD(x, y);
            PointD distorted = calibration.DistortPoint(ideal);

            UndistortResult result = calibration.Undistort(distorted);

            Assert.True(result.Converged, $"Residual {result.ResidualPixels:E3} px should converge.");
            Assert.InRange(result.ResidualPixels, 0, 1e-6);
            Assert.InRange(Math.Abs(result.Point.X - ideal.X), 0, 1e-6);
            Assert.InRange(Math.Abs(result.Point.Y - ideal.Y), 0, 1e-6);
            Assert.Equal(result.Point, calibration.UndistortPoint(distorted));
            Assert.InRange(result.Iterations, 0, 8);
        }

        [Fact]
        public void Undistort_WithoutDistortionCoefficients_ReturnsInput()
        {
            var calibration = new CameraCalibration { PrincipalX = 100, PrincipalY = 100, NormalizationRadius = 100 };
            var point = new PointD(140, 90);

            UndistortResult result = calibration.Undistort(point);

            Assert.True(result.Converged);
            Assert.Equal(point, result.Point);
            Assert.Equal(0.0, result.ResidualPixels);
            Assert.Equal(0, result.Iterations);
        }

        [Fact]
        public void Undistort_NonFinitePoint_ReportsNotConverged()
        {
            CameraCalibration calibration = CreateExtendedCalibration();

            UndistortResult result = calibration.Undistort(new PointD(double.NaN, 10));

            Assert.False(result.Converged);
            Assert.True(double.IsPositiveInfinity(result.ResidualPixels));
        }

        [Fact]
        public void Undistort_NonInvertibleRadialModel_ReportsNotConverged()
        {
            // K1 = -1 folds the model at normalized radius 1: no stable inverse exists there,
            // so the result must be flagged instead of silently returning an arbitrary point.
            var calibration = new CameraCalibration
            {
                PrincipalX = 100,
                PrincipalY = 100,
                NormalizationRadius = 100,
                K1 = -1.0
            };

            UndistortResult result = calibration.Undistort(new PointD(200, 100));

            Assert.False(result.Converged);
        }

        [Fact]
        public void GetPointUncertaintyPixels_ConvergedPoint_ReturnsCalibrationUncertainty()
        {
            CameraCalibration calibration = CreateExtendedCalibration();
            calibration.MeasurementUncertaintyPixels = 0.25;
            PointD distorted = calibration.DistortPoint(new PointD(410, 285));

            Assert.Equal(0.25, calibration.GetPointUncertaintyPixels(distorted), 9);
        }

        [Fact]
        public void GetPointUncertaintyPixels_NotConvergedPoint_IncludesUndistortionResidual()
        {
            var calibration = new CameraCalibration
            {
                PrincipalX = 100,
                PrincipalY = 100,
                NormalizationRadius = 100,
                K1 = -1.0,
                MeasurementUncertaintyPixels = 0.25
            };

            double uncertainty = calibration.GetPointUncertaintyPixels(new PointD(200, 100));

            Assert.True(uncertainty > 0.25, $"Expected the residual to inflate uncertainty, got {uncertainty}.");
        }

        [Fact]
        public void CorrectPixelLength_MatchesUndistortedEndpointDistance()
        {
            CameraCalibration calibration = CreateExtendedCalibration();
            var start = new PointD(120, 400);
            var end = new PointD(410, 285);

            double corrected = calibration.CorrectPixelLength(start, end);
            double expected = calibration.UndistortPoint(start).DistanceTo(calibration.UndistortPoint(end));

            Assert.Equal(expected, corrected, 9);
        }

        private static CameraCalibration CreateExtendedCalibration() => new()
        {
            PrincipalX = 320,
            PrincipalY = 240,
            NormalizationRadius = 320,
            K1 = 0.08,
            K2 = -0.015,
            K3 = 0.002,
            TangentialP1 = 0.001,
            TangentialP2 = -0.0008,
            ReprojectionErrorRms = 0.12
        };
    }
}
