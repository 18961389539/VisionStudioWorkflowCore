using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 信息面板与 ROI 辅助对畸变标定的应用测试。
    /// English: WPF-side application tests for how ROI helpers and the info panel consume
    /// calibration corrections. Pure model tests live in ImageViewer.Core.Tests.
    /// </summary>
    public class CameraCalibrationTests
    {
        [Fact]
        public void RoiCalibrationHelper_UsesEndpointCorrectionForBasicRadialModel()
        {
            var calibration = new CameraCalibration
            {
                PrincipalX = 0,
                PrincipalY = 0,
                NormalizationRadius = 100,
                K1 = 0.25
            };
            var line = new LineMeasureRoi
            {
                P1 = new PointD(100, 0),
                P2 = new PointD(200, 0)
            };

            double rawLength = line.P1.DistanceTo(line.P2);
            double endpointCorrection = calibration.CorrectPixelLength(line.P1, line.P2) / rawLength;
            double midpointCorrection = calibration.UndistortScaleFactor(new PointD(150, 0));

            Assert.Equal(endpointCorrection, RoiCalibrationHelper.GetLengthCorrection(line, calibration), 9);
            Assert.NotEqual(midpointCorrection, endpointCorrection);
        }

        [Fact]
        public void RoiCalibrationHelper_UsesJacobianDeterminantForAreaCorrection()
        {
            var calibration = new CameraCalibration
            {
                PrincipalX = 0,
                PrincipalY = 0,
                NormalizationRadius = 100,
                K1 = 0.25
            };
            var circle = new CircleRoi { Center = new PointD(150, 0), Radius = 10 };

            double areaCorrection = RoiCalibrationHelper.GetAreaCorrection(circle, calibration);
            double lengthSquareApproximation = Math.Pow(calibration.LocalLengthScaleFactor(circle.Center), 2);

            Assert.Equal(calibration.LocalAreaScaleFactor(circle.Center), areaCorrection, 9);
            Assert.NotEqual(lengthSquareApproximation, areaCorrection);
        }

        [Fact]
        public void RoiInfoService_BuildInfo_AppliesDistortionCorrectionAtRoiCenter()
        {
            var registry = RoiPluginRegistry.CreateBuiltIn();
            var calibration = CameraCalibration.CreateForImage(0.25, 0, 400, 200); // 主点 (200,100), R=100
            var circle = new CircleRoi { Center = new PointD(300, 100), Radius = 10 }; // dx=100 → r²=1

            string uncorrected = RoiInfoService.BuildInfo(circle, null, 0.01, "mm", registry, includeStatistics: false);
            string corrected = RoiInfoService.BuildInfo(circle, null, 0.01, "mm", registry, includeStatistics: false, calibration);

            // 无畸变: 10px * 0.01 = 0.10mm; 有畸变: 10px * 0.8 * 0.01 = 0.08mm
            Assert.Contains("0.10 mm", uncorrected);
            Assert.Contains("0.08 mm", corrected);
            Assert.DoesNotContain("0.10 mm", corrected);
        }

        [Fact]
        public void RoiInfoService_BuildInfo_WithoutCalibration_ShowsNoDistortionLine()
        {
            var registry = RoiPluginRegistry.CreateBuiltIn();
            var circle = new CircleRoi { Center = new PointD(100, 100), Radius = 5 };

            string info = RoiInfoService.BuildInfo(circle, null, 0.01, "mm", registry, includeStatistics: false);

            Assert.DoesNotContain("畸变校正", info);
        }
    }
}