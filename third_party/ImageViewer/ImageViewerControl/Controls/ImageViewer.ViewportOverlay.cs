namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private readonly IImageViewerDeferredRefreshScheduler _viewportOverlayRefreshScheduler;

        private void RequestViewportOverlayRefresh(bool immediate = false)
            => _viewportOverlayRefreshScheduler.Request(immediate);

        private void BeginViewportOverlayBatch()
            => _viewportOverlayRefreshScheduler.BeginBatch();

        private void EndViewportOverlayBatch(bool immediate = false)
            => _viewportOverlayRefreshScheduler.EndBatch(immediate);
    }
}
