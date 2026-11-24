using System.Globalization;

namespace VisionStudio.Api.Spc;

/// <summary>
/// 控制图计算（2026-11-23）。纯函数，不碰数据库，便于单测。
///
/// 支持三种常规图型：I-MR（子组大小 1）、Xbar-R（2..10 常用）、Xbar-S。
/// 控制限使用常规系数（d2/D3/D4/A2/A3/B3/B4/c4），规则为 Western Electric 1..4；
/// 变差图（MR/R/S）只应用规则 1——在极差图上看"连续 8 点同侧"没有过程含义。
/// </summary>
public static class SpcCalculator
{
    // 下标即子组大小；0/1 位不使用。数值取自 ASTM/SQC 常规控制图系数表。
    private static readonly double[] D2 =
    [
        0, 0, 1.128, 1.693, 2.059, 2.326, 2.534, 2.704, 2.847, 2.970, 3.078,
        3.173, 3.258, 3.336, 3.407, 3.472, 3.532, 3.588, 3.640, 3.689, 3.735,
        3.778, 3.819, 3.858, 3.895, 3.931
    ];

    private static readonly double[] C4 =
    [
        0, 0, 0.7979, 0.8862, 0.9213, 0.9400, 0.9515, 0.9594, 0.9650, 0.9693, 0.9727,
        0.9754, 0.9776, 0.9794, 0.9810, 0.9823, 0.9835, 0.9845, 0.9854, 0.9862, 0.9869,
        0.9876, 0.9882, 0.9887, 0.9892, 0.9896
    ];

    private static readonly double[] A2 =
    [
        0, 0, 1.880, 1.023, 0.729, 0.577, 0.483, 0.419, 0.373, 0.337, 0.308,
        0.285, 0.266, 0.249, 0.235, 0.223, 0.212, 0.203, 0.194, 0.187, 0.180,
        0.173, 0.167, 0.162, 0.157, 0.153
    ];

    private static readonly double[] A3 =
    [
        0, 0, 2.659, 1.954, 1.628, 1.427, 1.287, 1.182, 1.099, 1.032, 0.975,
        0.927, 0.886, 0.850, 0.817, 0.789, 0.763, 0.739, 0.718, 0.698, 0.680,
        0.663, 0.647, 0.633, 0.619, 0.606
    ];

    private static readonly double[] B3 =
    [
        0, 0, 0, 0, 0, 0, 0.030, 0.118, 0.185, 0.239, 0.284,
        0.321, 0.354, 0.382, 0.406, 0.428, 0.448, 0.466, 0.482, 0.497, 0.510,
        0.523, 0.534, 0.545, 0.555, 0.565
    ];

    private static readonly double[] B4 =
    [
        0, 0, 3.267, 2.568, 2.266, 2.089, 1.970, 1.882, 1.815, 1.761, 1.716,
        1.679, 1.646, 1.618, 1.594, 1.572, 1.552, 1.534, 1.518, 1.503, 1.490,
        1.477, 1.466, 1.455, 1.445, 1.435
    ];

    private static readonly double[] D3 =
    [
        0, 0, 0, 0, 0, 0, 0, 0.076, 0.136, 0.184, 0.223,
        0.256, 0.283, 0.307, 0.328, 0.347, 0.363, 0.378, 0.391, 0.403, 0.415,
        0.425, 0.434, 0.443, 0.451, 0.459
    ];

    private static readonly double[] D4 =
    [
        0, 0, 3.267, 2.574, 2.282, 2.114, 2.004, 1.924, 1.864, 1.816, 1.777,
        1.744, 1.717, 1.693, 1.672, 1.653, 1.637, 1.622, 1.608, 1.597, 1.585,
        1.575, 1.566, 1.557, 1.548, 1.541
    ];

    /// <summary>可靠控制限的经验下限：点太少时只给临时限，并明确告警。</summary>
    private const int RecommendedPoints = 20;

