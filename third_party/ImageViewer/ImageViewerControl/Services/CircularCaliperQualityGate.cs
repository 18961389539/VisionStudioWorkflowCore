using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CoreCircularDetectionResult = ImageViewer.Core.Analysis.CircularCaliperDetectionResult;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    /// <summary>
    /// 将圆卡尺的检测指标转换为面向质检的质量等级。
    /// Chinese: 检测成功不等于结果可信；质量门控使用覆盖范围、有效卡尺比例、置信度和拟合残差再次分级。
    /// English: A detector success is not automatically a trustworthy inspection result; this gate grades the evidence.
    /// </summary>
    internal static class CircularCaliperQualityGate
    {
        public static CircularCaliperQualityAssessment Evaluate(
            CircularCaliperMeasureRoi caliper,
            CircularCaliperDetectionResult result,
            ImageAnalysisQualityProfile? profile = null)
        {
            return Evaluate(caliper, result.ToCore(), profile);
        }

        public static CircularCaliperQualityAssessment Evaluate(
            CircularCaliperMeasureRoi caliper,
            CoreCircularDetectionResult result,
            ImageAnalysisQualityProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(caliper);
            profile ??= ImageAnalysisQualityProfile.Default;
            profile.Validate();

            double validRatio = Math.Clamp((double)result.ValidCaliperCount / Math.Max(1, caliper.CaliperCount), 0, 1);
            double angularCoverage = ComputeAngularCoverage(result.EdgePoints, result.DetectedCenter);
            if (!double.IsFinite(result.DetectedRadius) || result.DetectedRadius <= 0 ||
                !double.IsFinite(result.ResidualRms) || !double.IsFinite(result.Confidence))
            {
                return Failed(validRatio, angularCoverage, CircularCaliperQualityReason.InvalidGeometry);
            }

            double reviewResidualLimit = Math.Max(profile.ReviewResidualRms, result.DetectedRadius * 0.15);
            if (result.Confidence < profile.MinimumReviewConfidence)
            {
                return Failed(validRatio, angularCoverage, CircularCaliperQualityReason.LowConfidence);
            }

            if (validRatio < profile.MinimumReviewValidRatio)
            {
                return Failed(validRatio, angularCoverage, CircularCaliperQualityReason.InsufficientValidCalipers);
            }

            if (angularCoverage < profile.MinimumReviewAngularCoverageDegrees)
            {
                return Failed(validRatio, angularCoverage, CircularCaliperQualityReason.InsufficientAngularCoverage);
            }

            if (result.ResidualRms > reviewResidualLimit)
            {
                return Failed(validRatio, angularCoverage, CircularCaliperQualityReason.HighResidual);
            }

            double passResidualLimit = Math.Min(profile.PassResidualRms, Math.Max(0.5, result.DetectedRadius * 0.04));
            CircularCaliperQualityStatus status = result.Confidence >= profile.PassConfidence &&
                validRatio >= profile.PassValidRatio &&
                angularCoverage >= profile.PassAngularCoverageDegrees &&
                result.ResidualRms <= passResidualLimit
                ? CircularCaliperQualityStatus.Passed
                : CircularCaliperQualityStatus.Review;

            return new CircularCaliperQualityAssessment(
                status,
                validRatio,
                angularCoverage,
                status == CircularCaliperQualityStatus.Review
                    ? CircularCaliperQualityReason.BorderlineEvidence
                    : CircularCaliperQualityReason.None);
        }

        private static CircularCaliperQualityAssessment Failed(
            double validRatio,
            double angularCoverage,
            CircularCaliperQualityReason reason)
        {
            return new CircularCaliperQualityAssessment(
                CircularCaliperQualityStatus.Failed,
                validRatio,
                angularCoverage,
                reason);
        }

        private static double ComputeAngularCoverage(IReadOnlyList<PointD> points, PointD center)
        {
            if (points.Count < 3)
            {
                return 0;
            }

            List<double> angles = points
                .Select(point =>
                {
                    double angle = Math.Atan2(point.Y - center.Y, point.X - center.X);
                    return angle < 0 ? angle + Math.PI * 2 : angle;
                })
                .OrderBy(angle => angle)
                .ToList();
            double largestGap = 0;
            for (int i = 1; i < angles.Count; i++)
            {
                largestGap = Math.Max(largestGap, angles[i] - angles[i - 1]);
            }

            largestGap = Math.Max(largestGap, angles[0] + Math.PI * 2 - angles[^1]);
            return Math.Max(0, (Math.PI * 2 - largestGap) * 180 / Math.PI);
        }
    }

}
