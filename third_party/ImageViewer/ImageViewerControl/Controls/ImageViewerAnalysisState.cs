using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media.Imaging;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    /// <summary>
    /// 一次分析请求的凭据：不可变序号 + 专属取消源。
    /// Chinese: 提交结果前用 <see cref="ImageViewerAnalysisState.IsCurrentHistogramTicket"/> 之类的校验确认"我仍是最新一次请求"，
    /// 旧请求即使算完也不会写进界面。
    /// English: The ticket handed to one analysis request. Results are only committed while the ticket is still current,
    /// so a superseded request cannot overwrite newer output.
    /// </summary>
    internal sealed class ImageViewerAnalysisTicket : IDisposable
    {
        private int _isDisposed;

        public ImageViewerAnalysisTicket(long sequence)
        {
            Sequence = sequence;
            Cancellation = new CancellationTokenSource();
        }

        public long Sequence { get; }

        public CancellationTokenSource Cancellation { get; }

        public CancellationToken Token => Cancellation.Token;

        public bool IsCancellationRequested => Cancellation.IsCancellationRequested;

        public void Cancel()
        {
            if (Cancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            {
                return;
            }

            Cancellation.Dispose();
        }
    }

    /// <summary>
    /// 源图相关状态的不可变快照。
    /// Chinese: 位图、金字塔层级与 generation 一次性整体替换，
    /// 因此渲染与分析永远读不到"新位图 + 旧金字塔"这样的半成品组合。
    /// English: An immutable snapshot of the analysis source. Bitmap, pyramid levels, and generation always change together.
    /// </summary>
    internal sealed record ImageViewerAnalysisSnapshot(
        BitmapSource? Bitmap,
        IReadOnlyList<ImagePyramidLevel> PyramidLevels,
        long Generation)
    {
        public static ImageViewerAnalysisSnapshot Empty { get; } = new(null, [], 0);
    }

    internal sealed class ImageViewerAnalysisState
    {
        private static readonly ImageViewerRenderFrame EmptyRenderFrame = new(null, 0, 0, 0, 0, 1.0, false);

        private long _ticketSequence;
        private long _sourceGeneration;
        private ImageViewerAnalysisTicket? _histogramTicket;
        private ImageViewerAnalysisTicket? _profileTicket;
        private ImageViewerAnalysisTicket? _pyramidTicket;

        /// <summary>
        /// 源图快照。写入口只有 <see cref="ResetForSource"/> 与 <see cref="SetPyramidLevels"/> 两个，其余全是只读投影。
        /// </summary>
        public ImageViewerAnalysisSnapshot Snapshot { get; private set; } = ImageViewerAnalysisSnapshot.Empty;

        public BitmapSource? AnalysisBitmapSource => Snapshot.Bitmap;

        public IReadOnlyList<ImagePyramidLevel> PyramidLevels => Snapshot.PyramidLevels;

        public long SourceGeneration => Snapshot.Generation;

        public ImageViewerAnalysisTicket? HistogramTicket => _histogramTicket;

        public ImageViewerAnalysisTicket? ProfileTicket => _profileTicket;

        public ImageViewerAnalysisTicket? PyramidTicket => _pyramidTicket;

        // 渲染观测值：唯一写入者是 coordinator（渲染计划与状态栏），不参与跨线程协调，因此保持可写。
        public ImageViewerRenderFrame LastRenderFrame { get; set; } = EmptyRenderFrame;

        public TimeSpan LastPyramidBuildDuration { get; set; } = TimeSpan.Zero;

        public TimeSpan LastHistogramDuration { get; set; } = TimeSpan.Zero;

        public TimeSpan LastProfileDuration { get; set; } = TimeSpan.Zero;

        public bool IsShaderPseudoColorActive { get; set; }

        /// <summary>
        /// 切换源图：取消全部在途请求、递增 generation，并原子替换快照。
        /// Chinese: 这是"源图变化"的唯一入口，任何在此之前的分析结果都会因凭据/generation 校验失败而被丢弃。
        /// </summary>
        public void ResetForSource(BitmapSource? analysisBitmapSource)
        {
            ClearPyramidBuildWork();
            ClearAnalysisCaches();
            _sourceGeneration++;
            Snapshot = new ImageViewerAnalysisSnapshot(
                analysisBitmapSource,
                analysisBitmapSource != null ? [new ImagePyramidLevel(analysisBitmapSource, 1.0)] : [],
                _sourceGeneration);
            LastPyramidBuildDuration = TimeSpan.Zero;
            LastRenderFrame = EmptyRenderFrame;
            IsShaderPseudoColorActive = false;
        }

        /// <summary>
        /// 提交金字塔构建结果。
        /// Chinese: generation 与当前快照不一致（构建期间换过源图）时返回 false 并丢弃结果。
        /// </summary>
        public bool SetPyramidLevels(long generation, IReadOnlyList<ImagePyramidLevel> pyramidLevels, TimeSpan buildDuration)
        {
            ArgumentNullException.ThrowIfNull(pyramidLevels);
            if (generation != Snapshot.Generation)
            {
                return false;
            }

            Snapshot = Snapshot with { PyramidLevels = pyramidLevels };
            LastPyramidBuildDuration = buildDuration;
            return true;
        }

        public void ResetPyramidToBaseLevel()
        {
            ClearPyramidBuildWork();
            Snapshot = Snapshot with
            {
                PyramidLevels = Snapshot.Bitmap != null ? [new ImagePyramidLevel(Snapshot.Bitmap, 1.0)] : []
            };
            LastPyramidBuildDuration = TimeSpan.Zero;
        }

        /// <summary>
        /// 登记一次金字塔构建请求并取消上一篇请求。
        /// </summary>
        public ImageViewerAnalysisTicket BeginPyramidTicket() => BeginTicket(ref _pyramidTicket);

        public ImageViewerAnalysisTicket BeginHistogramTicket() => BeginTicket(ref _histogramTicket);

        public ImageViewerAnalysisTicket BeginProfileTicket() => BeginTicket(ref _profileTicket);

        public bool IsCurrentPyramidTicket(ImageViewerAnalysisTicket ticket) => IsCurrent(_pyramidTicket, ticket);

        public bool IsCurrentHistogramTicket(ImageViewerAnalysisTicket ticket) => IsCurrent(_histogramTicket, ticket);

        public bool IsCurrentProfileTicket(ImageViewerAnalysisTicket ticket) => IsCurrent(_profileTicket, ticket);

        /// <summary>
        /// 取消在途直方图请求（例如面板隐藏、暂停刷新）。
        /// </summary>
        public void CancelHistogramWork() => ClearHistogramWork();

        /// <summary>
        /// 取消在途剖面请求。
        /// </summary>
        public void CancelProfileWork() => ClearProfileWork();

        public void ClearAnalysisCaches()
        {
            ClearHistogramWork();
            ClearProfileWork();
        }

        public void ClearPyramidBuildWork() => ResetTicket(ref _pyramidTicket);

        public void ClearHistogramWork()
        {
            ResetTicket(ref _histogramTicket);
            LastHistogramDuration = TimeSpan.Zero;
        }

        public void ClearProfileWork()
        {
            ResetTicket(ref _profileTicket);
            LastProfileDuration = TimeSpan.Zero;
        }

        public void DisposeAnalysisWork()
        {
            ClearPyramidBuildWork();
            ClearAnalysisCaches();
        }

        private ImageViewerAnalysisTicket BeginTicket(ref ImageViewerAnalysisTicket? slot)
        {
            var ticket = new ImageViewerAnalysisTicket(++_ticketSequence);
            ResetTicket(ref slot);
            slot = ticket;
            return ticket;
        }

        private static void ResetTicket(ref ImageViewerAnalysisTicket? slot)
        {
            ImageViewerAnalysisTicket? previous = slot;
            slot = null;
            if (previous is null)
            {
                return;
            }

            previous.Cancel();
            previous.Dispose();
        }

        private static bool IsCurrent(ImageViewerAnalysisTicket? slot, ImageViewerAnalysisTicket ticket)
        {
            ArgumentNullException.ThrowIfNull(ticket);
            return ReferenceEquals(slot, ticket) && !ticket.IsCancellationRequested;
        }
    }
}
