using System;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Guards that a non-converged distortion inversion is carried into the report model and
    /// the readiness verdict instead of staying hidden inside the calibration object.
    /// </summary>
    public class DistortionAuditTests
    {
        [Fact]
        public void Audit_ConvergedPoints_ReportsCalibrationUncertaintyOnly()
        {
            CameraCalibration calibration = CreateWellConditionedCalibration();
            calibration.MeasurementUncertaintyPixels = 0.2;

            DistortionAudit audit = CameraCalibrationAudit.Audit(
                calibration,
                [new PointD(120, 400), new PointD(410, 285)]);

            Assert.Equal(2, audit.PointCount);
            Assert.False(audit.HasNonConvergedPoints);
            Assert.InRange(audit.MaximumResidualPixels, 0, 1e-6);
            Assert.Equal(0.2, audit.MaximumUncertaintyPixels, 9);
        }

        [Fact]
        public void Audit_NonConvergedPoint_CountsItAndInflatesUncertaintyToResidual()
        {
            // At this radius the folded radial model does not converge within the solver budget.
            CameraCalibration calibration = CreateFoldedCalibration();
            calibration.MeasurementUncertaintyPixels = 0.05;

            DistortionAudit audit = CameraCalibrationAudit.Audit(calibration, [new PointD(100, 0)]);

            Assert.Equal(1, audit.PointCount);
            Assert.Equal(1, audit.NonConvergedPointCount);
            Assert.True(audit.HasNonConvergedPoints);
            Assert.True(double.IsFinite(audit.MaximumResidualPixels));
            Assert.True(audit.MaximumResidualPixels > 0);
            Assert.Equal(audit.MaximumResidualPixels, audit.MaximumUncertaintyPixels, 9);
        }

        [Fact]
        public void Audit_WithoutCalibrationOrDistortion_ReturnsEmpty()
        {
            Assert.Equal(DistortionAudit.Empty, CameraCalibrationAudit.Audit(null, [new PointD(10, 10)]));

            var undistorted = new CameraCalibration { NormalizationRadius = 100 };
            DistortionAudit audit = CameraCalibrationAudit.Audit(undistorted, [new PointD(50, 0)]);

            Assert.False(audit.HasPoints);
            Assert.False(audit.HasNonConvergedPoints);
        }

        [Fact]
        public void Combine_SumsCoverageAndKeepsWorstCase()
        {
            var first = new DistortionAudit(2, 1, 0.5, 0.5);
            var second = new DistortionAudit(3, 0, 0.1, 0.2);

            DistortionAudit combined = first.Combine(second);

            Assert.Equal(5, combined.PointCount);
            Assert.Equal(1, combined.NonConvergedPointCount);
            Assert.Equal(0.5, combined.MaximumResidualPixels, 9);
            Assert.Equal(0.5, combined.MaximumUncertaintyPixels, 9);
        }

        [Fact]
        public void Readiness_WarnsWhenAnyMeasurementPointFailedUndistortion()
        {
            var calibration = CreateFoldedCalibration();
            DistortionAudit audit = CameraCalibrationAudit.Audit(calibration, [new PointD(100, 0)]);

            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: calibration,
                roiCount: 1,
                tolerancedRoiCount: 1,
                distortionAudit: audit);

            MeasurementReadinessCheck check = Assert.Single(
                readiness.Checks,
                candidate => candidate.Code == MeasurementWorkflowReadinessEvaluator.CodeCalibrationUndistortionNotConverged);
            Assert.Equal(MeasurementReadinessLevel.Warning, check.Level);
            Assert.Contains("未收敛", check.Message);
            Assert.False(readiness.IsBlocked);
        }

        [Fact]
        public void ReportCarriesAuditAndMarkdownExplainsInflatedUncertainty()
        {
            var calibration = CreateFoldedCalibration();
            DistortionAudit audit = CameraCalibrationAudit.Audit(calibration, [new PointD(100, 0)]);
            var item = new MeasurementReportItem("孔径", "长度", "mm", null, 2.5, 0.1, 0.1, "Passed", "NotEvaluable");

            MeasurementReport report = MeasurementReportBuilder.Build(
                [item],
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: calibration,
                distortionAudit: audit);
            string markdown = MeasurementReportRenderer.RenderMarkdown(report);

            Assert.NotNull(report.DistortionAudit);
            Assert.True(report.DistortionAudit.HasNonConvergedPoints);
            Assert.Contains("去畸变反解未收敛", markdown);
            Assert.Contains("逐点去畸变不确定度（最大）", markdown);
            Assert.Contains("相关尺寸和公差判定不可评估", markdown);
            Assert.False(report.Readiness.CanProduceSpecificationVerdict);
        }

        [Fact]
        public void ReportWithConvergedAuditDoesNotWarn()
        {
            CameraCalibration calibration = CreateWellConditionedCalibration();
            calibration.MeasurementUncertaintyPixels = 0.2;
            DistortionAudit audit = CameraCalibrationAudit.Audit(calibration, [new PointD(410, 285)]);
            var item = new MeasurementReportItem("孔径", "长度", "mm", 2.5, 2.5, 0.1, 0.1, "Passed", "Passed");

            MeasurementReport report = MeasurementReportBuilder.Build(
                [item],
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: calibration,
                distortionAudit: audit);
            string markdown = MeasurementReportRenderer.RenderMarkdown(report);

            Assert.DoesNotContain("去畸变反解未收敛", markdown);
            Assert.DoesNotContain("逐点去畸变不确定度（最大）", markdown);
            Assert.Contains("测量不确定度: 0.2 px", markdown);
        }

        private static CameraCalibration CreateWellConditionedCalibration() => new()
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

        private static CameraCalibration CreateFoldedCalibration() => new()
        {
            PrincipalX = 0,
            PrincipalY = 0,
            NormalizationRadius = 100,
            K1 = -1.0
        };
    }
}