    public static SpcAnalysis Analyze(SpcSeries series, SpcAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(options);

        var warnings = new List<string>();
        if (series.Truncated)
            warnings.Add("记录数或样本数超过读取上限，仅分析已读取的部分；请缩小时间范围。");
        if (series.UnsupportedRecords > 0)
            warnings.Add($"已跳过 {series.UnsupportedRecords} 条非数值记录（仅支持 Double / Integer / NumberList）。");
        if (series.EmptyRecords > 0)
            warnings.Add($"已跳过 {series.EmptyRecords} 条空数值列表记录。");
        if (series.NonFiniteSamples > 0)
            warnings.Add($"已丢弃 {series.NonFiniteSamples} 个非有限数值（NaN / Infinity）。");

        if (series.Samples.Count == 0)
        {
            warnings.Add("所选时间范围内没有可用的数值样本。");
            var emptyChart = BuildChart(ChartKind(options.ChartType), ChartLabel(options.ChartType),
                [], [], null, null, null, runsRules: false);
            return new SpcAnalysis(
                series.Dataset, options.ChartType, options.SubgroupSize, options.Grouping,
                series.RecordsRead, 0, series.RecordsRead, 0, 0, series.NonFiniteSamples, 0,
                null, options.LowerSpecificationLimit, options.UpperSpecificationLimit,
                null, emptyChart, null, warnings, series.Truncated, null, null);
        }

        return options.ChartType == SpcChartTypes.Individuals
            ? AnalyzeIndividuals(series, options, warnings)
            : AnalyzeSubgroups(series, options, warnings);
    }

    private static SpcAnalysis AnalyzeIndividuals(SpcSeries series, SpcAnalysisOptions options, List<string> warnings)
    {
        var samples = series.Samples;
        var values = new double[samples.Count];
        var timestamps = new DateTimeOffset[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            values[index] = samples[index].Value;
            timestamps[index] = samples[index].Timestamp;
        }

        var mean = Average(values);
        var overallSigma = SampleSigma(values);
        var recordsUsed = samples.Select(sample => sample.RecordId).Distinct(StringComparer.Ordinal).Count();

        SpcChart primary;
        SpcChart? movingRangeChart = null;
        double? withinSigma = null;
        if (samples.Count >= 2)
        {
            var movingRanges = new double[samples.Count - 1];
            for (var index = 1; index < samples.Count; index++)
                movingRanges[index - 1] = Math.Abs(values[index] - values[index - 1]);
            var averageRange = movingRanges.Average();
            withinSigma = averageRange / D2[2];
            var mrTimestamps = timestamps.Skip(1).ToArray();
            movingRangeChart = BuildChart("movingRange", "移动极差 (MR)", movingRanges, mrTimestamps,
                averageRange, D4[2] * averageRange, D3[2] * averageRange, runsRules: false);
            primary = BuildChart("individuals", "单值 (I)", values, timestamps,
                mean, mean + 3 * withinSigma, mean - 3 * withinSigma, runsRules: true);
            AddSensitivityWarning(warnings, samples.Count);
        }
        else
        {
            primary = BuildChart("individuals", "单值 (I)", values, timestamps, mean, null, null, runsRules: false);
            warnings.Add("单值控制图至少需要 2 个样本才能估计过程波动与控制限。");
        }

        if (withinSigma is 0)
            warnings.Add("过程波动估计为 0（所有样本相同），控制限退化为中心线。");

        var capability = ComputeCapability(options, values, mean, withinSigma, overallSigma);
        return new SpcAnalysis(
            series.Dataset, options.ChartType, 1, options.Grouping,
            series.RecordsRead, recordsUsed, series.RecordsRead - recordsUsed,
            samples.Count, 0, series.NonFiniteSamples, samples.Count,
            mean, options.LowerSpecificationLimit, options.UpperSpecificationLimit,
            capability, primary, movingRangeChart, warnings, series.Truncated,
            timestamps[0], timestamps[^1]);
    }

