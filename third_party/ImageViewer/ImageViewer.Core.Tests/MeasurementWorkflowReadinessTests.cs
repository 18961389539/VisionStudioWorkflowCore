using System;
using System.Linq;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// 离线工作流前置检查与报告的守卫测试。
    /// Chinese: 这些断言锁住的是"能不能给出判定"的语义，而不是具体文案——
    /// 文案会翻译会润色，但"未标定 ⇒ 阻断"、"无公差 ⇒ 不能判定"必须稳定。
    /// </summary>
    [Trait("Category", "Unit")]
    public sealed class MeasurementWorkflowReadinessTests
    {
        private static CameraCalibration CreateValidCalibration(double uncertaintyPixels = 0.2, double reprojectionRms = 0.08)
        {
            return new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                MeasurementUncertaintyPixels = uncertaintyPixels,
                ReprojectionErrorRms = reprojectionRms,
                CalibrationId = "CERT-X"
            };
        }

        [Fact]
        public void UncalibratedScaleBlocksAnyDimensionalVerdict()
        {
            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0,
                physicalUnit: null,
                calibration: null,
                roiCount: 1,
                tolerancedRoiCount: 1);

            // 没有比例标定 ⇒ 数值永远是像素，绝不能给尺寸判定。
            Assert.True(readiness.IsBlocked);
            Assert.False(readiness.CanProducePhysicalResults);
            Assert.False(readiness.CanProduceSpecificationVerdict);
            Assert.Contains(readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeCalibrationScaleUnavailable);
        }

        [Fact]
        public void MalformedCalibrationBlocksEvenWhenScaleIsPresent()
        {
            var malformed = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                ReprojectionErrorRms = double.NaN
            };

            Assert.True(malformed.IsEnabled);
            Assert.False(malformed.IsValid);

            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: malformed,
                roiCount: 2,
                tolerancedRoiCount: 2);

            // 比例是好的、ROI 也齐，但畸变系数非法 ⇒ 仍然阻断（否则会静默按恒等处理给出错值）。
            Assert.True(readiness.IsBlocked);
            Assert.Contains(readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeCalibrationInvalid);
        }

        [Fact]
        public void ScaleWithoutToleranceAllowsValuesButNotVerdicts()
        {
            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: null,
                roiCount: 3,
                tolerancedRoiCount: 0);

            Assert.False(readiness.IsBlocked);
            Assert.True(readiness.CanProducePhysicalResults);
            Assert.False(readiness.CanProduceSpecificationVerdict);
            Assert.Contains(readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeNoTolerance);
        }

        [Fact]
        public void FullyConfiguredWorkflowAllowsVerdicts()
        {
            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: CreateValidCalibration(),
                roiCount: 3,
                tolerancedRoiCount: 3,
                unmeasuredTolerancedRoiCount: 0,
                hasKnownReference: true);

            Assert.False(readiness.IsBlocked);
            Assert.True(readiness.CanProducePhysicalResults);
            Assert.True(readiness.CanProduceSpecificationVerdict);
            Assert.Equal(MeasurementReadinessLevel.Passed, readiness.HighestLevel);
        }

        [Fact]
        public void ExtendedDistortionWithoutAccuracyMetadataWarns()
        {
            var extended = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                K3 = 0.001,
                CalibrationId = "CERT-Y"
            };

            Assert.True(extended.HasExtendedDistortionModel);

            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: extended,
                roiCount: 1,
                tolerancedRoiCount: 1,
                hasKnownReference: true);

            // 用了高阶模型却说不清自己的精度 ⇒ 警告（不是阻断：数值仍可用，但无法解释残差）。
            Assert.False(readiness.IsBlocked);
            Assert.True(readiness.HasWarnings);
            Assert.Contains(readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeCalibrationAccuracyMissing);
        }

        [Fact]
        public void CalibrationWithoutIdentityWarnsButStillAllowsVerdicts()
        {
            var anonymous = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                MeasurementUncertaintyPixels = 0.2
            };

            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: anonymous,
                roiCount: 1,
                tolerancedRoiCount: 1,
                hasKnownReference: true);

            Assert.False(readiness.IsBlocked);
            Assert.True(readiness.CanProduceSpecificationVerdict);
            Assert.Contains(readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeCalibrationIdentityMissing);
        }

        [Fact]
        public void UnmeasuredTolerancedRoisMakeVerdictImpossible()
        {
            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: CreateValidCalibration(),
                roiCount: 2,
                tolerancedRoiCount: 2,
                unmeasuredTolerancedRoiCount: 2,
                hasKnownReference: true);

            // 全都没测到值 ⇒ 一个判定也给不出来，必须显式说明而不是报"0/0 通过"。
            Assert.True(readiness.HasWarnings);
            Assert.False(readiness.CanProduceSpecificationVerdict);
            Assert.Contains(readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeRoiNotMeasured);
        }

        [Fact]
        public void EmptyRoiSetBlocks()
        {
            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: CreateValidCalibration(),
                roiCount: 0,
                tolerancedRoiCount: 0);

            Assert.True(readiness.IsBlocked);
            Assert.Contains(readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeNoRois);
        }

        [Fact]
        public void BlockingChecksAreSurfacedSeparatelyFromWarnings()
        {
            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0,
                physicalUnit: null,
                calibration: null,
                roiCount: 2,
                tolerancedRoiCount: 1,
                unmeasuredTolerancedRoiCount: 1);

            Assert.Single(readiness.BlockingChecks);
            Assert.Equal(MeasurementWorkflowReadinessEvaluator.CodeCalibrationScaleUnavailable, readiness.BlockingChecks[0].Code);
            Assert.Equal(MeasurementReadinessLevel.Blocked, readiness.HighestLevel);
        }

        [Fact]
        public void NegativeCountsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 1, physicalUnit: "mm", calibration: null, roiCount: -1, tolerancedRoiCount: 0));
        }
    }

    [Trait("Category", "Unit")]
    public sealed class MeasurementReportTests
    {
        private static MeasurementReportItem Item(
            string label,
            double? measured,
            double? nominal = null,
            string specificationStatus = "NotConfigured",
            string detectionStatus = "NotApplicable")
        {
            return new MeasurementReportItem(
                label,
                "长度",
                "mm",
                measured,
                nominal,
                nominal.HasValue ? 0.1 : null,
                nominal.HasValue ? 0.1 : null,
                detectionStatus,
                specificationStatus);
        }

        [Fact]
        public void ReportCountsVerdictsSeparatelyFromUnmeasuredItems()
        {
            MeasurementReport report = MeasurementReportBuilder.Build(
                [
                    Item("A", 10.0, 10.0, "Passed"),
                    Item("B", 10.5, 10.0, "Failed"),
                    Item("C", null, 10.0, "NotEvaluable", "NotMeasured"),
                    Item("D", 5.0)
                ],
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: null);

            Assert.Equal(4, report.ItemCount);
            Assert.Equal(1, report.PassedCount);
            Assert.Equal(1, report.FailedCount);
            // C 与 D 都没有判定：C 是缺测得值，D 是没配公差——两者都不能算通过。
            Assert.Equal(2, report.NotEvaluableCount);
            Assert.Equal("Failed", report.OverallVerdict);
            Assert.True(report.HasFailure);
        }

        [Fact]
        public void UncalibratedReportShowsExplicitUncalibratedState()
        {
            MeasurementReport report = MeasurementReportBuilder.Build(
                [Item("A", 120.0)],
                pixelSize: 0,
                physicalUnit: null,
                calibration: null);

            // 未标定不能伪装成 1 px = 1 px；必须是显式状态。
            Assert.False(report.Scale.IsCalibrated);
            Assert.Equal("Blocked", report.OverallVerdict);
            Assert.Contains("未标定", report.Scale.Describe());
        }

        [Fact]
        public void RenderMarkdownCarriesVerdictReadinessAndCalibrationIdentity()
        {
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                MeasurementUncertaintyPixels = 0.2,
                ReprojectionErrorRms = 0.08,
                CalibrationId = "CERT-42",
                CalibratedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
            };

            MeasurementReport report = MeasurementReportBuilder.Build(
                [Item("孔径", 10.02, 10.0, "Passed")],
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: calibration,
                sourceName: "part-001.png",
                sourceSha256: "abcdef0123456789deadbeef",
                hasKnownReference: true);

            string markdown = MeasurementReportRenderer.RenderMarkdown(report);

            Assert.Contains("# 测量报告 – part-001.png", markdown);
            Assert.Contains("abcdef012345", markdown);
            Assert.Contains("CERT-42", markdown);
            Assert.Contains("1 px = 0.05 mm", markdown);
            Assert.Contains("测量不确定度: 0.2 px", markdown);
            // 0.2 px × 0.05 mm/px = 0.01 mm，必须换算到物理单位才对用户有意义。
            Assert.Contains("0.01 mm", markdown);
            Assert.Contains("| 孔径 |", markdown);
            Assert.Contains("通过", markdown);
            Assert.DoesNotContain("不可信", markdown);
        }

        [Fact]
        public void RenderMarkdownFlagsBlockedWorkflow()
        {
            MeasurementReport report = MeasurementReportBuilder.Build(
                [Item("孔径", 120.0, nominal: null)],
                pixelSize: 0,
                physicalUnit: null,
                calibration: null);

            string markdown = MeasurementReportRenderer.RenderMarkdown(report);

            Assert.Contains("已阻断", markdown);
            Assert.Contains("阻断", markdown);
            Assert.Contains("未设置像素标定比例", markdown);
        }

        [Fact]
        public void RenderMarkdownEscapesPipeCharactersInLabels()
        {
            MeasurementReport report = MeasurementReportBuilder.Build(
                [Item("a|b", 1.0)],
                pixelSize: 1,
                physicalUnit: "mm",
                calibration: null);

            string markdown = MeasurementReportRenderer.RenderMarkdown(report);

            // 未转义的竖线会撕开 Markdown 表格，必须转义。
            Assert.Contains("a\\|b", markdown);
        }

        [Fact]
        public void ReportWithoutToleranceSaysSoInsteadOfClaimingPass()
        {
            MeasurementReport report = MeasurementReportBuilder.Build(
                [Item("A", 10.0)],
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: null);

            Assert.Equal(0, report.PassedCount);
            Assert.Equal("NoVerdict", report.OverallVerdict);
            Assert.Contains(report.Readiness.Checks, check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeNoTolerance);
        }
    }
}
