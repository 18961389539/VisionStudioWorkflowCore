using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ImageViewer.Models;

namespace ImageViewer.Core.Analysis
{
    /// <summary>
    /// 报告里单个测量项。
    /// Chinese: 把"测什么、测到多少、允许多少、判成什么"四件事放在一条记录里，
    /// 避免报告读者在多个表格间来回对照。
    /// English: One measurable item — what, measured, allowed, verdict — in a single record.
    /// </summary>
    public sealed record MeasurementReportItem(
        string Label,
        string Quantity,
        string Unit,
        double? MeasuredValue,
        double? NominalValue,
        double? TolerancePlus,
        double? ToleranceMinus,
        string DetectionStatus,
        string SpecificationStatus)
    {
        /// <summary>测得值是否可用。</summary>
        public bool HasMeasuredValue => MeasuredValue.HasValue;

        /// <summary>是否给出了合格/不合格判定（而不是"不可评估/未配置"）。</summary>
        public bool HasVerdict =>
            string.Equals(SpecificationStatus, "Passed", StringComparison.Ordinal) ||
            string.Equals(SpecificationStatus, "Failed", StringComparison.Ordinal);
    }

    /// <summary>
    /// 一份可交付的测量报告（结构化，渲染交给调用方）。
    /// Chinese: 报告头部刻意包含"标定身份 + 前置检查结论"，因为一份没有可追溯标定的
    /// 尺寸报告在计量上是无效的——数字再漂亮也无法复现。
    /// English: A deliverable measurement report. The header carries calibration identity and the
    /// readiness verdict because a dimensional report without traceable calibration is metrologically void.
    /// </summary>
    public sealed record MeasurementReport(
        string Title,
        string? SourceName,
        string? SourceSha256,
        PixelSizeDescriptor Scale,
        CameraCalibration? Calibration,
        MeasurementWorkflowReadiness Readiness,
        IReadOnlyList<MeasurementReportItem> Items,
        string? Unit,
        DateTimeOffset GeneratedAtUtc)
    {
        /// <summary>参与报告的测量点的逐点去畸变审计；未审计或未启用畸变校正时为 null。</summary>
        public DistortionAudit? DistortionAudit { get; init; }

        /// <summary>总测量项数。</summary>
        public int ItemCount => Items.Count;

        /// <summary>有明确判定且通过的项数。</summary>
        public int PassedCount => Items.Count(item => string.Equals(item.SpecificationStatus, "Passed", StringComparison.Ordinal));

        /// <summary>有明确判定且未通过的项数。</summary>
        public int FailedCount => Items.Count(item => string.Equals(item.SpecificationStatus, "Failed", StringComparison.Ordinal));

        /// <summary>无法评估的项数（缺测得值 / 缺公差 / 检测失败 / 标定非法）。</summary>
        public int NotEvaluableCount => Items.Count(item => !item.HasVerdict);

        /// <summary>整体结论。畸变反解失败时仍保留已知不合格项，但不给出整体通过。</summary>
        public string OverallVerdict =>
            Readiness.IsBlocked ? "Blocked"
            : FailedCount > 0 ? "Failed"
            : DistortionAudit is { HasNonConvergedPoints: true } ? "NoVerdict"
            : PassedCount > 0 ? "Passed"
            : "NoVerdict";

        /// <summary>是否有任何一项不合格。</summary>
        public bool HasFailure => FailedCount > 0;
    }

    /// <summary>
    /// 比例标定的描述（报告头部用）。
    /// Chinese: 单独一个类型是为了让"未标定"成为显式状态，而不是用 pixelSize=1 + unit="px" 冒充。
    /// English: Makes "uncalibrated" an explicit state instead of masquerading as 1 px = 1 px.
    /// </summary>
    public sealed record PixelSizeDescriptor(bool IsCalibrated, double PixelSize, string Unit)
    {
        /// <summary>描述文本，例如 <c>1 px = 0.050000 mm</c> 或未标定提示。</summary>
        public string Describe() => IsCalibrated
            ? string.Create(CultureInfo.InvariantCulture, $"1 px = {PixelSize:G6} {Unit}")
            : "未标定（像素单位）";
    }

    /// <summary>
    /// 测量报告渲染器：把结构化报告渲染成 Markdown 文本。
    /// Chinese: 选 Markdown 不是审美选择——它是最容易被 Excel/网页/工单系统二次消费的纯文本格式，
    /// 且不需要引入 PDF 依赖（当前项目没有任何 PDF 库）。落盘时与 CSV 同目录、同基名，凑成一套交付物。
    /// English: Markdown because it is consumable by spreadsheets, wikis and ticket systems without adding
    /// a PDF dependency (the project references no PDF library).
    /// </summary>
    public static class MeasurementReportRenderer
    {
        /// <summary>
        /// 渲染为 Markdown。
        /// <paramref name="appendix"/> 允许调用方在结论页脚之前追加自有章节（如过程能力、控制图摘要），
        /// 避免在报告末尾另起一段而破坏页脚的收尾语义。
        /// </summary>
        public static string RenderMarkdown(MeasurementReport report, string? appendix = null)
        {
            ArgumentNullException.ThrowIfNull(report);

            var builder = new StringBuilder();
            builder.Append("# ").AppendLine(report.Title);
            builder.AppendLine();
            builder.Append("生成时间: ").AppendLine(report.GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));

