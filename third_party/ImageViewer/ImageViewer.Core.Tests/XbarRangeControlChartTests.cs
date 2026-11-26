using System;
using System.Collections.Generic;
using ImageViewer.Core.Spc;
using Xunit;

namespace ImageViewer.Core.Tests;

[Trait("Category", "Unit")]
public sealed class XbarRangeControlChartTests
{
    [Fact]
    public void Calculate_DerivesMeanAndRangeLimitsFromSubgroups()
    {
        double[] samples = BuildSubgroups((11, 1), (12, 1), (11, 2), (13, 1));

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Equal(4, chart.SubgroupCount);
        Assert.Equal(11.75, chart.GrandMean, 10);
        Assert.Equal(2.5, chart.MeanRange, 10);
        Assert.Equal(11.75, chart.MeanLimits.CenterLine, 10);
        Assert.Equal(11.75 + 1.88 * 2.5, chart.MeanLimits.UpperControlLimit, 10);
        Assert.Equal(11.75 - 1.88 * 2.5, chart.MeanLimits.LowerControlLimit, 10);
        Assert.Equal(2.5, chart.RangeLimits.CenterLine, 10);
        Assert.Equal(3.267 * 2.5, chart.RangeLimits.UpperControlLimit, 10);
        Assert.Equal(0d, chart.RangeLimits.LowerControlLimit, 10);
        Assert.False(chart.HasViolations);
    }

    [Fact]
    public void Calculate_UsesA2ConstantForSubgroupSizeThree()
    {
        double[] samples = [10, 11, 12, 10, 12, 14, 8, 10, 12];

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 3);

        // 子组均值 11/12/10，极差 2/4/4 → Rbar = 10/3。
        Assert.Equal(11d, chart.GrandMean, 10);
        Assert.Equal(10d / 3, chart.MeanRange, 10);
        Assert.Equal(11 + 1.023 * (10d / 3), chart.MeanLimits.UpperControlLimit, 10);
        Assert.Equal(2.574 * (10d / 3), chart.RangeLimits.UpperControlLimit, 10);
    }

    [Fact]
    public void Calculate_DropsTheIncompleteTailSubgroup()
    {
        double[] samples = [1, 2, 3, 4, 5];

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Equal(2, chart.SubgroupCount);
    }

    [Fact]
    public void Calculate_SkipsNonFiniteSamples()
    {
        double[] samples = [1, 2, double.NaN, 3, 4];

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Equal(2, chart.SubgroupCount);
        Assert.Equal(2.5, chart.GrandMean, 10);
    }

    [Fact]
    public void Calculate_FlagsPointsBeyondTheMeanControlLimits()
    {
        double[] samples = BuildSubgroups((0, 0.5), (0, 0.5), (0, 0.5), (10, 0.5));

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Contains(new ControlChartViolation(3, SpcControlChartKind.Mean, SpcControlChartRule.BeyondControlLimit), chart.Violations);
        Assert.DoesNotContain(chart.Violations, violation => violation.Chart == SpcControlChartKind.Range);
    }

    [Fact]
    public void Calculate_FlagsTwoOfThreeBeyondTwoSigma()
    {
        double[] samples = BuildSubgroups((0, 0.5), (4, 0.5), (4, 0.5));

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Contains(new ControlChartViolation(2, SpcControlChartKind.Mean, SpcControlChartRule.TwoOfThreeBeyondTwoSigma), chart.Violations);
    }

    [Fact]
    public void Calculate_FlagsFourOfFiveBeyondOneSigma()
    {
        double[] samples = BuildSubgroups((0, 0.5), (0, 0.5), (0, 0.5), (0, 0.5), (0, 0.5), (2, 0.5), (2, 0.5), (2, 0.5), (2, 0.5), (2, 0.5));

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Contains(new ControlChartViolation(8, SpcControlChartKind.Mean, SpcControlChartRule.FourOfFiveBeyondOneSigma), chart.Violations);
    }

    [Fact]
    public void Calculate_FlagsEightInARowOnOneSide()
    {
        double[] samples = BuildSubgroups(
            (0, 0.5), (0, 0.5), (0, 0.5), (0, 0.5), (0, 0.5), (0, 0.5), (0, 0.5), (0, 0.5),
            (0.6267, 0.5), (0.6267, 0.5), (0.6267, 0.5), (0.6267, 0.5), (0.6267, 0.5), (0.6267, 0.5), (0.6267, 0.5), (0.6267, 0.5));

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        // 前 8 点在中心线下方、后 8 点在上方，两段各构成一次 8 点同侧。
        Assert.Equal(2, chart.Violations.Count);
        Assert.All(chart.Violations, violation => Assert.Equal(SpcControlChartRule.EightInARowOnOneSide, violation.Rule));
        Assert.Contains(new ControlChartViolation(7, SpcControlChartKind.Mean, SpcControlChartRule.EightInARowOnOneSide), chart.Violations);
        Assert.Contains(new ControlChartViolation(15, SpcControlChartKind.Mean, SpcControlChartRule.EightInARowOnOneSide), chart.Violations);
    }

    [Fact]
    public void Calculate_FlagsSixInARowTrending()
    {
        double[] samples = BuildSubgroups((0, 0.5), (0.5, 0.5), (1, 0.5), (1.5, 0.5), (2, 0.5), (2.5, 0.5));

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Contains(new ControlChartViolation(5, SpcControlChartKind.Mean, SpcControlChartRule.SixInARowTrending), chart.Violations);
    }

    [Fact]
    public void Calculate_FlagsRangePointsBeyondTheRangeControlLimit()
    {
        double[] samples = BuildSubgroups((0, 0.5), (0, 0.5), (0, 0.5), (105, 10));

        XbarRangeControlChart chart = XbarRangeControlChart.Calculate(samples, 2);

        Assert.Contains(new ControlChartViolation(3, SpcControlChartKind.Range, SpcControlChartRule.BeyondControlLimit), chart.Violations);
        Assert.Single(chart.Violations, violation => violation.Chart == SpcControlChartKind.Range);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void Calculate_RejectsSubgroupSizesOutsideTheConstantTable(int subgroupSize)
    {
        double[] samples = BuildSubgroups((1, 0.5), (2, 0.5));

        Assert.Throws<ArgumentOutOfRangeException>(() => XbarRangeControlChart.Calculate(samples, subgroupSize));
    }

    [Fact]
    public void Calculate_RequiresTwoCompleteSubgroups()
    {
        double[] samples = [1, 2, 3];

        Assert.Throws<ArgumentException>(() => XbarRangeControlChart.Calculate(samples, 2));
        Assert.Throws<ArgumentNullException>(() => XbarRangeControlChart.Calculate(null!, 2));
    }

    private static double[] BuildSubgroups(params (double Mean, double HalfRange)[] subgroups)
    {
        var samples = new List<double>(subgroups.Length * 2);
        foreach ((double mean, double halfRange) in subgroups)
        {
            samples.Add(mean - halfRange);
            samples.Add(mean + halfRange);
        }

        return samples.ToArray();
    }
}
