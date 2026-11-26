using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    internal sealed class ViewportController
    {
        private readonly IImageViewerViewportHost _host;
        private readonly double _viewportPadding;
        private readonly double _minScale;
        private readonly double _maxScale;

        public ViewportController(IImageViewerViewportHost host, double viewportPadding, double minScale, double maxScale)
        {
            _host = host;
            _viewportPadding = viewportPadding;
            _minScale = minScale;
            _maxScale = maxScale;
        }

        public void FitToView()
        {
            if (!TryGetImageSize(out var imageSize))
            {
                return;
            }

            var state = _host.ViewportService.FitToViewport(_host.ViewportSize, imageSize, _viewportPadding);
            if (state != null)
            {
                ApplyViewportState(new ImageViewerViewportState(state.Value.Scale, state.Value.TranslateX, state.Value.TranslateY), allowBelowMinScale: true);
            }
        }

        public void ShowFullImage()
        {
            if (!TryGetImageSize(out var imageSize))
            {
                return;
            }

            var state = _host.ViewportService.FitToViewport(_host.ViewportSize, imageSize, padding: 0);
            if (state != null)
            {
                ApplyViewportState(new ImageViewerViewportState(state.Value.Scale, state.Value.TranslateX, state.Value.TranslateY), allowBelowMinScale: true);
            }
        }

        public void SetActualSize()
        {
            if (!TryGetImageSize(out var imageSize))
            {
                return;
            }

            double translateX = (_host.ViewportSize.Width - imageSize.Width) / 2;
            double translateY = (_host.ViewportSize.Height - imageSize.Height) / 2;
            ApplyViewportState(new ImageViewerViewportState(1.0, translateX, translateY));
        }

        public void FillToView()
        {
            if (!TryGetImageSize(out var imageSize)
                || _host.ViewportSize.Width <= 0
                || _host.ViewportSize.Height <= 0)
            {
                return;
            }

            double scale = Math.Max(
                _host.ViewportSize.Width / imageSize.Width,
                _host.ViewportSize.Height / imageSize.Height);
            double translateX = (_host.ViewportSize.Width - imageSize.Width * scale) / 2;
            double translateY = (_host.ViewportSize.Height - imageSize.Height * scale) / 2;
            ApplyViewportState(new ImageViewerViewportState(scale, translateX, translateY), allowBelowMinScale: true);
        }

        public void ZoomToSelection()
        {
            if (_host.SelectedRoi == null)
            {
                return;
            }

            Rect bounds = RoiGeometryService.GetBounds(_host.SelectedRoi);
            var state = _host.ViewportService.ZoomToBounds(_host.ViewportSize, bounds, _viewportPadding, _minScale, _maxScale);
            if (state != null)
            {
                ApplyViewportState(new ImageViewerViewportState(state.Value.Scale, state.Value.TranslateX, state.Value.TranslateY));
            }
        }

        public ImageViewerViewportState CurrentState => _host.ViewportState;

        public void ZoomAt(Point imagePoint, double zoomFactor)
        {
            ImageViewerViewportState nextState = ImageViewerViewportStateOperations.ZoomAt(_host.ViewportState, imagePoint, zoomFactor, _minScale, _maxScale);
            ApplyViewportState(nextState);
        }

        public void TranslateBy(Vector delta)
        {
            ImageViewerViewportState nextState = ImageViewerViewportStateOperations.TranslateBy(_host.ViewportState, delta);
            SetTranslation(nextState.TranslateX, nextState.TranslateY);
        }

        public void ApplyViewportState(ImageViewerViewportState state, bool allowBelowMinScale = false)
        {
            ImageViewerViewportState normalizedState = ImageViewerViewportStateOperations.Normalize(state, _minScale, _maxScale, allowBelowMinScale);

            _host.BeginViewportOverlayBatch();
            try
            {
                _host.ViewportState = normalizedState;
                _host.UpdateRenderedImage();
                _host.RequestViewportOverlayRefresh();
                _host.UpdatePixelGrid();
            }
            finally
            {
                _host.EndViewportOverlayBatch(immediate: true);
            }

            _host.UpdateProfile();
            _host.UpdateInfoPanel();
        }

        public void SetTranslation(double x, double y)
        {
            ImageViewerViewportState currentState = _host.ViewportState;
            _host.ViewportState = new ImageViewerViewportState(currentState.Scale, x, y);
            _host.UpdateRenderedImage();
            _host.RequestViewportOverlayRefresh();
        }

        public bool TryGetImageSize(out Size imageSize)
        {
            if (!ImageViewerImageSourceUtilities.TryGetSourceImageSize(_host.ImageSource, out imageSize))
            {
                return false;
            }

            return true;
        }

        public string? TryGetCurrentImagePath()
        {
            ImageSource? source = _host.ImageSource;
            if (ImageViewerImageSourceRegistry.TryGetPath(source, out string registeredPath))
            {
                return registeredPath;
            }

            return source is BitmapImage { UriSource: { IsFile: true } uri } ? uri.LocalPath : null;
        }

        public void ResetView()
        {
            ApplyViewportState(ImageViewerViewportState.Default);
        }
    }
}
