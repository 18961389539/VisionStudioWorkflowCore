using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageViewer.Controls;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    public class ImageViewStateControllerTests
    {
        [Fact]
        public void HandleScaleChanged_UpdatesTransformAndRefreshesOverlayState()
        {
            WpfTestRunner.Run(() =>
            {
                var host = new FakeImageViewStateHost
                {
                    Scale = 1.0,
                    ShowScaleBar = false,
                    ShowSnapGrid = false
                };
                var controller = new ImageViewStateController(host, minScale: 0.1);

                controller.HandleScaleChanged(2.5);

                Assert.Equal(2.5, host.Scale);
                Assert.Equal(1, host.ApplyScaleTransformCallCount);
                Assert.Equal(1, host.UpdateRenderedImageCallCount);
                Assert.Equal(1, host.RequestViewportOverlayRefreshCallCount);
                Assert.Equal(1, host.UpdatePixelGridCallCount);
            });
        }

        [Fact]
        public void ApplyImageSurfaceLayout_SizesImageSurfacesToSource()
        {
            WpfTestRunner.Run(() =>
            {
                var host = new FakeImageViewStateHost();
                var controller = new ImageViewStateController(host, minScale: 0.1);
                BitmapSource bitmap = CreateBitmap(pixelWidth: 12, pixelHeight: 8);

                controller.ApplyImageSurfaceLayout(bitmap);

                Assert.Equal(12, host.ImageContainer.Width);
                Assert.Equal(8, host.ImageContainer.Height);
                Assert.Equal(12, host.OverlayCanvas.Width);
                Assert.Equal(8, host.OverlayCanvas.Height);
                Assert.Equal(Visibility.Visible, host.OverlayCanvas.Visibility);
                Assert.Equal(12, host.SnapGridCanvas.Width);
                Assert.Equal(8, host.SnapGridCanvas.Height);
                Assert.Equal(12, host.PixelGridCanvas.Width);
                Assert.Equal(8, host.PixelGridCanvas.Height);
            });
        }

        [Fact]
        public void HandleShowScaleBarChanged_WhenVisible_UpdatesScaleBarGeometry()
        {
            WpfTestRunner.Run(() =>
            {
                var host = new FakeImageViewStateHost
                {
                    ShowScaleBar = true,
                    Scale = 2.0
                };
                var controller = new ImageViewStateController(host, minScale: 0.1);

                controller.HandleShowScaleBarChanged(true);

                Assert.Equal(Visibility.Visible, host.ScaleBarCanvas.Visibility);
                Assert.Equal(120, host.ScaleBarCanvas.Width, 3);
                Assert.Equal("len:50", host.ScaleBarText.Text);
                Assert.Equal(4, host.ScaleBarLine.Points.Count);
            });
        }

        [Fact]
        public void UpdateSnapGrid_OnlyCreatesLinesInsideTheVisibleRegion()
        {
            // 图像 4000×4000、间距 8 → 整幅图需要 501+501 条线；视口 320×240 只需要 41+31 条。
            WpfTestRunner.Run(() =>
            {
                var host = new FakeImageViewStateHost
                {
                    ShowSnapGrid = true,
                    GridSpacing = 8,
                    ImageSource = CreateBitmap(4000, 4000),
                    ViewerSize = new Size(320, 240),
                    Scale = 1.0,
                    TranslateX = 0,
                    TranslateY = 0
                };
                var controller = new ImageViewStateController(host, minScale: 0.1);

                controller.HandleShowSnapGridChanged();

                // 可视区域 x∈[0,320]、y∈[0,240]：41 条竖线 + 31 条横线（允许 ±2 的取整误差）。
                Assert.InRange(host.SnapGridCanvas.Children.Count, 41 + 31 - 2, 41 + 31 + 2);
            });
        }

        [Fact]
        public void UpdateSnapGrid_PanningAwayFromTheImage_CreatesNoLines()
        {
            WpfTestRunner.Run(() =>
            {
                var host = new FakeImageViewStateHost
                {
                    ShowSnapGrid = true,
                    GridSpacing = 8,
                    ImageSource = CreateBitmap(4000, 4000),
                    ViewerSize = new Size(320, 240),
                    Scale = 1.0,
                    TranslateX = -50000,
                    TranslateY = -50000
                };
                var controller = new ImageViewStateController(host, minScale: 0.1);

                controller.HandleShowSnapGridChanged();

                Assert.Empty(host.SnapGridCanvas.Children);
            });
        }

        [Fact]
        public void UpdateSnapGrid_WhenHidden_CreatesNoLines()
        {
            WpfTestRunner.Run(() =>
            {
                var host = new FakeImageViewStateHost
                {
                    ShowSnapGrid = false,
                    GridSpacing = 8,
                    ImageSource = CreateBitmap(4000, 4000),
                    ViewerSize = new Size(320, 240)
                };
                var controller = new ImageViewStateController(host, minScale: 0.1);

                controller.HandleShowSnapGridChanged();

                Assert.Empty(host.SnapGridCanvas.Children);
            });
        }

        private static BitmapSource CreateBitmap(int pixelWidth, int pixelHeight)
        {
            byte[] pixels = new byte[pixelWidth * pixelHeight];
            return BitmapSource.Create(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Gray8,
                null,
                pixels,
                pixelWidth);
        }

        private sealed class FakeImageViewStateHost : IImageViewStateHost
        {
            public Size ViewerSize { get; set; } = new(320, 240);

            public bool ShowScaleBar { get; set; }

            public bool ShowSnapGrid { get; set; }

            public double Scale { get; set; } = 1.0;

            public double TranslateX { get; set; }

            public double TranslateY { get; set; }

            public Rect GetVisibleImageRect(Size imageSize) =>
                ImageViewerImageSourceUtilities.GetVisibleImageRect(imageSize, ViewerSize, Scale, TranslateX, TranslateY);

            public double GridSpacing { get; set; } = 8;

            public ImageSource? ImageSource { get; set; }

            public FrameworkElement ImageContainer { get; } = new Canvas();

            public FrameworkElement OverlayCanvas { get; } = new Canvas();

            public Canvas SnapGridCanvas { get; } = new();

            public FrameworkElement PixelGridCanvas { get; } = new Canvas();

            public Canvas ScaleBarCanvas { get; } = new();

            public Polyline ScaleBarLine { get; } = new();

            public TextBlock ScaleBarText { get; } = new();

            public int ApplyScaleTransformCallCount { get; private set; }

            public int UpdateRenderedImageCallCount { get; private set; }

            public int UpdatePixelGridCallCount { get; private set; }

            public int RequestViewportOverlayRefreshCallCount { get; private set; }

            public void ApplyScaleTransform(double scale)
            {
                ApplyScaleTransformCallCount++;
                Scale = scale;
            }

            public void ApplyImageOrientation()
            {
            }

            public void SetCrosshairVisibility(Visibility visibility)
            {
            }

            public void SetInfoPanelVisibility(Visibility visibility)
            {
            }

            public void SetRoiListVisibility(Visibility visibility)
            {
            }

            public void SetScaleBarVisibility(Visibility visibility)
            {
                ScaleBarCanvas.Visibility = visibility;
            }

            public void UpdateRenderedImage()
            {
                UpdateRenderedImageCallCount++;
            }

            public void UpdateCrosshair(double x, double y)
            {
            }

            public void DrawRois()
            {
            }

            public void UpdatePixelGrid()
            {
                UpdatePixelGridCallCount++;
            }

            public void RequestViewportOverlayRefresh()
            {
                RequestViewportOverlayRefreshCallCount++;
            }

            public string FormatLength(double displayImageUnits)
            {
                return $"len:{displayImageUnits:F0}";
            }
        }
    }
}
