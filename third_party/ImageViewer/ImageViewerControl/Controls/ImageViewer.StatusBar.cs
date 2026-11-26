using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        /// <summary>
        /// 更新常驻状态栏：显示当前图像像素尺寸与文件路径；无图像时显示"未加载数据"。
        /// Chinese: 每次图像源变化时刷新状态栏上的图像信息，无图像时给出占位提示。
        /// English: Refreshes the persistent status bar with the current image size and file path.
        /// </summary>
        private void UpdateStatusBar()
        {
            if (statusBarTextBlock is null || statusBarBorder is null)
            {
                return;
            }

            string text;
            if (ImageSource is null || !ImageViewerImageSourceUtilities.TryGetSourceImageSize(ImageSource, out Size imageSize))
            {
                text = UiText.Get("StatusNoDataLoaded");
                statusBarTextBlock.ToolTip = null;
            }
            else
            {
                string? filePath = _controlComposition?.ViewportController.TryGetCurrentImagePath();
                string dimensions = UiText.Format("StatusBarImageSizeOnly", (int)imageSize.Width, (int)imageSize.Height);
                text = string.IsNullOrEmpty(filePath)
                    ? dimensions
                    : UiText.Format("StatusBarImageSizeAndPath", (int)imageSize.Width, (int)imageSize.Height, Path.GetFileName(filePath));
                statusBarTextBlock.ToolTip = filePath ?? dimensions;
            }

            if (!string.Equals(statusBarTextBlock.Text, text, StringComparison.Ordinal))
            {
                statusBarTextBlock.Text = text;
                // 状态栏已声明 LiveSetting=Polite，文本变化时主动通知屏幕阅读器
                RaiseLiveRegionChanged(statusBarTextBlock);
            }

            statusBarBorder.Visibility = Visibility.Visible;
            UpdateCalibrationIndicator();
        }

        /// <summary>
        /// 更新常驻标定状态。只要像素尺寸、单位或镜头畸变参数被设置，就在状态栏中保留醒目标识。
        /// Chinese: 标定状态不再依赖一次性的成功提示或选中 ROI 才能看到。
        /// English: Keep calibration visible in the status bar instead of relying on a transient hint or a selected ROI.
        /// </summary>
        private void UpdateCalibrationIndicator()
        {
            if (calibrationBadge is null || calibrationBadgeTextBlock is null)
            {
                return;
            }

            string unit = string.IsNullOrWhiteSpace(PhysicalUnit)
                ? UiText.Get("InfoUnitPixels")
                : PhysicalUnit.Trim();
            bool isCalibrated = double.IsFinite(PixelSize)
                && PixelSize > 0
                && (Calibration is not null
                    || Math.Abs(PixelSize - 1.0) > 1e-9
                    || !string.Equals(unit, UiText.Get("InfoUnitPixels"), StringComparison.OrdinalIgnoreCase));

            // A host may configure calibration before assigning an image. Keep that
            // state visible so the calibration UI is not coupled to image loading.
            if (ImageSource is null && !isCalibrated)
            {
                calibrationBadge.Visibility = Visibility.Collapsed;
                calibrationBadgeTextBlock.Text = string.Empty;
                calibrationBadgeTextBlock.ToolTip = null;
                return;
            }

            string text = isCalibrated
                ? UiText.FormatInvariant("CalibrationBadgeText", PixelSize, unit)
                : UiText.Get("CalibrationBadgePixels");
            if (isCalibrated)
            {
                text += " " + UiText.Get(Calibration switch
                {
                    { IsValid: false } => "CalibrationBadgeInvalidDistortionSuffix",
                    { IsEnabled: true } => "CalibrationBadgeDistortionSuffix",
                    _ => "CalibrationBadgeScaleOnlySuffix"
                });
            }

            // 标定精度元数据（重投影 RMS / 不确定度）之前没有任何出口，用户无法判断标定是否够用。
            // 把它挂在同一个徽标上：徽标文本给概览，ToolTip 给可直接核对的完整精度档案。
            string? accuracySuffix = null;
            if (isCalibrated && Calibration is { } calibration)
            {
                if (calibration.MeasurementUncertaintyPixels > 0)
                {
                    accuracySuffix = UiText.FormatInvariant(
                        "CalibrationBadgeUncertaintySuffix",
                        FormatPixelSizeLength(calibration.MeasurementUncertaintyPixels, PixelSize, unit),
                        unit);
                }
                else if (calibration.ReprojectionErrorRms > 0)
                {
                    accuracySuffix = UiText.FormatInvariant(
                        "CalibrationBadgeReprojectionSuffix",
                        FormatPixelSizeLength(calibration.ReprojectionErrorRms, PixelSize, unit),
                        unit);
                }
            }

            if (!string.IsNullOrEmpty(accuracySuffix))
            {
                text += " " + accuracySuffix;
            }

            bool textChanged = !string.Equals(calibrationBadgeTextBlock.Text, text, StringComparison.Ordinal);
            calibrationBadgeTextBlock.Text = text;
            calibrationBadgeTextBlock.ToolTip = BuildCalibrationTooltip(isCalibrated, unit);
            calibrationBadge.Visibility = Visibility.Visible;
            if (textChanged)
            {
                RaiseLiveRegionChanged(calibrationBadgeTextBlock);
            }
        }

        /// <summary>
        /// 把像素量换算成物理量用于精度描述。
        /// Chinese: PixelSize 未标定（1.0/无效）时退回像素单位，避免出现"1 px = 1 mm"的暗示。
        /// </summary>
        private static string FormatPixelSizeLength(double pixels, double pixelSize, string unit)
        {
            bool isPhysical = double.IsFinite(pixelSize) && pixelSize > 0
                && !string.Equals(unit, UiText.Get("InfoUnitPixels"), StringComparison.OrdinalIgnoreCase);
            return isPhysical
                ? (pixels * pixelSize).ToString("G4", CultureInfo.CurrentCulture)
                : pixels.ToString("G4", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// 构造标定详情 ToolTip：畸变模型 + 精度元数据 + 标定标识/时间。
        /// Chinese: 这些字段此前完全没有 UI 出口，是"支持亚像素不等于测准"的关键证据链。
        /// </summary>
        private string BuildCalibrationTooltip(bool isCalibrated, string unit)
        {
            if (!isCalibrated)
            {
                return UiText.Get("CalibrationBadgeUncalibratedText");
            }

            var builder = new StringBuilder();
            builder.Append(UiText.FormatInvariant("CalibrationBadgeTooltipScale", PixelSize, unit));
            if (Calibration is not { } calibration)
            {
                return builder.AppendLine().Append(UiText.Get("CalibrationBadgeTooltipNoDistortion")).ToString();
            }

            builder.Append(Environment.NewLine);
            if (!calibration.IsValid)
            {
                builder.Append(UiText.Get("CalibrationBadgeTooltipInvalidDistortion"));
            }
            else if (calibration.HasExtendedDistortionModel)
            {
                builder.Append(UiText.FormatInvariant(
                    "CalibrationBadgeTooltipDistortionExtended",
                    calibration.K1,
                    calibration.K2,
                    calibration.K3,
                    calibration.TangentialP1,
                    calibration.TangentialP2));
            }
            else if (calibration.IsEnabled)
            {
                builder.Append(UiText.FormatInvariant("CalibrationBadgeTooltipDistortion", calibration.K1, calibration.K2));
            }
            else
            {
                builder.Append(UiText.Get("CalibrationBadgeTooltipNoDistortion"));
            }

            if (calibration.ReprojectionErrorRms > 0 || calibration.MeasurementUncertaintyPixels > 0)
            {
                builder.Append(Environment.NewLine);
                builder.Append(UiText.FormatInvariant(
                    "CalibrationBadgeTooltipAccuracy",
                    calibration.ReprojectionErrorRms,
                    calibration.MeasurementUncertaintyPixels,
                    FormatPixelSizeLength(calibration.MeasurementUncertaintyPixels, PixelSize, unit),
                    unit));
            }

            if (!string.IsNullOrWhiteSpace(calibration.CalibrationId) || calibration.CalibratedAtUtc.HasValue)
            {
                builder.Append(Environment.NewLine);
                builder.Append(UiText.FormatInvariant(
                    "CalibrationBadgeTooltipIdentity",
                    string.IsNullOrWhiteSpace(calibration.CalibrationId) ? UiText.Get("CalibrationBadgeNoId") : calibration.CalibrationId,
                    calibration.CalibratedAtUtc.HasValue
                        ? calibration.CalibratedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
                        : UiText.Get("CalibrationBadgeNoTimestamp")));
            }

            return builder.ToString();
        }
    }
}
