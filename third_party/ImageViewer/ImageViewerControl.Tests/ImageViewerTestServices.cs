using System;
using System.IO;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Dialogs;
using ImageViewer.Plugins;
using ImageViewer.Services;

namespace ImageViewerControl.Tests
{
    internal static class ImageViewerTestServices
    {
        public static IImageViewerSessionStoragePolicy CreateSessionStoragePolicy(TimeSpan? autoSaveInterval = null)
        {
            return new LocalAppDataImageViewerSessionStoragePolicy(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                autoSaveInterval ?? TimeSpan.FromSeconds(30));
        }

        public static ImageViewerRuntimeServices CreateRuntimeServices(
            IImageViewerLogger? logger = null,
            IImageViewerSessionStoragePolicy? sessionStoragePolicy = null,
            IImageViewerFileDialogService? fileDialogService = null)
        {
            sessionStoragePolicy ??= CreateSessionStoragePolicy();

            var sessionService = new ImageViewerSessionService();
            return new ImageViewerRuntimeServices(
                new ImageViewerDialogService(),
                fileDialogService ?? new ImageViewerFileDialogService(),
                logger ?? new TraceImageViewerLogger(),
                new ImageViewerViewportService(),
                sessionService,
                new ImageViewerRecentProjectService(),
                new ImageViewerProjectPackageService(sessionService, sessionStoragePolicy),
                new ImageViewerRenderService(),
                SelectedRoiDetectionService.Default);
        }

        public static ImageViewerHostServices CreateHostServices(
            IImageViewerDispatcherTimerFactory dispatcherTimerFactory,
            IImageViewerRefreshSchedulerFactory refreshSchedulerFactory,
            IImageViewerLatestTaskSchedulerFactory latestTaskSchedulerFactory,
            IImageViewerPeriodicTaskSchedulerFactory periodicTaskSchedulerFactory,
            IImageViewerAnalysisDiagnostics analysisDiagnostics,
            IImageViewerSessionStoragePolicy? sessionStoragePolicy = null)
        {
            return new ImageViewerHostServices(
                dispatcherTimerFactory,
                refreshSchedulerFactory,
                latestTaskSchedulerFactory,
                periodicTaskSchedulerFactory,
                analysisDiagnostics,
                sessionStoragePolicy ?? CreateSessionStoragePolicy());
        }

        public static ImageViewerHost CreateHost(
            RoiPluginRegistry pluginRegistry,
            ImageViewerRuntimeServices runtimeServices,
            ImageViewerHostServices hostServices)
        {
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            ArgumentNullException.ThrowIfNull(runtimeServices);
            ArgumentNullException.ThrowIfNull(hostServices);

            return new ImageViewerHostBuilder()
                .UsePluginRegistry(pluginRegistry)
                .UseRuntimeServices(runtimeServices)
                .UseHostServices(hostServices)
                .Build();
        }

        /// <summary>
        /// Creates a host that owns the supplied runtime services; the host releases them with its last viewer.
        /// </summary>
        public static ImageViewerHost CreateOwningHost(
            RoiPluginRegistry pluginRegistry,
            ImageViewerRuntimeServices runtimeServices,
            ImageViewerHostServices hostServices)
        {
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            ArgumentNullException.ThrowIfNull(runtimeServices);
            ArgumentNullException.ThrowIfNull(hostServices);

            return new ImageViewerHostBuilder()
                .UsePluginRegistry(pluginRegistry)
                .UseRuntimeServices(runtimeServices, ownsRuntimeServices: true)
                .UseHostServices(hostServices)
                .Build();
        }
    }
}
