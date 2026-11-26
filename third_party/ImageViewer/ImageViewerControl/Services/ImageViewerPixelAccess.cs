using System;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    internal static class ImageViewerPixelAccess
    {
        public static string ComputePixelBufferSha256(BitmapSource bitmap, CancellationToken cancellationToken)
        {
            const int targetBandBytes = 128 * 1024;
            int stride = checked((int)(((long)bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8));
            int bandHeight = Math.Clamp(targetBandBytes / stride, 1, bitmap.PixelHeight);
            byte[] band = new byte[bandHeight * stride];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int y = 0; y < bitmap.PixelHeight; y += bandHeight)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rows = Math.Min(bandHeight, bitmap.PixelHeight - y);
                bitmap.CopyPixels(new Int32Rect(0, y, bitmap.PixelWidth, rows), band, stride, 0);
                hash.AppendData(band, 0, rows * stride);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public static BitmapSource NormalizeForIntensity(BitmapSource bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);

            if (bitmap.Format == PixelFormats.Gray8 ||
                bitmap.Format == PixelFormats.Gray16 ||
                bitmap.Format == PixelFormats.Bgr24 ||
                bitmap.Format == PixelFormats.Bgr32 ||
                bitmap.Format == PixelFormats.Bgra32)
            {
                return bitmap;
            }

            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = bitmap;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            return converted;
        }

        public static int GetBytesPerPixel(BitmapSource bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            return Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8);
        }

        /// <summary>
        /// Reads a pixel intensity without reducing a 16-bit grayscale sample to 8 bits.
        /// Color formats still return an 8-bit luminance value because their source channels
        /// are 8-bit. Callers that need a display-byte representation should use
        /// <see cref="ReadIntensity8"/> explicitly.
        /// </summary>
        public static ushort ReadIntensity(byte[] pixels, int index, int bytesPerPixel, PixelFormat format)
        {
            ArgumentNullException.ThrowIfNull(pixels);

            if (format == PixelFormats.Gray8)
            {
                return pixels[index];
            }

            if (format == PixelFormats.Gray16)
            {
                return (ushort)(pixels[index] | (pixels[index + 1] << 8));
            }

            if (bytesPerPixel >= 3)
            {
                byte b = pixels[index];
                byte g = pixels[index + 1];
                byte r = pixels[index + 2];
                return (ushort)Math.Clamp((int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, byte.MaxValue);
            }

            return pixels[index];
        }

        /// <summary>
        /// Reads an intensity mapped to the legacy 8-bit display range. This conversion is
        /// intentionally explicit so measurement and segmentation code can retain Gray16 data.
        /// </summary>
        public static byte ReadIntensity8(byte[] pixels, int index, int bytesPerPixel, PixelFormat format)
        {
            ushort value = ReadIntensity(pixels, index, bytesPerPixel, format);
            return format == PixelFormats.Gray16
                ? (byte)(value >> 8)
                : (byte)Math.Min(value, byte.MaxValue);
        }

        public static int GetIntensityMaximum(PixelFormat format)
        {
            return format == PixelFormats.Gray16 ? ushort.MaxValue : byte.MaxValue;
        }

        /// <summary>
        /// Bilinear intensity read from a packed pixel buffer. Coordinates clamp to the buffer
        /// bounds so callers can sample near the border without special cases; Gray16 keeps its
        /// full sample range.
        /// </summary>
        public static double ReadBilinearIntensity(
            byte[] pixels,
            int stride,
            int width,
            int height,
            int bytesPerPixel,
            PixelFormat format,
            double x,
            double y)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            double clampedX = Math.Clamp(x, 0, width - 1);
            double clampedY = Math.Clamp(y, 0, height - 1);
            int x0 = (int)Math.Floor(clampedX);
            int y0 = (int)Math.Floor(clampedY);
            int x1 = Math.Min(x0 + 1, width - 1);
            int y1 = Math.Min(y0 + 1, height - 1);
            double tx = clampedX - x0;
            double ty = clampedY - y0;
            double top = (ReadIntensity(pixels, (y0 * stride) + (x0 * bytesPerPixel), bytesPerPixel, format) * (1 - tx))
                       + (ReadIntensity(pixels, (y0 * stride) + (x1 * bytesPerPixel), bytesPerPixel, format) * tx);
            double bottom = (ReadIntensity(pixels, (y1 * stride) + (x0 * bytesPerPixel), bytesPerPixel, format) * (1 - tx))
                          + (ReadIntensity(pixels, (y1 * stride) + (x1 * bytesPerPixel), bytesPerPixel, format) * tx);
            return (top * (1 - ty)) + (bottom * ty);
        }
    }
}