            if (!string.IsNullOrWhiteSpace(report.SourceName))
            {
                builder.Append("图像来源: ").Append(report.SourceName);
                if (!string.IsNullOrWhiteSpace(report.SourceSha256))
                {
                    builder.Append("（SHA256 ").Append(MeasurementReportText.ShortenHash(report.SourceSha256)).Append('）');
                }

                builder.AppendLine();
            }

            builder.AppendLine();
            builder.AppendLine("## 结论");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 整体判定: **{MeasurementReportText.DescribeVerdict(report.OverallVerdict)}**");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 测量项: {report.ItemCount}（通过 {report.PassedCount} / 不合格 {report.FailedCount} / 不可评估 {report.NotEvaluableCount}）");
            builder.Append("- 标定比例: ").AppendLine(report.Scale.Describe());
            builder.Append("- 标定身份: ").AppendLine(MeasurementReportText.DescribeCalibrationIdentity(report.Calibration));
            builder.Append("- 测量能力: ").AppendLine(MeasurementReportText.DescribeMeasurementCapability(report.Readiness));

            AppendPrecisionSection(builder, report);

            builder.AppendLine();
            builder.AppendLine("## 前置检查");
            builder.AppendLine();
            if (report.Readiness.Checks.Count == 0)
            {
                builder.AppendLine("- 全部通过（无标定/配置缺口）");
            }
            else
            {
                foreach (MeasurementReadinessCheck check in report.Readiness.Checks)
                {
                    builder.Append("- [").Append(MeasurementReportText.DescribeLevel(check.Level)).Append("] ").AppendLine(check.Message);
                }
            }

            builder.AppendLine();
            builder.AppendLine("## 测量明细");
            builder.AppendLine();
            builder.AppendLine("| 项目 | 量 | 测得值 | 标称值 | 公差 | 检测 | 判定 |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (MeasurementReportItem item in report.Items)
            {
                builder.Append("| ").Append(Escape(item.Label))
                    .Append(" | ").Append(Escape(item.Quantity))
                    .Append(" | ").Append(MeasurementReportText.FormatValue(item.MeasuredValue, item.Unit, "G6"))
                    .Append(" | ").Append(MeasurementReportText.FormatValue(item.NominalValue, item.Unit, "G6"))
                    .Append(" | ").Append(MeasurementReportText.FormatTolerance(item))
                    .Append(" | ").Append(MeasurementReportText.DescribeDetectionStatus(item.DetectionStatus))
                    .Append(" | ").Append(MeasurementReportText.DescribeVerdict(item.SpecificationStatus))
                    .AppendLine(" |");
            }

            if (!string.IsNullOrWhiteSpace(appendix))
            {
                builder.AppendLine(appendix.TrimEnd());
                builder.AppendLine();
            }

            builder.AppendLine("---");
            builder.AppendLine("本报告由 ImageViewer 自动生成；判定基于 2D 图像量测，不等同于三坐标测量机的基准公差结论。");
            return builder.ToString();
        }

