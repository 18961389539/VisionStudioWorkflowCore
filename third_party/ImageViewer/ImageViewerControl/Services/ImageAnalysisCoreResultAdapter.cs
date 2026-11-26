using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using CoreCircularResult = ImageViewer.Core.Analysis.CircularCaliperDetectionResult;
using CoreLineResult = ImageViewer.Core.Analysis.LineCaliperDetectionResult;
using CoreDualLineResult = ImageViewer.Core.Analysis.LineMeasureGradientDetectionResult;

namespace ImageViewer.Services
{
    internal static partial class ImageAnalysisService
    {
        /// <summary>
        /// Runs the WPF detector and exposes its result as a framework-neutral Core contract.
        /// </summary>
        internal static bool TryDetectLineMeasureEdgesCore(
            BitmapSource bitmap,
            CaliperMeasureRoi line,
            out CoreDualLineResult result,
            ImageAnalysisQualityProfile? profile = null,
            IntensityImage? intensity = null)
        {
            bool success = TryDetectLineMeasureEdges(bitmap, line, out LineMeasureGradientDetectionResult legacy, profile, intensity);
            result = success ? legacy.ToCore() : default;
            return success;
        }

        internal static bool TryDetectLineCaliperEdgesCore(
            BitmapSource bitmap,
            LineCaliperMeasureRoi line,
            out CoreLineResult result,
            ImageAnalysisQualityProfile? profile = null,
            IntensityImage? intensity = null)
        {
            bool success = TryDetectLineCaliperEdges(bitmap, line, out LineCaliperDetectionResult legacy, profile, intensity);
            result = success ? legacy.ToCore() : default;
            return success;
        }

        internal static bool TryDetectCircularCaliperEdgesCore(
            BitmapSource bitmap,
            CircularCaliperMeasureRoi caliper,
            out CoreCircularResult result,
            ImageAnalysisQualityProfile? profile = null,
            IntensityImage? intensity = null)
        {
            bool success = TryDetectCircularCaliperEdges(bitmap, caliper, out CircularCaliperDetectionResult legacy, profile, intensity);
            result = success ? legacy.ToCore() : default;
            return success;
        }
    }
}