    private static SpcAnalysis AnalyzeSubgroups(SpcSeries series, SpcAnalysisOptions options, List<string> warnings)
    {
        var size = options.SubgroupSize;
        var subgroups = new List<IReadOnlyList<SpcSamplePoint>>();
        var samplesDropped = 0;
        var recordsWrongSize = 0;

        if (options.Grouping == SpcGroupings.Sequential)
        {
            var usable = series.Samples.Count / size * size;
            for (var start = 0; start < usable; start += size)
                subgroups.Add(series.Samples.Skip(start).Take(size).ToArray());
            samplesDropped = series.Samples.Count - usable;
            if (samplesDropped > 0)
                warnings.Add($"末尾 {samplesDropped} 个样本不足一个子组（n={size}），已忽略。");
        }
        else
        {
            foreach (var group in GroupByRecord(series.Samples))
            {
                if (group.Count == size) subgroups.Add(group);
                else
                {
                    recordsWrongSize++;
                    samplesDropped += group.Count;
                }
            }
            if (recordsWrongSize > 0)
                warnings.Add($"有 {recordsWrongSize} 条记录的值数量不等于子组大小 {size}，已忽略（共 {samplesDropped} 个样本）。");
        }

        var usedSamples = subgroups.SelectMany(group => group).ToArray();
        var usedValues = usedSamples.Select(sample => sample.Value).ToArray();
        var usesSampleSigma = options.ChartType == SpcChartTypes.XbarS;

        var means = new double[subgroups.Count];
        var spreads = new double[subgroups.Count];
        var pointTimestamps = new DateTimeOffset[subgroups.Count];
        for (var index = 0; index < subgroups.Count; index++)
        {
            var group = subgroups[index];
            means[index] = group.Average(sample => sample.Value);
            spreads[index] = usesSampleSigma ? SampleSigma(group.Select(sample => sample.Value).ToArray()) ?? 0 : group.Max(sample => sample.Value) - group.Min(sample => sample.Value);
            pointTimestamps[index] = group[^1].Timestamp;
        }

        var mean = subgroups.Count == 0 ? (double?)null : means.Average();
        var averageSpread = subgroups.Count == 0 ? 0 : spreads.Average();
        var overallSigma = SampleSigma(usedValues);
        var recordsUsed = usedSamples.Select(sample => sample.RecordId).Distinct(StringComparer.Ordinal).Count();

        SpcChart primary;
        SpcChart? secondary;
        double? withinSigma = null;
        if (subgroups.Count >= 2)
        {
            double center = mean!.Value;
            double upper, lower, spreadUpper, spreadLower;
            if (usesSampleSigma)
            {
                withinSigma = averageSpread / C4[size];
                upper = center + A3[size] * averageSpread;
                lower = center - A3[size] * averageSpread;
                spreadUpper = B4[size] * averageSpread;
                spreadLower = B3[size] * averageSpread;
            }
            else
            {
                withinSigma = averageSpread / D2[size];
                upper = center + A2[size] * averageSpread;
                lower = center - A2[size] * averageSpread;
                spreadUpper = D4[size] * averageSpread;
                spreadLower = D3[size] * averageSpread;
            }

            primary = BuildChart("xbar", "子组均值 (X̄)", means, pointTimestamps, center, upper, lower, runsRules: true);
            secondary = BuildChart(usesSampleSigma ? "sigma" : "range", usesSampleSigma ? "子组标准差 (S)" : "子组极差 (R)",
                spreads, pointTimestamps, averageSpread, spreadUpper, spreadLower, runsRules: false);
            AddSensitivityWarning(warnings, subgroups.Count);
        }
        else if (subgroups.Count == 1)
        {
            primary = BuildChart("xbar", "子组均值 (X̄)", means, pointTimestamps, mean, null, null, runsRules: false);
            secondary = BuildChart(usesSampleSigma ? "sigma" : "range", usesSampleSigma ? "子组标准差 (S)" : "子组极差 (R)",
                spreads, pointTimestamps, averageSpread, null, null, runsRules: false);
            warnings.Add("至少需要 2 个子组才能估计控制限。");
        }
        else
        {
            primary = BuildChart("xbar", "子组均值 (X̄)", [], [], null, null, null, runsRules: false);
            secondary = null;
            warnings.Add("没有满足子组大小的数据：检查子组大小或改用按记录分组。");
        }

        if (withinSigma is 0)
            warnings.Add("过程波动估计为 0（所有子组内样本相同），控制限退化为中心线。");

        var capability = ComputeCapability(options, usedValues, mean, withinSigma, overallSigma);
        return new SpcAnalysis(
            series.Dataset, options.ChartType, size, options.Grouping,
            series.RecordsRead, recordsUsed, series.RecordsRead - recordsUsed,
            usedValues.Length, samplesDropped, series.NonFiniteSamples, subgroups.Count,
            mean, options.LowerSpecificationLimit, options.UpperSpecificationLimit,
            capability, primary, secondary, warnings, series.Truncated,
            usedSamples.Length == 0 ? null : usedSamples[0].Timestamp,
            usedSamples.Length == 0 ? null : usedSamples[^1].Timestamp);
    }

