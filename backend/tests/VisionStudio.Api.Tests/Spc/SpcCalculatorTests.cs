using VisionStudio.Api.Spc;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 控制图数学：系数表、规则 1..4、分组方式的边界。纯函数测试，不依赖数据库。
/// </summary>
public sealed class SpcCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static SpcSeries Series(params double[] values)
    {
        var samples = values.Select((value, index) =>
            new SpcSamplePoint(Start.AddMinutes(index), value, $"rec-{index}", null)).ToArray();
        return new SpcSeries("caliper-width", samples, samples.Length, 0, 0, 0, false);
    }

    private static SpcSeries Records(params double[][] groups)
    {
        var samples = new List<SpcSamplePoint>();
        for (var group = 0; group < groups.Length; group++)
            foreach (var value in groups[group])
                samples.Add(new SpcSamplePoint(Start.AddMinutes(group), value, $"rec-{group}", $"SN-{group}"));
        return new SpcSeries("caliper-width", samples, groups.Length, 0, 0, 0, false);
    }

    private static SpcAnalysisOptions Individuals(double? lsl = null, double? usl = null)
        => new(SpcChartTypes.Individuals, 1, SpcGroupings.Sequential, lsl, usl);

    private static SpcAnalysisOptions Subgroups(string chartType, int size, string grouping = SpcGroupings.Sequential,
        double? lsl = null, double? usl = null)
        => new(chartType, size, grouping, lsl, usl);

    [Fact]
    public void IndividualsUseMovingRangeSigmaForLimitsAndCapability()
    {
        var analysis = SpcCalculator.Analyze(Series(2, 4, 6, 8, 10), Individuals(lsl: 0, usl: 12));

        Assert.Equal(SpcChartTypes.Individuals, analysis.ChartType);
        Assert.Equal(5, analysis.SubgroupCount);
        Assert.Equal(6, analysis.Mean!.Value, 9);
        Assert.Equal(6, analysis.PrimaryChart.CenterLine!.Value, 9);
        var sigma = 2 / 1.128;
        Assert.Equal(6 + 3 * sigma, analysis.PrimaryChart.UpperControlLimit!.Value, 4);
        Assert.Equal(6 - 3 * sigma, analysis.PrimaryChart.LowerControlLimit!.Value, 4);

        Assert.NotNull(analysis.SecondaryChart);
        Assert.Equal(4, analysis.SecondaryChart!.Points.Count);
        Assert.Equal(2, analysis.SecondaryChart.CenterLine!.Value, 9);
        Assert.Equal(3.267 * 2, analysis.SecondaryChart.UpperControlLimit!.Value, 4);
        Assert.Equal(0, analysis.SecondaryChart.LowerControlLimit!.Value, 9);

        Assert.NotNull(analysis.Capability);
        Assert.Equal(12 / (6 * sigma), analysis.Capability!.Cp!.Value, 4);
        Assert.Equal(6 / (3 * sigma), analysis.Capability.Cpk!.Value, 4);
        var overall = Math.Sqrt(10);
        Assert.Equal(12 / (6 * overall), analysis.Capability.Pp!.Value, 4);
        Assert.Equal(6 / (3 * overall), analysis.Capability.Ppk!.Value, 4);
        Assert.Equal(0, analysis.Capability.OutOfSpecificationCount);
    }

    [Fact]
    public void WesternElectricRulesReportEachPatternOnce()
    {
        var rule1 = SpcCalculator.EvaluateRules([0, 3.5, 0], 0, 1, runsRules: true);
        var first = Assert.Single(rule1);
        Assert.Equal("rule1", first.Rule);
        Assert.Equal(1, first.EndIndex);

        var rule2 = SpcCalculator.EvaluateRules([2.1, 0, 2.2], 0, 1, runsRules: true);
        Assert.Contains(rule2, violation => violation is { Rule: "rule2", StartIndex: 0, EndIndex: 2 });

        var rule3 = SpcCalculator.EvaluateRules([1.1, 1.2, 0, 1.3, 1.4], 0, 1, runsRules: true);
        Assert.Contains(rule3, violation => violation is { Rule: "rule3", StartIndex: 0, EndIndex: 4 });

        var rule4 = SpcCalculator.EvaluateRules([0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1], 0, 1, runsRules: true);
        var run = Assert.Single(rule4, violation => violation.Rule == "rule4");
        Assert.Equal(0, run.StartIndex);
        Assert.Equal(7, run.EndIndex);

        var limitOnly = SpcCalculator.EvaluateRules([3.5, 1.2, 0, 1.3, 1.4], 0, 1, runsRules: false);
        Assert.Single(limitOnly);
        Assert.Equal("rule1", limitOnly[0].Rule);
    }

    [Fact]
    public void XbarRMatchesStandardConstants()
    {
        var analysis = SpcCalculator.Analyze(Records([1, 2, 3], [2, 3, 4], [3, 4, 5]), Subgroups(SpcChartTypes.XbarR, 3));

        Assert.Equal(3, analysis.SubgroupCount);
        Assert.Equal(9, analysis.SamplesUsed);
        Assert.Equal(0, analysis.SamplesDropped);
        Assert.Equal(3, analysis.Mean!.Value, 9);
        Assert.Equal(3 + 1.023 * 2, analysis.PrimaryChart.UpperControlLimit!.Value, 4);
        Assert.Equal(3 - 1.023 * 2, analysis.PrimaryChart.LowerControlLimit!.Value, 4);
        Assert.Equal(2, analysis.SecondaryChart!.CenterLine!.Value, 9);
        Assert.Equal(2.574 * 2, analysis.SecondaryChart.UpperControlLimit!.Value, 4);
        Assert.Equal(0, analysis.SecondaryChart.LowerControlLimit!.Value, 9);
        Assert.Equal(3, analysis.SecondaryChart.Points.Count);
        Assert.Equal([2d, 2d, 2d], analysis.SecondaryChart.Points.Select(point => point.Value));
    }

    [Fact]
    public void XbarSUsesSampleSigmaAndC4()
    {
        var analysis = SpcCalculator.Analyze(Records([1, 2, 3], [2, 3, 4], [3, 4, 5]), Subgroups(SpcChartTypes.XbarS, 3));

        Assert.Equal(3 + 1.954 * 1, analysis.PrimaryChart.UpperControlLimit!.Value, 4);
        Assert.Equal(3 - 1.954 * 1, analysis.PrimaryChart.LowerControlLimit!.Value, 4);
        Assert.Equal("sigma", analysis.SecondaryChart!.Kind);
        Assert.Equal(1, analysis.SecondaryChart.CenterLine!.Value, 9);
        Assert.Equal(2.568, analysis.SecondaryChart.UpperControlLimit!.Value, 4);
        Assert.Equal(0, analysis.SecondaryChart.LowerControlLimit!.Value, 9);
        Assert.All(analysis.SecondaryChart.Points, point => Assert.Equal(1, point.Value, 9));
    }

    [Fact]
    public void SequentialGroupingDropsIncompleteTail()
    {
        var analysis = SpcCalculator.Analyze(Series(1, 2, 3, 4, 5, 6, 7, 8), Subgroups(SpcChartTypes.XbarR, 3));

        Assert.Equal(2, analysis.SubgroupCount);
        Assert.Equal(6, analysis.SamplesUsed);
        Assert.Equal(2, analysis.SamplesDropped);
        Assert.Contains(analysis.Warnings, warning => warning.Contains("末尾 2 个样本", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordGroupingSkipsRecordsWhoseValueCountDoesNotMatch()
    {
        var series = Records([1, 2, 3], [4, 5], [6, 7, 8]);
        var analysis = SpcCalculator.Analyze(series, Subgroups(SpcChartTypes.XbarR, 3, SpcGroupings.Record));

        Assert.Equal(2, analysis.SubgroupCount);
        Assert.Equal(2, analysis.RecordsUsed);
        Assert.Equal(1, analysis.RecordsIgnored);
        Assert.Equal(2, analysis.SamplesDropped);
        Assert.Contains(analysis.Warnings, warning => warning.Contains("不等于子组大小 3", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptySeriesReturnsEmptyChartAndCapability()
    {
        var analysis = SpcCalculator.Analyze(Series(), Individuals(lsl: 0, usl: 10));

        Assert.Empty(analysis.PrimaryChart.Points);
        Assert.Null(analysis.PrimaryChart.CenterLine);
        Assert.Null(analysis.SecondaryChart);
        Assert.Null(analysis.Mean);
        Assert.Null(analysis.Capability);
        Assert.Contains(analysis.Warnings, warning => warning.Contains("没有可用的数值样本", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantDataDoesNotDivideByZero()
    {
        var analysis = SpcCalculator.Analyze(Series(5, 5, 5, 5), Individuals(lsl: 0, usl: 10));

        Assert.Equal(5, analysis.PrimaryChart.UpperControlLimit!.Value, 9);
        Assert.Equal(5, analysis.PrimaryChart.LowerControlLimit!.Value, 9);
        Assert.Empty(analysis.PrimaryChart.Violations);
        Assert.Null(analysis.Capability!.Cp);
        Assert.Null(analysis.Capability.Cpk);
        Assert.Null(analysis.Capability.Pp);
        Assert.Null(analysis.Capability.Ppk);
        Assert.Contains(analysis.Warnings, warning => warning.Contains("过程波动估计为 0", StringComparison.Ordinal));
    }

    [Fact]
    public void CapabilityIsNullWithoutSpecificationLimits()
    {
        var analysis = SpcCalculator.Analyze(Series(2, 4, 6, 8, 10), Individuals());

        Assert.Null(analysis.Capability);
    }

    [Fact]
    public void OptionsResolveRejectsInvalidCombinations()
    {
        Assert.ThrowsAny<ArgumentException>(() => SpcAnalysisOptions.Resolve("individuals", 5, null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => SpcAnalysisOptions.Resolve("xbarR", 1, null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => SpcAnalysisOptions.Resolve(null, null, "batch", null, null));
        Assert.ThrowsAny<ArgumentException>(() => SpcAnalysisOptions.Resolve(null, 3, null, 10, 10));
        Assert.ThrowsAny<ArgumentException>(() => SpcAnalysisOptions.Resolve(null, 26, null, null, null));

        var resolved = SpcAnalysisOptions.Resolve(null, 5, null, null, null);
        Assert.Equal(SpcChartTypes.XbarR, resolved.ChartType);
        Assert.Equal(SpcChartTypes.XbarS, SpcAnalysisOptions.Resolve(null, 12, null, null, null).ChartType);
        Assert.Equal(SpcChartTypes.Individuals, SpcAnalysisOptions.Resolve(null, null, null, null, null).ChartType);
    }
}