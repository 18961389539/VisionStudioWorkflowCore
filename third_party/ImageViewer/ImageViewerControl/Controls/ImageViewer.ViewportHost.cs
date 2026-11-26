using System.Windows;
using System.Windows.Media;
using System;
using ImageViewer.Abstractions;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerViewportHost : IImageViewerViewportHost
    {
        private readonly ImageViewer _owner;
        private readonly Func<Size> _viewportSizeProvider;
        private readonly Func<ImageViewerViewportState> _viewportStateProvider;
        private readonly Action<ImageViewerViewportState> _applyViewportState;
        private readonly Action _beginViewportOverlayBatch;
        private readonly Action<bool> _endViewportOverlayBatch;
        private readonly Action _updatePixelGrid;
        private readonly Action _updateProfile;
        private readonly Action _updateInfoPanel;
        private readonly Action _updateRenderedImage;
        private readonly Action _requestViewportOverlayRefresh;

        public ImageViewerViewportHost(
            ImageViewer owner,
            Func<Size> viewportSizeProvider,
            Func<ImageViewerViewportState> viewportStateProvider,
            Action<ImageViewerViewportState> applyViewportState,
            Action beginViewportOverlayBatch,
            Action<bool> endViewportOverlayBatch,
            Action updatePixelGrid,
            Action updateProfile,
            Action updateInfoPanel,
            Action updateRenderedImage,
            Action requestViewportOverlayRefresh)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _viewportSizeProvider = viewportSizeProvider ?? throw new ArgumentNullException(nameof(viewportSizeProvider));
            _viewportStateProvider = viewportStateProvider ?? throw new ArgumentNullException(nameof(viewportStateProvider));
            _applyViewportState = applyViewportState ?? throw new ArgumentNullException(nameof(applyViewportState));
            _beginViewportOverlayBatch = beginViewportOverlayBatch ?? throw new ArgumentNullException(nameof(beginViewportOverlayBatch));
            _endViewportOverlayBatch = endViewportOverlayBatch ?? throw new ArgumentNullException(nameof(endViewportOverlayBatch));
            _updatePixelGrid = updatePixelGrid ?? throw new ArgumentNullException(nameof(updatePixelGrid));
            _updateProfile = updateProfile ?? throw new ArgumentNullException(nameof(updateProfile));
            _updateInfoPanel = updateInfoPanel ?? throw new ArgumentNullException(nameof(updateInfoPanel));
            _updateRenderedImage = updateRenderedImage ?? throw new ArgumentNullException(nameof(updateRenderedImage));
            _requestViewportOverlayRefresh = requestViewportOverlayRefresh ?? throw new ArgumentNullException(nameof(requestViewportOverlayRefresh));
        }

        public IImageViewerViewportService ViewportService => _owner.RuntimeServices.ViewportService;

        public ImageSource? ImageSource => _owner.ImageSource;

        public Size ViewportSize => _viewportSizeProvider();

        public RoiBase? SelectedRoi => _owner.ViewerState.SelectedRoi;

        public ImageViewerViewportState ViewportState
        {
            get => _viewportStateProvider();
            // Persist the viewport with a session, but browsing does not change the document.
            set => _applyViewportState(value);
        }

        public void BeginViewportOverlayBatch() => _beginViewportOverlayBatch();

        public void EndViewportOverlayBatch(bool immediate) => _endViewportOverlayBatch(immediate);

        public void UpdatePixelGrid() => _updatePixelGrid();

        public void UpdateProfile() => _updateProfile();

        public void UpdateInfoPanel() => _updateInfoPanel();

        public void UpdateRenderedImage() => _updateRenderedImage();

        public void RequestViewportOverlayRefresh() => _requestViewportOverlayRefresh();
    }
}
