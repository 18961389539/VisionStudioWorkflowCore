using System;
using System.Windows.Threading;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private ImageViewerBootstrapState CreateBootstrapState(ImageViewerDependencies dependencies)
        {
            ArgumentNullException.ThrowIfNull(dependencies);

            // 插件变更事件可能在任意线程触发（后台热加载/卸载）：迁移会改 ObservableCollection 并刷新 WPF
            // 视觉，必须回到 UI 线程执行。CheckAccess 快路径避免 UI 线程上的无谓调度。
            ImageViewerHostState hostState = new(
                dependencies.PluginRegistry,
                dependencies.RuntimeServices,
                dependencies.HostServices,
                migrationScheduler: action =>
                {
                    if (Dispatcher.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        _ = Dispatcher.BeginInvoke(action);
                    }
                });
            IImageViewerDeferredRefreshScheduler viewportOverlayRefreshScheduler = hostState.HostServices.RefreshSchedulerFactory.CreateDeferred(
                RefreshViewportOverlay,
                Dispatcher,
                DispatcherPriority.Render,
                TimeSpan.FromMilliseconds(16));
            IImageViewerForcedRefreshScheduler analysisRefreshScheduler = hostState.HostServices.RefreshSchedulerFactory.CreateForced(
                RefreshAnalysisPanels,
                Dispatcher,
                DispatcherPriority.Background,
                TimeSpan.FromMilliseconds(48));
            IImageViewerLatestTaskScheduler infoPanelStatisticsScheduler = hostState.HostServices.LatestTaskSchedulerFactory.Create();

            InitializeComponent();
            ApplyTheme();
            mainContextMenu.DataContext = this;

            return new ImageViewerBootstrapState(
                hostState,
                viewportOverlayRefreshScheduler,
                analysisRefreshScheduler,
                infoPanelStatisticsScheduler);
        }

        private void CompleteBootstrap()
        {
            _lifetime.Attach();
            UpdateToolbarLayout();
            UpdateRightOverlayPanelLayout();
            UpdateInteractionModeVisuals();
            UpdateStatusBar();
            UpdateRenderedImage();
            RefreshRoiDrawingMenuItems();
            ApplyMenuItemContentAlignment(mainContextMenu);
            UpdateContextMenuState();
        }

        private readonly record struct ImageViewerBootstrapState(
            ImageViewerHostState HostState,
            IImageViewerDeferredRefreshScheduler ViewportOverlayRefreshScheduler,
            IImageViewerForcedRefreshScheduler AnalysisRefreshScheduler,
            IImageViewerLatestTaskScheduler InfoPanelStatisticsScheduler);
    }
}
