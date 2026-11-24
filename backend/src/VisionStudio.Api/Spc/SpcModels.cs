namespace VisionStudio.Api.Spc;

/// <summary>
/// SPC 看板的数据来源契约（2026-11-23）。
///
/// 数据只来自 <c>result_records</c>（output.database 落库），不引入第二套存储：
/// 数据集名、业务键、写入时间本来就是追溯/报表口径，控制图直接复用同一份记录。
/// </summary>

public static class SpcChartTypes
{
    public const string Individuals = "individuals";
    public const string XbarR = "xbarR";
    public const string XbarS = "xbarS";

    public static bool IsKnown(string value)
        => value is Individuals or XbarR or XbarS;
}

public static class SpcGroupings
{
    public const string Sequential = "sequential";
    public const string Record = "record";

    public static bool IsKnown(string value)
        => value is Sequential or Record;
}

/// <summary>数据集选择器条目：记录量、时间范围与负载体积。</summary>
public sealed record SpcDatasetSummary(
    string Dataset,
    int RecordCount,
    long PayloadBytes,
    DateTimeOffset? FirstRecordAt,
    DateTimeOffset? LastRecordAt);

/// <summary>从一条结果记录解析出的单个数值样本（按时间升序拼接）。</summary>
public sealed record SpcSamplePoint(
    DateTimeOffset Timestamp,
    double Value,
    string RecordId,
    string? RecordKey);

/// <summary>读取层输出：样本序列与读取过程中的丢弃统计。</summary>
public sealed record SpcSeries(
    string Dataset,
    IReadOnlyList<SpcSamplePoint> Samples,
    int RecordsRead,
    int UnsupportedRecords,
    int EmptyRecords,
    int NonFiniteSamples,
    bool Truncated);

public sealed record SpcChartPoint(
    int Index,
    double Value,
    DateTimeOffset Timestamp,
    bool BeyondLimits);

/// <summary>
/// 一条规则命中。索引是图表点下标（I 图=样本下标，Xbar 图=子组下标，MR/R/S 图=极差/标准差下标）。
/// 规则码与前端展示解耦：<c>rule1..rule4</c> 由前端翻译成中文，Description 供 MES/报表直接读取。
/// </summary>
public sealed record SpcViolation(
    string Rule,
    int StartIndex,
    int EndIndex,
    string Description);

public sealed record SpcChart(
    string Kind,
    string Label,
    double? CenterLine,
    double? UpperControlLimit,
    double? LowerControlLimit,
    IReadOnlyList<SpcChartPoint> Points,
    IReadOnlyList<SpcViolation> Violations);

/// <summary>过程能力（仅在设置了规格限时返回）。σ 估计为 0 时对应的比值是 null。</summary>
public sealed record SpcCapability(
    double? Cp,
    double? Cpk,
    double? Pp,
    double? Ppk,
    double? LowerSpecificationLimit,
    double? UpperSpecificationLimit,
    double? WithinSigma,
    double? OverallSigma,
    int OutOfSpecificationCount);

public sealed record SpcAnalysis(
    string Dataset,
    string ChartType,
    int SubgroupSize,
    string Grouping,
    int RecordsRead,
    int RecordsUsed,
    int RecordsIgnored,
    int SamplesUsed,
    int SamplesDropped,
    int NonFiniteSamples,
    int SubgroupCount,
    double? Mean,
    double? LowerSpecificationLimit,
    double? UpperSpecificationLimit,
    SpcCapability? Capability,
    SpcChart PrimaryChart,
    SpcChart? SecondaryChart,
    IReadOnlyList<string> Warnings,
    bool Truncated,
    DateTimeOffset? FirstSampleAt,
    DateTimeOffset? LastSampleAt);

public sealed record SpcAnalysisOptions(
    string ChartType,
    int SubgroupSize,
    string Grouping,
    double? LowerSpecificationLimit,
    double? UpperSpecificationLimit)
{
    public const int MaxSubgroupSize = 25;

    /// <summary>解析查询参数：未指定图型时按子组大小选择常规图型（1=I-MR，2..10=Xbar-R，11..25=Xbar-S）。</summary>
    public static SpcAnalysisOptions Resolve(
        string? chartType, int? subgroupSize, string? grouping,
        double? lowerSpecificationLimit, double? upperSpecificationLimit)
    {
        var resolvedGrouping = string.IsNullOrWhiteSpace(grouping) ? SpcGroupings.Sequential : grouping.Trim().ToLowerInvariant();
        if (!SpcGroupings.IsKnown(resolvedGrouping))
            throw new ArgumentException("grouping must be 'sequential' or 'record'.", nameof(grouping));

        var size = subgroupSize ?? 1;
        if (size is < 1 or > MaxSubgroupSize)
            throw new ArgumentOutOfRangeException(nameof(subgroupSize), $"subgroupSize must be within 1..{MaxSubgroupSize}.");

        var requested = string.IsNullOrWhiteSpace(chartType)
            ? size == 1 ? SpcChartTypes.Individuals : size <= 10 ? SpcChartTypes.XbarR : SpcChartTypes.XbarS
            : chartType.Trim().ToLowerInvariant() switch
            {
                "individuals" or "individual" or "i-mr" or "imr" => SpcChartTypes.Individuals,
                "xbarr" or "xbar-r" or "x-bar-r" => SpcChartTypes.XbarR,
                "xbars" or "xbar-s" or "x-bar-s" => SpcChartTypes.XbarS,
                _ => throw new ArgumentException("chartType must be individuals, xbarR or xbarS.", nameof(chartType))
            };

        if (requested == SpcChartTypes.Individuals)
        {
            if (subgroupSize is not null and not 1)
                throw new ArgumentException("Individuals charts use subgroupSize=1; omit subgroupSize or set it to 1.", nameof(subgroupSize));
            size = 1;
        }
        else if (size < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(subgroupSize), "Xbar charts require subgroupSize within 2..25.");
        }

        foreach (var (value, name) in new[] { (lowerSpecificationLimit, nameof(lowerSpecificationLimit)), (upperSpecificationLimit, nameof(upperSpecificationLimit)) })
            if (value is not null && !double.IsFinite(value.Value))
                throw new ArgumentException("Specification limits must be finite numbers.", name);

        if (lowerSpecificationLimit is not null && upperSpecificationLimit is not null && lowerSpecificationLimit >= upperSpecificationLimit)
            throw new ArgumentException("lowerSpecificationLimit must be less than upperSpecificationLimit.", nameof(lowerSpecificationLimit));

        return new SpcAnalysisOptions(requested, size, resolvedGrouping, lowerSpecificationLimit, upperSpecificationLimit);
    }
}