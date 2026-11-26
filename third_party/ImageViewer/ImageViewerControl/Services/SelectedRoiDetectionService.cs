using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using CoreLineCaliperDetectionResult = ImageViewer.Core.Analysis.LineCaliperDetectionResult;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    public sealed class SelectedRoiDetectionService : ISelectedRoiDetectionService
    {
        public static SelectedRoiDetectionService Default { get; } = new();

        public bool TryDetectSelectedLineCaliperEdges(ImageSource? imageSource, RoiBase? selectedRoi, out CoreLineCaliperDetectionResult result)
        {
            return TryDetectSelectedLineCaliperEdges(imageSource, selectedRoi, out result, ImageAnalysisQualityProfile.Default);
        }

        public bool TryDetectSelectedLineCaliperEdges(ImageSource? imageSource, RoiBase? selectedRoi, out CoreLineCaliperDetectionResult result, ImageAnalysisQualityProfile? qualityProfile)
        {
            result = default;
            if (selectedRoi is not LineCaliperMeasureRoi lineCaliper)
            {
                return false;
            }

            if (imageSource is not BitmapSource bitmap)
            {
                return false;
            }

            if (!CaliperRoiDetection.TryDetectAndApply(bitmap, lineCaliper, out result, qualityProfile))
            {
                lineCaliper.ClearDetectedLine();
                return false;
            }

            return true;
        }
    }
}
