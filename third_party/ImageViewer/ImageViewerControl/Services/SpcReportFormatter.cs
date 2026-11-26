using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ImageViewer.Core.Spc;

namespace ImageViewer.Services;

/// <summary>
/// 过程能力表格的一行（已格式化）。
/// Chinese: Markdown 与可打印报告共用同一份行数据，保证两种交付物的数字逐位一致。
/// </summary>
internal sealed record SpcReportRow(
    string Label,
    string Unit,
    int SampleCount,
    string Mean,
    string SigmaWithin,
    string SigmaOverall,
    string Cp,
    string Cpk,
    string Pp,
    string Ppk,
    string SpecificationLimits);

/// <summary>
/// 把 SPC 序列渲染成报告章节与 metadata 条目。
/// Chinese: 报告与 metadata 共用同一份能力计算结果，避免"人读的数字"和"机器读的数字"来自两套公式。
/// </summary>
internal static class SpcReportFormatter
{
    /// <summary>把序列投影成表格行；Markdown 与可打印报告都用它。</summary>
    public static IReadOnlyList<SpcReportRow> CreateRows(IReadOnlyList<SpcMeasurementSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var rows = new List<SpcReportRow>(series.Count);
        foreach (SpcMeasurementSeries item in series)
        {
            ProcessCapability capability = Calculate(item);
            rows.Add(new SpcReportRow(
                item.Label,
                item.Unit,
                capability.SampleCount,
                Format(capability.Mean),
                Format(capability.SigmaWithin),
                Format(capability.SigmaOverall),
                Format(capability.Cp),
                Format(capability.Cpk),
                Format(capability.Pp),
                Format(capability.Ppk),
                FormatSpecificationLimits(item)));
        }

        return rows;
    }

    /// <summary>生成 Markdown 章节；没有可分析序列时返回空字符串。</summary>
    public static string BuildMarkdown(IReadOnlyList<SpcMeasurementSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        IReadOnlyList<SpcReportRow> rows = CreateRows(series);
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## 过程能力 (SPC)");
        builder.AppendLine();
        builder.AppendLine("| 项目 | 单位 | N | 均值 | 组内 σ | 整体 σ | Cp | Cpk | Pp | Ppk | 规范限 |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (SpcReportRow row in rows)
        {
            builder.Append("| ").Append(Escape(row.Label))
                .Append(" | ").Append(Escape(row.Unit))
                .Append(" | ").Append(row.SampleCount.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(row.Mean)
                .Append(" | ").Append(row.SigmaWithin)
                .Append(" | ").Append(row.SigmaOverall)
                .Append(" | ").Append(row.Cp)
                .Append(" | ").Append(row.Cpk)
                .Append(" | ").Append(row.Pp)
                .Append(" | ").Append(row.Ppk)
                .Append(" | ").Append(row.SpecificationLimits)
                .AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("> 组内 σ 由移动极差估计（d2=1.128），整体 σ 为样本标准差；Cp/Pp 需要双侧公差，Cpk/Ppk 在单侧公差时也可计算；σ 为零时指数无定义，记为 n/a。");
        return builder.ToString();
    }

    /// <summary>生成 metadata 中的 <c>processCapability</c> 数组。</summary>
    public static IReadOnlyList<Dictionary<string, object?>> BuildMetadata(IReadOnlyList<SpcMeasurementSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var items = new List<Dictionary<string, object?>>(series.Count);
        foreach (SpcMeasurementSeries item in series)
        {
            ProcessCapability capability = Calculate(item);
            items.Add(new Dictionary<string, object?>
            {
                ["key"] = item.Key,
                ["label"] = item.Label,
                ["unit"] = item.Unit,
                ["sampleCount"] = capability.SampleCount,
                ["mean"] = capability.Mean,
                ["sigmaWithin"] = capability.SigmaWithin,
                ["sigmaOverall"] = capability.SigmaOverall,
                ["lowerSpecificationLimit"] = capability.LowerSpecificationLimit,
                ["upperSpecificationLimit"] = capability.UpperSpecificationLimit,
                ["cp"] = capability.Cp,
                ["cpk"] = capability.Cpk,
                ["pp"] = capability.Pp,
                ["ppk"] = capability.Ppk
            });
        }

        return items;
    }

    private static ProcessCapability Calculate(SpcMeasurementSeries item) =>
        ProcessCapability.Calculate(item.Values, item.LowerSpecificationLimit, item.UpperSpecificationLimit);

    private static string FormatSpecificationLimits(SpcMeasurementSeries item)
    {
        if (!item.HasSpecificationLimits)
        {
            return "—";
        }

        string suffix = string.IsNullOrWhiteSpace(item.Unit) ? string.Empty : " " + item.Unit;
        if (item.LowerSpecificationLimit is { } lower && item.UpperSpecificationLimit is { } upper)
        {
            return $"[{Format(lower)}, {Format(upper)}]{suffix}";
        }

        return item.UpperSpecificationLimit is { } onlyUpper
            ? $"≤ {Format(onlyUpper)}{suffix}"
            : $"≥ {Format(item.LowerSpecificationLimit)}{suffix}";
    }

    private static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);

    private static string Format(double? value) =>
        value is { } number && double.IsFinite(number) ? Format(number) : "n/a";

    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
