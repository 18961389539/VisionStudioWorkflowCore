using System;
using ImageViewer.Abstractions;
using ImageViewer.Dialogs;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using ImageViewer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageViewer.Controls
{
    public interface IImageViewerFactory
    {
        ImageViewer CreateViewer();
    }

    internal sealed class ImageViewerFactory : IImageViewerFactory
    {
        private readonly ImageViewerHost _host;

        public ImageViewerFactory(ImageViewerHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public ImageViewer CreateViewer()
        {
            return _host.CreateViewer();
        }
    }

    /// <summary>
    /// 宿主：持有（或借用）一份 runtime services，并且是创建 viewer 的唯一入口。
    /// Chinese: 所有权规则只有一条——谁创建 runtime，谁负责释放。
    /// 宿主自己创建 runtime 时（<see cref="OwnsRuntimeServices"/> 为 true），宿主生命周期由它的 viewer 集合决定：
    /// 最后一个 viewer 释放租约时才释放 runtime，之后该宿主不可再创建 viewer。
    /// 宿主借用外部 runtime 时（容器或调用方创建，<see cref="OwnsRuntimeServices"/> 为 false），宿主不参与释放，由所有者负责。
    /// English: The single ownership rule is "whoever creates the runtime services releases them".
    /// An owning host ends its life with its last viewer; a borrowing host never disposes what it does not own.
    /// </summary>
    public sealed class ImageViewerHost : IDisposable, IAsyncDisposable
    {
        private readonly bool _ownsRuntimeServices;
        private int _activeViewers;
        private int _disposed;
        private int _runtimeReleased;

        public ImageViewerHost(
            ImageViewerDependencies dependencies,
            bool ownsRuntimeServices = false)
        {
            Dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
            _ownsRuntimeServices = ownsRuntimeServices;
        }

        public ImageViewerDependencies Dependencies { get; }

        /// <summary>
        /// True when this host is responsible for releasing <see cref="ImageViewerDependencies.RuntimeServices"/>.
        /// </summary>
        public bool OwnsRuntimeServices => _ownsRuntimeServices;

        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        internal int ActiveViewerCount => Volatile.Read(ref _activeViewers);

        /// <summary>
        /// 创建一个持有本宿主租约的 viewer。
        /// Chinese: 返回的 viewer 必须释放；释放时会归还租约，拥有 runtime 的宿主在最后一个 viewer 归还时释放 runtime。
        /// English: Creates a viewer that leases this host. Disposing the viewer returns the lease.
        /// </summary>
        public ImageViewer CreateViewer()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_ownsRuntimeServices && Volatile.Read(ref _runtimeReleased) == 1)
            {
                throw new ObjectDisposedException(
                    nameof(ImageViewerHost),
                    "This host already released its runtime services together with its last viewer. Create a new host instead of reusing this instance.");
            }

            return new ImageViewer(this);
        }

        /// <summary>
        /// Registers a viewer lease. Called by the viewer constructor so that both DI-created and
        /// hand-built viewers follow the same lifetime rules.
        /// </summary>
        internal void RegisterViewer()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Interlocked.Increment(ref _activeViewers);
        }

        /// <summary>
        /// Returns a viewer lease. The owning host releases its runtime services when the last lease is returned.
        /// </summary>
        internal void ReleaseViewer()
        {
            if (Interlocked.Decrement(ref _activeViewers) > 0)
            {
                return;
            }

            Interlocked.Exchange(ref _activeViewers, 0);
            if (_ownsRuntimeServices)
            {
                ReleaseOwnedRuntimeServices();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            // A host that still has viewers alive defers the release to the last viewer,
            // so disposing the host never pulls the runtime out from under a live viewer.
            if (_ownsRuntimeServices && Volatile.Read(ref _activeViewers) == 0)
            {
                ReleaseOwnedRuntimeServices();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (_ownsRuntimeServices && Volatile.Read(ref _activeViewers) == 0)
            {
                await ReleaseOwnedRuntimeServicesAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Releases the owned runtime services exactly once, whether the host or the last viewer gets there first.
        /// </summary>
        private void ReleaseOwnedRuntimeServices()
        {
            if (Interlocked.Exchange(ref _runtimeReleased, 1) == 1)
            {
                return;
            }

            Dependencies.RuntimeServices.Dispose();
        }

        private async ValueTask ReleaseOwnedRuntimeServicesAsync()
        {
            if (Interlocked.Exchange(ref _runtimeReleased, 1) == 1)
            {
                return;
            }

            await Dependencies.RuntimeServices.DisposeAsync().ConfigureAwait(false);
        }

        public static ImageViewerHost CreateDefault(RoiPluginRegistry? pluginRegistry = null)
        {
            return new ImageViewerHostBuilder()
                .UsePluginRegistry(ImageViewerPluginRegistryBootstrap.Resolve(pluginRegistry))
                .Build();
        }
    }

    public sealed class ImageViewerHostBuilder
    {
        private RoiPluginRegistry? _pluginRegistry;
        private ImageViewerRuntimeServices? _runtimeServices;
        private ImageViewerHostServices? _hostServices;
        private ImageViewerCompositionHooks? _compositionHooks;
        private bool _ownsRuntimeServices;

        public ImageViewerHostBuilder UsePluginRegistry(RoiPluginRegistry pluginRegistry)
        {
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            return this;
        }

        /// <summary>
        /// 使用调用方提供的 runtime services。
        /// Chinese: <paramref name="ownsRuntimeServices"/> 必须如实声明所有权——由调用方（或容器）创建的实例保持 false，
        /// 由宿主接管的实例才传 true。声明错方向就会泄漏或提前释放。
        /// English: Declare ownership honestly. Instances created by the caller (or a container) stay <c>false</c>;
        /// only pass <c>true</c> when the host takes over the release.
        /// </summary>
        public ImageViewerHostBuilder UseRuntimeServices(
            ImageViewerRuntimeServices runtimeServices,
            bool ownsRuntimeServices = false)
        {
            _runtimeServices = runtimeServices ?? throw new ArgumentNullException(nameof(runtimeServices));
            _ownsRuntimeServices = ownsRuntimeServices;
            return this;
        }

        public ImageViewerHostBuilder UseHostServices(ImageViewerHostServices hostServices)
        {
            _hostServices = hostServices ?? throw new ArgumentNullException(nameof(hostServices));
            return this;
        }

        /// <summary>
        /// 从容器接管宿主依赖。
        /// Chinese: 容器能提供 runtime services 时，所有权留在容器（宿主只借用），由作用域销毁负责释放；
        /// 容器拿不到时宿主自行创建并拥有，释放责任随之转移到宿主。
        /// English: When the container can supply runtime services they stay container-owned (the host only borrows them);
        /// otherwise the host creates and owns its own copy, and therefore also owns the release.
        /// </summary>
        public ImageViewerHostBuilder UseServiceProvider(IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);

            _pluginRegistry = ImageViewerPluginRegistryBootstrap.Resolve(serviceProvider);
            ImageViewerRuntimeServices? registeredRuntimeServices = serviceProvider.GetService<ImageViewerRuntimeServices>();
            _runtimeServices = registeredRuntimeServices ?? ImageViewerHostDefaults.CreateRuntimeServices(serviceProvider);
            _hostServices = serviceProvider.GetService<ImageViewerHostServices>()
                ?? ImageViewerHostDefaults.CreateHostServices(serviceProvider);
            _ownsRuntimeServices = registeredRuntimeServices is null;
            return this;
        }

        internal ImageViewerHostBuilder UseCompositionHooks(ImageViewerCompositionHooks compositionHooks)
        {
            _compositionHooks = compositionHooks ?? throw new ArgumentNullException(nameof(compositionHooks));
            return this;
        }

        public ImageViewerHost Build()
        {
            RoiPluginRegistry pluginRegistry = ImageViewerPluginRegistryBootstrap.Resolve(_pluginRegistry);
            ImageViewerHostServices hostServices = _hostServices ?? ImageViewerHostDefaults.CreateHostServices();
            ImageViewerRuntimeServices runtimeServices = _runtimeServices ?? ImageViewerHostDefaults.CreateRuntimeServices(hostServices.SessionStoragePolicy);

            // The host owns the runtime services only when it had to create them itself.
            // A runtime services instance supplied by a container or by the caller stays owned by that party.
            bool ownsRuntimeServices = _ownsRuntimeServices || _runtimeServices is null;
            ImageViewerDependencies dependencies = _compositionHooks is null
                ? new ImageViewerDependencies(pluginRegistry, runtimeServices, hostServices)
                : new ImageViewerDependencies(pluginRegistry, runtimeServices, hostServices, _compositionHooks);

            return new ImageViewerHost(dependencies, ownsRuntimeServices);
        }
    }

    internal sealed class ImageViewerHostState
    {
        private readonly ImageViewerPluginRuntimeState _pluginRuntimeState;

        public ImageViewerHostState(
            RoiPluginRegistry pluginRegistry,
            ImageViewerRuntimeServices runtimeServices,
            ImageViewerHostServices hostServices,
            Action<Action>? migrationScheduler = null)
        {
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            RuntimeServices = runtimeServices ?? throw new ArgumentNullException(nameof(runtimeServices));
            HostServices = hostServices ?? throw new ArgumentNullException(nameof(hostServices));
            _pluginRuntimeState = new ImageViewerPluginRuntimeState(pluginRegistry, RuntimeServices.SelectedRoiDetectionService, migrationScheduler);
        }

        public RoiPluginRegistry PluginRegistry => _pluginRuntimeState.PluginRegistry;

        /// <summary>
        /// 换用另一个插件注册表并迁移 ROI 状态。
        /// Chinese: 返回值是因新注册表不再认识其类型而被移除的 ROI；同一实例返回空列表（就地修改由 Changed 事件处理）。
        /// </summary>
        public IReadOnlyList<RoiBase> ApplyPluginRegistry(RoiPluginRegistry pluginRegistry) =>
            _pluginRuntimeState.ApplyPluginRegistry(pluginRegistry);

        /// <summary>
        /// 状态按注册表迁移完成时触发：被移除的 ROI 与它们的 unresolved 载荷。
        /// </summary>
        public event Action<ImageViewerRoiMigration>? RoiStateMigrated
        {
            add => _pluginRuntimeState.RoiStateMigrated += value;
            remove => _pluginRuntimeState.RoiStateMigrated -= value;
        }

        /// <summary>
        /// 断开对注册表的订阅（注册表可能被多个 viewer 共享）。
        /// </summary>
        public void DetachPluginRegistry() => _pluginRuntimeState.Detach();

        public ImageViewerRuntimeServices RuntimeServices { get; }

        public ImageViewerHostServices HostServices { get; }

        public ImageViewerViewModel ViewModel => _pluginRuntimeState.ViewModel;

        public RoiInteractionService RoiInteraction => _pluginRuntimeState.RoiInteraction;

        public Rendering.RoiRenderService RoiRenderer => _pluginRuntimeState.RoiRenderer;
    }

    internal static class ImageViewerHostDefaults
    {
        /// <summary>
        /// Builds the runtime services from container registrations.
        /// Chinese: render service 来自容器注册，因此归容器所有——runtime 只借用它，避免 runtime 与容器重复释放同一实例。
        /// English: The render service comes from the container, so the container owns it and the runtime only borrows it.
        /// </summary>
        public static ImageViewerRuntimeServices CreateRuntimeServices(IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);

            return new ImageViewerRuntimeServices(
                serviceProvider.GetRequiredService<IImageViewerDialogService>(),
                serviceProvider.GetRequiredService<IImageViewerFileDialogService>(),
                serviceProvider.GetRequiredService<IImageViewerLogger>(),
                serviceProvider.GetRequiredService<IImageViewerViewportService>(),
                serviceProvider.GetRequiredService<IImageViewerSessionService>(),
                serviceProvider.GetRequiredService<IImageViewerRecentProjectService>(),
                serviceProvider.GetRequiredService<IImageViewerProjectPackageService>(),
                serviceProvider.GetRequiredService<IImageViewerRenderService>(),
                serviceProvider.GetRequiredService<ISelectedRoiDetectionService>(),
                ownsRenderService: false);
        }

        /// <summary>
        /// Builds a self-contained runtime services instance. The runtime owns the render service it creates.
        /// </summary>
        public static ImageViewerRuntimeServices CreateRuntimeServices(IImageViewerSessionStoragePolicy sessionStoragePolicy)
        {
            ArgumentNullException.ThrowIfNull(sessionStoragePolicy);

            var sessionService = new ImageViewerSessionService();
            return new ImageViewerRuntimeServices(
                new ImageViewerDialogService(),
                new ImageViewerFileDialogService(),
                new TraceImageViewerLogger(),
                new ImageViewerViewportService(),
                sessionService,
                new ImageViewerRecentProjectService(),
                new ImageViewerProjectPackageService(sessionService, sessionStoragePolicy),
                new ImageViewerRenderService(),
                SelectedRoiDetectionService.Default);
        }

        public static ImageViewerHostServices CreateHostServices(IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);

            return new ImageViewerHostServices(
                serviceProvider.GetRequiredService<IImageViewerDispatcherTimerFactory>(),
                serviceProvider.GetRequiredService<IImageViewerRefreshSchedulerFactory>(),
                serviceProvider.GetRequiredService<IImageViewerLatestTaskSchedulerFactory>(),
                serviceProvider.GetRequiredService<IImageViewerPeriodicTaskSchedulerFactory>(),
                serviceProvider.GetRequiredService<IImageViewerAnalysisDiagnostics>(),
                serviceProvider.GetService<IImageViewerSessionStoragePolicy>() ?? new LocalAppDataImageViewerSessionStoragePolicy());
        }

        public static ImageViewerHostServices CreateHostServices()
        {
            var timerFactory = new WpfImageViewerDispatcherTimerFactory();
            return new ImageViewerHostServices(
                timerFactory,
                new DispatcherImageViewerRefreshSchedulerFactory(timerFactory),
                new LatestImageViewerTaskSchedulerFactory(),
                new DispatcherImageViewerPeriodicTaskSchedulerFactory(timerFactory),
                new LoggerImageViewerAnalysisDiagnostics(new DiagnosticsImageViewerTelemetry()));
        }
    }
}