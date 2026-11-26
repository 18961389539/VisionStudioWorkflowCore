using System;
using System.Collections.Generic;
using System.Linq;
using ImageViewer.Models;

namespace ImageViewer.Core.Analysis
{
    /// <summary>
    /// 单条前置检查的状态。
    /// Chinese: 分三档而不是布尔，因为"缺标定"和"标定非法"的严重程度不同：
    /// 前者可能只是用像素量、仍可出像素结果；后者会让所有尺寸判定都不可信。
    /// English: Three levels rather than a boolean — a missing calibration may still allow pixel-only
    /// results, while a malformed one invalidates every dimensional decision.
    /// </summary>
    public enum MeasurementReadinessLevel
    {
        /// <summary>检查通过。</summary>
        Passed = 0,

        /// <summary>可以继续，但结果的可解释性受限（例如未标定、无基准/无公差）。</summary>
        Warning = 1,

        /// <summary>不可继续：继续下去产出的数值必然是错的或不可判定的。</summary>
        Blocked = 2
    }

    /// <summary>
    /// 一条前置检查项。
    /// Chinese: 每项都带稳定的 Code（给程序判断/去重）与人读的 Message（给界面显示）。
    /// English: A single readiness check, carrying both a stable code and a human-readable message.
    /// </summary>
    public sealed record MeasurementReadinessCheck(
        string Code,
        MeasurementReadinessLevel Level,
        string Message)
    {
        /// <summary>该项是否阻止继续。</summary>
        public bool IsBlocking => Level == MeasurementReadinessLevel.Blocked;
    }

    /// <summary>
    /// "标定检查"步骤的结论：本次测量在数值上是否可信、还能继续哪一步。
    /// Chinese: 这是离线工作流（选择配方 → **标定检查** → 测量 → 复核 → 出报告）里的检查关口。
    /// 它刻意不去读图、不依赖 WPF，只根据"标定 + 像素比例 + 公差配置"给结论，因此
    /// 既能在打开图像前跑，也能在出报告前复跑（两者的判断依据完全一致）。
    /// English: The verdict of the calibration-check step. Deliberately UI-free and image-free so the
    /// same gate can run before acquisition and be re-run before reporting with identical evidence.
    /// </summary>
    public sealed record MeasurementWorkflowReadiness(IReadOnlyList<MeasurementReadinessCheck> Checks)
    {
        /// <summary>是否有任何阻断项。</summary>
        public bool IsBlocked => Checks.Any(check => check.IsBlocking);

        /// <summary>是否有任何警告项。</summary>
        public bool HasWarnings => Checks.Any(check => check.Level == MeasurementReadinessLevel.Warning);

        /// <summary>是否可以产出物理单位的尺寸结果（必须有可用标定比例）。</summary>
        public bool CanProducePhysicalResults { get; init; }

        /// <summary>是否可以给出尺寸判定（必须有可用标定、至少一个配公差的 ROI、且没有阻断项）。</summary>
        public bool CanProduceSpecificationVerdict { get; init; }

        /// <summary>最高严重级别，便于界面选颜色/图标。</summary>
        public MeasurementReadinessLevel HighestLevel =>
            Checks.Count == 0
                ? MeasurementReadinessLevel.Passed
                : Checks.Max(check => check.Level);

        /// <summary>所有阻断项（按出现顺序）。</summary>
        public IReadOnlyList<MeasurementReadinessCheck> BlockingChecks =>
            Checks.Where(check => check.IsBlocking).ToList();
    }

    /// <summary>
    /// 测量工作流前置检查服务。
    /// Chinese: 纯计算、无 WPF/Core-Persistence 依赖，宿主与插件都能复用；也是"标定检查"与
    /// 报告头部的唯一事实来源，避免两处各写一套判定而漂移。
    /// English: The single source of truth for measurement preconditions.
    /// </summary>
    public static class MeasurementWorkflowReadinessEvaluator
    {
        /// <summary>标定比例缺失/非法时的稳定代码。</summary>
        public const string CodeCalibrationScaleUnavailable = "calibration.scale-unavailable";

