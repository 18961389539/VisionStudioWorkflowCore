using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Localization;

namespace ImageViewer.Utils
{
    public static class PixelInfoFormatter
    {
        public static bool TryBuildPixelInfoText(BitmapSource bitmap, Point imagePos, out string text)
        {
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                text = string.Empty;
                return false;
            }

            int x = (int)imagePos.X;
            int y = (int)imagePos.Y;
            if (x < 0 || x >= bitmap.PixelWidth || y < 0 || y >= bitmap.PixelHeight)
            {
                text = UiText.FormatInvariant("PixelInfoOutsideBounds", imagePos.X, imagePos.Y);
                return false;
            }

            try
            {
                int bytesPerPixel = Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8);
                byte[] pixels = new byte[bytesPerPixel];
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, bytesPerPixel, 0);

                var builder = new StringBuilder();
                builder.Append(UiText.FormatInvariant("PixelInfoCoordinates", x, y));

                if (bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Bgr32)
                {
                    builder.Append(UiText.FormatInvariant("PixelInfoRgb", pixels[2], pixels[1], pixels[0]));
                }
                else if (bitmap.Format == PixelFormats.Rgb24)
                {
                    builder.Append(UiText.FormatInvariant("PixelInfoRgb", pixels[0], pixels[1], pixels[2]));
                }
                else if (bitmap.Format == PixelFormats.Gray8)
                {
                    builder.Append(UiText.FormatInvariant("PixelInfoGray", pixels[0]));
                }
                else if (bitmap.Format == PixelFormats.Gray16)
                {
                    ushort value = (ushort)(pixels[0] | (pixels[1] << 8));
                    builder.Append(UiText.FormatInvariant("PixelInfoGray", value));
                }
                else if (bytesPerPixel >= 3)
                {
                    builder.Append(UiText.FormatInvariant("PixelInfoValues", pixels[0], pixels[1], pixels[2]));
                }

                text = builder.ToString();
                return true;
            }
            catch
            {
                text = UiText.FormatInvariant("PixelInfoOutsideBounds", imagePos.X, imagePos.Y);
                return false;
            }
        }
    }
}
