using System;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerAnalysisComposition
    {
        public ImageViewerAnalysisComposition(
            ImageViewerAnalysisCoordinator analysisController,
            ImageViewerAnalysisCommandController analysisCommandController)
        {
            AnalysisController = analysisController ?? throw new ArgumentNullException(nameof(analysisController));
            AnalysisCommandController = analysisCommandController ?? throw new ArgumentNullException(nameof(analysisCommandController));
        }

        public ImageViewerAnalysisCoordinator AnalysisController { get; }

        public ImageViewerAnalysisCommandController AnalysisCommandController { get; }
    }
}