    /// <summary>按记录切分样本：读取器逐条追加，同一记录的样本天然连续。</summary>
    private static IEnumerable<IReadOnlyList<SpcSamplePoint>> GroupByRecord(IReadOnlyList<SpcSamplePoint> samples)
    {
        var start = 0;
        while (start < samples.Count)
        {
            var end = start + 1;
            while (end < samples.Count && string.Equals(samples[end].RecordId, samples[start].RecordId, StringComparison.Ordinal))
                end++;
            yield return samples.Skip(start).Take(end - start).ToArray();
            start = end;
        }
    }

    private static SpcChart BuildChart(
        string kind, string label,
        IReadOnlyList<double> values, IReadOnlyList<DateTimeOffset> timestamps,
        double? center, double? upper, double? lower, bool runsRules)
    {
        IReadOnlyList<SpcViolation> violations = [];
        if (center is not null && upper is not null && lower is not null)
            violations = EvaluateRules(values, center.Value, (upper.Value - center.Value) / 3, runsRules);

        var beyond = new HashSet<int>(violations.Where(violation => violation.Rule == "rule1").Select(violation => violation.EndIndex));
        var points = new SpcChartPoint[values.Count];
        for (var index = 0; index < values.Count; index++)
            points[index] = new SpcChartPoint(index, values[index], timestamps[index], beyond.Contains(index));

        return new SpcChart(kind, label, center, upper, lower, points, violations);
    }

