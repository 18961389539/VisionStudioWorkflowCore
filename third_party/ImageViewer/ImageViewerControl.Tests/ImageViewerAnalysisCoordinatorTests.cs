using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Core.Display;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Unit")]
    [Trait("Category", "Wpf")]
    public class ImageViewerAnalysisCoordinatorTests
    {
        [Fact]
        public void HandleRenderingOptionChanged_UsesShaderEffectWhenAvailable()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var renderService = new FakeRenderService
                {
                    DisplayEffect = new BlurEffect()
                };
                var host = CreateHost(renderService, bitmap);
                host.PreferShaderPseudoColor = true;
                host.PseudoColorPalette = PseudoColorPalette.Hot;
                var uiFacade = new FakeAnalysisUiFacade();
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                coordinator.HandleRenderingOptionChanged();

                Assert.NotNull(uiFacade.LastRenderedImagePlan);
                Assert.Same(renderService.DisplayEffect, uiFacade.LastRenderedImagePlan!.Effect);
                Assert.True(host.AnalysisState.IsShaderPseudoColorActive);
                Assert.True(renderService.LastBuildRenderFrameSettings.IsPassThrough);
            });
        }

        [Fact]
        public void BuildRenderStatus_ReturnsPureRenderStatusModel()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var renderService = new FakeRenderService();
                var host = CreateHost(renderService, bitmap);
                host.EnableGpuRendering = true;
                host.EnableImagePyramid = true;
                host.EnableTiledRendering = true;
                host.PrefetchAdjacentTiles = true;
                host.TileCacheMaximumMegabytes = 256;
                host.TilePrefetchRadius = 3;
                host.PseudoColorPalette = PseudoColorPalette.Jet;
                host.EnableAsyncAnalysis = true;
                host.PauseRealtimeHistogram = true;
                host.PauseRealtimeProfile = true;
                host.AnalysisState.LastRenderFrame = new ImageViewerRenderFrame(new DrawingImage(), 1, 2, 3, 4, 0.5, true);
                host.AnalysisState.LastPyramidBuildDuration = TimeSpan.FromMilliseconds(12);
                host.AnalysisState.LastHistogramDuration = TimeSpan.FromMilliseconds(21);
                host.AnalysisState.LastProfileDuration = TimeSpan.FromMilliseconds(34);
                host.AnalysisState.IsShaderPseudoColorActive = true;
                var coordinator = CreateCoordinator(host, new FakeAnalysisUiFacade(), new FakeProfileTargetResolver());

                ImageViewerRenderStatus status = coordinator.BuildRenderStatus();

                Assert.Equal(2, status.SourcePixelWidth);
                Assert.Equal(2, status.SourcePixelHeight);
                Assert.Equal(1, status.PyramidLevelCount);
                Assert.True(status.EnableGpuRendering);
                Assert.True(status.IsShaderPseudoColorActive);
                Assert.Equal(PseudoColorPalette.Jet, status.PseudoColorPalette);
                Assert.Equal(256, status.TileCacheMaximumMegabytes);
                Assert.Equal(3, status.TilePrefetchRadius);
                Assert.True(status.EnableAsyncAnalysis);
                Assert.True(status.PauseRealtimeHistogram);
                Assert.True(status.PauseRealtimeProfile);
                Assert.Equal(12, status.PyramidBuildDuration.TotalMilliseconds, 1);
                Assert.Equal(21, status.LastHistogramDuration.TotalMilliseconds, 1);
                Assert.Equal(34, status.LastProfileDuration.TotalMilliseconds, 1);
            });
        }

        [Fact]
        public void BuildRenderStatusSummary_FormatsStatusWithoutUiFacadeFormatting()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var host = CreateHost(new FakeRenderService(), bitmap);
                host.PseudoColorPalette = PseudoColorPalette.Hot;
                host.EnableImagePyramid = true;
                host.AnalysisState.LastRenderFrame = new ImageViewerRenderFrame(new DrawingImage(), 1, 2, 3, 4, 0.5, false);
                var coordinator = CreateCoordinator(host, new FakeAnalysisUiFacade(), new FakeProfileTargetResolver());

                string summary = coordinator.BuildRenderStatusSummary();

                Assert.Contains("Pseudo color: Hot", summary);
                Assert.Contains("Pyramid: On", summary);
            });
        }

        [Fact]
        public void UpdateRenderedImageForDisplaySettings_RunsCpuLutOffUiThread()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var gate = new ManualResetEventSlim(false);
                var renderService = new FakeRenderService
                {
                    RenderFrameEntered = entered,
                    RenderFrameGate = gate
                };
                var host = CreateHost(renderService, CreateBitmap());
                host.PseudoColorPalette = PseudoColorPalette.Hot;
                var uiFacade = new FakeAnalysisUiFacade
                {
                    RenderedImageApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                coordinator.UpdateRenderedImageForDisplaySettings();

                // 后台帧构建先进入并阻塞；若 LUT 仍在 UI 线程执行，这次调用本身就会卡住。
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(uiFacade.LastRenderedImagePlan);
                await Task.Yield();
                Assert.Null(uiFacade.LastRenderedImagePlan);

                gate.Set();
                await uiFacade.RenderedImageApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(uiFacade.LastRenderedImagePlan);
                Assert.Equal(PseudoColorPalette.Hot, renderService.LastBuildRenderFrameSettings.Palette);
            });
        }

        [Fact]
        public void HandlePseudoColorPaletteChanged_RefreshesOnlyTheRenderedImage()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var renderService = new FakeRenderService();
                var host = CreateHost(renderService, bitmap);
                host.ShowHistogram = true;
                host.ShowProfile = true;
                host.EnableAsyncAnalysis = true;
                host.PseudoColorPalette = PseudoColorPalette.Hot;
                var uiFacade = new FakeAnalysisUiFacade();
                uiFacade.LastHistogramOutput = new ImageViewerHistogramOutput([1, 2, 3], 3);
                uiFacade.LastProfileOutput = new ImageViewerProfileOutput([1, 2, 3]);
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver { TargetLine = CreateProfileLine() }, TimeSpan.Zero);

                coordinator.HandlePseudoColorPaletteChanged();

                // 图像必须重绘。
                Assert.Equal(1, renderService.BuildRenderFrameCallCount);

                // 直方图/剖面的计算基于原始强度图、绘制用固定颜色，都与调色板无关 → 不得重算，已呈现的输出保持不变。
                Assert.Equal(0, renderService.CreateHistogramAsyncCallCount);
                Assert.Equal(0, renderService.CreateProfileAsyncCallCount);
                Assert.NotNull(uiFacade.LastHistogramOutput);
                Assert.NotNull(uiFacade.LastProfileOutput);

                // 对照组：显式刷新确实会算——证明上面两个计数是有效观测点，而不是碰巧为 0。
                coordinator.RefreshAnalysisDisplays(force: true).GetAwaiter().GetResult();
                Assert.Equal(1, renderService.CreateHistogramAsyncCallCount);
                Assert.Equal(1, renderService.CreateProfileAsyncCallCount);
            });
        }

        [Fact]
        public void UpdatePseudoColorMenuState_AppliesPureMenuState()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var host = CreateHost(new FakeRenderService(), bitmap);
                host.PseudoColorPalette = PseudoColorPalette.Viridis;
                var uiFacade = new FakeAnalysisUiFacade();
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                coordinator.UpdatePseudoColorMenuState();

                Assert.NotNull(uiFacade.LastPseudoColorMenuState);
                Assert.Equal(PseudoColorPalette.Viridis, uiFacade.LastPseudoColorMenuState!.SelectedPalette);
            });
        }

        [Fact]
        public void HandleProfileVisibilityChanged_WhenVisible_RefreshesProfilePanel()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var host = CreateHost(new FakeRenderService(), bitmap);
                host.ShowProfile = true;
                var uiFacade = new FakeAnalysisUiFacade();
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver { TargetLine = CreateProfileLine() });

                coordinator.HandleProfileVisibilityChanged(true);

                Assert.True(uiFacade.IsProfilePanelVisible);
                Assert.NotNull(uiFacade.LastProfileOutput);
            });
        }

        [Fact]
        public void HandleHistogramVisibilityChanged_WhenHidden_CancelsOutstandingWorkAndClearsCanvas()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var host = CreateHost(new FakeRenderService(), bitmap);
                ImageViewerAnalysisTicket histogramTicket = host.AnalysisState.BeginHistogramTicket();
                var uiFacade = new FakeAnalysisUiFacade();
                uiFacade.LastHistogramOutput = new ImageViewerHistogramOutput([1, 2, 3], 3);
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                coordinator.HandleHistogramVisibilityChanged(false);

                Assert.True(histogramTicket.IsCancellationRequested);
                Assert.Null(host.AnalysisState.HistogramTicket);
                Assert.Null(uiFacade.LastHistogramOutput);
                Assert.False(uiFacade.IsHistogramPanelVisible);
            });
        }

        [Fact]
        public void UpdateHistogram_WhenPausedAndNotForced_DoesNotRefresh()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var renderService = new FakeRenderService();
                var host = CreateHost(renderService, bitmap);
                host.ShowHistogram = true;
                host.EnableAsyncAnalysis = false;
                host.PauseRealtimeHistogram = true;
                var uiFacade = new FakeAnalysisUiFacade();
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                coordinator.UpdateHistogram().GetAwaiter().GetResult();

                Assert.Null(uiFacade.LastHistogramOutput);
            });
        }

        [Fact]
        public void UpdateProfile_WhenNoTargetLine_ClearsProfileOutput()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var host = CreateHost(new FakeRenderService(), bitmap);
                host.ShowProfile = true;
                var uiFacade = new FakeAnalysisUiFacade
                {
                    LastProfileOutput = new ImageViewerProfileOutput([1, 2, 3])
                };
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                coordinator.UpdateProfile().GetAwaiter().GetResult();

                Assert.Null(uiFacade.LastProfileOutput);
            });
        }

        [Fact]
        public void PrepareAnalysisResourcesAsync_BuildsPyramidAndResetsExistingWork()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var renderService = new FakeRenderService
                {
                    AnalysisBitmap = bitmap,
                    PyramidLevelsResult =
                    [
                        new ImagePyramidLevel(bitmap, 1.0),
                        new ImagePyramidLevel(bitmap, 0.5)
                    ]
                };
                var host = CreateHost(renderService, bitmap);
                ImageViewerAnalysisTicket oldHistogramTicket = host.AnalysisState.BeginHistogramTicket();
                ImageViewerAnalysisTicket oldProfileTicket = host.AnalysisState.BeginProfileTicket();
                ImageViewerAnalysisTicket oldPyramidTicket = host.AnalysisState.BeginPyramidTicket();
                var coordinator = CreateCoordinator(host, new FakeAnalysisUiFacade(), new FakeProfileTargetResolver());

                coordinator.PrepareAnalysisResourcesAsync(bitmap).GetAwaiter().GetResult();

                Assert.True(oldHistogramTicket.IsCancellationRequested);
                Assert.True(oldProfileTicket.IsCancellationRequested);
                Assert.True(oldPyramidTicket.IsCancellationRequested);
                Assert.Null(host.AnalysisState.HistogramTicket);
                Assert.Null(host.AnalysisState.ProfileTicket);
                Assert.NotSame(oldPyramidTicket, host.AnalysisState.PyramidTicket);
                Assert.Equal(1, renderService.ClearTileCacheCallCount);
                Assert.Same(bitmap, host.AnalysisState.AnalysisBitmapSource);
                Assert.Equal(2, host.AnalysisState.PyramidLevels.Count);
                Assert.Equal(1, renderService.BuildPyramidAsyncCallCount);
                Assert.Equal(2, renderService.BuildRenderFrameCallCount);
            });
        }

        [Fact]
        public void PrepareAnalysisResourcesAsync_ClearsExistingAnalysisOutputsBeforePyramidCompletes()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                BitmapSource bitmap = CreateBitmap();
                var pyramidGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var renderService = new FakeRenderService
                {
                    AnalysisBitmap = bitmap,
                    PyramidGate = pyramidGate
                };
                var host = CreateHost(renderService, bitmap);
                var uiFacade = new FakeAnalysisUiFacade
                {
                    LastHistogramOutput = new ImageViewerHistogramOutput([1, 2, 3], 3),
                    LastProfileOutput = new ImageViewerProfileOutput([4, 5, 6])
                };
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                Task prepareTask = coordinator.PrepareAnalysisResourcesAsync(bitmap);

                Assert.Null(uiFacade.LastHistogramOutput);
                Assert.Null(uiFacade.LastProfileOutput);

                pyramidGate.SetResult(true);
                await prepareTask;
            });
        }

        [Fact]
        public void PrepareAnalysisResourcesAsync_ClearsAnalysisOutputsWhenPyramidBuildFails()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                BitmapSource bitmap = CreateBitmap();
                var renderService = new FakeRenderService
                {
                    AnalysisBitmap = bitmap,
                    PyramidException = new InvalidOperationException("pyramid failed")
                };
                var host = CreateHost(renderService, bitmap);
                var uiFacade = new FakeAnalysisUiFacade
                {
                    LastHistogramOutput = new ImageViewerHistogramOutput([1, 2, 3], 3),
                    LastProfileOutput = new ImageViewerProfileOutput([4, 5, 6])
                };
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareAnalysisResourcesAsync(bitmap));

                Assert.Null(uiFacade.LastHistogramOutput);
                Assert.Null(uiFacade.LastProfileOutput);
                Assert.Null(host.AnalysisState.PyramidTicket);
            });
        }

        [Fact]
        public void ClearAnalysisCaches_CancelsOutstandingWorkAndClearsCanvases()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource bitmap = CreateBitmap();
                var host = CreateHost(new FakeRenderService(), bitmap);
                ImageViewerAnalysisTicket histogramTicket = host.AnalysisState.BeginHistogramTicket();
                ImageViewerAnalysisTicket profileTicket = host.AnalysisState.BeginProfileTicket();
                var uiFacade = new FakeAnalysisUiFacade();
                uiFacade.LastHistogramOutput = new ImageViewerHistogramOutput([1, 2, 3], 3);
                uiFacade.LastProfileOutput = new ImageViewerProfileOutput([0, 127, 255]);
                var coordinator = CreateCoordinator(host, uiFacade, new FakeProfileTargetResolver());

                coordinator.ClearAnalysisCaches();

                Assert.True(histogramTicket.IsCancellationRequested);
                Assert.True(profileTicket.IsCancellationRequested);
                Assert.Null(host.AnalysisState.HistogramTicket);
                Assert.Null(host.AnalysisState.ProfileTicket);
                Assert.Null(uiFacade.LastHistogramOutput);
                Assert.Null(uiFacade.LastProfileOutput);
            });
        }

        [Fact]
        public void UpdateProfile_WhenAsyncRequestIsSupersededBySyncRefresh_DoesNotCommitStaleResult()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                BitmapSource bitmap = CreateBitmap();
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var renderService = new FakeRenderService
                {
                    ProfileGate = gate,
                    ProfileResult = [9, 9, 9]
                };
                var host = CreateHost(renderService, bitmap);
                host.ShowProfile = true;
                host.EnableAsyncAnalysis = true;
                var uiFacade = new FakeAnalysisUiFacade();
                var resolver = new FakeProfileTargetResolver { TargetLine = CreateProfileLine() };
                var coordinator = CreateCoordinator(host, uiFacade, resolver, TimeSpan.Zero);

                Task inFlight = coordinator.UpdateProfile();
                Assert.Equal(1, renderService.CreateProfileAsyncCallCount);

                // Turning the async mode off forces a synchronous refresh while the first request is still in flight.
                host.EnableAsyncAnalysis = false;
                await coordinator.UpdateProfile(force: true);

                Assert.NotNull(uiFacade.LastProfileOutput);

                uiFacade.LastProfileOutput = null;
                gate.SetResult(true);
                await inFlight;

                Assert.Null(uiFacade.LastProfileOutput);
            });
        }

        [Fact]
        public void UpdateProfile_WhenTargetLineDisappears_DiscardsInFlightResult()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                BitmapSource bitmap = CreateBitmap();
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var renderService = new FakeRenderService { ProfileGate = gate, IgnoreProfileCancellation = true };
                var host = CreateHost(renderService, bitmap);
                host.ShowProfile = true;
                host.EnableAsyncAnalysis = true;
                var uiFacade = new FakeAnalysisUiFacade();
                var resolver = new FakeProfileTargetResolver { TargetLine = CreateProfileLine() };
                var coordinator = CreateCoordinator(host, uiFacade, resolver, TimeSpan.Zero);

                Task inFlight = coordinator.UpdateProfile();

                resolver.TargetLine = null;
                await coordinator.UpdateProfile();

                Assert.Null(uiFacade.LastProfileOutput);

                gate.SetResult(true);
                await inFlight;

                Assert.Null(uiFacade.LastProfileOutput);
            });
        }

        private static ImageViewerAnalysisCoordinator CreateCoordinator(
            FakeAnalysisHost host,
            FakeAnalysisUiFacade uiFacade,
            FakeProfileTargetResolver profileTargetResolver,
            TimeSpan? asyncAnalysisDebounce = null)
        {
            return new ImageViewerAnalysisCoordinator(host, uiFacade, profileTargetResolver, new FakeErrorSink(), asyncAnalysisDebounce: asyncAnalysisDebounce);
        }

        private static FakeAnalysisHost CreateHost(FakeRenderService renderService, BitmapSource bitmap)
        {
            var host = new FakeAnalysisHost(renderService)
            {
                ImageSource = bitmap
            };

            host.AnalysisState.ResetForSource(bitmap);
            return host;
        }

        private static BitmapSource CreateBitmap()
        {
            return BitmapSource.Create(
                pixelWidth: 2,
                pixelHeight: 2,
                dpiX: 96,
                dpiY: 96,
                pixelFormat: PixelFormats.Gray8,
                palette: null,
                pixels: new byte[] { 0, 64, 128, 255 },
                stride: 2);
        }

        private static LineMeasureRoi CreateProfileLine()
        {
            return new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(1, 0)
            };
        }

        private sealed class FakeAnalysisHost : IImageViewerAnalysisHost
        {
            public FakeAnalysisHost(IImageViewerRenderService renderService)
            {
                RenderService = renderService;
            }

            public ImageViewerAnalysisState AnalysisState { get; } = new();

            public IImageViewerRenderService RenderService { get; }

            public ImageSource? ImageSource { get; set; }

            public bool EnableGpuRendering { get; set; }

            public bool PreferShaderPseudoColor { get; set; }

            public bool AllowCpuPseudoColorFallback { get; set; } = true;

            public PseudoColorPalette PseudoColorPalette { get; set; }

            public DisplayAdjustment DisplayAdjustment { get; set; } = DisplayAdjustment.Identity;

            public ImageViewerDisplaySettings DisplaySettings => new(PseudoColorPalette, DisplayAdjustment);

            public bool EnableImagePyramid { get; set; } = true;

            public bool AutoSelectPyramidLevel { get; set; } = true;

            public bool EnableTiledRendering { get; set; }

            public bool PrefetchAdjacentTiles { get; set; }

            public int TileCacheMaximumMegabytes { get; set; } = 128;

            public int TilePrefetchRadius { get; set; } = 1;

            public double Scale { get; set; } = 1.0;

            public Point Translation { get; set; }

            public Size ViewportSize { get; set; } = new(320, 240);

            public int HistogramBinCount { get; set; } = ImageViewerAnalysisCoordinator.HistogramBinCount;

            public bool ShowHistogram { get; set; }

            public bool ShowProfile { get; set; }

            public bool EnableAsyncAnalysis { get; set; }

            public bool PauseRealtimeHistogram { get; set; }

            public bool PauseRealtimeProfile { get; set; }
        }

        private sealed class FakeAnalysisUiFacade : IImageViewerAnalysisUiFacade
        {
            public bool IsHistogramPanelVisible { get; private set; }

            public bool IsProfilePanelVisible { get; private set; }

            public ImageViewerHistogramOutput? LastHistogramOutput { get; set; }

            public ImageViewerProfileOutput? LastProfileOutput { get; set; }

            public ImageViewerPseudoColorMenuState? LastPseudoColorMenuState { get; private set; }

            public ImageViewerRenderedImagePlan? LastRenderedImagePlan { get; private set; }

            public TaskCompletionSource? RenderedImageApplied { get; set; }

            public void SetHistogramPanelVisibility(bool isVisible)
            {
                IsHistogramPanelVisible = isVisible;
            }

            public void SetProfilePanelVisibility(bool isVisible)
            {
                IsProfilePanelVisible = isVisible;
            }

            public void ApplyRenderedImagePlan(ImageViewerRenderedImagePlan plan)
            {
                LastRenderedImagePlan = plan;
                RenderedImageApplied?.TrySetResult();
            }

            public void ApplyPseudoColorMenuState(ImageViewerPseudoColorMenuState state)
            {
                LastPseudoColorMenuState = state;
            }

            public void PresentHistogram(ImageViewerHistogramOutput? output)
            {
                LastHistogramOutput = output;
            }

            public void PresentProfile(ImageViewerProfileOutput? output)
            {
                LastProfileOutput = output;
            }

            public void PresentHistogramError(string message)
            {
                LastHistogramOutput = null;
            }

            public void PresentProfileError(string message)
            {
                LastProfileOutput = null;
            }
        }

        private sealed class FakeProfileTargetResolver : IImageViewerProfileTargetResolver
        {
            public LineMeasureRoi? TargetLine { get; set; }

            public LineMeasureRoi? GetProfileTargetLine() => TargetLine;
        }

        private sealed class FakeErrorSink : IImageViewerAnalysisErrorSink
        {
            public void LogNonCriticalError(string message, Exception ex)
            {
            }
        }

        private sealed class FakeRenderService : IImageViewerRenderService
        {
            public BitmapSource? AnalysisBitmap { get; set; }

            public Effect? DisplayEffect { get; set; }

            public IReadOnlyList<ImagePyramidLevel> PyramidLevelsResult { get; set; } = [];

            public TaskCompletionSource<bool>? PyramidGate { get; set; }

            public Exception? PyramidException { get; set; }

            public int BuildRenderFrameCallCount { get; private set; }

            public int BuildPyramidAsyncCallCount { get; private set; }

            public int ClearTileCacheCallCount { get; private set; }

            public int CreateProfileAsyncCallCount { get; private set; }

            public int CreateHistogramAsyncCallCount { get; private set; }

            public TaskCompletionSource<bool>? ProfileGate { get; set; }

            public TaskCompletionSource? RenderFrameEntered { get; set; }

            public ManualResetEventSlim? RenderFrameGate { get; set; }

            public byte[]? ProfileResult { get; set; } = [0, 127, 255];

            /// <summary>
            /// Simulates a service that ignores the cancellation token, so only the ticket identity guard can keep
            /// a superseded result out of the UI.
            /// </summary>
            public bool IgnoreProfileCancellation { get; set; }

            public ImageViewerDisplaySettings LastBuildRenderFrameSettings { get; private set; } = ImageViewerDisplaySettings.PassThrough;

            public ImageSource? BuildDisplaySource(ImageSource? source, ImageViewerDisplaySettings settings)
            {
                return source;
            }

            public void ApplyGpuCaching(Canvas imageContainer, bool enableGpuRendering)
            {
            }

            public void ClearTileCache()
            {
                ClearTileCacheCallCount++;
            }

            public BitmapSource? GetAnalysisBitmap(ImageSource? source)
            {
                return AnalysisBitmap ?? source as BitmapSource;
            }

            public Effect? CreateDisplayEffect(ImageViewerDisplaySettings settings)
            {
                // 与真实实现一致：透传设置不建效果，否则"没有调色板也没有调节"也会走 GPU 分支。
                return settings.IsPassThrough ? null : DisplayEffect;
            }

            public async Task<IReadOnlyList<ImagePyramidLevel>> BuildPyramidAsync(BitmapSource? source, CancellationToken cancellationToken)
            {
                BuildPyramidAsyncCallCount++;
                if (PyramidGate != null)
                {
                    await PyramidGate.Task;
                }

                if (PyramidException != null)
                {
                    throw PyramidException;
                }

                return PyramidLevelsResult;
            }

            public ImageViewerRenderFrame BuildRenderFrame(BitmapSource? source, IReadOnlyList<ImagePyramidLevel>? pyramid, Size viewport, double scale, Point translation, ImageViewerDisplaySettings settings, bool enableTiledRendering, bool autoSelectPyramidLevel, bool prefetchAdjacentTiles, int tileCacheMaximumMegabytes, int tilePrefetchRadius)
            {
                BuildRenderFrameCallCount++;
                LastBuildRenderFrameSettings = settings;
                RenderFrameEntered?.TrySetResult();
                RenderFrameGate?.Wait(TimeSpan.FromSeconds(2));
                return new ImageViewerRenderFrame(null, 10, 20, 30, 40, 1.0, enableTiledRendering);
            }

            public Task<int[]?> CreateHistogramAsync(BitmapSource? source, int binCount, CancellationToken cancellationToken)
            {
                CreateHistogramAsyncCallCount++;
                int[] histogram = new int[binCount];
                Array.Fill(histogram, 1);
                return Task.FromResult<int[]?>(histogram);
            }

            public Task<byte[]?> CreateProfileAsync(ImageViewerAnalysisRequest request, CancellationToken cancellationToken)
            {
                CreateProfileAsyncCallCount++;
                return CreateProfileCoreAsync(cancellationToken);
            }

            private async Task<byte[]?> CreateProfileCoreAsync(CancellationToken cancellationToken)
            {
                if (ProfileGate != null)
                {
                    await ProfileGate.Task.ConfigureAwait(false);
                }

                if (!IgnoreProfileCancellation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return ProfileResult;
            }
        }
    }
}
