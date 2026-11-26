using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    internal interface IImageViewerRoiPersistenceControllerHost
    {
        string? ShowSaveRoiDialog();

        string? ShowOpenRoiDialog();

        RoiPluginRegistry PluginRegistry { get; }

        IReadOnlyList<RoiBase> AllRois { get; }

        double PixelSize { get; set; }

        string PhysicalUnit { get; set; }

        ImageAnalysisQualityProfile QualityProfile { get; set; }

        /// <summary>把无法识别的 ROI 载荷交给会话的未识别通道。</summary>
        void AppendUnresolvedRois(IReadOnlyList<RoiPersistenceData> payloads);

        void ReplaceAllRois(IReadOnlyList<RoiBase> rois);

        void RefreshAllCaliperDetections();

        void DrawRois();

        void RefreshSelectedRoiPropertyPanel();

        void ShowNonCriticalError(string title, string message, Exception ex);

        void ShowStatusHint(string message, StatusHintKind kind);

        void ClearUndoHistory();
    }

    internal sealed class ImageViewerRoiPersistenceController
    {
        private readonly IImageViewerRoiPersistenceControllerHost _host;

        public ImageViewerRoiPersistenceController(IImageViewerRoiPersistenceControllerHost host)
        {
            _host = host;
        }

        public async Task SaveRoisAsync()
        {
            if (RoiToleranceDrafts.TryGetError(_host.AllRois, out string? message))
            {
                _host.ShowStatusHint(message, StatusHintKind.Error);
                return;
            }

            string? filePath = _host.ShowSaveRoiDialog();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                await RoiPersistenceService.SaveToFileAsync(filePath, _host.AllRois, _host.PixelSize, _host.PhysicalUnit, _host.PluginRegistry, qualityProfile: _host.QualityProfile);
                _host.ShowStatusHint(UiText.Get("StatusSaveRoiSuccess"), StatusHintKind.Success);
            }
            catch (Exception ex)
            {
                _host.ShowNonCriticalError(UiText.Get("ErrorSaveRoiTitle"), UiText.Get("ErrorSaveRoiMessage"), ex);
            }
        }

        public async Task LoadRoisAsync()
        {
            string? filePath = _host.ShowOpenRoiDialog();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                var result = await RoiPersistenceService.LoadFromFileAsync(filePath, _host.PluginRegistry);
                _host.ReplaceAllRois(result.Rois);
                _host.PixelSize = result.PixelSize;
                _host.PhysicalUnit = result.PhysicalUnit;
                if (result.QualityProfile is { } qualityProfile)
                {
                    _host.QualityProfile = qualityProfile;
                }
                _host.RefreshAllCaliperDetections();
                _host.DrawRois();
                _host.RefreshSelectedRoiPropertyPanel();
                _host.ClearUndoHistory();
                _host.ShowStatusHint(UiText.Format("StatusLoadRoiSuccess", result.Rois.Count), StatusHintKind.Success);
                _host.AppendUnresolvedRois(result.UnresolvedItems);
                ReportUnresolvedRois(result.UnresolvedItems);
            }
            catch (Exception ex)
            {
                _host.ShowNonCriticalError(UiText.Get("ErrorLoadRoiTitle"), UiText.Get("ErrorLoadRoiMessage"), ex);
            }
        }

        /// <summary>
        /// 提示无法识别的标注类型。
        /// Chinese: 缺少对应插件时这些标注不会被静默丢弃，状态栏按类型给出数量（源文件保持不变）。
        /// English: Reports payloads no plugin could resolve instead of dropping them silently.
        /// </summary>
        private void ReportUnresolvedRois(IReadOnlyList<RoiPersistenceData> unresolvedItems)
        {
            if (unresolvedItems.Count == 0)
            {
                return;
            }

            string typeNames = string.Join(
                ", ",
                unresolvedItems
                    .Select(item => item.Type)
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            _host.ShowStatusHint(
                UiText.Format("StatusLoadRoiUnresolved", unresolvedItems.Count, typeNames),
                StatusHintKind.Error);
        }
    }
}