        /// <summary>标定参数存在但非法时的稳定代码。</summary>
        public const string CodeCalibrationInvalid = "calibration.invalid";

        /// <summary>有畸变系数但没有精度记录时的稳定代码。</summary>
        public const string CodeCalibrationAccuracyMissing = "calibration.accuracy-missing";

        /// <summary>没有已标定身份（CalibrationId/时间）时的稳定代码。</summary>
        public const string CodeCalibrationIdentityMissing = "calibration.identity-missing";

        /// <summary>测量点畸变反解未收敛时的稳定代码。</summary>
        public const string CodeCalibrationUndistortionNotConverged = "calibration.undistortion-not-converged";

        /// <summary>没有任何 ROI 时的稳定代码。</summary>
        public const string CodeNoRois = "workflow.no-rois";

        /// <summary>没有任何配公差的 ROI 时的稳定代码。</summary>
        public const string CodeNoTolerance = "workflow.no-tolerance";

        /// <summary>存在配公差但未测到值的 ROI 时的稳定代码。</summary>
        public const string CodeRoiNotMeasured = "workflow.roi-not-measured";

        /// <summary>评估测量工作流能否继续。</summary>
        public static MeasurementWorkflowReadiness Evaluate(
            double pixelSize, string? physicalUnit, CameraCalibration? calibration,
            int roiCount, int tolerancedRoiCount, int unmeasuredTolerancedRoiCount = 0, bool hasKnownReference = false)
            => Evaluate(pixelSize, physicalUnit, calibration, roiCount, tolerancedRoiCount, null,
                unmeasuredTolerancedRoiCount, hasKnownReference);

        /// <summary>
        /// 评估测量工作流能否继续。
        /// </summary>
        /// <param name="pixelSize">每像素的物理长度；&lt;= 0 或非有限值表示未标定比例。</param>
        /// <param name="physicalUnit">物理单位；为空表示单位为像素。</param>
        /// <param name="calibration">畸变/精度标定记录，可为 null。</param>
        /// <param name="roiCount">参与测量的 ROI 数量。</param>
        /// <param name="tolerancedRoiCount">其中配置了公差的数量。</param>
        /// <param name="unmeasuredTolerancedRoiCount">其中配了公差但当前没有测得值的数量。</param>
        /// <param name="hasKnownReference">是否已知标准样件值（有才谈得上精度/偏差结论）。</param>
        /// <param name="distortionAudit">参与测量的点的逐点去畸变审计；为 null 表示未审计。</param>
        public static MeasurementWorkflowReadiness Evaluate(
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration,
            int roiCount,
            int tolerancedRoiCount,
            DistortionAudit? distortionAudit,
            int unmeasuredTolerancedRoiCount = 0,
            bool hasKnownReference = false)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(roiCount);
            ArgumentOutOfRangeException.ThrowIfNegative(tolerancedRoiCount);
            ArgumentOutOfRangeException.ThrowIfNegative(unmeasuredTolerancedRoiCount);

            var checks = new List<MeasurementReadinessCheck>();
            bool scaleAvailable = double.IsFinite(pixelSize) && pixelSize > 0;

            if (!scaleAvailable)
            {
                // 未标定不等于"不能测"，但产出的永远是像素数，任何物理公差都无从判定。
                checks.Add(new MeasurementReadinessCheck(
                    CodeCalibrationScaleUnavailable,
                    MeasurementReadinessLevel.Blocked,
                    "未设置像素标定比例（1 px = ? 物理长度），只能得到像素级数值，无法进行任何尺寸判定。"));
            }

