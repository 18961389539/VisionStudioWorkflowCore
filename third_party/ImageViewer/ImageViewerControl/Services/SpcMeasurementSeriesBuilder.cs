using System;
using System.Collections.Generic;
using System.Linq;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>
/// 一条可做 SPC 分析的测量序列。
/// Chinese: <see cref="Values"/> 的顺序就是抽样顺序；同一量名、同一单位的不同 ROI 才归为一条序列，
/// 混淆量名或单位会把不同物理意义的数据混进同一个分布。
/// </summary>
internal sealed record SpcMeasurementSeries(
    string Key,
    string Label,
    string Unit,
    IReadOnlyList<double> Values,
    double? LowerSpecificationLimit,
    double? UpperSpecificationLimit)
{
    /// <summary>下拉框显示名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Unit) ? Label : $"{Label} ({Unit})";

    /// <summary>是否配置了任意一侧规范限。</summary>
    public bool HasSpecificationLimits => LowerSpecificationLimit.HasValue || UpperSpecificationLimit.HasValue;
}

/// <summary>
/// 从当前图像 ROI 或批次导出行提取 SPC 序列。
/// Chinese: 单张图内同量名的多个 ROI 是"同一工件的重复特征"，跨图同名项才是过程随时间的样本。
/// 未测得（null/NaN）的值直接跳过，不当作 0：漏测不是"测得为零"。
/// 公差只在所有已配置公差一致时作为规范限使用，互相矛盾的公差会让判据失去意义，此时返回无规范限。
/// </summary>
internal static class SpcMeasurementSeriesBuilder
{
    /// <summary>由当前图像上的 ROI 构建序列（按 ROI 集合顺序抽样）。</summary>
    public static IReadOnlyList<SpcMeasurementSeries> BuildFromRois(
        IEnumerable<RoiBase> rois,
        double pixelSize,
        string? physicalUnit,
        CameraCalibration? calibration)
    {
        ArgumentNullException.ThrowIfNull(rois);

        var groups = new Dictionary<string, SeriesAccumulator>(StringComparer.Ordinal);
        var order = new List<SeriesAccumulator>();
        foreach (RoiBase roi in rois)
        {
            RoiInspectionResult inspection = RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration);
            if (inspection.MeasuredValue is not { } measured || !double.IsFinite(measured))
            {
                continue;
            }

            string quantity = RoiAnalysisExportService.DescribeQuantity(roi);
            string unit = RoiAnalysisExportService.DescribeUnit(roi, physicalUnit);
            string key = quantity + "\u001F" + unit;
            if (!groups.TryGetValue(key, out SeriesAccumulator? accumulator))
            {
                accumulator = new SeriesAccumulator(key, quantity, unit);
                groups.Add(key, accumulator);
                order.Add(accumulator);
            }

            accumulator.Add(measured, inspection.NominalValue, inspection.TolerancePlus, inspection.ToleranceMinus);
        }

        return order
            .Where(accumulator => accumulator.Values.Count >= 2)
            .Select(static accumulator => accumulator.ToSeries())
            .ToArray();
    }

    /// <summary>由批次导出行构建序列（按模板槽位跨图聚合，顺序即批次顺序）。</summary>
    public static IReadOnlyList<SpcMeasurementSeries> BuildFromBatchRows(
        IEnumerable<RoiAnalysisBatchCsvRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var groups = new Dictionary<string, SeriesAccumulator>(StringComparer.Ordinal);
        var order = new List<SeriesAccumulator>();
        int index = 0;
        foreach (RoiAnalysisBatchCsvRow row in rows)
        {
            string key = row.MeasurementItemKey ?? $"unidentified-row:{index}";
            index++;
            RoiInspectionResult inspection = row.Inspection;
            if (inspection.MeasuredValue is not { } measured || !double.IsFinite(measured))
            {
                continue;
            }

            if (!groups.TryGetValue(key, out SeriesAccumulator? accumulator))
            {
                accumulator = new SeriesAccumulator(key, row.DisplayLabel, row.Unit);
                groups.Add(key, accumulator);
                order.Add(accumulator);
            }

            accumulator.Add(measured, inspection.NominalValue, inspection.TolerancePlus, inspection.ToleranceMinus);
        }

        return order
            .Where(accumulator => accumulator.Values.Count >= 2)
            .Select(static accumulator => accumulator.ToSeries())
            .ToArray();
    }

    private sealed class SeriesAccumulator
    {
        private (double? Lower, double? Upper)? _limits;
        private bool _toleranceSeen;
        private bool _toleranceConflict;

        public SeriesAccumulator(string key, string label, string unit)
        {
            Key = key;
            Label = label;
            Unit = unit;
        }

        public string Key { get; }

        public string Label { get; }

        public string Unit { get; }

        public List<double> Values { get; } = [];

        public void Add(double value, double? nominal, double? plus, double? minus)
        {
            Values.Add(value);
            if (nominal is not { } center || !double.IsFinite(center))
            {
                return;
            }

            var limits = (
                Lower: minus is { } lower && double.IsFinite(lower) && lower >= 0 ? center - lower : (double?)null,
                Upper: plus is { } upper && double.IsFinite(upper) && upper >= 0 ? center + upper : (double?)null);
            if (!_toleranceSeen)
            {
                _toleranceSeen = true;
                _limits = limits;
            }
            else if (_limits != limits)
            {
                _toleranceConflict = true;
            }
        }

        public SpcMeasurementSeries ToSeries() => new(
            Key,
            Label,
            Unit,
            Values.ToArray(),
            _toleranceConflict ? null : _limits?.Lower,
            _toleranceConflict ? null : _limits?.Upper);
    }
}
