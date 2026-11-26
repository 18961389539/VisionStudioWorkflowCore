using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    /// <summary>
    /// MPR 切片重建服务。
    /// 同一方向和索引共享一次重建。最后一个等待者取消时，移除还在排队的切片，已开始的切片完成后仍可缓存。
    /// Callers share reconstruction by orientation and index. Queued work is dropped when its last waiter leaves.
    /// </summary>
    public sealed class VolumeSliceService
    {
        /// <summary>
        /// 每个体数据同时执行的重建上限。
        /// Chinese: 64 MB LRU 只约束已完成结果，不约束在途任务；快速切层时若不限制并发，会同时复制并着色多个完整平面。
        /// English: The 64 MB LRU bounds completed slices, not work in flight; cap parallel reconstructions so a fast slice
        /// sweep cannot keep many full planes alive at once.
        /// </summary>
        internal const int MaxConcurrentSliceBuilds = 2;

        private static readonly ConditionalWeakTable<VolumeData, CachedIntensityVolume> IntensityCaches = new();

        public static BitmapSource GetSlice(VolumeData volume, VolumeSliceOrientation orientation, int sliceIndex, CancellationToken cancellationToken = default)
            => GetSliceAsync(volume, orientation, sliceIndex, cancellationToken).GetAwaiter().GetResult();

        internal static Task<BitmapSource> GetSliceAsync(VolumeData volume, VolumeSliceOrientation orientation, int sliceIndex, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(volume);
            return orientation switch
            {
                VolumeSliceOrientation.Axial => Task.FromResult(volume.GetAxialSlice(sliceIndex)),
                VolumeSliceOrientation.Coronal or VolumeSliceOrientation.Sagittal =>
                    GetOrCreateSliceAsync(volume, orientation, sliceIndex, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Unsupported slice orientation.")
            };
        }

        /// <summary>
        /// 测试专用故障注入。
        /// Chinese: 返回非 null 异常时构建会立即抛出——否则"失败条目被移除并重建"这条不变式无法被测试
        /// （判据：重试时 <see cref="GetSliceBuildCount"/> 递增）。测试必须在 finally 里复位。
        /// English: Test-only hook. When it returns a non-null exception the build throws immediately; tests must reset it.
        /// </summary>
        internal static Func<VolumeData, (VolumeSliceOrientation Orientation, int Index), Exception?>? SliceBuildFaultForTest;

        /// <summary>
        /// 该卷实际执行过的重建次数。
        /// Chinese: 供测试观测"同一 (方向, 索引) 只构建一次"——没有这个观测点，single-flight 就只能靠代码审查。
        /// English: Exposes how many reconstructions actually ran, so single-flight can be asserted instead of reviewed.
        /// </summary>
        internal static int GetSliceBuildCount(VolumeData volume)
        {
            ArgumentNullException.ThrowIfNull(volume);
            return IntensityCaches.TryGetValue(volume, out CachedIntensityVolume? cache) ? cache.SliceBuildCount : 0;
        }

        /// <summary>已入账的切片字节数（观测"全部调用方取消也必须受预算约束"）。</summary>
        internal static long GetSliceCacheBytes(VolumeData volume)
        {
            ArgumentNullException.ThrowIfNull(volume);
            return IntensityCaches.TryGetValue(volume, out CachedIntensityVolume? cache) ? cache.SliceCacheBytes : 0;
        }

        /// <summary>条目数（观测"失败条目被移除"）。</summary>
        internal static int GetSliceEntryCount(VolumeData volume)
        {
            ArgumentNullException.ThrowIfNull(volume);
            return IntensityCaches.TryGetValue(volume, out CachedIntensityVolume? cache) ? cache.EntryCount : 0;
        }

        /// <summary>测试观测点：实际重建期间达到过的最大并发数。</summary>
        internal static int GetMaxObservedSliceBuilds(VolumeData volume)
        {
            ArgumentNullException.ThrowIfNull(volume);
            return IntensityCaches.TryGetValue(volume, out CachedIntensityVolume? cache) ? cache.MaxObservedSliceBuilds : 0;
        }

        private static Task<BitmapSource> GetOrCreateSliceAsync(VolumeData volume, VolumeSliceOrientation orientation, int sliceIndex, CancellationToken cancellationToken)
        {
            // 先做边界校验（保持原有异常语义），避免用一个非法索引去污染 single-flight 字典。
            int sliceCount = orientation == VolumeSliceOrientation.Coronal ? volume.Height : volume.Width;
            if ((uint)sliceIndex >= (uint)sliceCount)
            {
                throw new ArgumentOutOfRangeException(nameof(sliceIndex));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<BitmapSource>(cancellationToken);
            }

            CachedIntensityVolume cache = IntensityCaches.GetValue(volume, static source => new CachedIntensityVolume(source));
            return cache.GetSliceAsync(volume, orientation, sliceIndex, cancellationToken);
        }

        private sealed class CachedIntensityVolume
        {
            private const long MaxSliceCacheBytes = 64L * 1024 * 1024;

            private readonly object _sliceCacheLock = new();
            private readonly Dictionary<(VolumeSliceOrientation Orientation, int Index), SliceEntry> _slices = new();
            private readonly LinkedList<SliceEntry> _sliceCacheOrder = new();
            private readonly LinkedList<SliceEntry> _pendingBuilds = new();
            private long _sliceCacheBytes;
            private int _sliceBuildCount;
            private int _activeSliceBuildCount;
            private int _maxObservedSliceBuilds;

            public CachedIntensityVolume(VolumeData volume)
            {
                // Keep only the format decision here. Normalized bitmaps and intensity
                // buffers are created one axial slice at a time during MPR reconstruction.
                // Retaining one full buffer per axial slice made the first MPR request scale
                // with the entire volume instead of with the requested output plane.
                IsGray16 = volume.Slices.Any(slice => slice.Format == PixelFormats.Gray16);
            }

            public bool IsGray16 { get; }

            public int SliceBuildCount => Volatile.Read(ref _sliceBuildCount);

            public int EntryCount
            {
                get
                {
                    lock (_sliceCacheLock)
                    {
                        return _slices.Count;
                    }
                }
            }

            /// <summary>已入账的切片字节数。</summary>
            public long SliceCacheBytes => Volatile.Read(ref _sliceCacheBytes);

            public int MaxObservedSliceBuilds => Volatile.Read(ref _maxObservedSliceBuilds);

            private BitmapSource BuildCoronalSlice(VolumeData volume, int sliceIndex)
            {
                int width = volume.Width;
                int height = volume.Depth;
                int outputBytesPerPixel = IsGray16 ? 2 : 1;
                byte[] output = new byte[checked(width * height * outputBytesPerPixel)];
                byte[] sourcePixels = new byte[checked(width * 4)];

                for (int depthIndex = 0; depthIndex < height; depthIndex++)
                {
                    BitmapSource source = ImageViewerPixelAccess.NormalizeForIntensity(volume.GetAxialSlice(depthIndex));
                    int bytesPerPixel = ImageViewerPixelAccess.GetBytesPerPixel(source);
                    int sourceStride = checked(width * bytesPerPixel);
                    source.CopyPixels(new Int32Rect(0, sliceIndex, width, 1), sourcePixels, sourceStride, 0);
                    CopyRowToOutput(sourcePixels, bytesPerPixel, source.Format, output, depthIndex * width * outputBytesPerPixel, width, IsGray16);
                }

                return CreateOutputBitmap(width, height, output, IsGray16);
            }

            private BitmapSource BuildSagittalSlice(VolumeData volume, int sliceIndex)
            {
                int width = volume.Depth;
                int height = volume.Height;
                int outputBytesPerPixel = IsGray16 ? 2 : 1;
                byte[] output = new byte[checked(width * height * outputBytesPerPixel)];
                byte[] sourcePixels = new byte[checked(height * 4)];

                for (int depthIndex = 0; depthIndex < width; depthIndex++)
                {
                    BitmapSource source = ImageViewerPixelAccess.NormalizeForIntensity(volume.GetAxialSlice(depthIndex));
                    int bytesPerPixel = ImageViewerPixelAccess.GetBytesPerPixel(source);
                    source.CopyPixels(new Int32Rect(sliceIndex, 0, 1, height), sourcePixels, bytesPerPixel, 0);
                    CopyColumnToOutput(sourcePixels, bytesPerPixel, source.Format, output, depthIndex, width, height, IsGray16);
                }

                return CreateOutputBitmap(width, height, output, IsGray16);
            }

            public async Task<BitmapSource> GetSliceAsync(
                VolumeData volume,
                VolumeSliceOrientation orientation,
                int sliceIndex,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (orientation, sliceIndex);
                SliceEntry entry;
                lock (_sliceCacheLock)
                {
                    if (!_slices.TryGetValue(key, out entry!))
                    {
                        entry = new SliceEntry(key);
                        _slices.Add(key, entry);
                        entry.QueueNode = _pendingBuilds.AddLast(entry);
                    }
                    else if (entry.CacheNode is { } cached)
                    {
                        _sliceCacheOrder.Remove(cached);
                        _sliceCacheOrder.AddLast(cached);
                        return entry.Completion.Task.Result;
                    }

                    entry.WaiterCount++;
                    StartNextBuild(volume);
                }

                try
                {
                    return await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_sliceCacheLock)
                    {
                        if (--entry.WaiterCount == 0 && entry.QueueNode is { } queued)
                        {
                            _pendingBuilds.Remove(queued);
                            entry.QueueNode = null;
                            _slices.Remove(entry.Key);
                            entry.Completion.SetCanceled(cancellationToken);
                        }
                    }
                }
            }

            // Called under the cache lock. Removing the queue node commits a build to running;
            // callers may still leave independently while it runs and the result is cached on completion.
            private void StartNextBuild(VolumeData volume)
            {
                if (_activeSliceBuildCount >= MaxConcurrentSliceBuilds || _pendingBuilds.First is not { } next)
                {
                    return;
                }

                SliceEntry entry = next.Value;
                _pendingBuilds.RemoveFirst();
                entry.QueueNode = null;
                _activeSliceBuildCount++;
                _maxObservedSliceBuilds = Math.Max(_maxObservedSliceBuilds, _activeSliceBuildCount);
                _sliceBuildCount++;
                _ = Task.Run(() => BuildSlice(volume, entry));
            }

            private void BuildSlice(VolumeData volume, SliceEntry entry)
            {
                BitmapSource? slice = null;
                Exception? failure = null;
                try
                {
                    if (SliceBuildFaultForTest?.Invoke(volume, entry.Key) is { } fault)
                    {
                        throw fault;
                    }

                    slice = entry.Key.Orientation == VolumeSliceOrientation.Coronal
                        ? BuildCoronalSlice(volume, entry.Key.Index)
                        : BuildSagittalSlice(volume, entry.Key.Index);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                lock (_sliceCacheLock)
                {
                    _activeSliceBuildCount--;
                    if (failure != null)
                    {
                        _slices.Remove(entry.Key);
                        entry.Completion.SetException(failure);
                        // A build can fail after all callers have left.
                        _ = entry.Completion.Task.Exception;
                    }
                    else
                    {
                        RegisterCompletedSlice(entry, slice!);
                        entry.Completion.SetResult(slice!);
                    }

                    StartNextBuild(volume);
                }
            }

            private void RegisterCompletedSlice(SliceEntry entry, BitmapSource slice)
            {
                long bytes = checked((long)slice.PixelWidth * slice.PixelHeight * (IsGray16 ? 2 : 1));
                if (bytes > MaxSliceCacheBytes)
                {
                    _slices.Remove(entry.Key);
                    return;
                }

                entry.Bytes = bytes;
                entry.CacheNode = _sliceCacheOrder.AddLast(entry);
                _sliceCacheBytes += bytes;

                while (_sliceCacheBytes > MaxSliceCacheBytes && _sliceCacheOrder.First is { } oldest)
                {
                    _sliceCacheOrder.RemoveFirst();
                    SliceEntry evicted = oldest.Value;
                    _slices.Remove(evicted.Key);
                    _sliceCacheBytes -= evicted.Bytes;
                    evicted.CacheNode = null;
                }
            }

            private sealed class SliceEntry((VolumeSliceOrientation Orientation, int Index) key)
            {
                public (VolumeSliceOrientation Orientation, int Index) Key { get; } = key;
                public TaskCompletionSource<BitmapSource> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public int WaiterCount { get; set; }
                public LinkedListNode<SliceEntry>? QueueNode { get; set; }
                public LinkedListNode<SliceEntry>? CacheNode { get; set; }
                public long Bytes { get; set; }
            }

            private static void CopyRowToOutput(
                byte[] sourcePixels,
                int sourceBytesPerPixel,
                PixelFormat sourceFormat,
                byte[] output,
                int outputOffset,
                int width,
                bool gray16)
            {
                for (int x = 0; x < width; x++)
                {
                    ushort value = ImageViewerPixelAccess.ReadIntensity(sourcePixels, x * sourceBytesPerPixel, sourceBytesPerPixel, sourceFormat);
                    WriteOutputValue(output, outputOffset + x * (gray16 ? 2 : 1), value, gray16, sourceFormat);
                }
            }

            private static void CopyColumnToOutput(
                byte[] sourcePixels,
                int sourceBytesPerPixel,
                PixelFormat sourceFormat,
                byte[] output,
                int depthIndex,
                int outputWidth,
                int height,
                bool gray16)
            {
                for (int y = 0; y < height; y++)
                {
                    ushort value = ImageViewerPixelAccess.ReadIntensity(sourcePixels, y * sourceBytesPerPixel, sourceBytesPerPixel, sourceFormat);
                    WriteOutputValue(output, (y * outputWidth + depthIndex) * (gray16 ? 2 : 1), value, gray16, sourceFormat);
                }
            }

            private static void WriteOutputValue(byte[] output, int offset, ushort value, bool gray16, PixelFormat sourceFormat)
            {
                if (!gray16)
                {
                    output[offset] = (byte)Math.Min(value, byte.MaxValue);
                    return;
                }

                // A mixed-format volume uses Gray16 for the reconstructed output. Promote
                // 8-bit and color samples to the same full range as native Gray16 samples.
                ushort normalized = sourceFormat == PixelFormats.Gray16 ? value : (ushort)(value * 257);
                output[offset] = (byte)normalized;
                output[offset + 1] = (byte)(normalized >> 8);
            }

            private static BitmapSource CreateOutputBitmap(int width, int height, byte[] pixels, bool gray16)
            {
                BitmapSource result = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    gray16 ? PixelFormats.Gray16 : PixelFormats.Gray8,
                    null,
                    pixels,
                    width * (gray16 ? 2 : 1));
                result.Freeze();
                return result;
            }
        }
    }
}
