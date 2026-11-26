namespace ImageViewer.Models
{
    /// <summary>
    /// 圆测量检测结果的质量门控状态。
    /// Chinese: 用于区分可以直接使用、需要人工复核和没有可靠检测结果的圆测量。
    /// English: Quality-gate state for circular measurement results.
    /// </summary>
    public enum CircularCaliperQualityStatus
    {
        NotMeasured,
        Passed,
        Review,
        Failed
    }

    public enum CircularCaliperQualityReason
    {
        NotMeasured,
        None,
        DetectionFailed,
        InvalidGeometry,
        LowConfidence,
        InsufficientValidCalipers,
        InsufficientAngularCoverage,
        HighResidual,
        BorderlineEvidence
    }

    internal readonly record struct CircularCaliperQualityAssessment(
        CircularCaliperQualityStatus Status,
        double ValidRatio,
        double AngularCoverageDegrees,
        CircularCaliperQualityReason Reason);
}
