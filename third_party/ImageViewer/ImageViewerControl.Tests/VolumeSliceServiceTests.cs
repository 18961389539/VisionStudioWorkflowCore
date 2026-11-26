using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;
using Xunit.Abstractions;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// MPR 切片重建的 single-flight 守卫。
    /// Chinese: 拖动时同一 (方向, 索引) 会被反复请求，必须只重建一次；并且"调用方放弃"不能影响共享构建，
    /// 否则一次快速拖动就会让后续所有请求都拿到"已取消"。
    /// English: Guards single-flight reconstruction per (orientation, index). An abandoned caller must not affect the shared
    /// build, and repeated requests for the same plane must reuse it.
    /// </summary>
    public class VolumeSliceServiceTests(ITestOutputHelper output)
    {
        [Fact]
        public void GetSlice_ConcurrentRequestsForTheSamePlane_BuildOnce()
        {
            VolumeData volume = CreateVolume(depth: 64, width: 128, height: 96);
            const int callerCount = 8;
            var results = new BitmapSource?[callerCount];
            var exceptions = new Exception?[callerCount];
            using var startBarrier = new Barrier(callerCount);
            var threads = new List<Thread>(callerCount);

            for (int index = 0; index < callerCount; index++)
            {
                int callerIndex = index;
                var thread = new Thread(() =>
                {
                    startBarrier.SignalAndWait();
                    try
                    {
                        results[callerIndex] = VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, 5);
                    }
                    catch (Exception exception)
                    {
                        exceptions[callerIndex] = exception;
                    }
                });
                threads.Add(thread);
            }

            foreach (Thread thread in threads)
            {
                thread.Start();
            }

            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "切片重建未在预期时间内完成。");
            }

            Assert.All(exceptions, exception => Assert.Null(exception));
            Assert.Equal(1, VolumeSliceService.GetSliceBuildCount(volume));
            Assert.All(results, result => Assert.Same(results[0], result));
        }

        [Fact]
        public void GetSlice_RepeatedRequestForTheSamePlane_ReusesTheBuiltSlice()
        {
            VolumeData volume = CreateVolume(depth: 32, width: 64, height: 48);

            BitmapSource first = VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Sagittal, 3);
            BitmapSource second = VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Sagittal, 3);

            // 完成后的共享任务本身就是缓存：重复请求既不再重建，也返回同一实例。
            Assert.Same(first, second);
            Assert.Equal(1, VolumeSliceService.GetSliceBuildCount(volume));
        }

        [Fact]
        public void GetSlice_DifferentPlanes_BuildIndependently()
        {
            VolumeData volume = CreateVolume(depth: 16, width: 32, height: 24);

            VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, 1);
            VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, 2);
            VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Sagittal, 1);

            // single-flight 只合并同一把键，不把不同平面串行化：键包含方向与索引。
            Assert.Equal(3, VolumeSliceService.GetSliceBuildCount(volume));
        }

        [Fact]
        public void GetSlice_CancelledCaller_DoesNotPoisonTheSharedSlice()
        {
            VolumeData volume = CreateVolume(depth: 32, width: 64, height: 48);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, 2, cancellation.Token));
            Assert.Equal(0, VolumeSliceService.GetSliceBuildCount(volume));
            Assert.Equal(0, VolumeSliceService.GetSliceEntryCount(volume));

            BitmapSource slice = VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, 2);

            // 冠状面输出尺寸 = (Width, Depth)；被放弃的调用方不能留下"半成品"或已取消的结果。
            Assert.Equal(64, slice.PixelWidth);
            Assert.Equal(32, slice.PixelHeight);
            Assert.True(slice.IsFrozen);
            Assert.Equal(1, VolumeSliceService.GetSliceBuildCount(volume));
        }

        [Fact]
        public void GetSlice_OutOfRangePlaneIndex_Throws()
        {
            VolumeData volume = CreateVolume(depth: 16, width: 32, height: 24);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, 24));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Sagittal, 32));
        }

        [Fact]
        public async Task GetSliceAsync_LastWaiterCancelsRunningBuild_ResultIsStillCached()
        {
            VolumeData volume = CreateVolume(depth: 32, width: 64, height: 48);
            using var cancellation = new CancellationTokenSource();
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            TimeSpan timeout = TimeSpan.FromSeconds(15);

            VolumeSliceService.SliceBuildFaultForTest = (source, _) =>
            {
                if (ReferenceEquals(volume, source))
                {
                    started.Set();
                    if (!release.Wait(timeout))
                    {
                        return new TimeoutException("The test did not release the running slice.");
                    }
                }
                return null;
            };

            try
            {
                Task<BitmapSource> slice = VolumeSliceService.GetSliceAsync(volume, VolumeSliceOrientation.Coronal, 2, cancellation.Token);
                Assert.True(started.Wait(timeout));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slice);
            }
            finally
            {
                release.Set();
                VolumeSliceService.SliceBuildFaultForTest = null;
            }

            // Completion must account for the bitmap even when no caller is waiting.
            Assert.True(
                WaitForCondition(() => VolumeSliceService.GetSliceCacheBytes(volume) > 0),
                "构建完成的结果没有入账——全部调用方取消时绕过了预算。");
            Assert.Equal((long)64 * 32, VolumeSliceService.GetSliceCacheBytes(volume));

            BitmapSource cached = await VolumeSliceService.GetSliceAsync(volume, VolumeSliceOrientation.Coronal, 2);
            Assert.Equal(64, cached.PixelWidth);
            Assert.Equal(1, VolumeSliceService.GetSliceBuildCount(volume));
        }

        [Fact]
        [Trait("Category", "Performance")]
        public async Task GetSliceAsync_AbandonedBacklog_DoesNotDelayTheLatestSharedRequest()
        {
            VolumeData volume = CreateVolume(depth: 64, width: 256, height: 256);
            const int abandonedCount = 8;
            const int latestIndex = abandonedCount + VolumeSliceService.MaxConcurrentSliceBuilds;
            TimeSpan timeout = TimeSpan.FromSeconds(15);
            using var started = new CountdownEvent(VolumeSliceService.MaxConcurrentSliceBuilds);
            using var release = new ManualResetEventSlim();
            using var abandoned = new CancellationTokenSource();
            using var oneLatestCaller = new CancellationTokenSource();
            VolumeSliceService.SliceBuildFaultForTest = (source, key) =>
            {
                if (ReferenceEquals(volume, source) && key.Index < VolumeSliceService.MaxConcurrentSliceBuilds)
                {
                    started.Signal();
                    if (!release.Wait(timeout))
                    {
                        return new TimeoutException("The test did not release the active slices.");
                    }
                }
                return null;
            };

            try
            {
                Task<BitmapSource>[] running = Enumerable.Range(0, VolumeSliceService.MaxConcurrentSliceBuilds)
                    .Select(index => VolumeSliceService.GetSliceAsync(volume, VolumeSliceOrientation.Coronal, index))
                    .ToArray();
                Assert.True(started.Wait(timeout));
                Task<BitmapSource>[] backlog = Enumerable.Range(running.Length, abandonedCount)
                    .Select(index => VolumeSliceService.GetSliceAsync(volume, VolumeSliceOrientation.Coronal, index, abandoned.Token))
                    .ToArray();
                Assert.Equal(latestIndex, VolumeSliceService.GetSliceEntryCount(volume));

                Task<BitmapSource> leaving = VolumeSliceService.GetSliceAsync(volume, VolumeSliceOrientation.Coronal, latestIndex, oneLatestCaller.Token);
                Task<BitmapSource> latest = VolumeSliceService.GetSliceAsync(volume, VolumeSliceOrientation.Coronal, latestIndex);
                abandoned.Cancel();
                oneLatestCaller.Cancel();
                foreach (Task<BitmapSource> obsolete in backlog)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
                }
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
                Assert.Equal(running.Length + 1, VolumeSliceService.GetSliceEntryCount(volume));

                var stopwatch = Stopwatch.StartNew();
                release.Set();
                BitmapSource result = await latest.WaitAsync(timeout);
                output.WriteLine($"Newest slice finished {stopwatch.Elapsed.TotalMilliseconds:F2} ms after releasing the active builds.");
                await Task.WhenAll(running).WaitAsync(timeout);
                Assert.Equal(running.Length + 1, VolumeSliceService.GetSliceBuildCount(volume));
                Assert.Same(result, VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, latestIndex));

                await VolumeSliceService.GetSliceAsync(volume, VolumeSliceOrientation.Coronal, running.Length).WaitAsync(timeout);
                Assert.Equal(running.Length + 2, VolumeSliceService.GetSliceBuildCount(volume));
            }
            finally
            {
                release.Set();
                VolumeSliceService.SliceBuildFaultForTest = null;
            }
        }

        [Fact]
        public async Task GetSlice_RapidDifferentPlanes_NeverExceedsBuildConcurrencyLimit()
        {
            VolumeData volume = CreateVolume(depth: 64, width: 256, height: 256);
            const int planeCount = 8;
            var tasks = new Task<BitmapSource>[planeCount];

            for (int index = 0; index < planeCount; index++)
            {
                int planeIndex = index;
                tasks[index] = Task.Run(() =>
                    VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, planeIndex));
            }

            BitmapSource[] slices = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(slices, slice => Assert.NotNull(slice));
            Assert.Equal(planeCount, VolumeSliceService.GetSliceBuildCount(volume));
            Assert.InRange(
                VolumeSliceService.GetMaxObservedSliceBuilds(volume),
                1,
                VolumeSliceService.MaxConcurrentSliceBuilds);
        }

        [Fact]
        public void GetSlice_FaultedBuild_IsRemovedSoTheNextCallRetries()
        {
            VolumeData volume = CreateVolume(depth: 32, width: 64, height: 48);
            (VolumeSliceOrientation Orientation, int Index) key = (VolumeSliceOrientation.Sagittal, 1);

            try
            {
                VolumeSliceService.SliceBuildFaultForTest = (v, k) => new InvalidOperationException("injected");

                Assert.Throws<InvalidOperationException>(() =>
                    VolumeSliceService.GetSlice(volume, key.Orientation, key.Index));
                Assert.True(
                    WaitForCondition(() => VolumeSliceService.GetSliceEntryCount(volume) == 0),
                    "失败条目未被移除。");

                // 故障仍在注入：第二次调用必须真的重建（构建数 +1），而不是命中被缓存的 fault。
                Assert.Throws<InvalidOperationException>(() =>
                    VolumeSliceService.GetSlice(volume, key.Orientation, key.Index));
                Assert.Equal(2, VolumeSliceService.GetSliceBuildCount(volume));
            }
            finally
            {
                VolumeSliceService.SliceBuildFaultForTest = null;
            }

            // 注入解除后：条目已被移除，这次调用会重新构建并成功（矢状面输出尺寸 = (Depth, Height)）。
            BitmapSource slice = VolumeSliceService.GetSlice(volume, key.Orientation, key.Index);
            Assert.Equal(32, slice.PixelWidth);
            Assert.Equal(48, slice.PixelHeight);
            Assert.Equal(3, VolumeSliceService.GetSliceBuildCount(volume));
        }

        private static bool WaitForCondition(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 300; attempt++)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return condition();
        }

        private static VolumeData CreateVolume(int depth, int width, int height)
        {
            var slices = new List<BitmapSource>(depth);
            for (int index = 0; index < depth; index++)
            {
                slices.Add(CreateBitmap(width, height, (byte)(index * 7)));
            }

            return new VolumeData(slices);
        }

        private static BitmapSource CreateBitmap(int width, int height, byte seed)
        {
            byte[] pixels = new byte[width * height];
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = (byte)(seed + index);
            }

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
