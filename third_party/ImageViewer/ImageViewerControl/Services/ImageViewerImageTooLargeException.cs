using System;
using System.Globalization;

namespace ImageViewer.Services
{
    /// <summary>
    /// Chinese: 解码前发现像素规模超出上限时抛出，避免解压炸弹把宿主进程内存耗尽。
    /// English: Thrown before decoding when the pixel budget is exceeded.
    /// </summary>
    internal sealed class ImageViewerImageTooLargeException : InvalidOperationException
    {
        public ImageViewerImageTooLargeException(int pixelWidth, int pixelHeight, long maxPixelCount)
            : base(string.Format(
                CultureInfo.InvariantCulture,
                "Image dimensions {0}x{1} exceed the {2} pixel decode limit.",
                pixelWidth,
                pixelHeight,
                maxPixelCount))
        {
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            PixelCount = (long)pixelWidth * pixelHeight;
            MaxPixelCount = maxPixelCount;
        }

        public int PixelWidth { get; }

        public int PixelHeight { get; }

        public long PixelCount { get; }

        public long MaxPixelCount { get; }
    }
}
