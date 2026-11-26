using ImageViewer.Core.Analysis;
using Xunit;

namespace ImageViewer.Core.Tests;

/// <summary>
/// ①-2 守卫：精度验证闭环的数值契约。
/// Chinese: 支持亚像素并不等于测准；重复性（稳定）与偏差（准）必须分别量化，且不能互相冒充。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MeasurementPrecisionAnalyzerTests
{
    [Fact]
    public void ComputeRepeatability_UsesSampleStandardDeviation()
    {
        // 样本 σ（n-1 分母）= sqrt(2/2) = 1；总体 σ（n 分母）会是 sqrt(2/3) ≈ 0.8165。
        RepeatabilityStatistics stats = MeasurementPrecisionAnalyzer.ComputeRepeatability([10, 11, 12]);

        Assert.Equal(3, stats.SampleCount);
        Assert.Equal(11, stats.Mean, 10);
        Assert.Equal(1d, stats.StandardDeviation, 10);
        Assert.NotEqual(Math.Sqrt(2d / 3d), stats.StandardDeviation, 6);
        Assert.Equal(10, stats.Minimum);
        Assert.Equal(12, stats.Maximum);
        Assert.Equal(2, stats.Range);
    }

    [Fact]
    public void ComputeRepeatability_KeepsZeroAndNegativeValues()
    {
        // 测量结果可以是 0 或负值（如位置度偏差）；不得像卡尺样本那样被过滤掉。
        RepeatabilityStatistics stats = MeasurementPrecisionAnalyzer.ComputeRepeatability([-1, 0, 1]);

        Assert.Equal(3, stats.SampleCount);
        Assert.Equal(0, stats.Mean, 10);
        Assert.Equal(1, stats.StandardDeviation, 10);
    }

    [Fact]
    public void ComputeRepeatability_IgnoresNonFiniteValues()
    {
        RepeatabilityStatistics stats = MeasurementPrecisionAnalyzer.ComputeRepeatability([10, double.NaN, 12, double.PositiveInfinity]);

        Assert.Equal(2, stats.SampleCount);
        Assert.Equal(11, stats.Mean, 10);
    }

    [Fact]
    public void ComputeRepeatability_EmptyInputThrows()
    {
        Assert.Throws<ArgumentException>(() => MeasurementPrecisionAnalyzer.ComputeRepeatability([]));
        Assert.Throws<ArgumentException>(() => MeasurementPrecisionAnalyzer.ComputeRepeatability([double.NaN]));
    }

    [Fact]
    public void SingleSample_HasNoDispersionEstimates()
    {
        // 单次测量无从谈重复性：返回 null 而不是 0，否则 0 会被读成"完美重复"。
        RepeatabilityStatistics stats = MeasurementPrecisionAnalyzer.ComputeRepeatability([10]);

        Assert.Equal(1, stats.SampleCount);
        Assert.Null(stats.StandardError);
        Assert.Null(stats.ExpandedUncertainty);
        Assert.Null(stats.CoefficientOfVariationPercent);
        Assert.Equal(0, stats.Range);
    }

    [Fact]
    public void RepeatedSamples_ExposeUncertaintyEstimates()
    {
        RepeatabilityStatistics stats = MeasurementPrecisionAnalyzer.ComputeRepeatability([10, 10, 10, 10]);

        Assert.Equal(0, stats.StandardDeviation, 12);
        Assert.Equal(0, stats.ExpandedUncertainty!.Value, 12);
    }

    [Fact]
    public void CoefficientOfVariation_UndefinedNearZeroMean()
    {
        RepeatabilityStatistics stats = MeasurementPrecisionAnalyzer.ComputeRepeatability([-1e-13, 1e-13]);

        Assert.Null(stats.CoefficientOfVariationPercent);
    }

    [Fact]
    public void CompareToReference_ComputesSignedAndAbsoluteDeviation()
    {
        AccuracyComparison comparison = MeasurementPrecisionAnalyzer.CompareToReference(10.2, 10.0, toleranceLimit: 0.5);

        Assert.Equal(0.2, comparison.Deviation, 10);
        Assert.Equal(0.2, comparison.AbsoluteDeviation, 10);
        Assert.Equal(2.0, comparison.RelativeErrorPercent!.Value, 10);
        Assert.True(comparison.IsWithinTolerance);
        Assert.True(comparison.HasVerdict);
    }

    [Fact]
    public void CompareToReference_OutsideToleranceFails()
    {
        AccuracyComparison comparison = MeasurementPrecisionAnalyzer.CompareToReference(9.4, 10.0, toleranceLimit: 0.5);

        Assert.Equal(-0.6, comparison.Deviation, 10);
        Assert.False(comparison.IsWithinTolerance);
    }

    [Fact]
    public void CompareToReference_WithoutToleranceHasNoVerdict()
    {
        // 未给容差时不能把 IsWithinTolerance=false 当作"不合格"——那只是"没有判定依据"。
        AccuracyComparison comparison = MeasurementPrecisionAnalyzer.CompareToReference(10.2, 10.0);

        Assert.False(comparison.HasVerdict);
        Assert.False(comparison.IsWithinTolerance);
    }

    [Fact]
    public void CompareToReference_RejectsNonFiniteInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementPrecisionAnalyzer.CompareToReference(double.NaN, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementPrecisionAnalyzer.CompareToReference(10, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementPrecisionAnalyzer.CompareToReference(10, 10, toleranceLimit: -1));
    }

    [Fact]
    public void BuildReport_CombinesRepeatabilityAccuracyAndCalibrationUncertainty()
    {
        MeasurementPrecisionReport report = MeasurementPrecisionAnalyzer.BuildReport(
            [10.0, 10.1, 9.9, 10.05, 9.95],
            referenceValue: 10.0,
            toleranceLimit: 0.2,
            pixelSize: 0.5,
            unit: "mm",
            label: "孔径",
            calibrationUncertaintyPixels: 0.2,
            reprojectionErrorRms: 0.08);

        Assert.Equal("孔径", report.Label);
        Assert.Equal("mm", report.Unit);
        Assert.Equal(5, report.Repeatability.SampleCount);
        Assert.Equal(10.0, report.Repeatability.Mean, 10);
        Assert.NotNull(report.Accuracy);
        Assert.Equal(0.0, report.Accuracy!.Deviation, 10);
        Assert.True(report.Accuracy.IsWithinTolerance);

        // 标定不确定度 0.2 px × 0.5 mm/px = 0.1 mm
        Assert.Equal(0.2, report.CalibrationUncertaintyPixels!.Value, 10);
        Assert.Equal(0.1, report.CalibrationUncertaintyPhysical!.Value, 10);
        Assert.Equal(0.08, report.ReprojectionErrorRms!.Value, 10);
    }

    [Fact]
    public void CombinedUncertainty_IsRootSumSquareNotSimpleSum()
    {
        MeasurementPrecisionReport report = MeasurementPrecisionAnalyzer.BuildReport(
            [10.0, 10.02, 9.98],
            pixelSize: 1.0,
            unit: "mm",
            calibrationUncertaintyPixels: 0.3);

        double repeatExpanded = report.Repeatability.ExpandedUncertainty!.Value;
        double expected = Math.Sqrt((repeatExpanded * repeatExpanded) + (0.3 * 0.3));

        Assert.Equal(expected, report.CombinedUncertainty!.Value, 10);
        Assert.NotEqual(repeatExpanded + 0.3, report.CombinedUncertainty.Value, 10);
    }

    [Fact]
    public void CombinedUncertainty_IsNullWhenNoSourceAvailable()
    {
        MeasurementPrecisionReport report = MeasurementPrecisionAnalyzer.BuildReport([10], pixelSize: 1.0, unit: "mm");

        Assert.Null(report.CombinedUncertainty);
        Assert.Null(report.DeviationExceedsUncertainty);
    }

    [Fact]
    public void DeviationExceedsUncertainty_FlagsSystematicError()
    {
        // 重复性极好（σ≈0）但均值偏离 0.5 mm —— 这是系统误差，不是噪声，必须能被识别。
        MeasurementPrecisionReport report = MeasurementPrecisionAnalyzer.BuildReport(
            [10.5, 10.5, 10.5, 10.5],
            referenceValue: 10.0,
            pixelSize: 1.0,
            unit: "mm",
            calibrationUncertaintyPixels: 0.05);

        Assert.Equal(0.5, report.Accuracy!.AbsoluteDeviation, 10);
        Assert.True(report.DeviationExceedsUncertainty);
    }

    [Fact]
    public void DeviationWithinUncertainty_IsNotFlaggedAsSystematic()
    {
        MeasurementPrecisionReport report = MeasurementPrecisionAnalyzer.BuildReport(
            [10.01, 10.03, 9.99, 10.02],
            referenceValue: 10.0,
            pixelSize: 1.0,
            unit: "mm",
            calibrationUncertaintyPixels: 0.5);

        Assert.False(report.DeviationExceedsUncertainty);
    }

    [Fact]
    public void BuildReport_WithoutReferenceHasNoAccuracySection()
    {
        MeasurementPrecisionReport report = MeasurementPrecisionAnalyzer.BuildReport([10, 11], pixelSize: 1.0, unit: "mm");

        Assert.Null(report.Accuracy);
        Assert.Equal(2, report.Repeatability.SampleCount);
    }

    [Fact]
    public void BuildReport_RejectsInvalidPixelSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementPrecisionAnalyzer.BuildReport([10], pixelSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementPrecisionAnalyzer.BuildReport([10], pixelSize: double.NaN));
    }

    [Fact]
    public void GroupRepeatability_AggregatesPerKey()
    {
        IReadOnlyDictionary<string, RepeatabilityStatistics> groups = MeasurementPrecisionAnalyzer.GroupRepeatability(
        [
            ("孔A", 10.0),
            ("孔A", 10.1),
            ("孔A", 9.9),
            ("孔B", 20.0),
            ("孔B", 20.2)
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups["孔A"].SampleCount);
        Assert.Equal(10.0, groups["孔A"].Mean, 10);
        Assert.Equal(2, groups["孔B"].SampleCount);
        Assert.Equal(20.1, groups["孔B"].Mean, 10);
    }

    [Fact]
    public void GroupRepeatability_SkipsNonFiniteValues()
    {
        IReadOnlyDictionary<string, RepeatabilityStatistics> groups = MeasurementPrecisionAnalyzer.GroupRepeatability(
        [
            ("孔A", 10.0),
            ("孔A", double.NaN),
            ("孔A", 10.2)
        ]);

        Assert.Equal(2, groups["孔A"].SampleCount);
        Assert.Equal(10.1, groups["孔A"].Mean, 10);
    }
}