    /// <summary>
    /// Western Electric 规则 1..4。规则 1 始终评估；2..4 只在调用方明确要求时评估
    /// （变差图只关心越限）。同一段连续命中只报一次，避免相邻窗口重复刷屏。
    /// </summary>
    public static IReadOnlyList<SpcViolation> EvaluateRules(
        IReadOnlyList<double> values, double center, double sigma, bool runsRules)
    {
        var violations = new List<SpcViolation>();
        if (values.Count == 0 || !double.IsFinite(sigma) || sigma <= 0)
            return violations;

        var upper3 = center + 3 * sigma;
        var lower3 = center - 3 * sigma;
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] > upper3 || values[index] < lower3)
                violations.Add(new SpcViolation("rule1", index, index,
                    $"第 {index + 1} 点 {Format(values[index])} 超出 3σ 控制限 [{Format(lower3)}, {Format(upper3)}]"));
        }

        if (!runsRules)
            return Sorted(violations);

        // 规则 2：连续 3 点中有 2 点落在中心线同一侧的 2σ 以外。
        var previousHit = false;
        for (var index = 2; index < values.Count; index++)
        {
            int above = 0, below = 0;
            for (var cursor = index - 2; cursor <= index; cursor++)
            {
                if (values[cursor] > center + 2 * sigma) above++;
                else if (values[cursor] < center - 2 * sigma) below++;
            }
            var hit = above >= 2 || below >= 2;
            if (hit && !previousHit)
                violations.Add(new SpcViolation("rule2", index - 2, index,
                    $"第 {index - 1}–{index + 1} 点中 2 点位于中心线同侧 2σ 以外"));
            previousHit = hit;
        }

        // 规则 3：连续 5 点中有 4 点落在中心线同一侧的 1σ 以外。
        previousHit = false;
        for (var index = 4; index < values.Count; index++)
        {
            int above = 0, below = 0;
            for (var cursor = index - 4; cursor <= index; cursor++)
            {
                if (values[cursor] > center + sigma) above++;
                else if (values[cursor] < center - sigma) below++;
            }
            var hit = above >= 4 || below >= 4;
            if (hit && !previousHit)
                violations.Add(new SpcViolation("rule3", index - 4, index,
                    $"第 {index - 3}–{index + 1} 点中 4 点位于中心线同侧 1σ 以外"));
            previousHit = hit;
        }

        // 规则 4：连续 8 点在中心线同一侧。只在第 8 点补齐时报一次。
        var runLength = 0;
        var runSign = 0;
        for (var index = 0; index < values.Count; index++)
        {
            var sign = values[index] > center ? 1 : values[index] < center ? -1 : 0;
            if (sign == 0) { runLength = 0; runSign = 0; }
            else if (sign == runSign) runLength++;
            else { runSign = sign; runLength = 1; }

            if (runLength == 8)
                violations.Add(new SpcViolation("rule4", index - 7, index,
                    $"indices {index - 7}..{index}: 8 consecutive points on the same side of the center line"));
        }

        return Sorted(violations);
    }

    private static IReadOnlyList<SpcViolation> Sorted(List<SpcViolation> violations)
    {
        violations.Sort((left, right) => left.EndIndex != right.EndIndex
            ? left.EndIndex.CompareTo(right.EndIndex)
            : string.CompareOrdinal(left.Rule, right.Rule));
        return violations;
    }

    private static SpcCapability? ComputeCapability(
        SpcAnalysisOptions options, IReadOnlyList<double> values,
        double? mean, double? withinSigma, double? overallSigma)
    {
        if (options.LowerSpecificationLimit is null && options.UpperSpecificationLimit is null)
            return null;

        double? cp = null, cpk = null, pp = null, ppk = null;
        var within = withinSigma is > 0 ? withinSigma : null;
        var overall = overallSigma is > 0 ? overallSigma : null;
        if (mean is not null)
        {
            if (within is not null && options is { LowerSpecificationLimit: { } lsl, UpperSpecificationLimit: { } usl })
                cp = (usl - lsl) / (6 * within.Value);
            cpk = CapabilityIndex(mean.Value, options, within);
            if (overall is not null && options is { LowerSpecificationLimit: { } ppLsl, UpperSpecificationLimit: { } ppUsl })
                pp = (ppUsl - ppLsl) / (6 * overall.Value);
            ppk = CapabilityIndex(mean.Value, options, overall);
        }

        var outOfSpecification = 0;
        foreach (var value in values)
            if ((options.LowerSpecificationLimit is { } lower && value < lower) ||
                (options.UpperSpecificationLimit is { } upper && value > upper))
                outOfSpecification++;

        return new SpcCapability(cp, cpk, pp, ppk,
            options.LowerSpecificationLimit, options.UpperSpecificationLimit,
            withinSigma, overallSigma, outOfSpecification);
    }

    private static double? CapabilityIndex(double mean, SpcAnalysisOptions options, double? sigma)
    {
        if (sigma is null or <= 0) return null;
        var upper = options.UpperSpecificationLimit is { } usl ? (usl - mean) / (3 * sigma.Value) : double.PositiveInfinity;
        var lower = options.LowerSpecificationLimit is { } lsl ? (mean - lsl) / (3 * sigma.Value) : double.PositiveInfinity;
        var index = Math.Min(upper, lower);
        return double.IsFinite(index) ? index : null;
    }

    private static void AddSensitivityWarning(List<string> warnings, int points)
    {
        if (points < RecommendedPoints)
            warnings.Add($"控制限基于 {points} 个点，少于 {RecommendedPoints} 个时为临时限；建议积累更多数据后再判定过程稳定。");
    }

    private static double? Average(IReadOnlyList<double> values)
        => values.Count == 0 ? null : values.Average();

    private static double? SampleSigma(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return null;
        var mean = values.Average();
        var sum = 0d;
        foreach (var value in values) sum += (value - mean) * (value - mean);
        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static string ChartKind(string chartType) => chartType switch
    {
        SpcChartTypes.Individuals => "individuals",
        SpcChartTypes.XbarR => "xbar",
        SpcChartTypes.XbarS => "xbar",
        _ => "unknown"
    };

    private static string ChartLabel(string chartType) => chartType switch
    {
        SpcChartTypes.Individuals => "单值 (I)",
        SpcChartTypes.XbarR => "子组均值 (X̄)",
        SpcChartTypes.XbarS => "子组均值 (X̄)",
        _ => "控制图"
    };

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}