using System;
using System.Collections.Generic;

namespace ImageViewer.Models
{
    /// <summary>
    /// 逐点去畸变审计结果。
    /// Chinese: 把"牛顿反解是否收敛、最坏残差多大、最大不确定度多少"变成一组可随报告
    /// 传递的数字，避免只在 Undistort 内部知道而未收敛点被静默当成正常点使用。
    /// English: Per-point undistortion audit. Carries convergence counts, the worst inversion
    /// residual and the worst 1-sigma uncertainty so a non-converged point cannot be silently
    /// treated as an ordinary measurement point downstream.
    /// </summary>
    public sealed record DistortionAudit(
        int PointCount,
        int NonConvergedPointCount,
        double MaximumResidualPixels,
        double MaximumUncertaintyPixels)
    {
        /// <summary>空审计：没有点参与反解，或标定未启用畸变校正。</summary>
        public static DistortionAudit Empty { get; } = new(0, 0, 0, 0);

        /// <summary>是否存在未收敛的测量点。</summary>
        public bool HasNonConvergedPoints => NonConvergedPointCount > 0;

        /// <summary>是否有任何测量点参与审计。</summary>
        public bool HasPoints => PointCount > 0;

        /// <summary>
        /// 合并两个审计结果：点数与未收敛数相加，残差与不确定度取最大值。
        /// Chinese: 用于把逐 ROI 审计汇总成报告级结论；点数只用于说明覆盖范围，不做去重。
        /// </summary>
        public DistortionAudit Combine(DistortionAudit? other)
        {
            if (other is null)
            {
                return this;
            }

            return new DistortionAudit(
                PointCount + other.PointCount,
                NonConvergedPointCount + other.NonConvergedPointCount,
                Math.Max(MaximumResidualPixels, other.MaximumResidualPixels),
                Math.Max(MaximumUncertaintyPixels, other.MaximumUncertaintyPixels));
        }
    }

    /// <summary>
    /// 逐点去畸变审计入口。
    /// Chinese: 与 <see cref="CameraCalibration.GetPointUncertaintyPixels"/> 使用同一套反解与
    /// 不确定度规则，保证"报告里的数字"和"单点查询的结果"不会各算一套。
    /// English: Single entry point for pointwise auditing; uses the same inversion and uncertainty
    /// rules as <see cref="CameraCalibration.GetPointUncertaintyPixels"/>.
    /// </summary>
    public static class CameraCalibrationAudit
    {
        /// <summary>
        /// 审计一组图像点：统计未收敛点数、最大反解残差与最大 1σ 不确定度。
        /// Chinese: 标定未启用或参数非法时返回空审计（此时不做畸变校正，也没有反解失败可言）；
        /// 非有限坐标的点由上游几何校验负责拒绝，这里跳过以免污染残差统计。
        /// </summary>
        public static DistortionAudit Audit(CameraCalibration? calibration, IEnumerable<PointD> points)
        {
            ArgumentNullException.ThrowIfNull(points);

            if (calibration is not { IsEnabled: true, IsValid: true })
            {
                return DistortionAudit.Empty;
            }

            int pointCount = 0;
            int nonConvergedCount = 0;
            double maximumResidual = 0;
            double maximumUncertainty = calibration.MeasurementUncertaintyPixels;
            foreach (PointD point in points)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                {
                    continue;
                }

                pointCount++;
                UndistortResult result = calibration.Undistort(point);
                maximumResidual = Math.Max(maximumResidual, result.ResidualPixels);
                if (result.Converged)
                {
                    continue;
                }

                nonConvergedCount++;
                maximumUncertainty = Math.Max(maximumUncertainty, result.ResidualPixels);
            }

            return new DistortionAudit(pointCount, nonConvergedCount, maximumResidual, maximumUncertainty);
        }
    }
}
