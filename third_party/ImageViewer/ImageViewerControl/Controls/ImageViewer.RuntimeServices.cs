using System;
using ImageViewer.Abstractions;

namespace ImageViewer.Controls
{
    /// <summary>
    /// Runtime dependencies shared by viewers created from one host.
    /// Chinese: runtime 只释放自己创建的 render service；由容器提供的 render service 归容器所有，
    /// 否则会出现 runtime 与容器重复释放同一实例（例如容器注册的 singleton render service 被提前释放）。
    /// English: The runtime owns and disposes the render service <em>it created</em>; a container-provided render service
    /// stays container-owned. Other dependencies are always container-owned.
    /// </summary>
    public sealed class ImageViewerRuntimeServices : IDisposable, IAsyncDisposable
    {
        private readonly bool _ownsRenderService;
        private bool _disposed;

        public ImageViewerRuntimeServices(
            IImageViewerDialogService dialogService,
            IImageViewerFileDialogService fileDialogService,
            IImageViewerLogger logger,
            IImageViewerViewportService viewportService,
            IImageViewerSessionService sessionService,
            IImageViewerRecentProjectService recentProjectService,
            IImageViewerProjectPackageService projectPackageService,
            IImageViewerRenderService renderService,
            ISelectedRoiDetectionService selectedRoiDetectionService,
            bool ownsRenderService = true)
        {
            DialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            FileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ViewportService = viewportService ?? throw new ArgumentNullException(nameof(viewportService));
            SessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            RecentProjectService = recentProjectService ?? throw new ArgumentNullException(nameof(recentProjectService));
            ProjectPackageService = projectPackageService ?? throw new ArgumentNullException(nameof(projectPackageService));
            RenderService = renderService ?? throw new ArgumentNullException(nameof(renderService));
            SelectedRoiDetectionService = selectedRoiDetectionService ?? throw new ArgumentNullException(nameof(selectedRoiDetectionService));
            _ownsRenderService = ownsRenderService;
        }

        public IImageViewerDialogService DialogService { get; }

        public IImageViewerFileDialogService FileDialogService { get; }

        public IImageViewerLogger Logger { get; }

        public IImageViewerViewportService ViewportService { get; }

        public IImageViewerSessionService SessionService { get; }

        public IImageViewerRecentProjectService RecentProjectService { get; }

        public IImageViewerProjectPackageService ProjectPackageService { get; }

        public IImageViewerRenderService RenderService { get; }

        public ISelectedRoiDetectionService SelectedRoiDetectionService { get; }

        internal bool IsDisposed => _disposed;

        internal bool OwnsRenderService => _ownsRenderService;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_ownsRenderService && RenderService is IDisposable disposableRenderService)
            {
                disposableRenderService.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_ownsRenderService)
            {
                return;
            }

            if (RenderService is IAsyncDisposable asyncDisposableRenderService)
            {
                await asyncDisposableRenderService.DisposeAsync().ConfigureAwait(false);
            }
            else if (RenderService is IDisposable disposableRenderService)
            {
                disposableRenderService.Dispose();
            }
        }
    }
}