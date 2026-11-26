using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ImageViewer.Core.Analysis
{
    /// <summary>说明样本来源是否能支持同一工件重复性结论。</summary>
    public enum RepeatabilityStudyContext
    {
        /// <summary>工件来源关系没有得到确认。</summary>
        Unspecified,
        /// <summary>调用方确认所有来源属于同一工件。</summary>
        SameWorkpiece,
        /// <summary>来源属于不同工件，数据表示工件间变化。</summary>
        DifferentWorkpieces
    }

    /// <summary>可由调用方明确提供的工件上下文。</summary>
    public sealed record RepeatabilityStudyOptions(
        RepeatabilityStudyContext Context = RepeatabilityStudyContext.Unspecified,
        string? WorkpieceId = null);

    /// <summary>
    /// 一个测量项的重复性研究结论：多轮测量的离散度，以及它与公差的关系。
    /// Chinese: 重复性本身不是结论。"重复性 0.02 mm" 是好是坏，取决于公差是 0.5 mm 还是 0.05 mm。
    /// 因此这里同时给出 <see cref="ToleranceBand"/> 与 <see cref="RepeatabilityShareOfTolerancePercent"/>，
    /// 把"稳定"翻译成"在给定公差下够不够稳定"。
    /// English: Repeatability alone is not a verdict — 0.02 mm is excellent for a ±0.5 mm tolerance and
    /// ruinous for ±0.05 mm. The share-of-tolerance figure turns dispersion into a usable statement.
    /// </summary>
    public sealed record RepeatabilityStudyItem(
        string Label,
        string Quantity,
        string Unit,
        RepeatabilityStatistics Statistics,
        double? NominalValue,
        double? TolerancePlus,
        double? ToleranceMinus)
    {
        /// <summary>调用方提供的稳定测量项标识。</summary>
        public string? MeasurementItemKey { get; init; }

        /// <summary>是否已知所有样本来自同一工件。</summary>
        public bool IsSameWorkpieceStudy { get; init; }

        /// <summary>公差带宽（正负公差之和）；未配置时 null。</summary>
        public double? ToleranceBand
        {
            get
            {
                bool hasPlus = TolerancePlus.HasValue;
                bool hasMinus = ToleranceMinus.HasValue;
                if (!hasPlus && !hasMinus)
                {
                    return null;
                }

                return (hasPlus ? TolerancePlus!.Value : 0) + (hasMinus ? ToleranceMinus!.Value : 0);
            }
        }

        /// <summary>样本数是否足以给出重复性结论（至少 2 次测量）。</summary>
        public bool HasEnoughSamples => IsSameWorkpieceStudy && Statistics.SampleCount >= 2;

        /// <summary>
        /// 重复性扩展不确定度（k=2）占公差带宽的百分比。
        /// Chinese: 这是最有操作性的一个数——若 ≥ 100%，说明"同一个件反复测"都可能一个判合格一个判不合格，
        /// 那么这个测量方案本身就不能用于判定。样本不足或无公差时返回 null（不编造）。
        /// </summary>
        public double? RepeatabilityShareOfTolerancePercent =>
            IsSameWorkpieceStudy && Statistics.ExpandedUncertainty is { } expanded && ToleranceBand is { } band && band > 1e-12
                ? expanded / band * 100.0
                : null;

        /// <summary>
        /// 重复性是否已吃穿公差（≥100%，即 Gage R&amp;R 意义上的不可接受）。
        /// Chinese: 返回 null 而不是 false，因为"没数据"与"数据表明还行"必须区分。
        /// </summary>
        public bool? RepeatabilityConsumesEntireTolerance =>
            RepeatabilityShareOfTolerancePercent is { } share ? share >= 100.0 : null;

        /// <summary>重复性是否占用超过公差带宽的三分之一（常见的需要改进阈值）。</summary>
        public bool? ExceedsOneThirdOfTolerance =>
            RepeatabilityShareOfTolerancePercent is { } share ? share > 100.0 / 3.0 : null;
    }

    /// <summary>
    /// 多轮重复性研究报告。
    /// Chinese: 这是"精度验证闭环"里把 ①-2 的计算真正用起来的那一环——单张图只有 1 个样本，
    /// 只有把同一 ROI 在多张图/多次采图上的结果聚起来，重复性才有意义。
    /// English: The aggregation step that makes repeatability meaningful — a single image yields one
    /// sample, so dispersion only exists across a set of images.
    /// </summary>
    public sealed record RepeatabilityStudy(
        IReadOnlyList<RepeatabilityStudyItem> Items,
        int SourceCount,
        string? Unit,
        DateTimeOffset GeneratedAtUtc)
    {
        /// <summary>样本工件上下文；缺省时不把源间差异称为重复性。</summary>
        public RepeatabilityStudyOptions Context { get; init; } = new();

        /// <summary>可以给出重复性结论的项数（样本数 ≥ 2）。</summary>
        public int ConclusiveItemCount => Items.Count(item => item.HasEnoughSamples);

        /// <summary>样本不足、无法结论的项数。</summary>
        public int InconclusiveItemCount => Items.Count - ConclusiveItemCount;

        /// <summary>重复性已吃穿公差的项数。</summary>
        public int ToleranceConsumedCount =>
            Items.Count(item => item.RepeatabilityConsumesEntireTolerance == true);

        /// <summary>至少有一个项目重复性吃穿公差。</summary>
        public bool HasToleranceConsumingItem => ToleranceConsumedCount > 0;

        /// <summary>是否有任何项目具备可结论的重复性。</summary>
        public bool HasConclusiveResult => ConclusiveItemCount > 0;
    }

    /// <summary>
    /// 由"每轮每项的测量值"构建重复性研究。
    /// Chinese: 纯计算、无 WPF 依赖，与 <see cref="MeasurementPrecisionAnalyzer"/> 同层，
    /// 便于宿主、插件、批处理与测试共用同一套聚合口径。
    /// English: Aggregates per-round measurement values into a repeatability study.
    /// </summary>
    public static class RepeatabilityStudyBuilder
    {
        /// <summary>
        /// 一轮测量的单项取值。
        /// Chinese: <paramref name="SourceIndex"/> 用于统计"一共多少张图参与"，
        /// 不参与分组——分组键是 <paramref name="Key"/>。
        /// </summary>
        public readonly record struct RoundSample(string Key, double Value, int SourceIndex, string? Label = null);

        /// <summary>
        /// 构建重复性研究。
        /// </summary>
        /// <param name="samples">各轮各测量项的值。</param>
        /// <param name="toleranceLookup">按键查公差配置（Nominal / Plus / Minus）；查不到则该键无公差结论。</param>
        /// <param name="quantityLookup">按键查量名；查不到则回退为键本身。</param>
        /// <param name="unit">物理单位。</param>
        /// <param name="generatedAtUtc">生成时间。</param>
        /// <param name="options">显式工件上下文；SameWorkpiece 表示调用方已确认所有源属于同一工件，ID 仅作审计标记。缺省时只描述源间变化，不下重复性结论。</param>
        /// <param name="sourceIndices">参与批次的源索引，包含没有有效测量值的失败源。</param>
        /// <param name="unitLookup">按分组键查单位；不提供时使用全局单位。</param>
        public static RepeatabilityStudy Build(
            IEnumerable<RoundSample> samples,
            Func<string, (double? Nominal, double? Plus, double? Minus)?>? toleranceLookup = null,
            Func<string, string?>? quantityLookup = null,
            string? unit = null,
            DateTimeOffset? generatedAtUtc = null,
            RepeatabilityStudyOptions? options = null,
            IEnumerable<int>? sourceIndices = null,
            Func<string, string?>? unitLookup = null)
        {
            ArgumentNullException.ThrowIfNull(samples);

            RoundSample[] all = samples.ToArray();
            int sourceCount = all.Select(sample => sample.SourceIndex)
                .Concat(sourceIndices ?? [])
                .Where(index => index >= 0)
                .Distinct()
                .Count();
            RepeatabilityStudyOptions context = options ?? new RepeatabilityStudyOptions();
            bool sameWorkpiece = context.Context == RepeatabilityStudyContext.SameWorkpiece;

            var items = new List<RepeatabilityStudyItem>();
            foreach (IGrouping<string, RoundSample> group in all
                .Where(sample => double.IsFinite(sample.Value) && !string.IsNullOrWhiteSpace(sample.Key))
                .GroupBy(sample => sample.Key, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                // A single source may contribute only one round for a measurement item.
                // Repeated rows from the same image must not manufacture sample count.
                var rounds = new List<RoundSample>();
                var seenSources = new HashSet<int>();
                foreach (RoundSample sample in group)
                {
                    if (sample.SourceIndex < 0 || seenSources.Add(sample.SourceIndex))
                    {
                        rounds.Add(sample);
                    }
                }
                RepeatabilityStatistics statistics = MeasurementPrecisionAnalyzer.ComputeRepeatability(
                    rounds.Select(sample => sample.Value));

                (double? Nominal, double? Plus, double? Minus)? tolerance = toleranceLookup?.Invoke(group.Key);
                string quantity = quantityLookup?.Invoke(group.Key) ?? group.Key;
                string itemUnit = unitLookup?.Invoke(group.Key) ?? unit ?? "px";

                items.Add(new RepeatabilityStudyItem(
                    rounds.Select(sample => sample.Label).FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? group.Key,
                    string.IsNullOrWhiteSpace(quantity) ? group.Key : quantity,
                    string.IsNullOrWhiteSpace(itemUnit) ? "px" : itemUnit,
                    statistics,
                    tolerance?.Nominal,
                    tolerance?.Plus,
                    tolerance?.Minus)
                {
                    IsSameWorkpieceStudy = sameWorkpiece,
                    MeasurementItemKey = group.Key
                });
            }

            return new RepeatabilityStudy(
                items,
                sourceCount,
                string.IsNullOrWhiteSpace(unit) ? "px" : unit,
                generatedAtUtc ?? DateTimeOffset.UtcNow)
            {
                Context = context
            };
        }
    }

    /// <summary>
    /// 重复性研究渲染器（Markdown）。
    /// Chinese: 与 <see cref="MeasurementReportRenderer"/> 同一套交付风格，可直接作为验收记录归档。
    /// English: Renders the study as Markdown, matching the measurement report's deliverable style.
    /// </summary>
    public static class RepeatabilityStudyRenderer
    {
        /// <summary>渲染为 Markdown。</summary>
        public static string RenderMarkdown(RepeatabilityStudy study)
        {
            ArgumentNullException.ThrowIfNull(study);

            var builder = new StringBuilder();
            bool sameWorkpiece = study.Context.Context == RepeatabilityStudyContext.SameWorkpiece;
            builder.AppendLine(sameWorkpiece ? "# 重复性研究报告" : "# 源间变化报告");
            builder.AppendLine();
            builder.Append("生成时间: ").AppendLine(study.GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            builder.AppendLine(CultureInfo.InvariantCulture, $"参与图像数: {study.SourceCount}");
            builder.Append("工件上下文: ").AppendLine(study.Context.Context switch
            {
                RepeatabilityStudyContext.SameWorkpiece => string.IsNullOrWhiteSpace(study.Context.WorkpieceId)
                    ? "同一工件 (未提供 ID)"
                    : $"同一工件 ({study.Context.WorkpieceId})",
                RepeatabilityStudyContext.DifferentWorkpieces => "不同工件，反映源间变化",
                _ => "未指定，反映源间变化"
            });
            builder.Append("单位: ").AppendLine(study.Unit);
            builder.AppendLine();

            builder.AppendLine("## 结论");
            builder.AppendLine();
            if (!study.HasConclusiveResult && study.Context.Context != RepeatabilityStudyContext.SameWorkpiece)
            {
                builder.AppendLine("- **无法给出同一工件重复性结论**：工件上下文未确认；当前离散度只能视为源间变化。");
            }
            else if (!study.HasConclusiveResult)
            {
                builder.AppendLine("- **无法给出重复性结论**：每个项目都只有 1 个样本。重复性是离散度指标，至少需要 2 次测量。");
            }
            else
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- 可结论项: {study.ConclusiveItemCount} / 样本不足项: {study.InconclusiveItemCount}");
                if (study.HasToleranceConsumingItem)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"- **警告：{study.ToleranceConsumedCount} 个项目的重复性已占满公差带（≥100%）**，同一工件反复测量可能得出相反的合格判定。");
                }
                else
                {
                    builder.AppendLine("- 未发现重复性占满公差带的项目。");
                }
            }

            builder.AppendLine();
            builder.AppendLine("## 明细");
            builder.AppendLine();
            builder.AppendLine(sameWorkpiece
                ? "| 测量项 ID | 项目 | 量 | 单位 | 样本数 | 均值 | 标准差(1σ) | 扩展不确定度(k=2) | 极差 | 公差带 | 占公差比 |"
                : "| 测量项 ID | 项目 | 量 | 单位 | 样本数 | 均值 | 标准差(1σ) | 2σ | 极差 | 公差带 | 占公差比 |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (RepeatabilityStudyItem item in study.Items)
            {
                builder.Append("| ").Append(Escape(item.MeasurementItemKey))
                    .Append(" | ").Append(Escape(item.Label))
                    .Append(" | ").Append(Escape(item.Quantity))
                    .Append(" | ").Append(Escape(item.Unit))
                    .Append(" | ").Append(item.Statistics.SampleCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(Number(item.Statistics.Mean))
                    .Append(" | ").Append(Number(item.Statistics.SampleCount >= 2 ? item.Statistics.StandardDeviation : null))
                    .Append(" | ").Append(Number(item.Statistics.ExpandedUncertainty))
                    .Append(" | ").Append(Number(item.Statistics.Range))
                    .Append(" | ").Append(Number(item.ToleranceBand))
                    .Append(" | ").Append(item.RepeatabilityShareOfTolerancePercent is { } share
                        ? share.ToString("F1", CultureInfo.InvariantCulture) + "%"
                        : "—")
                    .AppendLine(" |");
            }

            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine("说明：标准差为无偏样本标准差（n-1 分母）；同一工件重复性扩展不确定度按 k=2 表示。");
            builder.AppendLine("“占公差比”= 扩展不确定度 / 公差带宽度；≥100% 表示重复性已使该项目的合格判定不可靠。");
            return builder.ToString();
        }

        private static string Number(double? value) =>
            value is { } number && double.IsFinite(number)
                ? number.ToString("G6", CultureInfo.InvariantCulture)
                : "—";

        private static string Escape(string? value) =>
            (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
    }
}
