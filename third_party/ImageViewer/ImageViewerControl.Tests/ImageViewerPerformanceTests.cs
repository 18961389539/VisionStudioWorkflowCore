using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Performance")]
    [Trait("Category", "Wpf")]
    public sealed class ImageViewerPerformanceTests
    {
        [Fact]
        public void RenderTileCache_RepeatedHits_ReturnCachedTiles()
        {
            WpfTestRunner.Run(() =>
            {
                using var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateLargeBitmap();
                Int32Rect rect = new(0, 0, 512, 512);

                cache.GetOrCreate(source, rect);
                for (int index = 0; index < 1_000; index++)
                {
                    Assert.True(cache.TryGet(source, rect, out _));
                }

                const int iterations = 25_000;
                for (int index = 0; index < iterations; index++)
                {
                    Assert.True(cache.TryGet(source, rect, out BitmapSource? cached));
                    Assert.NotNull(cached);
                }
            });
        }

        [Fact]
        public void RenderService_RepeatedFrames_ReuseCachedTile()
        {
            WpfTestRunner.Run(() =>
            {
                using var service = new ImageViewerRenderService();
                BitmapSource source = CreateLargeBitmap();
                var pyramid = new[] { new ImagePyramidLevel(source, 1.0) };
                ImageViewerRenderFrame? first = null;

                for (int index = 0; index < 5; index++)
                {
                    ImageViewerRenderFrame frame = service.BuildRenderFrame(
                        source,
                        pyramid,
                        new Size(512, 512),
                        1.0,
                        new Point(0, 0),
                        ImageViewerDisplaySettings.PassThrough,
                        enableTiledRendering: true,
                        autoSelectPyramidLevel: true,
                        prefetchAdjacentTiles: false,
                        tileCacheMaximumMegabytes: 64,
                        tilePrefetchRadius: 0);

                    first ??= frame;
                    Assert.Same(first.Source, frame.Source);
                }
            });
        }

        private static BitmapSource CreateLargeBitmap()
        {
            const int width = 2001;
            const int height = 2001;
            byte[] pixels = new byte[width * height];
            return BitmapSource.Create(
                width,
                height,
                96,
                96,
                System.Windows.Media.PixelFormats.Gray8,
                null,
                pixels,
                width);
        }
    }
}
