using System.Windows;
using ImageViewer.Controls;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerViewportStateOperationsTests
    {
        [Fact]
        public void Normalize_ClampsScaleToConfiguredRange()
        {
            ImageViewerViewportState state = ImageViewerViewportStateOperations.Normalize(
                new ImageViewerViewportState(0.01, 12, 34),
                minScale: 0.1,
                maxScale: 100);

            Assert.Equal(0.1, state.Scale);
            Assert.Equal(12, state.TranslateX);
            Assert.Equal(34, state.TranslateY);
        }

        [Fact]
        public void ZoomAt_AdjustsTranslationAroundAnchorPoint()
        {
            ImageViewerViewportState state = ImageViewerViewportStateOperations.ZoomAt(
                new ImageViewerViewportState(1.0, 10, 20),
                new Point(50, 25),
                zoomFactor: 1.1,
                minScale: 0.1,
                maxScale: 100);

            Assert.Equal(1.1, state.Scale, 5);
            Assert.Equal(5, state.TranslateX, 5);
            Assert.Equal(17.5, state.TranslateY, 5);
        }

        [Fact]
        public void TranslateBy_AddsDeltaWithoutChangingScale()
        {
            ImageViewerViewportState state = ImageViewerViewportStateOperations.TranslateBy(
                new ImageViewerViewportState(2.0, 3, 4),
                new Vector(7, -2));

            Assert.Equal(2.0, state.Scale);
            Assert.Equal(10, state.TranslateX);
            Assert.Equal(2, state.TranslateY);
        }
    }
}