using System;
using ImageViewer.Core.Analysis;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Builds deterministic framework-neutral <see cref="IntensityImage"/> instances so the
    /// managed algorithms can be tested without WPF bitmap types.
    /// </summary>
    internal static class IntensityImageFactory
    {
        /// <summary>Creates an 8-bit grayscale plane.</summary>
        public static IntensityImage CreateGray8(int width, int height, Func<int, int, byte> value)
        {
            var pixels = new ushort[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[(y * width) + x] = value(x, y);
                }
            }

            return new IntensityImage(width, height, byte.MaxValue, pixels);
        }

        /// <summary>Creates a 16-bit grayscale plane so the full dynamic range is exercised.</summary>
        public static IntensityImage CreateGray16(int width, int height, Func<int, int, ushort> value)
        {
            var pixels = new ushort[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[(y * width) + x] = value(x, y);
                }
            }

            return new IntensityImage(width, height, ushort.MaxValue, pixels);
        }
    }
}