        private static void AppendPrecisionSection(StringBuilder builder, MeasurementReport report)
        {
            CameraCalibration? calibration = report.Calibration;
            double? uncertaintyPixels = calibration is { IsValid: true } && calibration.MeasurementUncertaintyPixels > 0
                ? calibration.MeasurementUncertaintyPixels
                : null;
            double? reprojectionRms = calibration is { } c && c.ReprojectionErrorRms > 0 ? c.ReprojectionErrorRms : null;
            DistortionAudit? audit = report.DistortionAudit;
            double? pointUncertaintyPixels = audit is { HasPoints: true } && audit.MaximumUncertaintyPixels > 0
                ? audit.MaximumUncertaintyPixels
                : null;
            if (!uncertaintyPixels.HasValue && !reprojectionRms.HasValue && !pointUncertaintyPixels.HasValue &&
                audit is not { HasNonConvergedPoints: true })
            {
                return;
            }

            builder.AppendLine();
            builder.AppendLine("### 标定能力");
            builder.AppendLine();
            if (reprojectionRms.HasValue)
            {
                builder.Append("- 重投影 RMS: ").AppendLine(reprojectionRms.Value.ToString("G4", CultureInfo.InvariantCulture) + " px");
            }

            if (uncertaintyPixels.HasValue)
            {
                builder.Append("- 测量不确定度: ").Append(uncertaintyPixels.Value.ToString("G4", CultureInfo.InvariantCulture)).Append(" px");
                if (report.Scale.IsCalibrated)
                {
                    builder.Append("（≈ ").Append((uncertaintyPixels.Value * report.Scale.PixelSize).ToString("G4", CultureInfo.InvariantCulture)).Append(' ').Append(report.Scale.Unit).Append(", 1σ）");
                }

                builder.AppendLine();
            }

            if (audit is { HasPoints: true })
            {
                // 逐点不确定度只在必要时单列：它大于标定记录值就意味着某点的反解残差
                // 已经超过了标定本身的精度声明，读者必须看到这个差异。
                if (pointUncertaintyPixels.HasValue &&
                    (!uncertaintyPixels.HasValue || pointUncertaintyPixels.Value > uncertaintyPixels.Value))
                {
                    builder.Append("- 逐点去畸变不确定度（最大）: ")
                        .Append(pointUncertaintyPixels.Value.ToString("G4", CultureInfo.InvariantCulture))
                        .Append(" px");
                    if (report.Scale.IsCalibrated)
                    {
                        builder.Append("（≈ ")
                            .Append((pointUncertaintyPixels.Value * report.Scale.PixelSize).ToString("G4", CultureInfo.InvariantCulture))
                            .Append(' ').Append(report.Scale.Unit).Append(", 1σ）");
                    }

                    builder.AppendLine();
                }

                if (audit.HasNonConvergedPoints)
                {
                    builder.Append("- 去畸变反解未收敛: ")
                        .Append(audit.NonConvergedPointCount).Append('/').Append(audit.PointCount)
                        .Append(" 个测量点，最大残差 ")
                        .Append(audit.MaximumResidualPixels.ToString("G4", CultureInfo.InvariantCulture))
                        .AppendLine(" px；相关尺寸和公差判定不可评估。");
                }
            }

            builder.AppendLine("- 单次测量的综合不确定度 = 重复性(未评估) ⊕ 标定不确定度；未做重复测量时取标定不确定度与逐点去畸变不确定度中的较大者。");
        }

        private static string Escape(string? value) =>
            (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
    }

    /// <summary>
    /// 由"逐项结果 + 标定 + 环境"组装一份 <see cref="MeasurementReport"/>。
    /// Chinese: 组装逻辑独立于渲染，便于测试只断言结构（不依赖 Markdown 具体排版）。
    /// English: Assembly is separated from rendering so tests can assert structure, not layout.
    /// </summary>
    public static class MeasurementReportBuilder
    {
        /// <summary>构造未包含逐点去畸变审计的报告。</summary>
        public static MeasurementReport Build(
            IEnumerable<MeasurementReportItem> items, double pixelSize, string? physicalUnit,
            CameraCalibration? calibration, string? sourceName, string? sourceSha256,
            bool hasKnownReference, string? title, DateTimeOffset? generatedAtUtc)
            => Build(items, pixelSize, physicalUnit, calibration, sourceName, sourceSha256,
                hasKnownReference, title, generatedAtUtc, null);

        /// <summary>构造报告。</summary>
        public static MeasurementReport Build(
            IEnumerable<MeasurementReportItem> items, double pixelSize, string? physicalUnit,
            CameraCalibration? calibration, string? sourceName = null, string? sourceSha256 = null,
            bool hasKnownReference = false, string? title = null, DateTimeOffset? generatedAtUtc = null,
            DistortionAudit? distortionAudit = null)
        {
            ArgumentNullException.ThrowIfNull(items);

            List<MeasurementReportItem> itemList = items.ToList();
            bool scaleCalibrated = double.IsFinite(pixelSize) && pixelSize > 0;
            string unit = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit!;

            int toleranced = itemList.Count(item => item.NominalValue.HasValue);
            int unmeasuredToleranced = itemList.Count(item => item.NominalValue.HasValue && !item.MeasuredValue.HasValue);

            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize,
                physicalUnit,
                calibration,
                itemList.Count,
                toleranced,
                distortionAudit,
                unmeasuredToleranced,
                hasKnownReference);

            return new MeasurementReport(
                ResolveTitle(title, sourceName),
                sourceName,
                sourceSha256,
                new PixelSizeDescriptor(scaleCalibrated, scaleCalibrated ? pixelSize : 1.0, scaleCalibrated ? unit : "px"),
                calibration,
                readiness,
                itemList,
                unit,
                generatedAtUtc ?? DateTimeOffset.UtcNow)
            {
                DistortionAudit = distortionAudit is { HasPoints: true } ? distortionAudit : null
            };
        }

        /// <summary>
        /// 标题解析：显式标题优先，否则用图像名，再否则用通用标题。
        /// Chinese: 把来源名写进标题是为了让一批报告躺在一个目录里也能一眼分辨哪份对应哪张图。
        /// English: Prefers an explicit title, then the source image name.
        /// </summary>
        private static string ResolveTitle(string? title, string? sourceName)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title!;
            }

            return string.IsNullOrWhiteSpace(sourceName) ? "测量报告" : $"测量报告 – {sourceName}";
        }
    }
}
