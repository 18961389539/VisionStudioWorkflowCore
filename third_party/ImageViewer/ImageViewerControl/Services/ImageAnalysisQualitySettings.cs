using ImageViewer.Models;

namespace ImageViewer.Services
{
    /// <summary>
    /// Provides immutable defaults for callers that do not own a viewer instance.
    /// Viewer specific configuration is stored on <see cref="Controls.ImageViewer"/>
    /// and passed explicitly to analysis services.
    /// </summary>
    public static class ImageAnalysisQualitySettings
    {
        public static ImageAnalysisQualityProfile Default => ImageAnalysisQualityProfile.Default;
    }
}
