using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Core.Display;
using ImageViewer.Services;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Rendering;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Unit")]
    [Trait("Category", "Wpf")]
    public class ImageViewerRenderServiceTests
    {
        private static readonly ImageViewerDisplaySettings HotSettings = new(PseudoColorPalette.Hot);

        [Fact]
        public void DualEdgeCaliperSummaryAnchor_FollowsDetectedSegmentMidpoint()
        {
            var caliper = new CaliperMeasureRoi
            {
                CaliperCenter = new PointD(100, 100),
                P1 = new PointD(10, 20),
                P2 = new PointD(30, 40),
                HasDetectedEdges = true
            };

            Point anchor = DualEdgeCaliperRenderHelper.GetSummaryAnchor(caliper);

            Assert.Equal(new Point(20, 30), anchor);
        }

        [Fact]
        public void DualEdgeCaliperSummaryAnchor_IsOffsetAboveMeasuredSegment()
        {
            WpfTestRunner.Run(() =>
            {
                var caliper = new CaliperMeasureRoi
                {
                    P1 = new PointD(10, 100),
                    P2 = new PointD(90, 100),
                    HasDetectedEdges = true
                };
                var context = new RoiRenderContext(
                    new Canvas(),
                    new Canvas(),
                    static point => point,
                    scale: 1,
                    pixelSize: 1,
                    physicalUnit: "px",
                    showCaliperScores: false,
                    handleSize: 8,
                    infoTextOffset: 20,
                    angleArcRadius: 20,
                    pointAnnotationSize: 10,
                    polygonResizeHandlePadding: 4,
                    polygonCloseHighlightPadding: 6);

                Point anchor = DualEdgeCaliperRenderHelper.GetSummaryAnchor(context, caliper);

                Assert.Equal(new Point(50, 80), anchor);
            });
        }

        [Fact]
        public void DualEdgeCaliperSummaryAnchor_LeavesFullLabelWidthBesideVerticalSegment()
        {
            WpfTestRunner.Run(() =>
            {
                var caliper = new CaliperMeasureRoi
                {
                    P1 = new PointD(50, 10),
                    P2 = new PointD(50, 110),
                    HasDetectedEdges = true
                };
                caliper.ApplyWidthMeasurement(new CaliperWidthMeasurementResult([100], centerDistance: 100));
                var context = CreateRenderContext(new Canvas(), new Canvas());
                string summary = DualEdgeCaliperRenderHelper.BuildSummaryText(context, caliper);
                Point anchor = DualEdgeCaliperRenderHelper.GetSummaryAnchor(context, caliper, summary);
                Size labelSize = RoiRenderContext.MeasureInfoText(summary);

                Assert.True(anchor.X + labelSize.Width / 2 + 5 <= 50, $"Label right edge {anchor.X + labelSize.Width / 2} should remain left of x=50.");
                Assert.Equal("宽度：100.00 px", summary);
            });
        }

        [Fact]
        public void DualEdgeCaliperSummaryAnchor_UsesRoiCenterBeforeDetection()
        {
            var caliper = new CaliperMeasureRoi
            {
                CaliperCenter = new PointD(100, 100),
                P1 = new PointD(10, 20),
                P2 = new PointD(30, 40)
            };

            Point anchor = DualEdgeCaliperRenderHelper.GetSummaryAnchor(caliper);

            Assert.Equal(new Point(100, 100), anchor);
        }

        [Fact]
        public void NonSelectedDualEdgeCaliper_HidesSamplingRegionAndEdgeMarkersButKeepsDetectedResult()
        {
            WpfTestRunner.Run(() =>
            {
                var caliper = new CaliperMeasureRoi
                {
                    P1 = new PointD(10, 20),
                    P2 = new PointD(30, 20),
                    HasDetectedEdges = true,
                    Edge1Start = new PointD(10, 10),
                    Edge1End = new PointD(10, 30),
                    Edge2Start = new PointD(30, 10),
                    Edge2End = new PointD(30, 30),
                    RegionSegments = [new LineSegmentOverlay(new PointD(0, 0), new PointD(40, 0))],
                    CaliperBars = [new LineSegmentOverlay(new PointD(0, 5), new PointD(0, 15))],
                    Edge1Markers = [new LineSegmentOverlay(new PointD(9, 9), new PointD(11, 11))]
                };
                var context = CreateRenderContext(new Canvas(), new Canvas());
                new RoiRenderService(RoiPluginRegistry.CreateBuiltIn()).Render(caliper, context, null, isSelected: false);

                System.Windows.Shapes.Line[] lines = context.ScreenOverlayCanvas.Children.OfType<System.Windows.Shapes.Line>().ToArray();
                Assert.Equal(3, lines.Length);
                Assert.Contains(lines, line => line.Stroke == Brushes.LimeGreen);
            });
        }

        private static RoiRenderContext CreateRenderContext(Canvas overlayCanvas, Canvas screenOverlayCanvas)
        {
            return new RoiRenderContext(
                overlayCanvas,
                screenOverlayCanvas,
                static point => point,
                scale: 1,
                pixelSize: 1,
                physicalUnit: "px",
                showCaliperScores: false,
                handleSize: 8,
                infoTextOffset: 20,
                angleArcRadius: 20,
                pointAnnotationSize: 10,
                polygonResizeHandlePadding: 4,
                polygonCloseHighlightPadding: 6);
        }

        [Fact]
        public void BuildRenderFrame_ReusesCachedTileForRepeatedRequests()
        {
            WpfTestRunner.Run(() =>
            {
                var service = new ImageViewerRenderService();
                BitmapSource source = CreateLargeBitmap();
                var pyramid = new[] { new ImagePyramidLevel(source, 1.0) };

                ImageViewerRenderFrame first = service.BuildRenderFrame(
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

                ImageViewerRenderFrame second = service.BuildRenderFrame(
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

                service.ClearTileCache();

                ImageViewerRenderFrame third = service.BuildRenderFrame(
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

                Assert.True(first.IsTiled);
                Assert.Same(first.Source, second.Source);
                Assert.NotSame(second.Source, third.Source);
            });
        }

        [Fact]
        public void BuildRenderFrame_WhenSourceIsNull_ReturnsEmptyFrame()
        {
            WpfTestRunner.Run(() =>
            {
                var service = new ImageViewerRenderService();

                ImageViewerRenderFrame frame = service.BuildRenderFrame(
                    null,
                    null,
                    new Size(512, 512),
                    1.0,
                    new Point(0, 0),
                    ImageViewerDisplaySettings.PassThrough,
                    enableTiledRendering: true,
                    autoSelectPyramidLevel: true,
                    prefetchAdjacentTiles: true,
                    tileCacheMaximumMegabytes: 64,
                    tilePrefetchRadius: 2);

                Assert.Null(frame.Source);
                Assert.False(frame.IsTiled);
            });
        }

        [Fact]
        public void BuildRenderFrame_WithNonFrozenSource_ReturnsUsableFrame()
        {
            WpfTestRunner.Run(() =>
            {
                var service = new ImageViewerRenderService();
                BitmapSource source = CreateLargeBitmap();
                Assert.False(source.IsFrozen);

                ImageViewerRenderFrame frame = service.BuildRenderFrame(
                    source,
                    [new ImagePyramidLevel(source, 1.0)],
                    new Size(512, 512),
                    1.0,
                    new Point(0, 0),
                    ImageViewerDisplaySettings.PassThrough,
                    enableTiledRendering: true,
                    autoSelectPyramidLevel: true,
                    prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64,
                    tilePrefetchRadius: 0);

                Assert.NotNull(frame.Source);
                Assert.True(frame.IsTiled);
            });
        }

        [Fact]
        public void BuildRenderFrame_TiledCropKeepsImageAndOverlayCoordinatesAlignedAtHighZoom()
        {
            WpfTestRunner.Run(() =>
            {
                const int edgeX = 120;
                const double zoom = 12;
                const double translationX = -1242.8;
                var service = new ImageViewerRenderService();
                BitmapSource source = CreateStepBitmap(2100, 2000, edgeX);

                ImageViewerRenderFrame frame = service.BuildRenderFrame(
                    source,
                    [new ImagePyramidLevel(source, 1.0)],
                    new Size(631, 1240),
                    zoom,
                    new Point(translationX, 0),
                    ImageViewerDisplaySettings.PassThrough,
                    enableTiledRendering: true,
                    autoSelectPyramidLevel: false,
                    prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 128,
                    tilePrefetchRadius: 0);

                BitmapSource renderedTile = Assert.IsAssignableFrom<BitmapSource>(frame.Source);
                byte[] pixels = new byte[renderedTile.PixelWidth * renderedTile.PixelHeight];
                renderedTile.CopyPixels(pixels, renderedTile.PixelWidth, 0);
                int localEdgeX = Array.FindIndex(pixels, value => value == 48);
                Assert.True(localEdgeX >= 0);

                double imageEdgeScreenX = (frame.Left + localEdgeX * frame.Width / renderedTile.PixelWidth) * zoom + translationX;
                double overlayEdgeScreenX = edgeX * zoom + translationX;
                Assert.InRange(Math.Abs(imageEdgeScreenX - overlayEdgeScreenX), 0, 0.1);
            });
        }

        [Fact]
        public void RenderTileCache_SetSmallBudget_EvictsOlderEntries()
        {
            WpfTestRunner.Run(() =>
            {
                var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateLargeBitmap();
                Int32Rect firstRect = new(0, 0, 512, 512);
                Int32Rect secondRect = new(512, 0, 512, 512);
                BitmapSource first = cache.GetOrCreate(source, firstRect);
                cache.SetMaximumBytes(512L * 512L + 1);
                cache.GetOrCreate(source, secondRect);

                Assert.False(cache.TryGet(source, firstRect, out _));
                Assert.True(cache.TryGet(source, secondRect, out BitmapSource? current));
                Assert.Same(current, cache.GetOrCreate(source, secondRect));
                Assert.NotSame(first, current);
            });
        }

        [Fact]
        public void RenderTileCache_PrefetchRects_RespectRadiusAndImageBounds()
        {
            IReadOnlyList<Int32Rect> none = ImageViewerRenderTileCache.BuildPrefetchRects(
                new Int32Rect(0, 0, 512, 512), 1024, 1024, 0);
            IReadOnlyList<Int32Rect> rects = ImageViewerRenderTileCache.BuildPrefetchRects(
                new Int32Rect(0, 0, 512, 512), 1024, 1024, 1);

            Assert.Empty(none);
            Assert.NotEmpty(rects);
            Assert.All(rects, rect =>
            {
                Assert.InRange(rect.X, 0, 1023);
                Assert.InRange(rect.Y, 0, 1023);
                Assert.True(rect.X + rect.Width <= 1024);
                Assert.True(rect.Y + rect.Height <= 1024);
            });
        }

        [Fact]
        public void RenderTileCache_Dispose_PreventsNewPrefetchWork()
        {
            WpfTestRunner.Run(() =>
            {
                using var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateLargeBitmap();

                cache.Dispose();
                cache.Prefetch(source, [new Int32Rect(0, 0, 512, 512)]);

                Assert.False(cache.TryGet(source, new Int32Rect(0, 0, 512, 512), out _));
            });
        }

        [Fact]
        public void RenderTileCache_DisposeAsync_IsIdempotent()
        {
            WpfTestRunner.Run(() =>
            {
                var cache = new ImageViewerRenderTileCache();
                cache.Dispose();

                ValueTask first = cache.DisposeAsync();
                ValueTask second = cache.DisposeAsync();

                first.AsTask().GetAwaiter().GetResult();
                second.AsTask().GetAwaiter().GetResult();
                Assert.False(cache.TryGet(CreateLargeBitmap(), new Int32Rect(0, 0, 512, 512), out _));
            });
        }

        [Fact]
        public void RenderTileCache_PrefetchIsBoundedAndClearCancelsIt()
        {
            WpfTestRunner.Run(() =>
            {
                using var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateFrozenLargeBitmap();
                Func<BitmapSource, BitmapSource> palette = tile =>
                    ImageViewerDisplaySourceService.ApplyDisplayLut(tile, HotSettings);
                Int32Rect[] rects = BuildInBoundsTileRects();

                cache.Prefetch(source, rects, HotSettings, palette);

                // 待处理上限：投机预取不允许无上限排队（候选 rect 数远大于上限，入队数必须被截断）。
                int queued = cache.PendingPrefetchCount;
                Assert.True(queued > 0);
                Assert.True(queued < rects.Length, $"全部 {rects.Length} 个 rect 都入了队，说明预取没有上限。");
                Assert.True(queued <= 64, $"待处理预取数 {queued} 超过上限。");

                cache.Clear();

                // 等到每个已入队的任务都结束（成功/取消/异常都算结束）再断言，否则读到的是"还没轮到取消"的中间态。
                Assert.True(
                    WaitForPrefetchCompletion(cache, queued),
                    $"预取任务未全部结束：finished={cache.FinishedPrefetchCount}，expected={queued}。");

                // 清缓存要让在途/排队的预取停下来，而不是让它们跑完再丢弃结果。
                Assert.Equal(0, cache.FailedPrefetchCount);
                Assert.True(cache.CancelledPrefetchCount > 0, "Clear 之后仍有排队的预取在跑。");
                Assert.InRange(cache.MaxObservedConcurrentPrefetches, 1, 2);
                Assert.Equal(0, cache.CurrentBytes);
            });
        }

        [Fact]
        public void RenderTileCache_DerivesCropsFromTheTileEntryWithoutSeparateEntries()
        {
            WpfTestRunner.Run(() =>
            {
                var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateFrozenLargeBitmap();
                Int32Rect tileRect = new(0, 0, 512, 512);
                var factoryCalls = 0;
                Func<BitmapSource, BitmapSource> palette = tile =>
                {
                    factoryCalls++;
                    return ImageViewerDisplaySourceService.ApplyDisplayLut(tile, HotSettings);
                };

                Int32Rect viewRect = new(10, 10, 400, 300);
                BitmapSource first = cache.GetOrCreateTileView(source, tileRect, HotSettings, viewRect, palette);
                BitmapSource repeated = cache.GetOrCreateTileView(source, tileRect, HotSettings, viewRect, palette);
                BitmapSource other = cache.GetOrCreateTileView(source, tileRect, HotSettings, new Int32Rect(50, 20, 100, 100), palette);

                // 派生视图按 (tile, 视图矩形) 复用：重复帧拿到同一实例（WPF 不会每帧重传纹理）。
                Assert.Same(first, repeated);
                Assert.NotSame(first, other);

                // 着色只做一次；多个可见 crop 共用同一个 tile 条目——可见 crop 不再单独入账。
                Assert.Equal(1, factoryCalls);
                Assert.Equal(1, cache.EntryCount);

                // 预算记的是条目真正持有的像素：整块已着色 tile（BGRA32），不是 crop 面积。
                Assert.Equal((long)tileRect.Width * tileRect.Height * 4, cache.CurrentBytes);
            });
        }

        [Fact]
        public void RenderTileCache_TileProcessingDoesNotBlockExistingEntries()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateFrozenLargeBitmap();
                Int32Rect firstRect = new(0, 0, 512, 512);
                Int32Rect secondRect = new(512, 0, 512, 512);
                cache.GetOrCreate(source, firstRect);

                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<BitmapSource> processing = Task.Run(() => cache.GetOrCreateTileView(
                    source,
                    secondRect,
                    HotSettings,
                    new Int32Rect(0, 0, secondRect.Width, secondRect.Height),
                    tile =>
                    {
                        entered.TrySetResult();
                        release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                        return ImageViewerDisplaySourceService.ApplyDisplayLut(tile, HotSettings);
                    }));

                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    // 另一个线程处理新 tile 时，已有的缓存命中不能排在它的像素变换后面。
                    Task<bool> existingHit = Task.Run(() => cache.TryGet(source, firstRect, out _));
                    Assert.True(await existingHit.WaitAsync(TimeSpan.FromSeconds(2)), "tile 处理持锁阻塞了已有缓存命中。");
                }
                finally
                {
                    release.TrySetResult();
                }

                await processing.WaitAsync(TimeSpan.FromSeconds(10));
            });
        }

        [Fact]
        public void RenderTileCache_EvictingTheTileAlsoReleasesItsDerivedViews()
        {
            WpfTestRunner.Run(() =>
            {
                var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateFrozenLargeBitmap();
                Int32Rect tileRect = new(0, 0, 512, 512);
                var factoryCalls = 0;
                Func<BitmapSource, BitmapSource> palette = tile =>
                {
                    factoryCalls++;
                    return ImageViewerDisplaySourceService.ApplyDisplayLut(tile, HotSettings);
                };

                cache.SetMaximumBytes(1);
                cache.GetOrCreateTileView(source, tileRect, HotSettings, new Int32Rect(10, 10, 400, 300), palette);
                cache.GetOrCreateTileView(source, tileRect, HotSettings, new Int32Rect(10, 10, 400, 300), palette);

                // 条目被预算淘汰后不留下任何账外内存：视图是挂在条目上的，条目没了视图也没了。
                Assert.Equal(0, cache.CurrentBytes);
                Assert.Equal(0, cache.EntryCount);
                Assert.Equal(2, factoryCalls);
            });
        }

        [Fact]
        public void RenderTileCache_PrefetchUsesTheSameKeyAndTransformAsTheVisiblePath()
        {
            WpfTestRunner.Run(() =>
            {
                var failures = new List<Exception>();
                using var cache = new ImageViewerRenderTileCache(failures.Add);
                BitmapSource source = CreateFrozenLargeBitmap();
                Int32Rect tileRect = new(0, 0, 512, 512);
                Func<BitmapSource, BitmapSource> palette = tile =>
                    ImageViewerDisplaySourceService.ApplyDisplayLut(tile, HotSettings);

                cache.Prefetch(source, [tileRect], HotSettings, palette);
                Assert.True(WaitForPrefetchCompletion(cache, 1), "预取任务未在预期时间内结束。");
                Assert.Empty(failures);

                Assert.True(cache.TryGet(source, tileRect, HotSettings, out BitmapSource? prefetched));
                Assert.Equal(PixelFormats.Bgra32, prefetched!.Format);

                // 未着色的键下不能有"半成品"：否则可见路径会把它当成已着色结果直接显示。
                Assert.False(cache.TryGet(source, tileRect, ImageViewerDisplaySettings.PassThrough, out _));
            });
        }

        [Fact]
        public void BuildFrame_NonTiledPath_WithPalette_ReusesTheColorizedInstance()
        {
            // 非 tiled 路径也要"着色一次、实例复用"：之前每帧全量重着色（4MP ≈ 15MB 拷贝）并让 WPF 重传纹理。
            WpfTestRunner.Run(() =>
            {
                using var service = new ImageViewerRenderService();
                BitmapSource source = CreateFrozenLargeBitmap();
                var pyramid = new[] { new ImagePyramidLevel(source, 1.0) };

                ImageViewerRenderFrame first = service.BuildRenderFrame(
                    source, pyramid, new Size(512, 512), 1.0, new Point(0, 0), HotSettings,
                    enableTiledRendering: false, autoSelectPyramidLevel: true, prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64, tilePrefetchRadius: 0);
                ImageViewerRenderFrame second = service.BuildRenderFrame(
                    source, pyramid, new Size(512, 512), 1.0, new Point(0, 0), HotSettings,
                    enableTiledRendering: false, autoSelectPyramidLevel: true, prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64, tilePrefetchRadius: 0);

                Assert.Equal(PixelFormats.Bgra32, Assert.IsAssignableFrom<BitmapSource>(first.Source).Format);
                Assert.Same(first.Source, second.Source);
            });
        }

        [Fact]
        public void BuildRenderFrame_DifferentDisplaySettings_DoNotReuseTheCachedTile()
        {
            // 显示设置是整个缓存键的一部分：换一组调节必须重新着色，不能显示上一组设置的缓存像素。
            WpfTestRunner.Run(() =>
            {
                using var service = new ImageViewerRenderService();
                BitmapSource source = CreateFrozenLargeBitmap();
                var pyramid = new[] { new ImagePyramidLevel(source, 1.0) };
                var inverted = new ImageViewerDisplaySettings(
                    PseudoColorPalette.Hot,
                    DisplayAdjustment.Identity with { Invert = true });

                ImageViewerRenderFrame plain = service.BuildRenderFrame(
                    source, pyramid, new Size(512, 512), 1.0, new Point(0, 0), HotSettings,
                    enableTiledRendering: true, autoSelectPyramidLevel: true, prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64, tilePrefetchRadius: 0);
                ImageViewerRenderFrame adjusted = service.BuildRenderFrame(
                    source, pyramid, new Size(512, 512), 1.0, new Point(0, 0), inverted,
                    enableTiledRendering: true, autoSelectPyramidLevel: true, prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64, tilePrefetchRadius: 0);

                Assert.NotSame(plain.Source, adjusted.Source);
            });
        }

        [Fact]
        public void BuildFrame_NonTiledPath_WithoutPalette_ReturnsTheSource()
        {
            WpfTestRunner.Run(() =>
            {
                using var service = new ImageViewerRenderService();
                BitmapSource source = CreateFrozenLargeBitmap();
                var pyramid = new[] { new ImagePyramidLevel(source, 1.0) };

                ImageViewerRenderFrame frame = service.BuildRenderFrame(
                    source, pyramid, new Size(512, 512), 1.0, new Point(0, 0), ImageViewerDisplaySettings.PassThrough,
                    enableTiledRendering: false, autoSelectPyramidLevel: true, prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64, tilePrefetchRadius: 0);

                Assert.Same(source, frame.Source);
            });
        }

        [Fact]
        public void BuildRenderFrame_WithPalette_ReusesTheColorizedTileAndItsDerivedView()
        {
            WpfTestRunner.Run(() =>
            {
                var service = new ImageViewerRenderService();
                BitmapSource source = CreateLargeBitmap();
                var pyramid = new[] { new ImagePyramidLevel(source, 1.0) };

                ImageViewerRenderFrame first = service.BuildRenderFrame(
                    source, pyramid, new Size(512, 512), 1.0, new Point(0, 0), HotSettings,
                    enableTiledRendering: true, autoSelectPyramidLevel: true, prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64, tilePrefetchRadius: 0);
                ImageViewerRenderFrame second = service.BuildRenderFrame(
                    source, pyramid, new Size(512, 512), 1.0, new Point(0, 0), HotSettings,
                    enableTiledRendering: true, autoSelectPyramidLevel: true, prefetchAdjacentTiles: false,
                    tileCacheMaximumMegabytes: 64, tilePrefetchRadius: 0);

                Assert.Equal(PixelFormats.Bgra32, Assert.IsAssignableFrom<BitmapSource>(first.Source).Format);
                Assert.Same(first.Source, second.Source);
            });
        }

        /// <summary>
        /// 与生产一致：分析位图在进入渲染前一定是冻结的（<c>GetAnalysisBitmap</c> 保证），
        /// 未冻结的位图是线程亲和对象，预取在线程池上读它会抛跨线程异常。
        /// </summary>
        private static BitmapSource CreateFrozenLargeBitmap()
        {
            BitmapSource bitmap = CreateLargeBitmap();
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// 生成远多于预取上限的、全部落在图像范围内的候选 rect，用来验证"待处理队列有上限"。
        /// </summary>
        private static Int32Rect[] BuildInBoundsTileRects()
        {
            const int size = 128;
            var rects = new List<Int32Rect>();
            for (int y = 0; y + size <= 2001; y += size)
            {
                for (int x = 0; x + size <= 2001; x += size)
                {
                    rects.Add(new Int32Rect(x, y, size, size));
                }
            }

            return [.. rects];
        }

        private static bool WaitForPrefetchCompletion(ImageViewerRenderTileCache cache, int expectedCount)
        {
            for (int attempt = 0; attempt < 300; attempt++)
            {
                if (cache.FinishedPrefetchCount >= expectedCount)
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return cache.FinishedPrefetchCount >= expectedCount;
        }

        private static BitmapSource CreateLargeBitmap()
        {
            int width = 2001;
            int height = 2001;
            byte[] pixels = new byte[width * height];
            return BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Gray8,
                null,
                pixels,
                width);
        }

        private static BitmapSource CreateStepBitmap(int width, int height, int edgeX)
        {
            byte[] pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[y * width + x] = (byte)(x < edgeX ? 255 : 48);
                }
            }

            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        }
    }
}
