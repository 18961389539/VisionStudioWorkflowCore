using System;
using ImageViewer.Abstractions;
using ImageViewer.Dialogs;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImageViewer.Controls
{
    public static class ImageViewerServiceCollectionExtensions
    {
        public static IServiceCollection AddImageViewerRuntimeServices(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            AddPluginAndSessionServices(services);
            AddRenderServices(services);
            AddRuntimeHostServices(services);

            return services;
        }

        private static void AddPluginAndSessionServices(IServiceCollection services)
        {
            services.TryAdd(ServiceDescriptor.Singleton<RoiPluginRegistry>(static _ => ImageViewerPluginRegistryBootstrap.CreateDefault()));
            services.TryAdd(ServiceDescriptor.Singleton<IImageViewerSessionStoragePolicy>(static _ => new LocalAppDataImageViewerSessionStoragePolicy()));
            services.TryAddSingleton<IImageViewerDialogService, ImageViewerDialogService>();
            services.TryAddSingleton<IImageViewerFileDialogService, ImageViewerFileDialogService>();
            services.TryAddSingleton<IImageViewerLogger, TraceImageViewerLogger>();
            services.TryAddSingleton<IImageViewerViewportService, ImageViewerViewportService>();
            services.TryAddSingleton<IImageViewerSessionService, ImageViewerSessionService>();
            services.TryAddSingleton<IImageViewerRecentProjectService, ImageViewerRecentProjectService>();
            services.TryAdd(ServiceDescriptor.Singleton<IImageViewerProjectPackageService>(static serviceProvider =>
                new ImageViewerProjectPackageService(
                    serviceProvider.GetRequiredService<IImageViewerSessionService>(),
                    serviceProvider.GetRequiredService<IImageViewerSessionStoragePolicy>())));
        }

        private static void AddRenderServices(IServiceCollection services)
        {
            // Scoped: one render service (and therefore one tile cache) per scope, shared by every
            // render-service interface resolved inside that scope. Registering these as transient
            // would silently hand out a separate tile cache per interface and per consumer.
            services.TryAddScoped<ImageViewerRenderService>();
            services.TryAdd(ServiceDescriptor.Scoped<IImageViewerRenderService>(static serviceProvider =>
                serviceProvider.GetRequiredService<ImageViewerRenderService>()));
            services.TryAdd(ServiceDescriptor.Scoped<IImageViewerDisplayRenderService>(static serviceProvider =>
                serviceProvider.GetRequiredService<ImageViewerRenderService>()));
            services.TryAdd(ServiceDescriptor.Scoped<IImageViewerFrameRenderService>(static serviceProvider =>
                serviceProvider.GetRequiredService<ImageViewerRenderService>()));
            services.TryAdd(ServiceDescriptor.Scoped<IImageViewerAnalysisRenderService>(static serviceProvider =>
                serviceProvider.GetRequiredService<ImageViewerRenderService>()));
        }

        private static void AddRuntimeHostServices(IServiceCollection services)
        {
            services.TryAddSingleton<ISelectedRoiDetectionService>(static _ => SelectedRoiDetectionService.Default);

            // Scoped: the runtime services live exactly as long as the scope (window/dialog) that resolved
            // them, and the container releases them when that scope is disposed.
            services.TryAdd(ServiceDescriptor.Scoped<ImageViewerRuntimeServices>(static serviceProvider =>
                ImageViewerHostDefaults.CreateRuntimeServices(serviceProvider)));
        }

        public static IServiceCollection AddImageViewerHostServices(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            AddHostSchedulingServices(services);
            AddHostAnalysisServices(services);

            return services;
        }

        private static void AddHostSchedulingServices(IServiceCollection services)
        {
            services.TryAddSingleton<IImageViewerDispatcherTimerFactory, WpfImageViewerDispatcherTimerFactory>();
            services.TryAdd(ServiceDescriptor.Singleton<IImageViewerRefreshSchedulerFactory>(static serviceProvider =>
                new DispatcherImageViewerRefreshSchedulerFactory(serviceProvider.GetRequiredService<IImageViewerDispatcherTimerFactory>())));
            services.TryAddSingleton<IImageViewerLatestTaskSchedulerFactory, LatestImageViewerTaskSchedulerFactory>();
            services.TryAdd(ServiceDescriptor.Singleton<IImageViewerPeriodicTaskSchedulerFactory>(static serviceProvider =>
                new DispatcherImageViewerPeriodicTaskSchedulerFactory(
                    serviceProvider.GetRequiredService<IImageViewerDispatcherTimerFactory>(),
                    exception => serviceProvider.GetRequiredService<IImageViewerLogger>().LogError("Periodic image viewer task failed.", exception))));
        }

        private static void AddHostAnalysisServices(IServiceCollection services)
        {
            services.TryAddSingleton<IImageViewerTelemetry, DiagnosticsImageViewerTelemetry>();
            services.TryAdd(ServiceDescriptor.Singleton<IImageViewerAnalysisDiagnostics>(static serviceProvider =>
                new LoggerImageViewerAnalysisDiagnostics(serviceProvider.GetRequiredService<IImageViewerTelemetry>())));
            services.TryAdd(ServiceDescriptor.Singleton<ImageViewerHostServices>(static serviceProvider =>
                ImageViewerHostDefaults.CreateHostServices(serviceProvider)));
        }

        public static IServiceCollection AddImageViewerMicrosoftLogging(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.Replace(ServiceDescriptor.Singleton<IImageViewerLogger, MicrosoftExtensionsImageViewerLogger>());
            return services;
        }

        /// <summary>
        /// 注册 viewer 控件、宿主、runtime 与 render 服务。
        /// Chinese: 生命周期边界只有三层——
        /// ① Singleton：无状态或应用级共享（插件注册表、对话框/会话/最近项目服务、调度器工厂、遥测）；
        /// ② Scoped：与窗口/对话框同寿命（<see cref="ImageViewerHost"/>、<see cref="ImageViewerRuntimeServices"/>、
        ///    <see cref="ImageViewerRenderService"/> 及其接口别名、<see cref="ImageViewer"/>、<see cref="IImageViewerFactory"/>），
        ///    同 scope 内共享、由容器在 scope 销毁时释放；
        /// ③ 无 Transient 注册：控件与 runtime 都是可释放的资源，注册成 transient 会让容器长期持有引用并把释放推迟到容器销毁。
        /// 请在窗口级 scope 内解析这些类型，并建议开启 <c>ValidateScopes</c> 让误用立即失败。
        /// English: Three lifetime tiers only — application-lifetime singletons, window-scoped host/runtime/render/viewer
        /// (shared inside a scope and released by the container with it), and no transient registrations for disposable resources.
        /// Resolve these types from a window-level scope and enable <c>ValidateScopes</c> to catch misuse.
        /// </summary>
        public static IServiceCollection AddImageViewerHost(this IServiceCollection services, Action<ImageViewerHostBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddImageViewerRuntimeServices();
            services.AddImageViewerHostServices();
            services.TryAdd(ServiceDescriptor.Scoped<ImageViewerHost>(serviceProvider =>
            {
                var builder = new ImageViewerHostBuilder().UseServiceProvider(serviceProvider);
                configure?.Invoke(builder);
                return builder.Build();
            }));
            services.TryAdd(ServiceDescriptor.Scoped<IImageViewerFactory>(static serviceProvider =>
                new ImageViewerFactory(serviceProvider.GetRequiredService<ImageViewerHost>())));
            services.TryAdd(ServiceDescriptor.Scoped<ImageViewer>(static serviceProvider =>
                serviceProvider.GetRequiredService<ImageViewerHost>().CreateViewer()));

            return services;
        }
    }
}