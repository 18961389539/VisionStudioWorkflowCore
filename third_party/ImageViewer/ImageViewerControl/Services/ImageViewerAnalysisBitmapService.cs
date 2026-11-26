using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    internal sealed class ImageViewerAnalysisBitmapService
    {
        public static Task<int[]?> CreateHistogramAsync(BitmapSource? source, int binCount, CancellationToken cancellationToken)
        {
            if (source == null)
            {
                return Task.FromResult<int[]?>(null);
            }

            return Task.Run<int[]?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ImageAnalysisService.CreateHistogram(source, binCount);
            }, cancellationToken);
        }

        public static Task<byte[]?> CreateProfileAsync(ImageViewerAnalysisRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.Run<byte[]?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ImageAnalysisService.CreateProfile(request.Bitmap, request.P1, request.P2);
            }, cancellationToken);
        }

        public static Task<ushort[]?> CreateProfile16Async(ImageViewerAnalysisRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.Run<ushort[]?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ImageAnalysisService.CreateProfile16(request.Bitmap, request.P1, request.P2);
            }, cancellationToken);
        }
    }
}
