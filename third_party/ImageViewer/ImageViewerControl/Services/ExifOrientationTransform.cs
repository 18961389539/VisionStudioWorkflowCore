using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    /// <summary>
    /// Chinese: 把 EXIF Orientation 应用到解码后的位图。WPF 解码器不会自动旋正，手机/相机 JPEG 需要这一步。
    /// English: Applies the EXIF orientation to a decoded bitmap; WPF decoders do not rotate automatically.
    /// </summary>
    internal static class ExifOrientationTransform
    {
        internal const int NormalOrientation = 1;

        internal static int ReadOrientation(ImageMetadata? metadata)
        {
            if (metadata is not BitmapMetadata bitmapMetadata)
            {
                return NormalOrientation;
            }

            try
            {
                object? value = bitmapMetadata.GetQuery("System.Photo.Orientation");
                int orientation = value switch
                {
                    ushort ushortValue => ushortValue,
                    byte byteValue => byteValue,
                    int intValue => intValue,
                    _ => NormalOrientation
                };

                return orientation is >= 1 and <= 8 ? orientation : NormalOrientation;
            }
            catch (NotSupportedException)
            {
                return NormalOrientation;
            }
            catch (InvalidOperationException)
            {
                return NormalOrientation;
            }
        }

        internal static BitmapSource Apply(BitmapSource source, int orientation)
        {
            ArgumentNullException.ThrowIfNull(source);

            BitmapSource result = orientation switch
            {
                2 => Flip(source, flipHorizontally: true),
                3 => Rotate(source, 180),
                4 => Flip(source, flipHorizontally: false),
                5 => Flip(Rotate(source, 90), flipHorizontally: true),
                6 => Rotate(source, 90),
                7 => Flip(Rotate(source, 90), flipHorizontally: false),
                8 => Rotate(source, 270),
                _ => source
            };

            if (!ReferenceEquals(result, source) && result.CanFreeze && !result.IsFrozen)
            {
                result.Freeze();
            }

            return result;
        }

        private static BitmapSource Rotate(BitmapSource source, double angle)
        {
            return new TransformedBitmap(source, new RotateTransform(angle));
        }

        private static BitmapSource Flip(BitmapSource source, bool flipHorizontally)
        {
            var transform = flipHorizontally
                ? new ScaleTransform(-1, 1, source.PixelWidth / 2.0, source.PixelHeight / 2.0)
                : new ScaleTransform(1, -1, source.PixelWidth / 2.0, source.PixelHeight / 2.0);
            return new TransformedBitmap(source, transform);
        }
    }
}
