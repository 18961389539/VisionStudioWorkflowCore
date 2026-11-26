using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using CoreCircularResult = ImageViewer.Core.Analysis.CircularCaliperDetectionResult;
using CoreDualLineResult = ImageViewer.Core.Analysis.LineMeasureGradientDetectionResult;
using CoreLineResult = ImageViewer.Core.Analysis.LineCaliperDetectionResult;

namespace ImageViewer.Services
{
    internal static class CaliperRoiDetection
    {
        public static bool Supports(RoiBase? roi) => roi is CaliperMeasureRoi or LineCaliperMeasureRoi or CircularCaliperMeasureRoi;

        public static bool TryDetectAndApply(BitmapSource bitmap, RoiBase roi, ImageAnalysisQualityProfile profile, IntensityImage? intensity) =>
            roi switch
            {
                CaliperMeasureRoi line => TryDetectAndApply(bitmap, line, out _, profile, intensity),
                LineCaliperMeasureRoi line => TryDetectAndApply(bitmap, line, out _, profile, intensity),
                CircularCaliperMeasureRoi circle => TryDetectAndApply(bitmap, circle, out _, profile, intensity),
                _ => false
            };

        public static bool TryDetectAndApply(BitmapSource bitmap, CaliperMeasureRoi roi, out CoreDualLineResult result,
            ImageAnalysisQualityProfile? profile, IntensityImage? intensity = null)
        {
            if (!ImageAnalysisService.TryDetectLineMeasureEdgesCore(bitmap, roi, out result, profile, intensity))
            {
                return false;
            }

            RoiDetectionResultMapper.Apply(roi, result);
            return true;
        }

        public static bool TryDetectAndApply(BitmapSource bitmap, LineCaliperMeasureRoi roi, out CoreLineResult result,
            ImageAnalysisQualityProfile? profile, IntensityImage? intensity = null)
        {
            if (!ImageAnalysisService.TryDetectLineCaliperEdgesCore(bitmap, roi, out result, profile, intensity))
            {
                return false;
            }

            RoiDetectionResultMapper.Apply(roi, result);
            return true;
        }

        public static bool TryDetectAndApply(BitmapSource bitmap, CircularCaliperMeasureRoi roi, out CoreCircularResult result,
            ImageAnalysisQualityProfile? profile, IntensityImage? intensity = null)
        {
            if (!ImageAnalysisService.TryDetectCircularCaliperEdgesCore(bitmap, roi, out result, profile, intensity))
            {
                return false;
            }

            RoiDetectionResultMapper.Apply(roi, result, profile);
            return true;
        }

        public static void Clear(RoiBase roi)
        {
            switch (roi)
            {
                case CaliperMeasureRoi line:
                    line.ClearDetectedEdges();
                    break;
                case LineCaliperMeasureRoi line:
                    line.ClearDetectedLine();
                    break;
                case CircularCaliperMeasureRoi circle:
                    circle.ClearDetectedEdges();
                    break;
            }
        }
    }
}
