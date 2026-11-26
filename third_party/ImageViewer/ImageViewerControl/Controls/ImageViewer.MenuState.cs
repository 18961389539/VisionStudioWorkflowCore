namespace ImageViewer.Controls
{
    internal sealed record ImageViewerMenuStateSnapshot(
        ImageViewerContextMenuState Context,
        ImageViewerFileMenuState File,
        ImageViewerViewCommandMenuState View,
        ImageViewerAnalysisMenuState Analysis)
    {
        public static ImageViewerMenuStateSnapshot Empty { get; } =
            new(default, ImageViewerFileMenuState.Empty, default, ImageViewerAnalysisMenuState.Empty);
    }
}