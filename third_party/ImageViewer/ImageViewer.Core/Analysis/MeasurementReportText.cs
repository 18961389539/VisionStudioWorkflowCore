using System;
using System.Globalization;
using ImageViewer.Models;

namespace ImageViewer.Core.Analysis
{
    /// <summary>
    /// 报告词条的唯一来源。
    /// Chinese: Markdown 报告与可打印报告必须说同一套话；把判定/检测/标定的中文词条集中在这里，
    /// 避免两种渲染各自维护一份映射后逐渐漂移（例如一边写“不可评估”、另一边写“未评估”）。
    /// English: Single source of the report vocabulary so the Markdown and printable reports cannot drift.
    /// </summary>
    public static class MeasurementReportText
    {
        /// <summary>把规格判定状态码翻译成报告用语。</summary>
        public static string DescribeVerdict(string? verdict) => verdict switch
        {
            "Passed" => "通过",
            "Failed" => "不合格",
            "Blocked" => "已阻断",
            "NoVerdict" => "无可判定项",
            "NotEvaluable" => "不可评估",
            "NotConfigured" => "未配置公差",
            "NotApplicable" => "不适用",
            "Review" => "需复核",
            _ => verdict ?? string.Empty
        };

        /// <summary>把前置检查级别翻译成报告用语。</summary>
        public static string DescribeLevel(MeasurementReadinessLevel level) => level switch
        {
            MeasurementReadinessLevel.Passed => "通过",
            MeasurementReadinessLevel.Warning => "警告",
            _ => "阻断"
        };

        /// <summary>把检测证据状态码翻译成报告用语。</summary>
        public static string DescribeDetectionStatus(string? status) => status switch
        {
            "NotApplicable" => "不适用",
            "NotMeasured" => "未测得",
            "Passed" => "通过",
            "Review" => "需复核",
            "Failed" => "检测失败",
            _ => status ?? string.Empty
        };

        /// <summary>测量能力描述：能出物理量、能出判定是两件不同的事。</summary>
        public static string DescribeMeasurementCapability(MeasurementWorkflowReadiness readiness) =>
            readiness.CanProduceSpecificationVerdict
                ? "可给出尺寸判定"
                : readiness.CanProducePhysicalResults
                    ? "只能给出物理测量值，无法给出尺寸判定"
                    : "无法给出物理单位的测量结果";

        /// <summary>标定身份：标识、时间、畸变模型；缺失时显式说明为什么缺失。</summary>
        public static string DescribeCalibrationIdentity(CameraCalibration? calibration)
        {
            if (calibration is not { IsEnabled: true })
            {
                return "无（仅比例标定或未标定）";
            }

            if (!calibration.IsValid)
            {
                return "非法（已按恒等处理，尺寸结果不可信）";
            }

            string id = string.IsNullOrWhiteSpace(calibration.CalibrationId) ? "（无标识）" : calibration.CalibrationId!;
            string when = calibration.CalibratedAtUtc is { } timestamp
                ? timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                : "（无时间戳）";
            string model = calibration.HasExtendedDistortionModel ? "扩展畸变模型" : "径向畸变 (K1/K2)";
            return $"{id} · {when} · {model}";
        }

        /// <summary>格式化带单位的数值；无值或非有限值统一记为 <c>—</c>。</summary>
        public static string FormatValue(double? value, string? unit, string format = "G6")
        {
            if (value is not { } number || !double.IsFinite(number))
            {
                return "—";
            }

            string text = number.ToString(format, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(unit) || unit == "px" ? text : text + " " + unit;
        }

        /// <summary>格式化标称值 ± 公差；没有公差时记为 <c>—</c>。</summary>
        public static string FormatTolerance(MeasurementReportItem item)
        {
            if (item.NominalValue is not { } nominal || !double.IsFinite(nominal))
            {
                return "—";
            }

            double plus = item.TolerancePlus is { } p && double.IsFinite(p) ? p : 0;
            double minus = item.ToleranceMinus is { } m && double.IsFinite(m) ? m : 0;
            if (plus == 0 && minus == 0)
            {
                return "—";
            }

            return string.Create(CultureInfo.InvariantCulture, $"+{plus:G6} / -{minus:G6}");
        }

        /// <summary>来源哈希只显示前缀，避免报告头部被 64 个十六进制字符占满。</summary>
        public static string ShortenHash(string hash) => hash.Length <= 12 ? hash : hash[..12];
    }
}
