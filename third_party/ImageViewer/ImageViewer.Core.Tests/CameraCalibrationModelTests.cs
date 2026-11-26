using System;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Core-level Brown-Conrady model tests: radial scale factors, normalization, factory
    /// defaults, validity gating, and local length scaling.
    /// Chinese: 这些断言只依赖 Core 模型，放在 Core.Tests 才能在最低层定位回归；WPF 控件与
    /// 信息面板的校正应用测试仍留在 ImageViewerControl.Tests。
    /// </summary>
    public class CameraCalibrationModelTests
    {
        [Fact]
        public void UndistortScaleFactor_NoCoefficients_ReturnsOne()
        {
            var calibration = new CameraCalibration { PrincipalX = 100, PrincipalY = 100, NormalizationRadius = 100 };

            Assert.Equal(1.0, calibration.UndistortScaleFactor(new PointD(150, 150)), 9);
        }

        [Fact]
        public void UndistortScaleFactor_AtPrincipalPoint_ReturnsOne()
        {
            var calibration = new CameraCalibration { PrincipalX = 100, PrincipalY = 80, NormalizationRadius = 100, K1 = 0.1 };

            Assert.Equal(1.0, calibration.UndistortScaleFactor(new PointD(100, 80)), 9);
        }

        [Fact]
        public void UndistortScaleFactor_PositiveK1_ReducesOffCenterMagnification()
        {
            // dx=100, R=100 → r²=1; local scale=1+K1=1.25 → factor=0.8
            var calibration = new CameraCalibration { PrincipalX = 100, PrincipalY = 100, NormalizationRadius = 100, K1 = 0.25 };

            double factor = calibration.UndistortScaleFactor(new PointD(200, 100));

            Assert.Equal(0.8, factor, 9);
        }

        [Fact]
        public void UndistortScaleFactor_ExtremeCoefficients_AreClamped()
        {
            var calibration = new CameraCalibration { PrincipalX = 100, PrincipalY = 100, NormalizationRadius = 100, K1 = 10 };

            double factor = calibration.UndistortScaleFactor(new PointD(200, 100));

            Assert.InRange(factor, 0.0, 5.0);
            Assert.True(factor > 0);
        }

        [Fact]
        public void UndistortScaleFactor_InvalidCalibration_ReturnsOne()
        {
            var calibration = new CameraCalibration { NormalizationRadius = 100, K1 = double.NaN };

            Assert.False(calibration.IsValid);
            Assert.Equal(1.0, calibration.UndistortScaleFactor(new PointD(150, 100)), 9);
        }

        [Fact]
        public void LocalLengthScaleFactor_BasicModel_MatchesUndistortScaleFactor()
        {
            var calibration = new CameraCalibration { PrincipalX = 0, PrincipalY = 0, NormalizationRadius = 100, K1 = 0.25, K2 = -0.05 };
            var point = new PointD(150, 0);

            Assert.Equal(
                calibration.UndistortScaleFactor(point),
                calibration.LocalLengthScaleFactor(point),
                12);
        }

        [Fact]
        public void NormalizedRadiusSquared_AtPrincipalPoint_IsZero()
        {
            var calibration = new CameraCalibration { PrincipalX = 50, PrincipalY = 60, NormalizationRadius = 10 };

            Assert.Equal(0.0, calibration.NormalizedRadiusSquared(new PointD(50, 60)), 9);
        }

        [Fact]
        public void NormalizedRadiusSquared_UsesPrincipalOffset()
        {
            var calibration = new CameraCalibration { PrincipalX = 90, PrincipalY = 80, NormalizationRadius = 10 };

            Assert.Equal(1.0, calibration.NormalizedRadiusSquared(new PointD(100, 80)), 9);
        }

        [Fact]
        public void CreateForImage_SetsPrincipalToCenter_AndRadiusToHalfShortSide()
        {
            var calibration = CameraCalibration.CreateForImage(0.1, 0.01, 1920, 1080);

            Assert.Equal(960, calibration.PrincipalX, 9);
            Assert.Equal(540, calibration.PrincipalY, 9);
            Assert.Equal(540, calibration.NormalizationRadius, 9);
        }

        [Fact]
        public void CreateForImage_ZeroDimensions_UsesSafeDefaults()
        {
            var calibration = CameraCalibration.CreateForImage(0.1, 0.01, 0, 0);

            Assert.Equal(0, calibration.PrincipalX, 9);
            Assert.Equal(0, calibration.PrincipalY, 9);
            Assert.Equal(1, calibration.NormalizationRadius, 9);
        }

        [Fact]
        public void CorrectPixelLength_AppliesFactorAtMidpoint()
        {
            var calibration = new CameraCalibration { PrincipalX = 0, PrincipalY = 0, NormalizationRadius = 100, K1 = 0.25 };

            double corrected = calibration.CorrectPixelLength(100, new PointD(100, 0));

            Assert.Equal(80.0, corrected, 9);
        }

        [Fact]
        public void IsEnabled_IgnoresCoefficientsBelowTheConfiguredEpsilon()
        {
            Assert.False(new CameraCalibration { K1 = 1e-13 }.IsEnabled);
            Assert.True(new CameraCalibration { K1 = 1e-6 }.IsEnabled);
            Assert.True(new CameraCalibration { TangentialP2 = -1e-6 }.IsEnabled);
        }

        [Fact]
        public void ExtendedBrownConradyModel_IsValidAndReportsExtendedModel()
        {
            var calibration = new CameraCalibration
            {
                PrincipalX = 320,
                PrincipalY = 240,
                NormalizationRadius = 320,
                K1 = 0.08,
                K2 = -0.015,
                K3 = 0.002,
                TangentialP1 = 0.001,
                TangentialP2 = -0.0008
            };

            Assert.True(calibration.IsValid);
            Assert.True(calibration.HasExtendedDistortionModel);
        }

        [Fact]
        public void Calibration_WithNonFiniteParameter_IsInvalidAndDoesNotTransform()
        {
            var calibration = new CameraCalibration
            {
                PrincipalX = 10,
                PrincipalY = 10,
                NormalizationRadius = 10,
                K1 = double.NaN
            };
            PointD point = new(14, 12);

            Assert.False(calibration.IsValid);
            Assert.Equal(point, calibration.DistortPoint(point));
            Assert.Equal(point, calibration.UndistortPoint(point));
        }
    }
}