#pragma warning disable CS1591

using System;

namespace ImageViewer.Models
{
    /// <summary>
    /// 影像算法得到的证据质量。该状态只回答“检测结果是否可信”，不代表尺寸是否合格。
    /// </summary>
    public enum RoiDetectionStatus
    {
        NotApplicable,
        NotMeasured,
        Passed,
        Review,
        Failed
    }

    /// <summary>
    /// 将测量值与工艺公差比较后的状态。该状态独立于检测证据质量。
    /// </summary>
    public enum RoiSpecificationStatus
    {
        NotApplicable,
        NotConfigured,
        NotEvaluable,
        Passed,
        Failed
    }

    /// <summary>
    /// 一条 ROI 的检测质量和尺寸判定结果。
    /// </summary>
    public readonly record struct RoiInspectionResult(
        RoiDetectionStatus DetectionStatus,
        RoiSpecificationStatus SpecificationStatus,
        double? MeasuredValue,
        double? NominalValue,
        double? TolerancePlus,
        double? ToleranceMinus)
    {
        public DistortionAudit? DistortionAudit { get; init; }

        public bool HasMeasuredValue => MeasuredValue is { } value && double.IsFinite(value);
    }
}

#pragma warning restore CS1591
