using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Effects;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerDisplayRenderService
    {
        ImageSource? BuildDisplaySource(ImageSource? source, ImageViewerDisplaySettings settings);

        void ApplyGpuCaching(System.Windows.Controls.Canvas imageContainer, bool enableGpuRendering);

        Effect? CreateDisplayEffect(ImageViewerDisplaySettings settings);
    }

    public interface IImageViewerFrameRenderService
    {
        void ClearTileCache();

        Task<IReadOnlyList<ImagePyramidLevel>> BuildPyramidAsync(BitmapSource? source, CancellationToken cancellationToken);

        ImageViewerRenderFrame BuildRenderFrame(BitmapSource? source, IReadOnlyList<ImagePyramidLevel>? pyramid, Size viewport, double scale, Point translation, ImageViewerDisplaySettings settings, bool enableTiledRendering, bool autoSelectPyramidLevel, bool prefetchAdjacentTiles, int tileCacheMaximumMegabytes, int tilePrefetchRadius);
    }

    public interface IImageViewerAnalysisRenderService
    {
        BitmapSource? GetAnalysisBitmap(ImageSource? source);

        Task<int[]?> CreateHistogramAsync(BitmapSource? source, int binCount, CancellationToken cancellationToken);

        Task<byte[]?> CreateProfileAsync(ImageViewerAnalysisRequest request, CancellationToken cancellationToken);
    }

    public interface IImageViewerHighBitDepthAnalysisRenderService
    {
        Task<ushort[]?> CreateProfile16Async(ImageViewerAnalysisRequest request, CancellationToken cancellationToken);
    }

    public interface IImageViewerRenderService :
        IImageViewerDisplayRenderService,
        IImageViewerFrameRenderService,
        IImageViewerAnalysisRenderService
    {
    }
}
