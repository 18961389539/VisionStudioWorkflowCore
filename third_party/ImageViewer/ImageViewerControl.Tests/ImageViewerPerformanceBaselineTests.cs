using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using ImageViewer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 可量化性能基准。
    /// Chinese: 设计原则——**断言的必须确定**，时间只测量、只输出、不做硬阈值，因为 CI 机器差异会让时间断言随机红。
    /// 因此本文件断言的是确定性指标（重建次数、缓存字节、条目数、命中率、峰值并发），
    /// 并把耗时/内存通过 <see cref="ITestOutputHelper"/> 打印出来，供人工对比基线。
    /// 每个用例都配一条"灾难级回归"护栏（例如"重建次数不得超过请求数"、"缓存字节不得超过预算的合理倍数"），
    /// 它宽松到不会因机器差异误报，但能抓住数量级级别的退化。
    /// English: Deterministic counters are asserted; timings and memory are measured and reported only, with loose
    /// catastrophe-level guard rails so machine variance never produces flakes but order-of-magnitude regressions still fail.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Performance")]
    [Trait("Category", "Wpf")]
    public sealed class ImageViewerPerformanceBaselineTests
    {
        private static readonly ImageViewerDisplaySettings HotSettings = new(PseudoColorPalette.Hot);
        private readonly ITestOutputHelper _output;

        public ImageViewerPerformanceBaselineTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// 大图缩放：反复构建同一帧应复用缓存 tile，不得重复着色。
        /// </summary>
        [Fact]
        public void LargeImage_RepeatedFrames_ReuseCachedTileWithinBudget()
        {
            WpfTestRunner.Run(() =>
            {
                using var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateLargeBitmap(4000, 4000);
                var rect = new Int32Rect(0, 0, 512, 512);

                var stopwatch = Stopwatch.StartNew();
                for (int index = 0; index < 2_000; index++)
                {
                    cache.GetOrCreate(source, rect);
                }

                stopwatch.Stop();

                double memoryMegabytes = cache.CurrentBytes / (1024d * 1024d);
                int hitsPerSecond = (int)(2_000 / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));
                _output.WriteLine($"大图缩放：2000 次请求，{stopwatch.ElapsedMilliseconds} ms，命中率 {hitsPerSecond}/s，" +
                                  $"条目 {cache.EntryCount}，缓存 {memoryMegabytes:F1} MB");

                // 确定性：同一键只入账一个条目。
                Assert.Equal(1, cache.EntryCount);
                // 灾难级护栏：条目缓存不得随请求次数增长。
                Assert.True(cache.EntryCount <= 4, $"重复请求同一 tile 不应增加条目数，实际 {cache.EntryCount}。");
            });
        }

        /// <summary>
        /// 大图缩放（伪彩）：着色是昂贵步骤，必须只在缓存未命中时执行一次。
        /// </summary>
        [Fact]
        public void LargeImage_PseudoColor_ColorizesOnceAndReportsCost()
        {
            WpfTestRunner.Run(() =>
            {
                using var cache = new ImageViewerRenderTileCache();
                BitmapSource source = CreateLargeBitmap(3000, 3000);
                var rect = new Int32Rect(0, 0, 512, 512);

                int colorizeCount = 0;
                BitmapSource ValueFactory(BitmapSource input)
                {
                    colorizeCount++;
                    return ImageViewerDisplaySourceService.ApplyDisplayLut(input, HotSettings);
                }

                var stopwatch = Stopwatch.StartNew();
                for (int index = 0; index < 200; index++)
                {
                    cache.GetOrCreateTileView(source, rect, HotSettings, rect, ValueFactory);
                }

                stopwatch.Stop();

                _output.WriteLine($"伪彩缩放：200 帧，着色 {colorizeCount} 次，{stopwatch.ElapsedMilliseconds} ms，" +
                                  $"缓存 {cache.CurrentBytes / (1024d * 1024d):F1} MB");

                Assert.Equal(1, colorizeCount);
            });
        }

        /// <summary>
        /// 数千 ROI：批量增删与撤销/重做不得随规模出现数量级退化，并报告每千次操作耗时。
        /// Chinese: 注意撤销栈有深度上限（默认 200），因此 5000 次新增后只有最后 200 次可撤销——
        /// 这本身就是有界性的一部分，断言以此为准。
        /// </summary>
        [Fact]
        public void ThousandsOfRois_BatchMutationsAndUndoRedoStayLinear()
        {
            WpfTestRunner.Run(() =>
            {
                const int roiCount = 5_000;
                var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
                var undoRedo = viewModel.UndoRedo;

                var stopwatch = Stopwatch.StartNew();
                for (int index = 0; index < roiCount; index++)
                {
                    undoRedo.Execute(new AddRoiCommand(new LineMeasureRoi(), viewModel));
                }

                stopwatch.Stop();
                long addMilliseconds = stopwatch.ElapsedMilliseconds;
                int afterAdd = viewModel.AllRois.Count;

                // 撤销到不能再撤销：受 MaxDepth 约束，最多只能回退 DefaultMaxDepth 次。
                stopwatch.Restart();
                int undone = 0;
                while (undoRedo.CanUndo)
                {
                    undoRedo.Undo();
                    undone++;
                }

                stopwatch.Stop();
                long undoMilliseconds = stopwatch.ElapsedMilliseconds;

                _output.WriteLine($"{roiCount} ROI：新增 {addMilliseconds} ms（剩 {afterAdd}），" +
                                  $"撤销 {undone} 次 {undoMilliseconds} ms（剩 {viewModel.AllRois.Count}），CanRedo={undoRedo.CanRedo}");

                Assert.Equal(roiCount, afterAdd);
                Assert.Equal(UndoRedoManager.DefaultMaxDepth, undone);
                Assert.Equal(roiCount - UndoRedoManager.DefaultMaxDepth, viewModel.AllRois.Count);
                Assert.True(undoRedo.CanRedo);
                // 灾难级护栏：5000 次增 + 全部撤销若超过 30 秒，说明复杂度退化成 O(n²)。
                Assert.True(addMilliseconds + undoMilliseconds < 30_000,
                    $"数千 ROI 操作耗时异常：新增 {addMilliseconds} ms、撤销 {undoMilliseconds} ms。");
            });
        }

        /// <summary>
        /// 数千 ROI：清空（ClearAll）应一次性完成，不逐条 I/O。
        /// </summary>
        [Fact]
        public void ThousandsOfRois_ClearAll_CompletesInOnePass()
        {
            WpfTestRunner.Run(() =>
            {
                var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
                for (int index = 0; index < 2_000; index++)
                {
                    viewModel.UndoRedo.Execute(new AddRoiCommand(new LineMeasureRoi(), viewModel));
                }

                var stopwatch = Stopwatch.StartNew();
                viewModel.UndoRedo.Execute(new ClearAllRoisCommand(viewModel));
                stopwatch.Stop();

                _output.WriteLine($"清空 2000 ROI：{stopwatch.ElapsedMilliseconds} ms");

                Assert.Empty(viewModel.AllRois);
                Assert.True(stopwatch.ElapsedMilliseconds < 5_000, $"清空 2000 ROI 耗时 {stopwatch.ElapsedMilliseconds} ms。");
            });
        }

        /// <summary>
        /// MPR 快速切片：连续扫过多个平面，每个平面只构建一次，并报告构建耗时。
        /// </summary>
        [Fact]
        public void RapidMprSlicing_EachPlaneBuildsOnceAndReportsCost()
        {
            VolumeData volume = CreateVolume(depth: 96, width: 256, height: 256);

            var stopwatch = Stopwatch.StartNew();
            for (int index = 0; index < 96; index++)
            {
                VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, index);
            }

            for (int index = 0; index < 96; index++)
            {
                VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, index);
            }

            stopwatch.Stop();

            int buildCount = VolumeSliceService.GetSliceBuildCount(volume);
            _output.WriteLine($"MPR：192 次请求覆盖 96 个平面，构建 {buildCount} 次，{stopwatch.ElapsedMilliseconds} ms，" +
                              $"缓存 {VolumeSliceService.GetSliceCacheBytes(volume) / (1024d * 1024d):F1} MB，" +
                              $"条目 {VolumeSliceService.GetSliceEntryCount(volume)}");

            // 确定性：第二轮是纯命中，96 个平面各只构建一次。
            Assert.Equal(96, buildCount);
        }

        /// <summary>
        /// MPR 缓存有界性：滑过远超预算的平面后，缓存字节仍受上限约束。
        /// Chinese: 用冠状面（由 Service 重建并缓存）；轴位面直接复用 <see cref="VolumeData"/> 自带的切片缓存，
        /// 不经过本服务的 LRU，不能作为有界性判据。
        /// </summary>
        [Fact]
        public void MprSlicing_CacheStaysBounded_WhenSweepingPastBudget()
        {
            // 冠状面输出为 (Width, Depth) 像素，索引上限是 Height；width=1024、depth=400 → 每张约 1.6MB，
            // 扫过 256 张约 410MB，远超 64MB 预算，因此必然触发 LRU 淘汰。
            VolumeData volume = CreateVolume(depth: 400, width: 1024, height: 256);

            for (int index = 0; index < 256; index++)
            {
                VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, index);
            }

            long bytes = VolumeSliceService.GetSliceCacheBytes(volume);
            int entries = VolumeSliceService.GetSliceEntryCount(volume);
            _output.WriteLine($"MPR 有界性：滑过 256 个冠状面（1024×400），缓存 {bytes / (1024d * 1024d):F1} MB，条目 {entries}");

            // 64 MB 有界 LRU：不可能把 256 张约 1.6MB 的位图全留下。
            Assert.True(bytes <= 80L * 1024 * 1024, $"MPR 缓存超过 80MB 预算约束：{bytes} 字节。");
            Assert.True(entries < 256, $"MPR 缓存条目数 {entries} 未受 LRU 约束（应显著少于 256）。");
        }

        private static BitmapSource CreateLargeBitmap(int width, int height)
        {
            byte[] pixels = new byte[width * height];
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = (byte)(index * 31);
            }

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze();
            return bitmap;
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
