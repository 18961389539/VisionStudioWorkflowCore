using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerImageViewStateHost : IImageViewStateHost
    {
        private readonly ImageViewer _owner;
        private readonly FrameworkElement _imageContainer;
        private readonly FrameworkElement _overlayCanvas;
        private readonly Canvas _snapGridCanvas;
        private readonly FrameworkElement _pixelGridCanvas;
        private readonly Canvas _scaleBarCanvas;
        private readonly Polyline _scaleBarLine;
        private readonly TextBlock _scaleBarText;
        private readonly UIElement _crosshairHorizontal;
        private readonly UIElement _crosshairVertical;
        private readonly UIElement _infoPanel;
        private readonly UIElement _roiListPanel;
        private readonly ScaleTransform _scaleTransform;
        private readonly Action _updateRenderedImage;
        private readonly Action<double, double> _updateCrosshair;
        private readonly Action _drawRois;
        private readonly Action _updatePixelGrid;
        private readonly Action _requestViewportOverlayRefresh;
        private readonly Func<double, string> _formatLength;

        public ImageViewerImageViewStateHost(
            ImageViewer owner,
            FrameworkElement imageContainer,
            FrameworkElement overlayCanvas,
            Canvas snapGridCanvas,
            FrameworkElement pixelGridCanvas,
            Canvas scaleBarCanvas,
            Polyline scaleBarLine,
            TextBlock scaleBarText,
            UIElement crosshairHorizontal,
            UIElement crosshairVertical,
            UIElement infoPanel,
            UIElement roiListPanel,
            ScaleTransform scaleTransform,
            Action updateRenderedImage,
            Action<double, double> updateCrosshair,
            Action drawRois,
            Action updatePixelGrid,
            Action requestViewportOverlayRefresh,
            Func<double, string> formatLength)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _imageContainer = imageContainer ?? throw new ArgumentNullException(nameof(imageContainer));
            _overlayCanvas = overlayCanvas ?? throw new ArgumentNullException(nameof(overlayCanvas));
            _snapGridCanvas = snapGridCanvas ?? throw new ArgumentNullException(nameof(snapGridCanvas));
            _pixelGridCanvas = pixelGridCanvas ?? throw new ArgumentNullException(nameof(pixelGridCanvas));
            _scaleBarCanvas = scaleBarCanvas ?? throw new ArgumentNullException(nameof(scaleBarCanvas));
            _scaleBarLine = scaleBarLine ?? throw new ArgumentNullException(nameof(scaleBarLine));
            _scaleBarText = scaleBarText ?? throw new ArgumentNullException(nameof(scaleBarText));
            _crosshairHorizontal = crosshairHorizontal ?? throw new ArgumentNullException(nameof(crosshairHorizontal));
            _crosshairVertical = crosshairVertical ?? throw new ArgumentNullException(nameof(crosshairVertical));
            _infoPanel = infoPanel ?? throw new ArgumentNullException(nameof(infoPanel));
            _roiListPanel = roiListPanel ?? throw new ArgumentNullException(nameof(roiListPanel));
            _scaleTransform = scaleTransform ?? throw new ArgumentNullException(nameof(scaleTransform));
            _updateRenderedImage = updateRenderedImage ?? throw new ArgumentNullException(nameof(updateRenderedImage));
            _updateCrosshair = updateCrosshair ?? throw new ArgumentNullException(nameof(updateCrosshair));
            _drawRois = drawRois ?? throw new ArgumentNullException(nameof(drawRois));
            _updatePixelGrid = updatePixelGrid ?? throw new ArgumentNullException(nameof(updatePixelGrid));
            _requestViewportOverlayRefresh = requestViewportOverlayRefresh ?? throw new ArgumentNullException(nameof(requestViewportOverlayRefresh));
            _formatLength = formatLength ?? throw new ArgumentNullException(nameof(formatLength));
        }

        public Size ViewerSize => new(_owner.ActualWidth, _owner.ActualHeight);
        public bool ShowScaleBar => _owner.ShowScaleBar;
        public bool ShowSnapGrid => _owner.ShowSnapGrid;
        public double Scale => _owner.Scale;

        public Rect GetVisibleImageRect(Size imageSize) => _owner.GetVisibleImageRect(imageSize);
        public double GridSpacing => _owner.GridSpacing;
        public ImageSource? ImageSource => _owner.ImageSource;
        public FrameworkElement ImageContainer => _imageContainer;
        public FrameworkElement OverlayCanvas => _overlayCanvas;
        public Canvas SnapGridCanvas => _snapGridCanvas;
        public FrameworkElement PixelGridCanvas => _pixelGridCanvas;
        public Canvas ScaleBarCanvas => _scaleBarCanvas;
        public Polyline ScaleBarLine => _scaleBarLine;
        public TextBlock ScaleBarText => _scaleBarText;

        public void ApplyScaleTransform(double scale)
        {
            _scaleTransform.ScaleX = scale;
            _scaleTransform.ScaleY = scale;
        }

        public void ApplyImageOrientation() => _owner.ApplyImageOrientation();

        public void SetCrosshairVisibility(Visibility visibility)
        {
            _crosshairHorizontal.Visibility = visibility;
            _crosshairVertical.Visibility = visibility;
        }

        public void SetInfoPanelVisibility(Visibility visibility)
        {
            _infoPanel.Visibility = visibility;
        }

        public void SetRoiListVisibility(Visibility visibility)
        {
            _roiListPanel.Visibility = visibility;
        }

        public void SetScaleBarVisibility(Visibility visibility)
        {
            _scaleBarCanvas.Visibility = visibility;
        }

        public void UpdateRenderedImage() => _updateRenderedImage();

        public void UpdateCrosshair(double x, double y) => _updateCrosshair(x, y);

        public void DrawRois() => _drawRois();

        public void UpdatePixelGrid() => _updatePixelGrid();

        public void RequestViewportOverlayRefresh() => _requestViewportOverlayRefresh();

        public string FormatLength(double displayImageUnits) => _formatLength(displayImageUnits);
    }
}
