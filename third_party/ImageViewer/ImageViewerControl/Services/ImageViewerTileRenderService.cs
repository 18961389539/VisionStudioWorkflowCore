using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    internal sealed class ImageViewerTileRenderService : IDisposable, IAsyncDisposable
    {
        private const long LargeImageThresholdPixels = 4_000_000;
        private const double TileMarginScreenPixels = 192;
        private readonly ImageViewerRenderTileCache _tileCache = new();

        public void ClearTileCache() => _tileCache.Clear();

        internal bool IsDisposed => _tileCache.IsDisposed;

        public void Dispose() => _tileCache.Dispose();

        public ValueTask DisposeAsync() => _tileCache.DisposeAsync();

        public ImageViewerRenderFrame BuildFrame(
            BitmapSource source,
            IReadOnlyList<ImagePyramidLevel> levels,
            ImageViewerDisplaySettings settings,
            Size viewport,
            double scale,
            Point translation,
            bool enableTiledRendering,
            bool autoSelectPyramidLevel,
            bool prefetchAdjacentTiles,
            int tileCacheMaximumMegabytes,
            int tilePrefetchRadius)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(levels);
            ArgumentNullException.ThrowIfNull(settings);
            _tileCache.SetMaximumBytes(Math.Max(1, tileCacheMaximumMegabytes) * 1024L * 1024L);

            IReadOnlyList<ImagePyramidLevel> effectiveLevels = levels.Count > 0
                ? levels
                : [new ImagePyramidLevel(source, 1.0)];
            BitmapSource workingSource = effectiveLevels[0].Bitmap;
            ImagePyramidLevel level = SelectLevel(effectiveLevels, scale, autoSelectPyramidLevel);
            bool useTiledRendering = enableTiledRendering && IsLargeImage(workingSource) && IsValid(viewport);

            if (!useTiledRendering)
            {
                // 非 tiled 路径复用同一个 tile 缓存：整幅图就是一块"tile"。着色是最贵的一步（整块拷贝 +
                // 逐像素查表），缓存它才能在重复帧上省下来（之前这里每帧全量重着色 4MP ≈ 15MB BGRA 拷贝，
                // 且不参与预算）；实例复用也让 WPF 不必每帧重传纹理。
                Int32Rect fullImageRect = new(0, 0, workingSource.PixelWidth, workingSource.PixelHeight);
                Func<BitmapSource, BitmapSource>? nonTiledValueFactory = settings.IsPassThrough
                    ? null
                    : tile => ImageViewerDisplaySourceService.ApplyDisplayLut(tile, settings);
                BitmapSource displaySource = _tileCache.GetOrCreateTileView(workingSource, fullImageRect, settings, fullImageRect, nonTiledValueFactory);
                return new ImageViewerRenderFrame(displaySource, 0, 0, displaySource.PixelWidth, displaySource.PixelHeight, 1.0, false);
            }

            Rect visibleRegion = GetVisibleRegion(workingSource, viewport, scale, translation, prefetchAdjacentTiles);
            if (visibleRegion.IsEmpty)
            {
                BitmapSource displaySource = settings.IsPassThrough
                    ? level.Bitmap
                    : ImageViewerDisplaySourceService.ApplyDisplayLut(level.Bitmap, settings);
                return new ImageViewerRenderFrame(displaySource, 0, 0, workingSource.PixelWidth, workingSource.PixelHeight, level.ScaleFactor, false);
            }

            Int32Rect sourceCrop = ToCropRect(level.Bitmap, visibleRegion, level.ScaleFactor);

            // 只缓存网格对齐的 tile，且把"着色"这一步放进 tile 的键里：着色是最贵的一步（整块拷贝 + 逐像素查表），
            // 缓存它才能在静止帧上省下来。可见 crop 不再单独成为缓存条目——它是挂在 tile 上的派生视图，
            // 这样既保留"重复帧返回同一实例"（WPF 不会每帧重传纹理），也不会产生账外内存。
            Int32Rect tileRect = ImageViewerRenderTileCache.ExpandToTileGrid(sourceCrop, level.Bitmap.PixelWidth, level.Bitmap.PixelHeight);
            Func<BitmapSource, BitmapSource>? valueFactory = settings.IsPassThrough
                ? null
                : tile => ImageViewerDisplaySourceService.ApplyDisplayLut(tile, settings);

            Int32Rect cropWithinTile = new(sourceCrop.X - tileRect.X, sourceCrop.Y - tileRect.Y, sourceCrop.Width, sourceCrop.Height);
            BitmapSource tiledSource = _tileCache.GetOrCreateTileView(level.Bitmap, tileRect, settings, cropWithinTile, valueFactory);

            Prefetch(level.Bitmap, tileRect, settings, valueFactory, prefetchAdjacentTiles, tilePrefetchRadius);
            return BuildTiledFrame(tiledSource, sourceCrop, level.ScaleFactor);
        }

        private static ImageViewerRenderFrame BuildTiledFrame(BitmapSource source, Int32Rect sourceCrop, double scaleFactor)
        {
            double resolvedScaleFactor = Math.Max(scaleFactor, double.Epsilon);
            // The bitmap starts at the integer crop origin. Placing it at the
            // fractional visible-region origin introduces a sub-pixel offset;
            // at high zoom that becomes a visible gap between the image edge
            // and its ROI overlay. Keep the image frame anchored to the exact
            // pixels that were cropped, expressed in base-image coordinates.
            return new ImageViewerRenderFrame(
                source,
                sourceCrop.X / resolvedScaleFactor,
                sourceCrop.Y / resolvedScaleFactor,
                sourceCrop.Width / resolvedScaleFactor,
                sourceCrop.Height / resolvedScaleFactor,
                scaleFactor,
                true);
        }

        private void Prefetch(
            BitmapSource source,
            Int32Rect rect,
            ImageViewerDisplaySettings settings,
            Func<BitmapSource, BitmapSource>? valueFactory,
            bool enabled,
            int radius)
        {
            int effectiveRadius = enabled ? Math.Max(0, radius) : 0;
            if (effectiveRadius > 0)
            {
                _tileCache.Prefetch(
                    source,
                    ImageViewerRenderTileCache.BuildPrefetchRects(rect, source.PixelWidth, source.PixelHeight, effectiveRadius),
                    settings,
                    valueFactory);
            }
        }

        private static bool IsLargeImage(BitmapSource source) => (long)source.PixelWidth * source.PixelHeight >= LargeImageThresholdPixels;

        private static bool IsValid(Size size) => !double.IsNaN(size.Width) && !double.IsNaN(size.Height) && size.Width > 0 && size.Height > 0;

        private static ImagePyramidLevel SelectLevel(IReadOnlyList<ImagePyramidLevel> levels, double scale, bool autoSelectPyramidLevel)
        {
            if (!autoSelectPyramidLevel || levels.Count == 0)
            {
                return levels[0];
            }

            ImagePyramidLevel selected = levels[0];
            double target = Math.Clamp(scale, levels[^1].ScaleFactor, 1.0);
            double bestDistance = double.MaxValue;
            foreach (ImagePyramidLevel level in levels)
            {
                double distance = Math.Abs(Math.Log(level.ScaleFactor, 2) - Math.Log(target, 2));
                if (distance < bestDistance)
                {
                    selected = level;
                    bestDistance = distance;
                }
            }

            return selected;
        }

        private static Rect GetVisibleRegion(BitmapSource source, Size viewport, double scale, Point translation, bool prefetchAdjacentTiles)
        {
            if (!IsValid(viewport) || scale <= 0)
            {
                return new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            }

            double left = Math.Max(0, -translation.X / scale);
            double top = Math.Max(0, -translation.Y / scale);
            double right = Math.Min(source.PixelWidth, (viewport.Width - translation.X) / scale);
            double bottom = Math.Min(source.PixelHeight, (viewport.Height - translation.Y) / scale);
            if (right <= left || bottom <= top)
            {
                return Rect.Empty;
            }

            double margin = (prefetchAdjacentTiles ? TileMarginScreenPixels : 32) / Math.Max(scale, 0.1);
            return ClampRect(new Rect(left - margin, top - margin, (right - left) + margin * 2, (bottom - top) + margin * 2), source.PixelWidth, source.PixelHeight);
        }

        private static Rect ClampRect(Rect rect, int maxWidth, int maxHeight)
        {
            double x = Math.Clamp(rect.X, 0, maxWidth);
            double y = Math.Clamp(rect.Y, 0, maxHeight);
            double right = Math.Clamp(rect.Right, x, maxWidth);
            double bottom = Math.Clamp(rect.Bottom, y, maxHeight);
            return new Rect(x, y, right - x, bottom - y);
        }

        private static Int32Rect ToCropRect(BitmapSource source, Rect visibleRegion, double scaleFactor)
        {
            int x = Math.Clamp((int)Math.Floor(visibleRegion.X * scaleFactor), 0, source.PixelWidth - 1);
            int y = Math.Clamp((int)Math.Floor(visibleRegion.Y * scaleFactor), 0, source.PixelHeight - 1);
            int width = Math.Max(1, Math.Min(source.PixelWidth - x, (int)Math.Ceiling(visibleRegion.Width * scaleFactor)));
            int height = Math.Max(1, Math.Min(source.PixelHeight - y, (int)Math.Ceiling(visibleRegion.Height * scaleFactor)));
            return new Int32Rect(x, y, width, height);
        }
    }
}
