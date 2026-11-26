#pragma warning disable CS1591

using System;

namespace ImageViewer.Models
{
    /// <summary>
    /// 可替换的检测质量门限配置。
    /// Chinese: 设备、镜头和工件变化时可以注入项目级门限，不必修改算法代码。
    /// English: Replaceable quality thresholds for project- and fixture-specific inspection limits.
    /// </summary>
    public sealed record ImageAnalysisQualityProfile
    {
        public double MaxCaliperScore { get; init; } = 255.0;
        public double MinimumDetectionConfidence { get; init; } = 0.05;

        public double MinimumReviewConfidence { get; init; } = 0.15;
        public double MinimumReviewValidRatio { get; init; } = 0.50;
        public double MinimumReviewAngularCoverageDegrees { get; init; } = 180.0;
        public double PassConfidence { get; init; } = 0.55;
        public double PassValidRatio { get; init; } = 0.70;
        public double PassAngularCoverageDegrees { get; init; } = 270.0;
        public double PassResidualRms { get; init; } = 1.5;
        public double ReviewResidualRms { get; init; } = 4.0;

        public int VolumeOverexposedMinimumBin { get; init; } = 250;
        public int VolumeUnderexposedMaximumBin { get; init; } = 5;
        public int VolumeLowContrastMinimumRange { get; init; } = 16;

        public int AutomaticCircleMinimumValidCalipers { get; init; } = 24;
        public double AutomaticCircleCaliperMinimumGradient { get; init; } = 8.0;
        public double AutomaticCircleMinimumConfidence { get; init; } = 0.15;
        public double AutomaticCircleMinimumAngularCoverageDegrees { get; init; } = 180.0;
        public double AutomaticCircleResidualFraction { get; init; } = 0.08;
        public double AutomaticCircleMinimumResidualPixels { get; init; } = 3.0;
        public double AutomaticCircleBoundaryEvidenceRatio { get; init; } = 0.60;

        public static ImageAnalysisQualityProfile Default { get; } = new();

        public void Validate()
        {
            ValidateUnitInterval(MinimumDetectionConfidence, nameof(MinimumDetectionConfidence));
            ValidateUnitInterval(MinimumReviewConfidence, nameof(MinimumReviewConfidence));
            ValidateUnitInterval(MinimumReviewValidRatio, nameof(MinimumReviewValidRatio));
            ValidateUnitInterval(PassConfidence, nameof(PassConfidence));
            ValidateUnitInterval(PassValidRatio, nameof(PassValidRatio));
            ValidatePositive(MaxCaliperScore, nameof(MaxCaliperScore));
            ValidateNonNegative(MinimumReviewAngularCoverageDegrees, nameof(MinimumReviewAngularCoverageDegrees));
            ValidateNonNegative(PassAngularCoverageDegrees, nameof(PassAngularCoverageDegrees));
            ValidatePositive(PassResidualRms, nameof(PassResidualRms));
            ValidatePositive(ReviewResidualRms, nameof(ReviewResidualRms));
            ValidateByteBin(VolumeOverexposedMinimumBin, nameof(VolumeOverexposedMinimumBin));
            ValidateByteBin(VolumeUnderexposedMaximumBin, nameof(VolumeUnderexposedMaximumBin));
            if (VolumeLowContrastMinimumRange < 0 || VolumeLowContrastMinimumRange > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(VolumeLowContrastMinimumRange), VolumeLowContrastMinimumRange, "Contrast range must be between 0 and 255.");
            }
            if (AutomaticCircleMinimumValidCalipers < 3 || AutomaticCircleMinimumValidCalipers > 180)
            {
                throw new ArgumentOutOfRangeException(nameof(AutomaticCircleMinimumValidCalipers), AutomaticCircleMinimumValidCalipers, "Automatic circle caliper count must be between 3 and 180.");
            }
            ValidatePositive(AutomaticCircleCaliperMinimumGradient, nameof(AutomaticCircleCaliperMinimumGradient));
            ValidateUnitInterval(AutomaticCircleMinimumConfidence, nameof(AutomaticCircleMinimumConfidence));
            ValidateNonNegative(AutomaticCircleMinimumAngularCoverageDegrees, nameof(AutomaticCircleMinimumAngularCoverageDegrees));
            ValidateUnitInterval(AutomaticCircleResidualFraction, nameof(AutomaticCircleResidualFraction));
            ValidatePositive(AutomaticCircleMinimumResidualPixels, nameof(AutomaticCircleMinimumResidualPixels));
            ValidateUnitInterval(AutomaticCircleBoundaryEvidenceRatio, nameof(AutomaticCircleBoundaryEvidenceRatio));
            if (PassConfidence < MinimumReviewConfidence || PassValidRatio < MinimumReviewValidRatio ||
                PassAngularCoverageDegrees < MinimumReviewAngularCoverageDegrees || PassResidualRms > ReviewResidualRms)
            {
                throw new ArgumentException("Pass thresholds must be at least as strict as review thresholds.");
            }
        }

        private static void ValidateUnitInterval(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(name, value, "Threshold must be finite and between 0 and 1.");
            }
        }

        private static void ValidatePositive(double value, string name)
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(name, value, "Threshold must be a finite positive value.");
            }
        }

        private static void ValidateNonNegative(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(name, value, "Threshold must be finite and non-negative.");
            }
        }

        private static void ValidateByteBin(int value, string name)
        {
            if (value < 0 || value > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(name, value, "Histogram threshold must be between 0 and 255.");
            }
        }
    }
}

#pragma warning restore CS1591
