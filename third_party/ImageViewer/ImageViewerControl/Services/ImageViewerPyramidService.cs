using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    internal sealed class ImageViewerPyramidService
    {
        private const int PyramidMinDimension = 512;

        public static Task<IReadOnlyList<ImagePyramidLevel>> BuildAsync(BitmapSource? source, CancellationToken cancellationToken)
        {
            if (source == null)
            {
                return Task.FromResult<IReadOnlyList<ImagePyramidLevel>>([]);
            }

            BitmapSource workingSource = EnsureFrozenBitmap(source);
            return Task.Run<IReadOnlyList<ImagePyramidLevel>>(() =>
            {
                List<ImagePyramidLevel> levels = [new(workingSource, 1.0)];
                BitmapSource current = workingSource;
                double scaleFactor = 1.0;

                while (!cancellationToken.IsCancellationRequested &&
                       (current.PixelWidth > PyramidMinDimension || current.PixelHeight > PyramidMinDimension))
                {
                    scaleFactor *= 0.5;
                    var next = new TransformedBitmap(current, new System.Windows.Media.ScaleTransform(0.5, 0.5));
                    if (next.CanFreeze)
                    {
                        next.Freeze();
                    }

                    current = next;
                    levels.Add(new ImagePyramidLevel(current, scaleFactor));
                    if (current.PixelWidth <= 1 || current.PixelHeight <= 1)
                    {
                        break;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return (IReadOnlyList<ImagePyramidLevel>)levels;
            }, cancellationToken);
        }

        private static BitmapSource EnsureFrozenBitmap(BitmapSource source)
        {
            if (source.IsFrozen)
            {
                return source;
            }

            BitmapSource clone = source.Clone();
            if (clone.CanFreeze)
            {
                clone.Freeze();
            }

            return clone;
        }
    }
}