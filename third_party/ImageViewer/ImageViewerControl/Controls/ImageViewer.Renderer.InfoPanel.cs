using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private bool IsInteractiveRoiManipulationActive()
        {
            return _interactionManipulationState.HasActiveRoiManipulation;
        }

        private void CancelPendingInfoPanelUpdate()
        {
            _infoPanelStatisticsScheduler.Cancel();
        }

        private void SetInfoPanelText(string text)
        {
            if (!string.Equals(ViewModel.InfoText, text, StringComparison.Ordinal))
            {
                ViewModel.InfoText = text;
            }

            if (!string.Equals(infoTextBlock.Text, text, StringComparison.Ordinal))
            {
                infoTextBlock.Text = text;
            }
        }

        private async Task QueueInfoPanelStatisticsUpdateAsync(RoiBase selectedRoi, RoiBase roiSnapshot, BitmapSource bitmap, string baseText, int delayMilliseconds = 120)
        {
            await _infoPanelStatisticsScheduler.ScheduleAsync(async cancellationToken =>
            {
                try
                {
                    string? statisticsText = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return RoiInfoService.TryBuildStatisticsText(bitmap, roiSnapshot, out string text) ? text : null;
                    }, cancellationToken);

                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(ViewModel.SelectedRoi, selectedRoi) || IsInteractiveRoiManipulationActive())
                    {
                        return;
                    }

                    string finalText = string.IsNullOrEmpty(statisticsText)
                        ? baseText
                        : string.Concat(baseText, Environment.NewLine, statisticsText);

                    _cachedInfoBitmap = bitmap;
                    _cachedInfoRoi = selectedRoi;
                    _cachedInfoPixelSize = PixelSize;
                    _cachedInfoUnit = PhysicalUnit;
                    _cachedInfoText = finalText;
                    SetInfoPanelText(finalText);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    LogNonCriticalError("Failed to update info panel statistics", ex);
                }
            }, delayMilliseconds);
        }

        private void UpdateInfoPanel(bool force = false)
        {
            var vm = ViewModel;
            if (!ShowInfoPanel)
            {
                CancelPendingInfoPanelUpdate();
                SetInfoPanelText(string.Empty);
                return;
            }

            string text = string.Empty;
            BitmapSource? bitmap = GetAnalysisBitmapSource();
            RoiBase? selectedRoi = vm.SelectedRoi;
            bool isInteractive = IsInteractiveRoiManipulationActive();

            if (selectedRoi != null)
            {
                text = BuildRoiInfo(selectedRoi, includeImageStatistics: false);
                _cachedInfoBitmap = null;
                _cachedInfoRoi = selectedRoi;
                _cachedInfoPixelSize = PixelSize;
                _cachedInfoUnit = PhysicalUnit;
                _cachedInfoText = text;
            }
            else
            {
                CancelPendingInfoPanelUpdate();
                _cachedInfoRoi = null;
                _cachedInfoBitmap = null;
                _cachedInfoPixelSize = PixelSize;
                _cachedInfoUnit = PhysicalUnit;
                _cachedInfoText = string.Empty;
            }

            SetInfoPanelText(text);

            if (selectedRoi == null || bitmap == null)
            {
                return;
            }

            if (isInteractive && !force)
            {
                CancelPendingInfoPanelUpdate();
                return;
            }

            _ = BackgroundOperationObserver.ObserveAsync(
                QueueInfoPanelStatisticsUpdateAsync(selectedRoi, selectedRoi.Clone(), bitmap, text, force ? 0 : 120),
                "Update info panel statistics");
        }

        private string BuildRoiInfo(RoiBase roi, bool includeImageStatistics = true)
        {
            return RoiInfoService.BuildInfo(roi, includeImageStatistics ? GetAnalysisBitmapSource() : null, PixelSize, PhysicalUnit, PluginRegistry, includeImageStatistics, Calibration);
        }

        private void UpdateHistogram(bool force = false)
        {
            _ = BackgroundOperationObserver.ObserveAsync(_analysisController.UpdateHistogram(force), "Update histogram");
        }

        private void UpdateProfile(bool force = false)
        {
            _ = BackgroundOperationObserver.ObserveAsync(_analysisController.UpdateProfile(force), "Update profile");
        }
    }
}
