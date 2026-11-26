using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class AdaptiveImageViewer : UserControl, IDisposable, IAsyncDisposable
    {
        private SegmentationResult? _pendingSegmentation;
        private VolumeQualityReport? _qualityReport;

        private async void OnQualityClick(object sender, RoutedEventArgs e)
        {
            if (_volume == null)
            {
                statusText.Text = UiText.Get("StatusLoadVolumeFirstQuality");
                return;
            }

            CancellationTokenSource operation = BeginOperation(UiText.Get("StatusAnalyzingQuality"));
            dataQualityPanel.Visibility = Visibility.Visible;
            try
            {
                VolumeData volume = _volume!;
                VolumeQualityReport report = await Task.Run(() => VolumeQualityAnalyzer.Analyze(volume, _imageViewer.QualityProfile), operation.Token);
                if (!IsCurrentOperation(operation))
                {
                    return;
                }

                _qualityReport = report;
                anomalyList.Items.Clear();
                foreach (VolumeAnomaly anomaly in report.Anomalies)
                {
                    anomalyList.Items.Add(UiText.Format("StatusAnomalyItem", anomaly.SliceIndex + 1, anomaly.Message));
                }

                statusText.Text = report.HasAnomalies ? UiText.Format("StatusQualityFound", report.Anomalies.Count) : UiText.Get("StatusQualityPassed");
                retryButton.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                if (IsCurrentOperation(operation))
                {
                    statusText.Text = UiText.Get("StatusQualityCancelled");
                }
            }
            catch (Exception)
            {
                if (IsCurrentOperation(operation))
                {
                    statusText.Text = UiText.Get("StatusQualityFailed");
                    retryButton.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                bool wasCurrent = ReferenceEquals(_operationCancellation, operation);
                EndOperation(operation);
                if (wasCurrent)
                {
                    UpdateDataQualityPanelVisibility();
                }
            }
        }

        private async void OnSegmentClick(object sender, RoutedEventArgs e)
        {
            if (_volume == null)
            {
                statusText.Text = UiText.Get("StatusLoadVolumeFirstSegmentation");
                return;
            }

            VolumeData volume = _volume!;
            int sliceIndex = Math.Max(0, _volumeViewer.CurrentSliceIndex);
            BitmapSource slice = volume.GetAxialSlice(sliceIndex);
            CancellationTokenSource operation = BeginOperation(UiText.Get("StatusAnalyzingSegmentation"));
            try
            {
                SegmentationResult segmentation = await Task.Run(() => SegmentationPipelineService
                    .Segment(slice, new Rect(0, 0, slice.PixelWidth, slice.PixelHeight), cancellationToken: operation.Token), operation.Token);
                if (!IsCurrentOperation(operation) || !ReferenceEquals(_volume, volume))
                {
                    return;
                }

                _pendingSegmentation = segmentation
                    with
                { SliceIndex = sliceIndex };
                segmentationText.Text = UiText.Format("StatusSegmentationCandidates", _pendingSegmentation.Blobs.Count);
                statusText.Text = UiText.Get("StatusSegmentationComplete");
                UpdateDataQualityPanelVisibility();
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                if (IsCurrentOperation(operation))
                {
                    statusText.Text = UiText.Get("StatusSegmentationCancelled");
                }
            }
            catch (Exception)
            {
                if (IsCurrentOperation(operation))
                {
                    _pendingSegmentation = null;
                    statusText.Text = UiText.Get("StatusSegmentationFailed");
                }
            }
            finally
            {
                bool wasCurrent = ReferenceEquals(_operationCancellation, operation);
                EndOperation(operation);
                if (wasCurrent)
                {
                    UpdateButtonStates();
                }
            }
        }

        private void OnAnomalySelected(object sender, SelectionChangedEventArgs e)
        {
            if (anomalyList.SelectedIndex < 0 || _qualityReport == null || _volume == null)
            {
                return;
            }

            VolumeAnomaly anomaly = _qualityReport.Anomalies[anomalyList.SelectedIndex];
            _volumeViewer.SelectSlice(anomaly.SliceIndex);
            DisplayMode = AdaptiveDisplayMode.AxialSlice;
            statusText.Text = UiText.Format("StatusAnomalyLocated", anomaly.SliceIndex + 1);
        }

        private void OnAcceptSegmentationClick(object sender, RoutedEventArgs e)
        {
            if (_pendingSegmentation is not SegmentationResult segmentation || _volume == null || segmentation.Blobs.Count == 0)
            {
                statusText.Text = UiText.Get("StatusSegmentationEmpty");
                return;
            }

            BitmapSource slice = _volume.GetAxialSlice(segmentation.SliceIndex);
            var roi = new BlobAnalysisRoi
            {
                Center = new PointD(slice.PixelWidth / 2d, slice.PixelHeight / 2d),
                Width = slice.PixelWidth,
                Height = slice.PixelHeight,
                Label = UiText.Get("SegmentationRoiLabel"),
                DetectedBlobs = segmentation.Blobs.ToList()
            };

            _imageViewer.ImageSource = slice;
            if (!_imageViewer.AddRoi(roi))
            {
                statusText.Text = UiText.Get("StatusSegmentationFailed");
                return;
            }

            _imageViewer.ShowRoiList = true;
            DisplayMode = AdaptiveDisplayMode.TwoDimensional;
            _pendingSegmentation = null;
            segmentationText.Text = string.Empty;
            UpdateDataQualityPanelVisibility();
            statusText.Text = UiText.Format("StatusSegmentationAccepted", roi.DetectedBlobs.Count);
            UpdateButtonStates();
        }

        private void OnRejectSegmentationClick(object sender, RoutedEventArgs e)
        {
            _pendingSegmentation = null;
            segmentationText.Text = UiText.Get("StatusCandidateRejected");
            UpdateDataQualityPanelVisibility();
            statusText.Text = UiText.Get("StatusNoRoiChanged");
            UpdateButtonStates();
        }

        private void OnRetryQualityClick(object sender, RoutedEventArgs e) => OnQualityClick(sender, e);

        private void UpdateDataQualityPanelVisibility()
        {
            bool hasQualityResult = _qualityReport is not null || retryButton.Visibility == Visibility.Visible;
            bool hasSegmentationResult = _pendingSegmentation is not null;
            dataQualityPanel.Visibility = hasQualityResult || hasSegmentationResult ? Visibility.Visible : Visibility.Collapsed;
            segmentationActionsPanel.Visibility = hasSegmentationResult ? Visibility.Visible : Visibility.Collapsed;
            qualityActionsPanel.Visibility = retryButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
