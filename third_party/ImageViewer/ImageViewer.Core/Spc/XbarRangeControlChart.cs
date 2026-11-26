using System;
using System.Collections.Generic;

namespace ImageViewer.Core.Spc;

/// <summary>控制图类型。</summary>
public enum SpcControlChartKind
{
    /// <summary>子组均值图（Xbar）。</summary>
    Mean,

    /// <summary>子组极差图（R）。</summary>
    Range
}

/// <summary>判异规则（Western Electric 规则子集）。</summary>
public enum SpcControlChartRule
{
    /// <summary>点超出控制限（3σ）。</summary>
    BeyondControlLimit,

    /// <summary>连续 3 点中有 2 点落在同侧 2σ 之外。</summary>
    TwoOfThreeBeyondTwoSigma,

    /// <summary>连续 5 点中有 4 点落在同侧 1σ 之外。</summary>
    FourOfFiveBeyondOneSigma,

    /// <summary>连续 8 点位于中心线同一侧。</summary>
    EightInARowOnOneSide,

    /// <summary>连续 6 点单调上升或下降。</summary>
    SixInARowTrending
}

/// <summary>一条中心线/控制限。</summary>
public readonly record struct ControlChartLimits(double CenterLine, double UpperControlLimit, double LowerControlLimit);

/// <summary>一个子组的统计量；<see cref="Index"/> 是完整子组的序号（从 0 开始）。</summary>
public readonly record struct ControlChartPoint(int Index, double Mean, double Range);

/// <summary>判异记录：某个子组上触发的规则。</summary>
public readonly record struct ControlChartViolation(int PointIndex, SpcControlChartKind Chart, SpcControlChartRule Rule);

/// <summary>
/// Xbar-R 控制图。
/// Chinese: 只使用完整子组（末尾不足一组的样本被丢弃），子组大小支持 2..10；至少需要 2 个完整子组。
/// 极差图只判超出控制限；均值图依次判定 3σ 超限、2/3 点 2σ、4/5 点 1σ、8 点同侧、6 点趋势。
/// 同一子组只记录最严重的一条规则（按上述顺序），避免一个超限点同时被低阶规则重复标记。
/// English: Complete subgroups only, sizes 2..10, at least 2 subgroups. The range chart checks control limits only;
/// the mean chart applies Western Electric rules with one (highest severity) violation per point.
/// </summary>
public sealed class XbarRangeControlChart
{
    /// <summary>子组大小对应的 A2 常数（下标为子组大小）。</summary>
    private static readonly double[] A2 = [0d, 0d, 1.880d, 1.023d, 0.729d, 0.577d, 0.483d, 0.419d, 0.373d, 0.337d, 0.308d];

    /// <summary>子组大小对应的 D3 常数（下标为子组大小）。</summary>
    private static readonly double[] D3 = [0d, 0d, 0d, 0d, 0d, 0d, 0d, 0.076d, 0.136d, 0.184d, 0.223d];

    /// <summary>子组大小对应的 D4 常数（下标为子组大小）。</summary>
    private static readonly double[] D4 = [0d, 0d, 3.267d, 2.574d, 2.282d, 2.114d, 2.004d, 1.924d, 1.864d, 1.816d, 1.777d];

    private XbarRangeControlChart(
        int subgroupSize,
        ControlChartLimits meanLimits,
        ControlChartLimits rangeLimits,
        double grandMean,
        double meanRange,
        ControlChartPoint[] points,
        ControlChartViolation[] violations)
    {
        SubgroupSize = subgroupSize;
        MeanLimits = meanLimits;
        RangeLimits = rangeLimits;
        GrandMean = grandMean;
        MeanRange = meanRange;
        Points = points;
        Violations = violations;
    }

    /// <summary>子组大小。</summary>
    public int SubgroupSize { get; }

    /// <summary>完整子组的数量。</summary>
    public int SubgroupCount => Points.Count;

    /// <summary>均值图的中心线与控制限。</summary>
    public ControlChartLimits MeanLimits { get; }

    /// <summary>极差图的中心线与控制限。</summary>
    public ControlChartLimits RangeLimits { get; }

    /// <summary>子组均值的均值（Xbarbar）。</summary>
    public double GrandMean { get; }

    /// <summary>子组极差的均值（Rbar）。</summary>
    public double MeanRange { get; }

    /// <summary>各完整子组的均值与极差，顺序与输入一致。</summary>
    public IReadOnlyList<ControlChartPoint> Points { get; }

    /// <summary>判异记录，按子组顺序排列。</summary>
    public IReadOnlyList<ControlChartViolation> Violations { get; }

    /// <summary>是否存在任何判异。</summary>
    public bool HasViolations => Violations.Count > 0;

