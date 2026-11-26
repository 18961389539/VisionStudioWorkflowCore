using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    internal sealed class ImageViewerRenderTileCache : IDisposable, IAsyncDisposable
    {
        private const int TileSizePixels = 512;
        private const long DefaultMaximumBytes = 128L * 1024L * 1024L;

        /// <summary>同时在跑的预取任务上限。</summary>
        private const int MaxConcurrentPrefetches = 2;

        /// <summary>待处理预取任务上限。预取是投机工作，当前视野的像素有同步路径兜底，堆积无上限没有收益。</summary>
        private const int MaxQueuedPrefetches = 64;

        /// <summary>单个 tile 上缓存的派生视图数量上限（视图只占元数据，超限整批丢弃重建即可）。</summary>
        private const int MaxDerivedViewsPerTile = 8;

        private readonly object _gate = new();
        private readonly Dictionary<RenderTileCacheKey, CacheEntry> _entries = new();
        private readonly LinkedList<RenderTileCacheKey> _lru = new();
        private readonly HashSet<PrefetchTicket> _inFlightPrefetches = new();
        private readonly HashSet<Task> _prefetchTasks = new();
        private readonly SemaphoreSlim _prefetchGate = new(MaxConcurrentPrefetches, MaxConcurrentPrefetches);
        private CancellationTokenSource _prefetchCancellationTokenSource = new();
        private readonly Action<Exception> _logError;

        private long _currentBytes;
        private long _usageSequence;
        private long _generation;
        private long _maximumBytes = DefaultMaximumBytes;
        private int _activePrefetchCount;
        private int _maxObservedConcurrentPrefetches;
        private int _cancelledPrefetchCount;
        private int _finishedPrefetchCount;
        private int _failedPrefetchCount;
        private bool _disposed;
        private Task? _disposeTask;

        public ImageViewerRenderTileCache(Action<Exception>? logError = null)
        {
            _logError = logError ?? (exception => Trace.WriteLine($"Image viewer tile prefetch failed: {exception}"));
        }

        public void SetMaximumBytes(long maximumBytes)
        {
            lock (_gate)
            {
                _maximumBytes = Math.Max(1, maximumBytes);
                TrimToBudget();
            }
        }

        internal bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        internal int PendingPrefetchCount
        {
            get
            {
                lock (_gate)
                {
                    return _inFlightPrefetches.Count;
                }
            }
        }

        internal int MaxObservedConcurrentPrefetches => Volatile.Read(ref _maxObservedConcurrentPrefetches);

        internal int CancelledPrefetchCount => Volatile.Read(ref _cancelledPrefetchCount);

        /// <summary>已结束的预取任务数（成功、取消、异常都算）——供测试等待"队列真正排空"。</summary>
        internal int FinishedPrefetchCount => Volatile.Read(ref _finishedPrefetchCount);

        /// <summary>预取过程中抛异常的次数。</summary>
        internal int FailedPrefetchCount => Volatile.Read(ref _failedPrefetchCount);

        internal long CurrentBytes
        {
            get
            {
                lock (_gate)
                {
                    return _currentBytes;
                }
            }
        }

        internal int EntryCount
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>
        /// 清空缓存并取消在途预取。
        /// Chinese: 只递增 generation 能防止旧任务写脏数据，但不会让它们停下来——已排队的任务仍会烧 CPU，
        /// 并通过闭包继续钉住旧的 BitmapSource。所以这里先换掉取消源，再在锁外取消（取消会同步触发注册回调）。
        /// English: Bumping the generation stops stale writes but not the work: queued prefetches keep burning CPU and keep
        /// the previous BitmapSource alive through their closures. Swap and cancel the token source outside the lock.
        /// </summary>
        public void Clear()
        {
            CancellationTokenSource cancelled;
            lock (_gate)
            {
                _generation++;
                _entries.Clear();
                _lru.Clear();
                _inFlightPrefetches.Clear();
                _currentBytes = 0;
                _usageSequence = 0;

                cancelled = _prefetchCancellationTokenSource;
                _prefetchCancellationTokenSource = new CancellationTokenSource();
            }

            CancelPrefetchWork(cancelled);
        }

        /// <summary>
        /// 取消在途预取。
        /// Chinese: 由 <see cref="Clear"/> 调用（每次换图、手动清缓存）。原来的私有 CancelPrefetch 是死代码——
        /// 取消属于 Clear 路径，而不是躺在旁边的私有方法。
        /// English: Called from <see cref="Clear"/> (every image switch and manual cache clear). The previous private
        /// CancelPrefetch was dead code; cancellation belongs on the Clear path.
        /// </summary>
        private static void CancelPrefetchWork(CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }
        }

        public BitmapSource GetOrCreate(BitmapSource source, Int32Rect rect)
        {
            return GetOrCreate(source, rect, ImageViewerDisplaySettings.PassThrough, valueFactory: null);
        }

        /// <summary>
        /// 取"最终可显示的 tile"，缺失时用 <paramref name="valueFactory"/> 生成后入缓存。
        /// Chinese: 键是 (源, 网格对齐矩形, 显示设置)，条目就是已经处理好的 tile——它是唯一被记账的对象。
        /// 显示设置整体入键：只带调色板不带调节参数会让调过亮度的 tile 被当成合法缓存直接复用。
        /// English: Keyed by (source, grid-aligned rect, display settings); the entry is the processed tile and the only
        /// accounted object. The settings enter the key as one value so a brightness change cannot reuse a stale tile.
        /// </summary>
        public BitmapSource GetOrCreate(
            BitmapSource source,
            Int32Rect rect,
            ImageViewerDisplaySettings settings,
            Func<BitmapSource, BitmapSource>? valueFactory)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(settings);

            RenderTileCacheKey key = new(source, rect, settings);
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? entry))
                {
                    Touch(entry);
                    return entry.Bitmap;
                }
            }

            BitmapSource created = CreateCroppedBitmap(source, rect);
            if (valueFactory != null)
            {
                created = valueFactory(created);
            }

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? existing))
                {
                    Touch(existing);
                    return existing.Bitmap;
                }

                AddEntry(key, created);
                return created;
            }
        }

        /// <summary>
        /// 取 tile 内 <paramref name="viewRect"/> 处的可显示位图（可见 crop）。
        /// Chinese: 派生视图挂在 tile 条目上、随 tile 一起淘汰。这样同时满足两件事——
        /// ① 重复帧拿到**同一个实例**（WPF 不会每帧重传纹理）；② 视图钉住的就是那份已被记账的 tile，
        /// 不存在"按 crop 面积记账、实际钉住整块位图"的账外内存。
        /// English: Derived crops hang off the tile entry and die with it. That keeps instance identity across repeated frames
        /// (no per-frame texture re-upload) while the view only pins the tile that is already accounted for.
        /// </summary>
        public BitmapSource GetOrCreateTileView(
            BitmapSource source,
            Int32Rect tileRect,
            ImageViewerDisplaySettings settings,
            Int32Rect viewRect,
            Func<BitmapSource, BitmapSource>? tileValueFactory)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(settings);

            RenderTileCacheKey key = new(source, tileRect, settings);
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? entry))
                {
                    Touch(entry);
                    return GetOrCreateViewCore(entry, viewRect);
                }
            }

            // 像素处理不能放在锁内：非 tiled 路径的整幅 LUT 可能持续几十毫秒，
            // 持锁会让其它线程的缓存命中、预取登记全部排队。
            BitmapSource created = CreateCroppedBitmap(source, tileRect);
            if (tileValueFactory != null)
            {
                created = tileValueFactory(created);
            }

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? existing))
                {
                    Touch(existing);
                    return GetOrCreateViewCore(existing, viewRect);
                }

                return GetOrCreateViewCore(AddEntry(key, created), viewRect);
            }
        }

        private static BitmapSource GetOrCreateViewCore(CacheEntry entry, Int32Rect viewRect)
        {
            if (viewRect.X == 0 && viewRect.Y == 0 && viewRect.Width == entry.Bitmap.PixelWidth && viewRect.Height == entry.Bitmap.PixelHeight)
            {
                return entry.Bitmap;
            }

            if (entry.DerivedViews.TryGetValue(viewRect, out BitmapSource? derivedView))
            {
                return derivedView;
            }

            // 视图本身不占像素、只占极少元数据，所以这里只做数量上限，不做 LRU：超限就整批丢掉重建。
            if (entry.DerivedViews.Count >= MaxDerivedViewsPerTile)
            {
                entry.DerivedViews.Clear();
            }

            var view = new CroppedBitmap(entry.Bitmap, viewRect);
            if (view.CanFreeze)
            {
                view.Freeze();
            }

            entry.DerivedViews[viewRect] = view;
            return view;
        }

        public bool TryGet(BitmapSource source, Int32Rect rect, ImageViewerDisplaySettings settings, out BitmapSource? bitmap)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(settings);

            RenderTileCacheKey key = new(source, rect, settings);
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? entry))
                {
                    Touch(entry);
                    bitmap = entry.Bitmap;
                    return true;
                }
            }

            bitmap = null;
            return false;
        }

        public bool TryGet(BitmapSource source, Int32Rect rect, out BitmapSource? bitmap)
        {
            return TryGet(source, rect, ImageViewerDisplaySettings.PassThrough, out bitmap);
        }

        public void Prefetch(BitmapSource source, IEnumerable<Int32Rect> rects)
        {
            Prefetch(source, rects, ImageViewerDisplaySettings.PassThrough, valueFactory: null);
        }

        /// <summary>
        /// 预取若干 tile。
        /// Chinese: 预取必须与可见路径写同一把键、同一个变换——否则预取出来的条目要么用不上（显示设置不一致），
        /// 要么被当成已处理好的结果直接显示（亮度/调色板错误）。
        /// English: Prefetch must use the same key and the same transform as the visible path, otherwise prefetched entries
        /// are either unusable or get displayed as if they were already processed.
        /// </summary>
        public void Prefetch(
            BitmapSource source,
            IEnumerable<Int32Rect> rects,
            ImageViewerDisplaySettings settings,
            Func<BitmapSource, BitmapSource>? valueFactory)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(rects);
            ArgumentNullException.ThrowIfNull(settings);

            foreach (Int32Rect rect in rects)
            {
                QueuePrefetch(source, rect, settings, valueFactory);
            }
        }

        private void QueuePrefetch(BitmapSource source, Int32Rect rect, ImageViewerDisplaySettings settings, Func<BitmapSource, BitmapSource>? valueFactory)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            RenderTileCacheKey key = new(source, rect, settings);
            PrefetchTicket ticket;
            CancellationToken cancellationToken;
            lock (_gate)
            {
                if (_disposed || _entries.ContainsKey(key))
                {
                    return;
                }

                // 未冻结的位图是线程亲和对象：预取跑在线程池上，读它的 PixelWidth 就会抛跨线程访问异常。
                // 生产路径（ImageViewerDisplaySourceService.GetAnalysisBitmap）保证传入的是冻结位图；
                // 这里对其它来源直接跳过——预取本来就是投机工作，跳过比在后台抛异常更诚实。
                if (!source.IsFrozen)
                {
                    return;
                }

                // 待处理上限：拖动大图时相邻 tile 会持续变化，无上限排队等于把线程池铺满。
                if (_inFlightPrefetches.Count >= MaxQueuedPrefetches)
                {
                    return;
                }

                ticket = new PrefetchTicket(key, _generation);
                if (!_inFlightPrefetches.Add(ticket))
                {
                    return;
                }

                cancellationToken = _prefetchCancellationTokenSource.Token;
            }

            // 故意不把 token 交给 Task.Run：token 已取消时任务会以"已取消"状态创建、任务体根本不执行，
            // 于是 finally 里的 _inFlightPrefetches 摘除也不会发生，有待处理上限时会永久堵住后续预取。
            Task prefetchTask = Task.Run(() => RunPrefetchAsync(ticket, source, rect, valueFactory, cancellationToken));

            lock (_gate)
            {
                _prefetchTasks.Add(prefetchTask);
            }

            _ = prefetchTask.ContinueWith(
                completedTask =>
                {
                    lock (_gate)
                    {
                        _prefetchTasks.Remove(completedTask);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task RunPrefetchAsync(
            PrefetchTicket ticket,
            BitmapSource source,
            Int32Rect rect,
            Func<BitmapSource, BitmapSource>? valueFactory,
            CancellationToken cancellationToken)
        {
            bool gateAcquired = false;
            try
            {
                // 并发上限：投机预取不应与当前视野的同步渲染争抢线程池。
                await _prefetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
                TrackPrefetchStarted();
                cancellationToken.ThrowIfCancellationRequested();

                BitmapSource bitmap = CreateCroppedBitmap(source, rect);
                if (valueFactory != null)
                {
                    bitmap = valueFactory(bitmap);
                }

                lock (_gate)
                {
                    if (!_disposed && ticket.Generation == _generation && !_entries.ContainsKey(ticket.Key))
                    {
                        AddEntry(ticket.Key, bitmap);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TrackPrefetchCancelled();
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedPrefetchCount);
                _logError(exception);
            }
            finally
            {
                if (gateAcquired)
                {
                    TrackPrefetchCompleted();
                    _prefetchGate.Release();
                }

                Interlocked.Increment(ref _finishedPrefetchCount);

                // 按 (key, generation) 摘除：Clear() 之后同一 tile 可能已被新一代重新排队，
                // 只按 key 摘除会把新一代的登记误删，导致重复排队。
                lock (_gate)
                {
                    _inFlightPrefetches.Remove(ticket);
                }
            }
        }

        private void TrackPrefetchStarted()
        {
            int active = Interlocked.Increment(ref _activePrefetchCount);
            int observed = Volatile.Read(ref _maxObservedConcurrentPrefetches);
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(ref _maxObservedConcurrentPrefetches, active, observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }
        }

        private void TrackPrefetchCompleted() => Interlocked.Decrement(ref _activePrefetchCount);

        private void TrackPrefetchCancelled() => Interlocked.Increment(ref _cancelledPrefetchCount);

        public void Dispose()
        {
            _ = BeginDispose();
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(BeginDispose());
        }

        private Task BeginDispose()
        {
            Task[] tasks;
            lock (_gate)
            {
                if (_disposeTask != null)
                {
                    return _disposeTask;
                }

                _disposed = true;
                _prefetchCancellationTokenSource.Cancel();
                tasks = [.. _prefetchTasks];
                _entries.Clear();
                _lru.Clear();
                _inFlightPrefetches.Clear();
                _currentBytes = 0;
                _disposeTask = CompleteDisposeAsync(tasks);
                return _disposeTask;
            }
        }

        private async Task CompleteDisposeAsync(Task[] tasks)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logError(exception);
            }
            finally
            {
                _prefetchCancellationTokenSource.Dispose();
                _prefetchGate.Dispose();
            }
        }

        private CacheEntry AddEntry(RenderTileCacheKey key, BitmapSource bitmap)
        {
            long byteCount = EstimateByteCount(bitmap);
            var node = new LinkedListNode<RenderTileCacheKey>(key);
            _lru.AddFirst(node);
            var entry = new CacheEntry(bitmap, node, byteCount, _usageSequence++);
            _entries[key] = entry;
            _currentBytes += byteCount;
            TrimToBudget();
            return entry;
        }

        private void Touch(CacheEntry entry)
        {
            if (entry.Node.List != null)
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
            }

            entry.UsageSequence = _usageSequence++;
        }

        private void TrimToBudget()
        {
            while (_currentBytes > _maximumBytes && _lru.Last != null)
            {
                LinkedListNode<RenderTileCacheKey> node = _lru.Last;
                _lru.RemoveLast();

                if (_entries.Remove(node.Value, out CacheEntry? entry))
                {
                    _currentBytes = Math.Max(0, _currentBytes - entry.ByteCount);
                }
            }
        }

        private static long EstimateByteCount(BitmapSource bitmap)
        {
            int bytesPerPixel = Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8);
            return (long)bitmap.PixelWidth * bitmap.PixelHeight * bytesPerPixel;
        }

        private static BitmapSource CreateCroppedBitmap(BitmapSource source, Int32Rect rect)
        {
            if (rect.X == 0 && rect.Y == 0 && rect.Width == source.PixelWidth && rect.Height == source.PixelHeight)
            {
                return source;
            }

            var cropped = new CroppedBitmap(source, rect);
            if (cropped.CanFreeze)
            {
                cropped.Freeze();
            }

            return cropped;
        }

        internal static Int32Rect ExpandToTileGrid(Int32Rect rect, int maxWidth, int maxHeight)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return rect;
            }

            int left = Math.Max(0, FloorToTile(rect.X));
            int top = Math.Max(0, FloorToTile(rect.Y));
            int right = Math.Min(maxWidth, CeilToTile(rect.X + rect.Width));
            int bottom = Math.Min(maxHeight, CeilToTile(rect.Y + rect.Height));

            int width = Math.Max(1, right - left);
            int height = Math.Max(1, bottom - top);
            return new Int32Rect(left, top, width, height);
        }

        internal static IReadOnlyList<Int32Rect> BuildPrefetchRects(Int32Rect rect, int maxWidth, int maxHeight, int radius)
        {
            if (radius <= 0 || rect.Width <= 0 || rect.Height <= 0)
            {
                return [];
            }

            var rects = new List<Int32Rect>();
            for (int distance = 1; distance <= radius; distance++)
            {
                int horizontalOffset = rect.Width * distance;
                int verticalOffset = rect.Height * distance;

                AddIfValid(rects, Shift(rect, horizontalOffset, 0, maxWidth, maxHeight));
                AddIfValid(rects, Shift(rect, -horizontalOffset, 0, maxWidth, maxHeight));
                AddIfValid(rects, Shift(rect, 0, verticalOffset, maxWidth, maxHeight));
                AddIfValid(rects, Shift(rect, 0, -verticalOffset, maxWidth, maxHeight));
            }

            return rects;
        }

        private static void AddIfValid(List<Int32Rect> rects, Int32Rect rect)
        {
            if (rect.Width > 0 && rect.Height > 0)
            {
                rects.Add(rect);
            }
        }

        private static Int32Rect Shift(Int32Rect rect, int deltaX, int deltaY, int maxWidth, int maxHeight)
        {
            int left = Math.Clamp(rect.X + deltaX, 0, Math.Max(0, maxWidth - 1));
            int top = Math.Clamp(rect.Y + deltaY, 0, Math.Max(0, maxHeight - 1));
            int right = Math.Min(maxWidth, left + rect.Width);
            int bottom = Math.Min(maxHeight, top + rect.Height);
            return new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        private static int FloorToTile(int value) => (value / TileSizePixels) * TileSizePixels;

        private static int CeilToTile(int value) => ((value + TileSizePixels - 1) / TileSizePixels) * TileSizePixels;

        private sealed class CacheEntry
        {
            public CacheEntry(BitmapSource bitmap, LinkedListNode<RenderTileCacheKey> node, long byteCount, long usageSequence)
            {
                Bitmap = bitmap;
                Node = node;
                ByteCount = byteCount;
                UsageSequence = usageSequence;
            }

            public BitmapSource Bitmap { get; }

            public LinkedListNode<RenderTileCacheKey> Node { get; }

            public long ByteCount { get; }

            public long UsageSequence { get; set; }

            /// <summary>
            /// 该 tile 上派生出的可见 crop 视图。
            /// Chinese: 视图不占像素、只钉住本条目这份已被记账的 tile，因此不需要单独计入预算；随条目一起释放。
            /// English: Crop views own no pixels and only pin this already-accounted tile, so they are not budgeted separately
            /// and are released together with the entry.
            /// </summary>
            public Dictionary<Int32Rect, BitmapSource> DerivedViews { get; } = new();
        }

        private readonly record struct RenderTileCacheKey(BitmapSource Source, Int32Rect Rect, ImageViewerDisplaySettings Settings);

        /// <summary>
        /// 一次预取任务的登记凭据。
        /// Chinese: 带上 generation，才能在 Clear() 之后按"这一代"精确摘除；只按 key 摘除会误删新一代的登记。
        /// English: Carries the generation so a finishing task removes exactly its own registration; removing by key alone
        /// could drop a newer registration for the same tile.
        /// </summary>
        private readonly record struct PrefetchTicket(RenderTileCacheKey Key, long Generation);
    }
}
