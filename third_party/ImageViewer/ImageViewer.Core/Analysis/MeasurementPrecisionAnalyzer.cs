using System;
using System.Collections.Generic;
using System.Linq;

namespace ImageViewer.Core.Analysis
{
    /// <summary>
    /// 重复性统计结果（同一目标多次测量的离散程度）。
    /// Chinese: 这是"精度验证闭环"的第一半——没有重复性，单次测量值无法支撑任何公差判定。
    /// 与 <see cref="ImageViewer.Core.Measurements.MeasurementStatistics"/> 的区别是刻意的：
    /// 后者用于卡尺边缘样本（过滤非正值、总体 σ、n 分母），这里用于测量结果的重复性研究
    /// （保留任意有限值含 0/负值、样本 σ、n-1 分母）——重复性必须用无偏估计量，否则样本少时会低估离散度。
    /// English: Dispersion statistics for repeated measurements of the same target. Deliberately distinct
    /// from <c>MeasurementStatistics</c>: that type filters non-positive caliper samples and uses the
    /// population σ; repeatability studies need the unbiased sample σ (n-1) and must not drop valid values.
    /// </summary>
    public sealed record RepeatabilityStatistics(
        int SampleCount,
        double Mean,
        double StandardDeviation,
        double Minimum,
        double Maximum,
        double Range)
    {
        /// <summary>
        /// 样本标准差（1σ，n-1 分母）。
        /// Chinese: 样本数 &lt; 2 时无定义，返回 null 而不是 0——0 会被误读成"完美重复性"。
        /// </summary>
        public double? StandardError => SampleCount >= 2 ? StandardDeviation / Math.Sqrt(SampleCount) : null;

        /// <summary>扩展不确定度（k=2，约 95% 置信）。</summary>
        public double? ExpandedUncertainty => StandardDeviation is var sd && SampleCount >= 2 ? sd * 2.0 : null;

        /// <summary>相对重复性（变异系数，百分比）；均值接近 0 时无意义。</summary>
        public double? CoefficientOfVariationPercent =>
            SampleCount >= 2 && Math.Abs(Mean) > 1e-12
                ? StandardDeviation / Math.Abs(Mean) * 100.0
                : null;
    }

    /// <summary>
    /// 与已知标准值的比对结果（物理精度）。
    /// Chinese: 这是"精度验证闭环"的第二半——重复性只说明"稳定"，偏差才说明"准"。
    /// English: Comparison of the measured value against a known reference (accuracy, not just precision).
    /// </summary>
    public sealed record AccuracyComparison(
        double MeasuredMean,
        double ReferenceValue,
        double Deviation,
        double AbsoluteDeviation,
        double? RelativeErrorPercent,
        bool IsWithinTolerance,
        double? ToleranceLimit)
    {
        /// <summary>判定用容差是否可用于给出结论。</summary>
        public bool HasVerdict => ToleranceLimit.HasValue;
    }

    /// <summary>
    /// 测量精度报告：重复性 + 已知样件偏差 + 标定不确定度。
    /// Chinese: 三者缺一不可。只报重复性会掩盖系统偏差；只报偏差无法判断单次测量可信度；
    /// 不报标定不确定度则无法解释偏差是否落在标定能力范围内。
    /// English: Combined precision report — repeatability, reference deviation, and calibration uncertainty.
    /// </summary>
    public sealed record MeasurementPrecisionReport(
        string? Label,
        string Unit,
        RepeatabilityStatistics Repeatability,
        AccuracyComparison? Accuracy,
        double? CalibrationUncertaintyPixels,
        double? CalibrationUncertaintyPhysical,
        double? ReprojectionErrorRms)
    {
        /// <summary>
        /// 综合不确定度（物理单位，k=2）：重复性扩展不确定度与标定不确定度按方和根合成。
        /// Chinese: 两者独立来源，不应简单相加；合成后才是"这次测量结果给出去时该带的误差范围"。
        /// </summary>
        public double? CombinedUncertainty
        {
            get
            {
                if (!Repeatability.ExpandedUncertainty.HasValue && !CalibrationUncertaintyPhysical.HasValue)
                {
                    return null;
                }

                double repeat = Repeatability.ExpandedUncertainty ?? 0;
                double calibration = CalibrationUncertaintyPhysical ?? 0;
                return Math.Sqrt(repeat * repeat + calibration * calibration);
            }
        }

        /// <summary>偏差是否超出综合不确定度（超出说明存在系统性误差，不只是随机噪声）。</summary>
        public bool? DeviationExceedsUncertainty =>
            Accuracy is { } accuracy && CombinedUncertainty is { } combined
                ? accuracy.AbsoluteDeviation > combined
                : null;
    }

