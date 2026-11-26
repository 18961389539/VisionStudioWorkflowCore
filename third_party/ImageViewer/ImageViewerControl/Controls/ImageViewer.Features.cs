using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Drawing;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Utils;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private const double ViewportPadding = 16;

        public void ShowRoiProperties(RoiBase roi) => _dialogWorkflowService.ShowRoiProperties(roi);

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use ShowRoiProperties(RoiBase) instead.", false)]
        public void ShowRoiPropertiesDialog(RoiBase roi) => ShowRoiProperties(roi);

        private bool TryApplyCaliperDetection(RoiBase roi, IntensityImage? intensity = null)
        {
            if (roi is CaliperMeasureRoi dual)
            {
                dual.EnsureCaliperRegion();
            }

            if (GetAnalysisBitmapSource() is BitmapSource bitmap &&
                CaliperRoiDetection.TryDetectAndApply(bitmap, roi, QualityProfile, intensity))
            {
                return true;
            }

            CaliperRoiDetection.Clear(roi);
            if (roi is CircularCaliperMeasureRoi circle and not ArcCaliperMeasureRoi)
            {
                circle.MarkQualityFailure();
            }

            return false;
        }

        private bool TryCreateAutomaticCircle(Point seed, out CircularCaliperMeasureRoi roi)
        {
            if (GetAnalysisBitmapSource() is BitmapSource bitmap &&
                ImageAnalysisService.TryDetectAutomaticCircle(bitmap, seed, out roi, QualityProfile))
            {
                return true;
            }

            // 静默失败会让操作员分不清"没点中"和"这里确实没有圆"，给出可重试的明确提示。
            ShowStatusHint(UiText.Get("StatusAutomaticCircleNotFound"), StatusHintKind.Error);
            roi = null!;
            return false;
        }

        private bool TrySnapPointToEdge(Point seed, out Point snapped, out double score, out double confidence)
        {
            if (GetAnalysisBitmapSource() is BitmapSource bitmap)
            {
                EdgeSnapResult result = SnapPointToEdge(bitmap, seed, QualityProfile);
                if (result.Found)
                {
                    snapped = result.Point;
                    score = result.Score;
                    confidence = result.Confidence;
                    return true;
                }
            }

            snapped = default;
            score = 0;
            confidence = 0;
            return false;
        }

        /// <summary>
        /// 后台执行边缘吸附；线程亲和状态（分析位图、质量档）在调用线程上取好。
        /// Chinese: 分析位图来自冻结副本，后台只读像素，不触碰 UI 对象；未冻结时退回同步搜索，
        /// 保持旧行为而不是静默失去吸附。
        /// English: Thread-affine state is captured on the calling thread; the frozen analysis bitmap
        /// is safe to read on a worker. A non-frozen source falls back to the synchronous probe.
        /// </summary>
        private Task<EdgeSnapResult> SnapPointToEdgeAsync(Point seed, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<EdgeSnapResult>(cancellationToken);
            }

            BitmapSource? bitmap = GetAnalysisBitmapSource();
            ImageAnalysisQualityProfile profile = QualityProfile;
            if (bitmap is null)
            {
                return Task.FromResult(EdgeSnapResult.NotFound);
            }

            if (!bitmap.IsFrozen)
            {
                return Task.FromResult(SnapPointToEdge(bitmap, seed, profile));
            }

            return Task.Run(
                () => cancellationToken.IsCancellationRequested
                    ? EdgeSnapResult.NotFound
                    : SnapPointToEdge(bitmap, seed, profile),
                cancellationToken);
        }

        private static EdgeSnapResult SnapPointToEdge(BitmapSource bitmap, Point seed, ImageAnalysisQualityProfile profile)
            => ImageAnalysisService.TrySnapPointToEdge(bitmap, seed, out Point snapped, out double score, out double confidence, profile)
                ? new EdgeSnapResult(true, snapped, score, confidence)
                : EdgeSnapResult.NotFound;

        private bool TryRefreshCaliperDetection(RoiBase? roi, IntensityImage? intensity = null)
        {
            return roi switch
            {
                BlobAnalysisRoi blob => TryApplyBlobAnalysis(blob),
                { } caliper when CaliperRoiDetection.Supports(caliper) => TryApplyCaliperDetection(caliper, intensity),
                _ => false
            };
        }

        private bool TryApplyBlobAnalysis(BlobAnalysisRoi roi)
        {
            if (GetAnalysisBitmapSource() is not BitmapSource bitmap)
            {
                roi.DetectedBlobs?.Clear();
                return false;
            }

            roi.DetectedBlobs = BlobAnalysisService.DetectBlobs(bitmap, roi, roi.UseOtsu, roi.ManualThreshold, roi.DetectDark, roi.MinArea);
            return true;
        }

        private void RefreshAllCaliperDetections()
        {
            // One conversion serves every caliper ROI in this pass; per-ROI conversion of a
            // large image otherwise dominates refresh time as the ROI count grows.
            IntensityImage? intensity = null;
            foreach (RoiBase roi in ViewModel.AllRois)
            {
                if (intensity is null &&
                    CaliperRoiDetection.Supports(roi) &&
                    GetAnalysisBitmapSource() is BitmapSource bitmap)
                {
                    intensity = VisionAlgorithmEntryPoint.CreateIntensity(bitmap);
                }

                TryRefreshCaliperDetection(roi, intensity);
            }
        }

        private async void OnFeatureMenuCommandClick(object sender, RoutedEventArgs e)
        {
            if (TryGetTaggedCommand(sender, out ImageViewerFeatureMenuCommand command))
            {
                await RunUiOperationAsync($"功能命令 {command}", () => _featureMenuCommandController.ExecuteAsync(command));
            }
        }

    }
}
