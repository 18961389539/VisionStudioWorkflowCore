using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Display;
using ImageViewer.Rendering;

namespace ImageViewer.Services
{
    internal sealed class ImageViewerDisplaySourceService
    {
        public static ImageSource? BuildDisplaySource(ImageSource? source, ImageViewerDisplaySettings settings)
        {
            if (source is not BitmapSource bitmap || settings.IsPassThrough)
            {
                return source;
            }

            return ApplyDisplayLut(bitmap, settings);
        }

        public static void ApplyGpuCaching(Canvas imageContainer, bool enableGpuRendering)
        {
            ArgumentNullException.ThrowIfNull(imageContainer);
            imageContainer.CacheMode = enableGpuRendering ? new BitmapCache() : null;
        }

        public static BitmapSource? GetAnalysisBitmap(ImageSource? source)
        {
            if (source is not BitmapSource bitmap)
            {
                return null;
            }

            if (bitmap is RenderTargetBitmap)
            {
                var detachedBitmap = new WriteableBitmap(bitmap);
                if (detachedBitmap.CanFreeze)
                {
                    detachedBitmap.Freeze();
                }

                return detachedBitmap;
            }

            if (bitmap.IsFrozen)
            {
                return bitmap;
            }

            BitmapSource clone = bitmap.Clone();
            if (clone.CanFreeze)
            {
                clone.Freeze();
            }

            return clone;
        }

        /// <summary>
        /// 创建显示查找表的着色器效果。
        /// Chinese: 与 CPU 路径共用 <see cref="GetDisplayLut"/>；返回 null 表示"不需要显示处理"或"编译失败"，
        /// 调用方据此回退到 CPU 路径。
        /// English: Shares the table with the CPU path; null means "no display processing needed" or "compilation failed".
        /// </summary>
        public static Effect? CreateDisplayEffect(ImageViewerDisplaySettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (settings.IsPassThrough)
            {
                return null;
            }

            try
            {
                return new DisplayLutShaderEffect(settings);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// CPU 显示查找表（窗宽窗位/亮度/对比度/伽马/反色 + 伪彩色）。
        /// Chinese: 取值只有一份定义——<see cref="GetDisplayLut"/> 生成的那张表，Shader 采样同一张表。
        /// 索引方式按模式区分：启用调色板时按**强度**索引（与 Shader 的 `dot(rgb, 0.299/0.587/0.114)` 同权重）；
        /// 未启用调色板时按**逐通道**索引，这样调节彩色图不会把图变成灰度。灰度格式两种模式取值相同。
        /// 强度与 alpha 的规则与伪彩路径一致：Gray16 按 v/257 落到 256 级显示码，Bgra32/Pbgra32 保留输入 alpha。
        /// English: One table for both paths. With a palette the table is indexed by intensity; without one it is indexed
        /// per channel so adjusting an RGB image keeps its colors. Gray formats agree either way.
        /// </summary>
        internal static BitmapSource ApplyDisplayLut(BitmapSource source, ImageViewerDisplaySettings settings)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(settings);

            if (settings.IsPassThrough)
            {
                return source;
            }

            settings.Validate();
            BitmapSource readableSource = NormalizeForColorRead(source);
            int width = readableSource.PixelWidth;
            int height = readableSource.PixelHeight;
            int bytesPerPixel = ImageViewerPixelAccess.GetBytesPerPixel(readableSource);
            int stride = checked(width * bytesPerPixel);
            byte[] sourcePixels = new byte[checked(stride * height)];
            readableSource.CopyPixels(sourcePixels, stride, 0);

            bool hasAlpha = readableSource.Format == PixelFormats.Bgra32 || readableSource.Format == PixelFormats.Pbgra32;
            bool isGrayFormat = readableSource.Format == PixelFormats.Gray8 || readableSource.Format == PixelFormats.Gray16;
            bool perChannel = settings.UsesPerChannelIndex;
            byte[] displayLut = GetDisplayLut(settings);
            byte[] colorPixels = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int sourceIndex = i * bytesPerPixel;
                int colorIndex = i * 4;
                if (perChannel && !isGrayFormat)
                {
                    // 逐通道：每个通道用自己的 8 位显示码索引同一张表（表项是灰度，取同分量即该通道的新值）。
                    colorPixels[colorIndex] = displayLut[sourcePixels[sourceIndex] * 4];
                    colorPixels[colorIndex + 1] = displayLut[sourcePixels[sourceIndex + 1] * 4 + 1];
                    colorPixels[colorIndex + 2] = displayLut[sourcePixels[sourceIndex + 2] * 4 + 2];
                }
                else
                {
                    int lutIndex = ReadDisplayIndex(sourcePixels, sourceIndex, bytesPerPixel, readableSource.Format) * 4;
                    colorPixels[colorIndex] = displayLut[lutIndex];
                    colorPixels[colorIndex + 1] = displayLut[lutIndex + 1];
                    colorPixels[colorIndex + 2] = displayLut[lutIndex + 2];
                }

                colorPixels[colorIndex + 3] = hasAlpha ? sourcePixels[sourceIndex + 3] : byte.MaxValue;
            }

            var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, colorPixels, width * 4);
            result.Freeze();
            return result;
        }

