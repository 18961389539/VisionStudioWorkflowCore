using System;

namespace ImageViewer.Services
{
    internal static class ImageViewerValidation
    {
        public static void ValidatePixelSize(double pixelSize)
        {
            if (!double.IsFinite(pixelSize) || pixelSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelSize), pixelSize, "Pixel size must be a finite positive value.");
            }
        }
    }
}