            if (calibration is { IsEnabled: true, IsValid: false })
            {
                // 配了畸变但系数不完整——静默按恒等处理会让所有尺寸判定失真，直接阻断。
                checks.Add(new MeasurementReadinessCheck(
                    CodeCalibrationInvalid,
                    MeasurementReadinessLevel.Blocked,
                    "标定参数不完整或非法（非有限值/归一化半径 <= 0），畸变校正无法应用，尺寸结果不可信。"));
            }
            else if (calibration is { HasExtendedDistortionModel: true } && !HasAccuracyMetadata(calibration))
            {
                // 用了高阶畸变模型却说不出自己的精度，等于无法解释残差。
                checks.Add(new MeasurementReadinessCheck(
                    CodeCalibrationAccuracyMissing,
                    MeasurementReadinessLevel.Warning,
                    "标定使用了高阶畸变模型（K3/P1/P2）但未记录重投影误差或测量不确定度，无法判断偏差是否在标定能力范围内。"));
            }
            else if (calibration is { IsEnabled: true } && !HasAccuracyMetadata(calibration))
            {
                checks.Add(new MeasurementReadinessCheck(
                    CodeCalibrationAccuracyMissing,
                    MeasurementReadinessLevel.Warning,
                    "标定未记录重投影误差或测量不确定度，报告无法给出综合不确定度。"));
            }

            if (calibration is { IsEnabled: true, IsValid: true } && string.IsNullOrWhiteSpace(calibration.CalibrationId))
            {
                checks.Add(new MeasurementReadinessCheck(
                    CodeCalibrationIdentityMissing,
                    MeasurementReadinessLevel.Warning,
                    "标定没有标识（CalibrationId），结果无法追溯到具体的那次标定。"));
            }

            if (distortionAudit is { HasNonConvergedPoints: true })
            {
                checks.Add(new MeasurementReadinessCheck(
                    CodeCalibrationUndistortionNotConverged,
                    MeasurementReadinessLevel.Warning,
                    FormattableString.Invariant(
                        $"有 {distortionAudit.NonConvergedPointCount}/{distortionAudit.PointCount} 个测量点的畸变反解未收敛（最大残差 {distortionAudit.MaximumResidualPixels:G4} px），相关尺寸和公差判定不可评估。")));
            }

            if (roiCount <= 0)
            {
                checks.Add(new MeasurementReadinessCheck(
                    CodeNoRois,
                    MeasurementReadinessLevel.Blocked,
                    "当前没有参与测量的 ROI，没有可报告的结果。"));
            }
            else if (tolerancedRoiCount <= 0)
            {
                checks.Add(new MeasurementReadinessCheck(
                    CodeNoTolerance,
                    MeasurementReadinessLevel.Warning,
                    "没有任何 ROI 配置公差，只能报告测量值，无法给出合格/不合格判定。"));
            }

            if (unmeasuredTolerancedRoiCount > 0)
            {
                checks.Add(new MeasurementReadinessCheck(
                    CodeRoiNotMeasured,
                    MeasurementReadinessLevel.Warning,
                    $"有 {unmeasuredTolerancedRoiCount} 个配置了公差的 ROI 尚无测得值（未检测或检测被拒绝），这些项将被判为不可评估。"));
            }

            if (!hasKnownReference && tolerancedRoiCount > 0)
            {
                // 精度闭环需要已知样件；没有它只能谈重复性，不能谈准确性。
                checks.Add(new MeasurementReadinessCheck(
                    "workflow.no-reference",
                    MeasurementReadinessLevel.Warning,
                    "未提供已知尺寸的标准样件值，只能评估重复性，无法评估测量准确性（偏差）。"));
            }

            bool hasBlocking = checks.Any(check => check.IsBlocking);
            return new MeasurementWorkflowReadiness(checks)
            {
                CanProducePhysicalResults = scaleAvailable && !hasBlocking,
                CanProduceSpecificationVerdict =
                    scaleAvailable && !hasBlocking && tolerancedRoiCount > 0 && unmeasuredTolerancedRoiCount < tolerancedRoiCount
            };
        }

        private static bool HasAccuracyMetadata(CameraCalibration calibration)
        {
            return calibration.ReprojectionErrorRms > 0 || calibration.MeasurementUncertaintyPixels > 0;
        }
    }
}
