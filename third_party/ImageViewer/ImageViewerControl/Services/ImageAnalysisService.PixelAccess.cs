using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    /// <summary>
    /// 像素与位图访问
    /// Chinese: 灰度读取、位图格式归一化、平均采样，以及直方图与剖面提取。
    /// English: Intensity reads, bitmap format normalization, averaged sampling, histogram and profile extraction.
    /// </summary>
    internal static partial class ImageAnalysisService
    {
        /// <summary>直方图按行带读取，避免为大图额外分配整幅字节缓冲。</summary>
        private const int HistogramBandBytes = 4 * 1024 * 1024;

        public static int[] CreateHistogram(BitmapSource bitmap, int binCount)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binCount);

            bitmap = NormalizeBitmap(bitmap);

            int bytesPerPixel = Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8);
            int stride = bitmap.PixelWidth * bytesPerPixel;
            int intensityRange = ImageViewerPixelAccess.GetIntensityMaximum(bitmap.Format) + 1;
            int[] histogram = new int[binCount];

            int bandRows = Math.Max(1, Math.Min(bitmap.PixelHeight, HistogramBandBytes / Math.Max(1, stride)));
            byte[] pixels = ArrayPool<byte>.Shared.Rent(stride * bandRows);
            try
            {
                int copiedRows = 0;
                while (copiedRows < bitmap.PixelHeight)
                {
                    int rows = Math.Min(bandRows, bitmap.PixelHeight - copiedRows);
                    bitmap.CopyPixels(new Int32Rect(0, copiedRows, bitmap.PixelWidth, rows), pixels, stride, 0);
                    int bandLength = rows * stride;
                    for (int index = 0; index < bandLength; index += bytesPerPixel)
                    {
                        ushort intensity = GetPixelIntensity(pixels, index, bytesPerPixel, bitmap.Format);
                        int binIndex = (int)((long)intensity * binCount / intensityRange);
                        if (binIndex >= binCount)
                        {
                            binIndex = binCount - 1;
                        }

                        histogram[binIndex]++;
                    }

                    copiedRows += rows;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }

            return histogram;
        }

        public static ushort[] CreateProfile16(BitmapSource bitmap, Point start, Point end)
        {
            ArgumentNullException.ThrowIfNull(bitmap);

            bitmap = NormalizeBitmap(bitmap);

            var points = GetLinePoints(start, end);
            if (points.Count == 0)
            {
                return Array.Empty<ushort>();
            }

            int minX = (int)points.Min(p => p.X);
            int maxX = (int)points.Max(p => p.X);
            int minY = (int)points.Min(p => p.Y);
            int maxY = (int)points.Max(p => p.Y);

            if (maxX < 0 || maxY < 0 || minX >= bitmap.PixelWidth || minY >= bitmap.PixelHeight)
            {
                return Array.Empty<ushort>();
            }

            int roiX = Math.Max(0, minX);
            int roiY = Math.Max(0, minY);
            int roiW = Math.Min(bitmap.PixelWidth, maxX + 1) - roiX;
            int roiH = Math.Min(bitmap.PixelHeight, maxY + 1) - roiY;
            if (roiW <= 0 || roiH <= 0)
            {
                return Array.Empty<ushort>();
            }

            int bytesPerPixel = (bitmap.Format.BitsPerPixel + 7) / 8;
            int stride = roiW * bytesPerPixel;
            byte[] pixels = new byte[roiH * stride];
            bitmap.CopyPixels(new Int32Rect(roiX, roiY, roiW, roiH), pixels, stride, 0);

            ushort[] profileData = new ushort[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                int pixelX = (int)points[i].X;
                int pixelY = (int)points[i].Y;
                if (pixelX < roiX || pixelX >= roiX + roiW || pixelY < roiY || pixelY >= roiY + roiH)
                {
                    continue;
                }

                int localX = pixelX - roiX;
                int localY = pixelY - roiY;
                int index = localY * stride + localX * bytesPerPixel;
                profileData[i] = GetPixelIntensity(pixels, index, bytesPerPixel, bitmap.Format);
            }

            return profileData;
        }

        /// <summary>
        /// Legacy 8-bit profile view. New callers should use <see cref="CreateProfile16"/>.
        /// </summary>
        public static byte[] CreateProfile(BitmapSource bitmap, Point start, Point end)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ushort[] profile = CreateProfile16(bitmap, start, end);
            ushort maximum = (ushort)ImageViewerPixelAccess.GetIntensityMaximum(NormalizeBitmap(bitmap).Format);
            if (maximum == byte.MaxValue)
            {
                return profile.Select(value => (byte)value).ToArray();
            }

            return profile
                .Select(value => (byte)Math.Clamp((int)Math.Round(value * 255d / maximum), 0, 255))
                .ToArray();
        }

        private static List<Point> GetLinePoints(Point start, Point end)
        {
            int x0 = (int)start.X;
            int y0 = (int)start.Y;
            int x1 = (int)end.X;
            int y1 = (int)end.Y;

            var points = new List<Point>();
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            int cx = x0;
            int cy = y0;
            while (true)
            {
                points.Add(new Point(cx, cy));
                if (cx == x1 && cy == y1)
                {
                    break;
                }

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    cx += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    cy += sy;
                }
            }

            return points;
        }

        private static ushort GetPixelIntensity(byte[] pixels, int index, int bytesPerPixel, PixelFormat format)
        {
            return ImageViewerPixelAccess.ReadIntensity(pixels, index, bytesPerPixel, format);
        }

        private static BitmapSource NormalizeBitmap(BitmapSource bitmap)
        {
            return ImageViewerPixelAccess.NormalizeForIntensity(bitmap);
        }
    }
}