        /// <summary>
        /// 归一化到"可以按像素直接读强度"的格式。
        /// Chinese: `Pbgra32` 的 RGB 是预乘值，不能直接当强度查表；先由 WPF 转成直通的 `Bgra32`，
        /// 与 Shader 的反预乘路径保持同一语义。其它格式也统一转 Bgra32，而不是 Gray8，避免丢 alpha。
        /// English: `Pbgra32` stores premultiplied RGB and must be converted to straight `Bgra32` before the LUT;
        /// other formats also go through `Bgra32` so alpha survives the conversion.
        /// </summary>
        private static BitmapSource NormalizeForColorRead(BitmapSource source)
        {
            if (source.Format == PixelFormats.Pbgra32)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                if (converted.CanFreeze)
                {
                    converted.Freeze();
                }

                return converted;
            }

            return source.Format == PixelFormats.Gray8 ||
                   source.Format == PixelFormats.Gray16 ||
                   source.Format == PixelFormats.Bgr24 ||
                   source.Format == PixelFormats.Bgr32 ||
                   source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        /// <summary>
        /// 求显示码索引（0..255）。
        /// Chinese: 16 位强度按 v/257 落到 0..255——与 Shader 的 `intensity * 255`（intensity = v/65535）在数学上同一个映射。
        /// English: 16-bit intensities map as v/257, which is the same mapping as the shader's `intensity * 255`.
        /// </summary>
        private static int ReadDisplayIndex(byte[] pixels, int index, int bytesPerPixel, PixelFormat format)
        {
            ushort intensity = ImageViewerPixelAccess.ReadIntensity(pixels, index, bytesPerPixel, format);
            if (format == PixelFormats.Gray16)
            {
                return Math.Clamp((int)Math.Round(intensity / 257d), 0, 255);
            }

            return Math.Min(intensity, byte.MaxValue);
        }

        /// <summary>
        /// 把显示查找表包成 256×1 位图，供 Shader 采样。
        /// Chinese: 这是 CPU 与 GPU 共用的唯一定义——着色器不再自带调色板公式，也不再自带传递函数。
        /// English: The table as a 256×1 bitmap so the shader samples it instead of carrying its own formulas.
        /// </summary>
        internal static BitmapSource CreateDisplayLutBitmap(ImageViewerDisplaySettings settings)
        {
            byte[] lut = GetDisplayLut(settings);
            var bitmap = BitmapSource.Create(256, 1, 96, 96, PixelFormats.Bgra32, null, lut, 256 * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// 生成显示查找表（BGRA，256 项，表项 i 对应输入 i/255）。
        /// Chinese: 传递曲线来自 <see cref="DisplayTransfer"/>，调色板取值来自本文件的 <see cref="GetPaletteColor"/>；
        /// 两者在这里合成一张表，因此调节与伪彩的组合只有一处定义，测试与 Shader 采样路径都读这一份。
        /// 未启用调色板时表项是灰度（三个分量同值），这正是逐通道索引所要求的形状。
        /// English: Composes the transfer curve and the palette into one table, so the combination is defined exactly once.
        /// </summary>
        internal static byte[] GetDisplayLut(ImageViewerDisplaySettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            double[] curve = DisplayTransfer.CreateNormalizedLut(settings.Adjustment, 256);
            var lut = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                Color color = settings.Palette == PseudoColorPalette.None
                    ? ToGrayColor(curve[i])
                    : GetPaletteColor(curve[i], settings.Palette);
                int offset = i * 4;
                lut[offset] = color.B;
                lut[offset + 1] = color.G;
                lut[offset + 2] = color.R;
                lut[offset + 3] = 255;
            }

            return lut;
        }

        private static Color ToGrayColor(double value)
        {
            byte level = (byte)Math.Clamp((int)Math.Round(Math.Clamp(value, 0d, 1d) * 255d), 0, 255);
            return Color.FromRgb(level, level, level);
        }

        private static Color GetPaletteColor(double value, PseudoColorPalette palette)
        {
            value = Math.Clamp(value, 0d, 1d);
            return palette switch
            {
                PseudoColorPalette.Hot => Interpolate(value,
                    (0.0, Colors.Black),
                    (0.33, Colors.DarkRed),
                    (0.66, Colors.Orange),
                    (1.0, Colors.Yellow)),
                PseudoColorPalette.Jet => Interpolate(value,
                    (0.0, Color.FromRgb(0, 0, 128)),
                    (0.35, Colors.Cyan),
                    (0.66, Colors.Yellow),
                    (1.0, Color.FromRgb(128, 0, 0))),
                PseudoColorPalette.Viridis => Interpolate(value,
                    (0.0, Color.FromRgb(68, 1, 84)),
                    (0.33, Color.FromRgb(59, 82, 139)),
                    (0.66, Color.FromRgb(33, 145, 140)),
                    (1.0, Color.FromRgb(253, 231, 37))),
                _ => Colors.Transparent
            };
        }

        private static Color Interpolate(double value, params (double Stop, Color Color)[] stops)
        {
            if (stops.Length == 0)
            {
                return Colors.Transparent;
            }

            if (value <= stops[0].Stop)
            {
                return stops[0].Color;
            }

            for (int i = 1; i < stops.Length; i++)
            {
                if (value <= stops[i].Stop)
                {
                    double range = stops[i].Stop - stops[i - 1].Stop;
                    double t = range <= 0 ? 0 : (value - stops[i - 1].Stop) / range;
                    return Color.FromRgb(
                        (byte)Math.Round(stops[i - 1].Color.R + (stops[i].Color.R - stops[i - 1].Color.R) * t),
                        (byte)Math.Round(stops[i - 1].Color.G + (stops[i].Color.G - stops[i - 1].Color.G) * t),
                        (byte)Math.Round(stops[i - 1].Color.B + (stops[i].Color.B - stops[i - 1].Color.B) * t));
                }
            }

            return stops[^1].Color;
        }
    }
}
