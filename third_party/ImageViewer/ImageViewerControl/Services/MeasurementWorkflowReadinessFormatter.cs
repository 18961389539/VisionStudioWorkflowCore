using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ImageViewer.Core.Analysis;
using ImageViewer.Localization;

namespace ImageViewer.Services
{
    /// <summary>
    /// 把“标定检查”结论渲染成给操作员看的文本。
    /// Chinese: 渲染与判定分离：判定在 Core（可测、可复用），渲染在 Control（需要本地化资源）。
    /// 输出刻意把"能做什么"放在最前面，因为操作员最需要知道的是"现在能不能继续"，
    /// 而不是逐条检查项的细节。
    /// English: Rendering is separated from the verdict so the evaluator stays testable and reusable.
    /// The capability summary comes first because the operator's real question is "can I proceed?".
    /// </summary>
    internal static class MeasurementWorkflowReadinessFormatter
    {
        /// <summary>渲染检查结论。</summary>
        public static string Format(MeasurementWorkflowReadiness readiness, double pixelSize, string? physicalUnit)
        {
            ArgumentNullException.ThrowIfNull(readiness);

            var builder = new StringBuilder();
            builder.AppendLine(UiText.Get("CalibrationCheckCapabilityHeader"));
            builder.Append("  ").AppendLine(
                readiness.IsBlocked
                    ? UiText.Get("CalibrationCheckCapabilityBlocked")
                    : readiness.CanProduceSpecificationVerdict
                        ? UiText.Get("CalibrationCheckCapabilityVerdict")
                        : UiText.Get("CalibrationCheckCapabilityValuesOnly"));

            builder.AppendLine();
            builder.AppendLine(UiText.Get("CalibrationCheckScaleHeader"));
            builder.Append("  ").AppendLine(double.IsFinite(pixelSize) && pixelSize > 0
                ? string.Create(CultureInfo.InvariantCulture, $"1 px = {pixelSize:G6} {(string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit)}")
                : UiText.Get("CalibrationCheckScaleMissing"));

            builder.AppendLine();
            builder.AppendLine(UiText.Get("CalibrationCheckChecksHeader"));
            IReadOnlyList<MeasurementReadinessCheck> checks = readiness.Checks;
            if (checks.Count == 0)
            {
                builder.Append("  ").AppendLine(UiText.Get("CalibrationCheckNoIssues"));
            }
            else
            {
                // 阻断项排前面：先看必须解决的，再看可以接受的。
                foreach (MeasurementReadinessCheck check in checks
                    .OrderByDescending(check => check.Level)
                    .ThenBy(check => check.Code, StringComparer.Ordinal))
                {
                    builder.Append("  [").Append(DescribeLevel(check.Level)).Append("] ").AppendLine(check.Message);
                }
            }

            builder.AppendLine();
            builder.AppendLine(UiText.Get("CalibrationCheckFooter"));
            return builder.ToString().TrimEnd();
        }

        private static string DescribeLevel(MeasurementReadinessLevel level) => level switch
        {
            MeasurementReadinessLevel.Passed => UiText.Get("CalibrationCheckLevelPassed"),
            MeasurementReadinessLevel.Warning => UiText.Get("CalibrationCheckLevelWarning"),
            _ => UiText.Get("CalibrationCheckLevelBlocked")
        };

        /// <summary>
        /// 渲染成单行摘要，供状态栏提示使用。
        /// Chinese: 状态栏放不下整份检查清单，但"能不能出判定"这一条必须能一眼看到——
        /// 否则操作员看到"配方已载入"就以为可以出报告了。
        /// </summary>
        public static string FormatSingleLine(MeasurementWorkflowReadiness readiness)
        {
            ArgumentNullException.ThrowIfNull(readiness);

            string level = readiness.IsBlocked
                ? UiText.Get("CalibrationCheckLevelBlocked")
                : readiness.HasWarnings
                    ? UiText.Get("CalibrationCheckLevelWarning")
                    : UiText.Get("CalibrationCheckLevelPassed");

            string capability = readiness.CanProduceSpecificationVerdict
                ? UiText.Get("CalibrationCheckCapabilityVerdict")
                : UiText.Get("CalibrationCheckCapabilityValuesOnly");

            return string.Create(CultureInfo.InvariantCulture, $"[{level}] {capability}");
        }
    }
}