    /// <summary>
    /// 精度验证服务：把"重复测量一组结果 + 一个已知标准值"变成可决策的精度报告。
    /// Chinese: 纯计算、无 UI/WPF 依赖，放在 Core 便于宿主与插件复用，也便于测试。
    /// English: Turns repeated measurements plus a known reference into an actionable precision report.
    /// </summary>
    public static class MeasurementPrecisionAnalyzer
    {
        /// <summary>
        /// 计算重复性统计。
        /// Chinese: 只接受有限值；NaN/Infinity 会被拒绝而不是静默污染均值。
        /// </summary>
        public static RepeatabilityStatistics ComputeRepeatability(IEnumerable<double> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            double[] samples = values.Where(double.IsFinite).ToArray();
            if (samples.Length == 0)
            {
                throw new ArgumentException("重复性统计至少需要一个有限测量值。", nameof(values));
            }

            double mean = samples.Average();
            double min = samples.Min();
            double max = samples.Max();
            double stdDev = 0;
            if (samples.Length >= 2)
            {
                double sumSquared = samples.Sum(value => (value - mean) * (value - mean));
                stdDev = Math.Sqrt(sumSquared / (samples.Length - 1));
            }

            return new RepeatabilityStatistics(samples.Length, mean, stdDev, min, max, max - min);
        }

        /// <summary>
        /// 与已知标准值比对。
        /// Chinese: ToleranceLimit 为 null 时只给偏差不给结论（IsWithinTolerance=false 会被误读成"不合格"）。
        /// </summary>
        public static AccuracyComparison CompareToReference(double measuredValue, double referenceValue, double? toleranceLimit = null)
        {
            if (!double.IsFinite(measuredValue))
            {
                throw new ArgumentOutOfRangeException(nameof(measuredValue), "测量值必须是有限数。");
            }

            if (!double.IsFinite(referenceValue))
            {
                throw new ArgumentOutOfRangeException(nameof(referenceValue), "标准值必须是有限数。");
            }

            if (toleranceLimit is { } limit && (!double.IsFinite(limit) || limit < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(toleranceLimit), "容差必须是有限非负数。");
            }

            double deviation = measuredValue - referenceValue;
            double absoluteDeviation = Math.Abs(deviation);
            double? relativeError = Math.Abs(referenceValue) > 1e-12
                ? deviation / referenceValue * 100.0
                : null;

            return new AccuracyComparison(
                measuredValue,
                referenceValue,
                deviation,
                absoluteDeviation,
                relativeError,
                toleranceLimit.HasValue && absoluteDeviation <= toleranceLimit.Value,
                toleranceLimit);
        }

        /// <summary>
        /// 生成完整精度报告。
        /// Chinese: pixelSize 用于把标定不确定度（像素）换算成物理量，与测量值同单位才可比较。
        /// </summary>
        public static MeasurementPrecisionReport BuildReport(
            IEnumerable<double> measuredValues,
            double? referenceValue = null,
            double? toleranceLimit = null,
            double pixelSize = 1.0,
            string? unit = null,
            string? label = null,
            double? calibrationUncertaintyPixels = null,
            double? reprojectionErrorRms = null)
        {
            ArgumentNullException.ThrowIfNull(measuredValues);
            if (!double.IsFinite(pixelSize) || pixelSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelSize), "像素尺寸必须是有限正数。");
            }

            RepeatabilityStatistics repeatability = ComputeRepeatability(measuredValues);
            AccuracyComparison? accuracy = referenceValue.HasValue
                ? CompareToReference(repeatability.Mean, referenceValue.Value, toleranceLimit)
                : null;

            double? uncertaintyPhysical = calibrationUncertaintyPixels is { } uncertainty && double.IsFinite(uncertainty) && uncertainty >= 0
                ? uncertainty * pixelSize
                : null;

            return new MeasurementPrecisionReport(
                label,
                string.IsNullOrWhiteSpace(unit) ? "px" : unit,
                repeatability,
                accuracy,
                calibrationUncertaintyPixels,
                uncertaintyPhysical,
                reprojectionErrorRms);
        }

        /// <summary>
        /// 把多轮测量结果按"每轮同一 ROI 的主测量值"聚合。
        /// Chinese: 输入是 (测量轮次 → 每轮各 ROI 的测量值)，输出是"每个 ROI 一条重复性统计"；
        /// 用于批量分析（多张图 / 多次采图）后的重复性评估。
        /// </summary>
        public static IReadOnlyDictionary<string, RepeatabilityStatistics> GroupRepeatability(
            IEnumerable<(string Key, double Value)> samples)
        {
            ArgumentNullException.ThrowIfNull(samples);

            return samples
                .Where(sample => double.IsFinite(sample.Value) && !string.IsNullOrWhiteSpace(sample.Key))
                .GroupBy(sample => sample.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => ComputeRepeatability(group.Select(sample => sample.Value)),
                    StringComparer.Ordinal);
        }
    }
}
