using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ImageViewer.Controls
{
    internal interface IImageViewStateController
    {
        void HandleRootGridSizeChanged();
        void HandleLoaded();
        void HandleCalibrationChanged();
        void HandleShowPixelGridChanged();
        void HandleShowSnapGridChanged();
        void HandleShowCrosshairChanged(bool isVisible);
        void HandleShowInfoPanelChanged(bool isVisible);
        void HandleShowRoiListChanged(bool isVisible);
        void HandleShowScaleBarChanged(bool isVisible);
        void HandleScaleChanged(double scale);
        void ApplyImageSurfaceLayout(ImageSource? source);
        void RefreshImageOverlays();
    }

    internal interface IImageViewStateHost
    {
        Size ViewerSize { get; }
        bool ShowScaleBar { get; }
        bool ShowSnapGrid { get; }
        double Scale { get; }

        Rect GetVisibleImageRect(Size imageSize);

        double GridSpacing { get; }
        ImageSource? ImageSource { get; }
        FrameworkElement ImageContainer { get; }
        FrameworkElement OverlayCanvas { get; }
        Canvas SnapGridCanvas { get; }
        FrameworkElement PixelGridCanvas { get; }
        Canvas ScaleBarCanvas { get; }
        Polyline ScaleBarLine { get; }
        TextBlock ScaleBarText { get; }

        void ApplyScaleTransform(double scale);
        void ApplyImageOrientation();
        void SetCrosshairVisibility(Visibility visibility);
        void SetInfoPanelVisibility(Visibility visibility);
        void SetRoiListVisibility(Visibility visibility);
        void SetScaleBarVisibility(Visibility visibility);
        void UpdateRenderedImage();
        void UpdateCrosshair(double x, double y);
        void DrawRois();
        void UpdatePixelGrid();
        void RequestViewportOverlayRefresh();
        string FormatLength(double displayImageUnits);
    }

    internal sealed class ImageViewStateController : IImageViewStateController
    {
        private readonly IImageViewStateHost _host;
        private readonly double _minScale;

        public ImageViewStateController(IImageViewStateHost host, double minScale)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _minScale = minScale;
        }

        public void HandleRootGridSizeChanged()
        {
            _host.UpdateRenderedImage();
            _host.UpdateCrosshair(_host.ViewerSize.Width / 2, _host.ViewerSize.Height / 2);
        }

        public void HandleLoaded()
        {
            _host.UpdateRenderedImage();
            RefreshImageOverlays();
        }

        public void HandleCalibrationChanged()
        {
            UpdateScaleBar();
            _host.DrawRois();
        }

        public void HandleShowPixelGridChanged()
        {
            _host.UpdatePixelGrid();
            UpdateSnapGrid();
        }

        public void HandleShowSnapGridChanged()
        {
            UpdateSnapGrid();
        }

        public void HandleShowCrosshairChanged(bool isVisible)
        {
            _host.SetCrosshairVisibility(isVisible ? Visibility.Visible : Visibility.Collapsed);
        }

        public void HandleShowInfoPanelChanged(bool isVisible)
        {
            _host.SetInfoPanelVisibility(isVisible ? Visibility.Visible : Visibility.Collapsed);
        }

        public void HandleShowRoiListChanged(bool isVisible)
        {
            _host.SetRoiListVisibility(isVisible ? Visibility.Visible : Visibility.Collapsed);
        }

        public void HandleShowScaleBarChanged(bool isVisible)
        {
            _host.SetScaleBarVisibility(isVisible ? Visibility.Visible : Visibility.Collapsed);
            if (isVisible)
            {
                UpdateScaleBar();
            }
        }

        public void HandleScaleChanged(double scale)
        {
            _host.ApplyScaleTransform(scale);
            _host.UpdateRenderedImage();
            _host.RequestViewportOverlayRefresh();
            _host.UpdatePixelGrid();
            UpdateSnapGrid();
            UpdateScaleBar();
        }

        public void ApplyImageSurfaceLayout(ImageSource? source)
        {
            if (!ImageViewerImageSourceUtilities.TryGetSourceImageSize(source, out Size imageSize))
            {
                return;
            }

            _host.ImageContainer.Width = imageSize.Width;
            _host.ImageContainer.Height = imageSize.Height;
            _host.OverlayCanvas.Width = imageSize.Width;
            _host.OverlayCanvas.Height = imageSize.Height;
            _host.OverlayCanvas.Visibility = Visibility.Visible;
            _host.SnapGridCanvas.Width = imageSize.Width;
            _host.SnapGridCanvas.Height = imageSize.Height;
            _host.PixelGridCanvas.Width = imageSize.Width;
            _host.PixelGridCanvas.Height = imageSize.Height;
            _host.ApplyImageOrientation();
        }

        public void RefreshImageOverlays()
        {
            _host.UpdatePixelGrid();
            UpdateSnapGrid();
            UpdateScaleBar();
        }

        private void UpdateScaleBar()
        {
            if (!_host.ShowScaleBar || _host.Scale <= 0)
            {
                return;
            }

            double rawImageUnits = 100.0 / _host.Scale;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawImageUnits)));
            double normalized = rawImageUnits / magnitude;

            double niceValue;
            if (normalized < 1.5)
            {
                niceValue = 1;
            }
            else if (normalized < 3.5)
            {
                niceValue = 2;
            }
            else if (normalized < 7.5)
            {
                niceValue = 5;
            }
            else
            {
                niceValue = 10;
            }

            double displayImageUnits = niceValue * magnitude;
            double displayScreenWidth = displayImageUnits * _host.Scale;

            _host.ScaleBarCanvas.Width = displayScreenWidth + 20;
            _host.ScaleBarLine.Points = new PointCollection
            {
                new Point(0, 0),
                new Point(0, 10),
                new Point(displayScreenWidth, 10),
                new Point(displayScreenWidth, 0)
            };

            Canvas.SetLeft(_host.ScaleBarText, displayScreenWidth / 2 - 20);
            _host.ScaleBarText.Text = _host.FormatLength(displayImageUnits);
        }

        private void UpdateSnapGrid()
        {
            _host.SnapGridCanvas.Children.Clear();

            if (!_host.ShowSnapGrid || _host.GridSpacing <= 0 || !ImageViewerImageSourceUtilities.TryGetSourceImageSize(_host.ImageSource, out Size imageSize))
            {
                return;
            }

            // 只创建可视区域内的网格线：整幅图可能有上千条线，而视口通常只覆盖一小块。
            Rect visible = _host.GetVisibleImageRect(imageSize);
            if (visible.IsEmpty)
            {
                return;
            }

            double width = imageSize.Width;
            double height = imageSize.Height;
            double spacing = _host.GridSpacing;
            double thickness = 1 / Math.Max(_host.Scale, _minScale);

            double firstX = Math.Floor(Math.Max(0, visible.Left) / spacing) * spacing;
            double lastX = Math.Min(width, visible.Right);
            for (double x = firstX; x <= lastX; x += spacing)
            {
                _host.SnapGridCanvas.Children.Add(new Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = height,
                    Stroke = Brushes.DimGray,
                    StrokeThickness = thickness,
                    Opacity = 0.45,
                    IsHitTestVisible = false
                });
            }

            double firstY = Math.Floor(Math.Max(0, visible.Top) / spacing) * spacing;
            double lastY = Math.Min(height, visible.Bottom);
            for (double y = firstY; y <= lastY; y += spacing)
            {
                _host.SnapGridCanvas.Children.Add(new Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = width,
                    Y2 = y,
                    Stroke = Brushes.DimGray,
                    StrokeThickness = thickness,
                    Opacity = 0.45,
                    IsHitTestVisible = false
                });
            }
        }
    }
}
