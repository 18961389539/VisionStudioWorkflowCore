using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;

namespace ImageViewer.Services
{
    internal interface IImageViewerAnalysisPipeline
    {
        ValueTask<int[]> CreateHistogramAsync(BitmapSource bitmap, int binCount, CancellationToken cancellationToken = default);

        ValueTask<ushort[]> CreateProfile16Async(BitmapSource bitmap, Point start, Point end, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// WPF adapter that composes host-independent analysis stages around bitmap-specific algorithms.
    /// </summary>
    internal sealed class ImageViewerAnalysisPipeline : IImageViewerAnalysisPipeline
    {
        private readonly IAnalysisPipeline<HistogramContext> _histogramPipeline;
        private readonly IAnalysisPipeline<ProfileContext> _profilePipeline;

        public ImageViewerAnalysisPipeline()
        {
            _histogramPipeline = new AnalysisPipeline<HistogramContext>
            ([new DelegateAnalysisStage<HistogramContext>((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Result = ImageAnalysisService.CreateHistogram(context.Bitmap, context.BinCount);
                return ValueTask.CompletedTask;
            })]);

            _profilePipeline = new AnalysisPipeline<ProfileContext>
            ([new DelegateAnalysisStage<ProfileContext>((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Result = ImageAnalysisService.CreateProfile16(context.Bitmap, context.Start, context.End);
                return ValueTask.CompletedTask;
            })]);
        }

        public async ValueTask<int[]> CreateHistogramAsync(BitmapSource bitmap, int binCount, CancellationToken cancellationToken = default)
        {
            var context = new HistogramContext(bitmap, binCount);
            await _histogramPipeline.ExecuteAsync(context, cancellationToken);
            return context.Result ?? [];
        }

        public async ValueTask<ushort[]> CreateProfile16Async(BitmapSource bitmap, Point start, Point end, CancellationToken cancellationToken = default)
        {
            var context = new ProfileContext(bitmap, start, end);
            await _profilePipeline.ExecuteAsync(context, cancellationToken);
            return context.Result ?? [];
        }

        private sealed class HistogramContext(BitmapSource bitmap, int binCount)
        {
            public BitmapSource Bitmap { get; } = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
            public int BinCount { get; } = binCount;
            public int[]? Result { get; set; }
        }

        private sealed class ProfileContext(BitmapSource bitmap, Point start, Point end)
        {
            public BitmapSource Bitmap { get; } = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
            public Point Start { get; } = start;
            public Point End { get; } = end;
            public ushort[]? Result { get; set; }
        }
    }
}