    /// <summary>
    /// Chinese: 由按顺序排列的个体测量值构造 Xbar-R 控制图。非有限值被跳过；不足一个完整子组的尾部样本被丢弃。
    /// English: Builds the chart from ordered individual values, skipping non-finite samples and dropping the incomplete tail subgroup.
    /// </summary>
    /// <param name="samples">按时间顺序排列的个体测量值。</param>
    /// <param name="subgroupSize">子组大小（2..10）。</param>
    public static XbarRangeControlChart Calculate(IReadOnlyList<double> samples, int subgroupSize)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (subgroupSize is < 2 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(subgroupSize), subgroupSize, "The subgroup size must be between 2 and 10.");
        }

        var values = new List<double>(samples.Count);
        foreach (double sample in samples)
        {
            if (double.IsFinite(sample))
            {
                values.Add(sample);
            }
        }

        int subgroupCount = values.Count / subgroupSize;
        if (subgroupCount < 2)
        {
            throw new ArgumentException("At least two complete subgroups are required for a control chart.", nameof(samples));
        }

        var points = new ControlChartPoint[subgroupCount];
        double meanSum = 0;
        double rangeSum = 0;
        for (int subgroup = 0; subgroup < subgroupCount; subgroup++)
        {
            int start = subgroup * subgroupSize;
            double minimum = values[start];
            double maximum = values[start];
            double sum = 0;
            for (int offset = 0; offset < subgroupSize; offset++)
            {
                double sample = values[start + offset];
                sum += sample;
                minimum = Math.Min(minimum, sample);
                maximum = Math.Max(maximum, sample);
            }

            double mean = sum / subgroupSize;
            double range = maximum - minimum;
            points[subgroup] = new ControlChartPoint(subgroup, mean, range);
            meanSum += mean;
            rangeSum += range;
        }

        double grandMean = meanSum / subgroupCount;
        double meanRange = rangeSum / subgroupCount;
        var meanLimits = new ControlChartLimits(
            grandMean,
            grandMean + A2[subgroupSize] * meanRange,
            grandMean - A2[subgroupSize] * meanRange);
        var rangeLimits = new ControlChartLimits(
            meanRange,
            D4[subgroupSize] * meanRange,
            D3[subgroupSize] * meanRange);

        var violations = new List<ControlChartViolation>();
        EvaluateMeanChart(points, meanLimits, violations);
        EvaluateRangeChart(points, rangeLimits, violations);
        violations.Sort(static (left, right) => left.PointIndex.CompareTo(right.PointIndex));
        return new XbarRangeControlChart(subgroupSize, meanLimits, rangeLimits, grandMean, meanRange, points, violations.ToArray());
    }

    private static void EvaluateMeanChart(ControlChartPoint[] points, ControlChartLimits limits, List<ControlChartViolation> violations)
    {
        double sigma = (limits.UpperControlLimit - limits.CenterLine) / 3;
        for (int index = 0; index < points.Length; index++)
        {
            double value = points[index].Mean;
            if (value > limits.UpperControlLimit || value < limits.LowerControlLimit)
            {
                violations.Add(new ControlChartViolation(index, SpcControlChartKind.Mean, SpcControlChartRule.BeyondControlLimit));
                continue;
            }

            if (sigma > 0 && MatchesTwoOfThree(points, index, limits.CenterLine, sigma))
            {
                violations.Add(new ControlChartViolation(index, SpcControlChartKind.Mean, SpcControlChartRule.TwoOfThreeBeyondTwoSigma));
                continue;
            }

            if (sigma > 0 && MatchesFourOfFive(points, index, limits.CenterLine, sigma))
            {
                violations.Add(new ControlChartViolation(index, SpcControlChartKind.Mean, SpcControlChartRule.FourOfFiveBeyondOneSigma));
                continue;
            }

            if (MatchesEightInARow(points, index, limits.CenterLine))
            {
                violations.Add(new ControlChartViolation(index, SpcControlChartKind.Mean, SpcControlChartRule.EightInARowOnOneSide));
                continue;
            }

            if (MatchesSixTrending(points, index))
            {
                violations.Add(new ControlChartViolation(index, SpcControlChartKind.Mean, SpcControlChartRule.SixInARowTrending));
            }
        }
    }

    private static void EvaluateRangeChart(ControlChartPoint[] points, ControlChartLimits limits, List<ControlChartViolation> violations)
    {
        for (int index = 0; index < points.Length; index++)
        {
            double range = points[index].Range;
            if (range > limits.UpperControlLimit || range < limits.LowerControlLimit)
            {
                violations.Add(new ControlChartViolation(index, SpcControlChartKind.Range, SpcControlChartRule.BeyondControlLimit));
            }
        }
    }

    private static bool MatchesTwoOfThree(ControlChartPoint[] points, int index, double centerLine, double sigma)
    {
        if (index < 2)
        {
            return false;
        }

        return CountBeyond(points, index - 2, index, centerLine, 2 * sigma) >= 2;
    }

    private static bool MatchesFourOfFive(ControlChartPoint[] points, int index, double centerLine, double sigma)
    {
        if (index < 4)
        {
            return false;
        }

        return CountBeyond(points, index - 4, index, centerLine, sigma) >= 4;
    }

    private static int CountBeyond(ControlChartPoint[] points, int start, int end, double centerLine, double distance)
    {
        int above = 0;
        int below = 0;
        for (int index = start; index <= end; index++)
        {
            double value = points[index].Mean;
            if (value > centerLine + distance)
            {
                above++;
            }
            else if (value < centerLine - distance)
            {
                below++;
            }
        }

        return Math.Max(above, below);
    }

    private static bool MatchesEightInARow(ControlChartPoint[] points, int index, double centerLine)
    {
        if (index < 7)
        {
            return false;
        }

        bool allAbove = true;
        bool allBelow = true;
        for (int cursor = index - 7; cursor <= index; cursor++)
        {
            double value = points[cursor].Mean;
            allAbove &= value > centerLine;
            allBelow &= value < centerLine;
        }

        return allAbove || allBelow;
    }

    private static bool MatchesSixTrending(ControlChartPoint[] points, int index)
    {
        if (index < 5)
        {
            return false;
        }

        bool increasing = true;
        bool decreasing = true;
        for (int cursor = index - 4; cursor <= index; cursor++)
        {
            double previous = points[cursor - 1].Mean;
            double current = points[cursor].Mean;
            increasing &= current > previous;
            decreasing &= current < previous;
        }

        return increasing || decreasing;
    }
}
