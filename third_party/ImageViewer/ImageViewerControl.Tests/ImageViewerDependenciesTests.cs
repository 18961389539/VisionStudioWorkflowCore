using System;
using System.IO;
using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Dialogs;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Trait("Category", "Smoke")]
    public class ImageViewerDependenciesTests
    {
        [Fact]
        public void Constructor_WithRuntimeServices_ExposesLegacyForwarders()
        {
            RoiPluginRegistry pluginRegistry = RoiPluginRegistry.CreateBuiltIn();
            ImageViewerRuntimeServices runtimeServices = ImageViewerTestServices.CreateRuntimeServices();

            var dependencies = new ImageViewerDependencies(pluginRegistry, runtimeServices);

            Assert.Same(pluginRegistry, dependencies.PluginRegistry);
            Assert.Same(runtimeServices, dependencies.RuntimeServices);
            Assert.Same(runtimeServices.DialogService, dependencies.DialogService);
            Assert.Same(runtimeServices.FileDialogService, dependencies.FileDialogService);
            Assert.Same(runtimeServices.Logger, dependencies.Logger);
            Assert.Same(runtimeServices.ViewportService, dependencies.ViewportService);
            Assert.Same(runtimeServices.SessionService, dependencies.SessionService);
            Assert.Same(runtimeServices.RecentProjectService, dependencies.RecentProjectService);
            Assert.Same(runtimeServices.ProjectPackageService, dependencies.ProjectPackageService);
            Assert.Same(runtimeServices.RenderService, dependencies.RenderService);
            Assert.Same(runtimeServices.SelectedRoiDetectionService, dependencies.SelectedRoiDetectionService);
            Assert.NotNull(dependencies.HostServices);
        }

        [Fact]
        public void HostBuilder_Build_UsesConfiguredPluginRegistryAndRuntimeServices()
        {
            RoiPluginRegistry pluginRegistry = RoiPluginRegistry.CreateBuiltIn();
            ImageViewerRuntimeServices runtimeServices = ImageViewerTestServices.CreateRuntimeServices();
            ImageViewerHostServices hostServices = ImageViewerTestServices.CreateHostServices(
                new RecordingDispatcherTimerFactory(),
                new RecordingRefreshSchedulerFactory(),
                new RecordingLatestTaskSchedulerFactory(),
                new RecordingPeriodicTaskSchedulerFactory(),
                new NoOpAnalysisDiagnostics());

            ImageViewerHost host = ImageViewerTestServices.CreateHost(pluginRegistry, runtimeServices, hostServices);

            Assert.Same(pluginRegistry, host.Dependencies.PluginRegistry);
            Assert.Same(runtimeServices, host.Dependencies.RuntimeServices);
            Assert.Same(hostServices, host.Dependencies.HostServices);
        }

        [Fact]
        public void CreateDialogWorkflowAdapter_UsesCompositionHookWithRuntimeServices()
        {
            ImageViewerRuntimeServices runtimeServices = ImageViewerTestServices.CreateRuntimeServices();
            var adapter = new FakeDialogWorkflowAdapter();
            ImageViewerRuntimeServices? capturedRuntimeServices = null;
            var hooks = new ImageViewerCompositionHooks(
                (ownerWindowProvider, services) =>
                {
                    capturedRuntimeServices = services;
                    return adapter;
                },
                (dependencies, workflowAdapter) => new ImageViewerDialogWorkflowService(dependencies, workflowAdapter),
                (owner, dependencies) => throw new NotSupportedException("Control composition should not be created in this test."));
            var dependencies = new ImageViewerDependencies(RoiPluginRegistry.CreateBuiltIn(), runtimeServices, hooks);

            IImageViewerDialogWorkflowAdapter result = dependencies.CreateDialogWorkflowAdapter(() => null);

            Assert.Same(adapter, result);
            Assert.Same(runtimeServices, capturedRuntimeServices);
        }

        [Fact]
        public void AddImageViewerHost_UsesExistingRegistrations()
        {
            var services = new ServiceCollection();
            RoiPluginRegistry pluginRegistry = RoiPluginRegistry.CreateBuiltIn();
            var sessionStoragePolicy = ImageViewerTestServices.CreateSessionStoragePolicy(TimeSpan.FromSeconds(12));
            var timerFactory = new RecordingDispatcherTimerFactory();
            var refreshSchedulerFactory = new RecordingRefreshSchedulerFactory();
            var latestTaskSchedulerFactory = new RecordingLatestTaskSchedulerFactory();
            var periodicTaskSchedulerFactory = new RecordingPeriodicTaskSchedulerFactory();
            var diagnostics = new NoOpAnalysisDiagnostics();

            services.AddSingleton(pluginRegistry);
            services.AddSingleton<IImageViewerDispatcherTimerFactory>(timerFactory);
            services.AddSingleton<IImageViewerRefreshSchedulerFactory>(refreshSchedulerFactory);
            services.AddSingleton<IImageViewerLatestTaskSchedulerFactory>(latestTaskSchedulerFactory);
            services.AddSingleton<IImageViewerPeriodicTaskSchedulerFactory>(periodicTaskSchedulerFactory);
            services.AddSingleton<IImageViewerAnalysisDiagnostics>(diagnostics);
            services.AddSingleton<IImageViewerSessionStoragePolicy>(sessionStoragePolicy);
            services.AddImageViewerHost();

            using ServiceProvider serviceProvider = services.BuildServiceProvider();
            ImageViewerHost host = serviceProvider.GetRequiredService<ImageViewerHost>();

            Assert.Same(pluginRegistry, host.Dependencies.PluginRegistry);
            Assert.Same(timerFactory, host.Dependencies.HostServices.DispatcherTimerFactory);
            Assert.Same(refreshSchedulerFactory, host.Dependencies.HostServices.RefreshSchedulerFactory);
            Assert.Same(latestTaskSchedulerFactory, host.Dependencies.HostServices.LatestTaskSchedulerFactory);
            Assert.Same(periodicTaskSchedulerFactory, host.Dependencies.HostServices.PeriodicTaskSchedulerFactory);
            Assert.Same(diagnostics, host.Dependencies.HostServices.AnalysisDiagnostics);
            Assert.Same(sessionStoragePolicy, host.Dependencies.HostServices.SessionStoragePolicy);
        }

        [Fact]
        public void AddImageViewerHost_SharesRuntimeServicesInsideScopeAndIsolatesAcrossScopes()
        {
            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddImageViewerHost()
                .BuildServiceProvider();
            using IServiceScope firstScope = serviceProvider.CreateScope();
            using IServiceScope secondScope = serviceProvider.CreateScope();

            ImageViewerRuntimeServices first = firstScope.ServiceProvider.GetRequiredService<ImageViewerRuntimeServices>();
            ImageViewerRuntimeServices firstAgain = firstScope.ServiceProvider.GetRequiredService<ImageViewerRuntimeServices>();
            ImageViewerRuntimeServices second = secondScope.ServiceProvider.GetRequiredService<ImageViewerRuntimeServices>();

            Assert.Same(first, firstAgain);
            Assert.NotSame(first, second);
            Assert.NotSame(first.RenderService, second.RenderService);
        }

        [Fact]
        public void AddImageViewerHost_ResolvesEveryRenderServiceFacadeToTheSameScopeInstance()
        {
            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddImageViewerHost()
                .BuildServiceProvider();
            using IServiceScope scope = serviceProvider.CreateScope();

            ImageViewerRenderService concrete = scope.ServiceProvider.GetRequiredService<ImageViewerRenderService>();
            ImageViewerRuntimeServices runtimeServices = scope.ServiceProvider.GetRequiredService<ImageViewerRuntimeServices>();

            Assert.Same(concrete, scope.ServiceProvider.GetRequiredService<IImageViewerRenderService>());
            Assert.Same(concrete, scope.ServiceProvider.GetRequiredService<IImageViewerDisplayRenderService>());
            Assert.Same(concrete, scope.ServiceProvider.GetRequiredService<IImageViewerFrameRenderService>());
            Assert.Same(concrete, scope.ServiceProvider.GetRequiredService<IImageViewerAnalysisRenderService>());
            Assert.Same(concrete, runtimeServices.RenderService);
        }

        [Fact]
        public void AddImageViewerHost_BorrowsContainerOwnedRuntimeServicesAndRenderService()
        {
            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddImageViewerHost()
                .BuildServiceProvider();
            using IServiceScope scope = serviceProvider.CreateScope();
            ImageViewerRuntimeServices runtimeServices = scope.ServiceProvider.GetRequiredService<ImageViewerRuntimeServices>();

            ImageViewerHost host = scope.ServiceProvider.GetRequiredService<ImageViewerHost>();

            Assert.False(host.OwnsRuntimeServices);
            Assert.False(runtimeServices.OwnsRenderService);
        }

        [Fact]
        public void AddImageViewerHost_ScopeDisposalReleasesContainerOwnedRuntimeServices()
        {
            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddImageViewerHost()
                .BuildServiceProvider();
            ImageViewerRuntimeServices runtimeServices;
            ImageViewerRenderService renderService;

            using (IServiceScope scope = serviceProvider.CreateScope())
            {
                runtimeServices = scope.ServiceProvider.GetRequiredService<ImageViewerRuntimeServices>();
                renderService = scope.ServiceProvider.GetRequiredService<ImageViewerRenderService>();

                Assert.False(runtimeServices.IsDisposed);
                Assert.False(renderService.IsDisposed);
            }

            Assert.True(runtimeServices.IsDisposed);
            Assert.True(renderService.IsDisposed);
        }

        [Fact]
        public void AddImageViewerHost_ForwardsAnalysisErrorsToHostTelemetry()
        {
            var telemetry = new RecordingTelemetry();
            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddSingleton<IImageViewerTelemetry>(telemetry)
                .AddImageViewerHost()
                .BuildServiceProvider();

            Exception exception = new InvalidOperationException("analysis failed");
            serviceProvider.GetRequiredService<IImageViewerAnalysisDiagnostics>().LogNonCriticalError(
                new RecordingLogger(),
                "analysis",
                exception);

            Assert.Equal("analysis", telemetry.Operation);
            Assert.Same(exception, telemetry.Exception);
        }

        [Fact]
        public void Host_Dispose_PreventsFurtherViewerCreation()
        {
            ImageViewerHost host = ImageViewerTestServices.CreateHost(
                RoiPluginRegistry.CreateBuiltIn(),
                ImageViewerTestServices.CreateRuntimeServices(),
                ImageViewerTestServices.CreateHostServices(
                    new RecordingDispatcherTimerFactory(),
                    new RecordingRefreshSchedulerFactory(),
                    new RecordingLatestTaskSchedulerFactory(),
                    new RecordingPeriodicTaskSchedulerFactory(),
                    new NoOpAnalysisDiagnostics()));

            host.Dispose();

            Assert.Throws<ObjectDisposedException>(() => host.CreateViewer());
        }

        [Fact]
        public void HostBuilder_ExternalRuntimeServicesRemainExternalAfterServiceProviderConfiguration()
        {
            using ServiceProvider serviceProvider = new ServiceCollection()
                .AddImageViewerHost()
                .BuildServiceProvider();
            ImageViewerRuntimeServices externalRuntimeServices = ImageViewerTestServices.CreateRuntimeServices();

            ImageViewerHost host = new ImageViewerHostBuilder()
                .UseServiceProvider(serviceProvider)
                .UseRuntimeServices(externalRuntimeServices)
                .Build();

            host.Dispose();

            Assert.False(externalRuntimeServices.IsDisposed);
            externalRuntimeServices.Dispose();
        }

        [Fact]
        public void Host_Dispose_ReleasesOwnedRuntimeServicesIdempotently()
        {
            ImageViewerHost host = ImageViewerHost.CreateDefault();
            ImageViewerRuntimeServices runtimeServices = host.Dependencies.RuntimeServices;

            host.Dispose();
            host.Dispose();

            Assert.True(runtimeServices.IsDisposed);
        }

        private sealed class FakeDialogWorkflowAdapter : IImageViewerDialogWorkflowAdapter
        {
            public string? ShowOpenImageDialog() => throw new NotSupportedException();

            public string? ShowTextInput(string message, string defaultValue) => throw new NotSupportedException();

            public string? ShowSaveRoiDialog() => throw new NotSupportedException();

            public string? ShowOpenRoiDialog() => throw new NotSupportedException();

            public string? ShowSaveSessionDialog() => throw new NotSupportedException();

            public string? ShowOpenSessionDialog() => throw new NotSupportedException();

            public string? ShowSaveProjectPackageDialog() => throw new NotSupportedException();

            public string? ShowSaveRecipeDialog() => throw new NotSupportedException();

            public string? ShowOpenRecipeDialog() => throw new NotSupportedException();

            public string? ShowSaveSnapshotDialog() => throw new NotSupportedException();

            public string? ShowSaveAnalysisCsvDialog() => throw new NotSupportedException();

            public CalibrationDialogResult? ShowCalibrationDialog(string currentUnit, CameraCalibration? currentCalibration) => throw new NotSupportedException();

            public CaliperMeasureRoi? ShowLineMeasureCaliperSettingsDialog(CaliperMeasureRoi roi, Action<CaliperMeasureRoi>? previewAction = null) => throw new NotSupportedException();

            public LineCaliperMeasureRoi? ShowLineCaliperSettingsDialog(LineCaliperMeasureRoi roi, Action<LineCaliperMeasureRoi>? previewAction = null) => throw new NotSupportedException();

            public CircularCaliperMeasureRoi? ShowCircularCaliperSettingsDialog(CircularCaliperMeasureRoi roi, Action<CircularCaliperMeasureRoi>? previewAction = null) => throw new NotSupportedException();

            public void ShowPropertyEditor(string title, FrameworkElement editor) => throw new NotSupportedException();

            public void ShowReadOnlyText(string title, string text) => throw new NotSupportedException();

            public void ShowWarning(string title, string message) => throw new NotSupportedException();
        }

        private sealed class RecordingDispatcherTimerFactory : IImageViewerDispatcherTimerFactory
        {
            public System.Windows.Threading.DispatcherTimer Create(System.Windows.Threading.Dispatcher dispatcher, System.Windows.Threading.DispatcherPriority priority, TimeSpan interval)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class NoOpAnalysisDiagnostics : IImageViewerAnalysisDiagnostics
        {
            public void LogNonCriticalError(IImageViewerLogger logger, string message, Exception exception)
            {
            }
        }

        private sealed class RecordingTelemetry : IImageViewerTelemetry
        {
            public string? Operation { get; private set; }

            public Exception? Exception { get; private set; }

            public void RecordNonCriticalError(string operation, Exception exception)
            {
                Operation = operation;
                Exception = exception;
            }
        }

        private sealed class RecordingLatestTaskSchedulerFactory : IImageViewerLatestTaskSchedulerFactory
        {
            public IImageViewerLatestTaskScheduler Create()
            {
                return new NoOpLatestTaskScheduler();
            }
        }

        private sealed class RecordingPeriodicTaskSchedulerFactory : IImageViewerPeriodicTaskSchedulerFactory
        {
            public IImageViewerPeriodicTaskScheduler Create(Func<System.Threading.Tasks.Task> callback, System.Windows.Threading.Dispatcher dispatcher, System.Windows.Threading.DispatcherPriority priority, TimeSpan interval)
            {
                return new NoOpPeriodicTaskScheduler();
            }
        }

        private sealed class RecordingRefreshSchedulerFactory : IImageViewerRefreshSchedulerFactory
        {
            public IImageViewerDeferredRefreshScheduler CreateDeferred(Action refreshAction, System.Windows.Threading.Dispatcher dispatcher, System.Windows.Threading.DispatcherPriority priority, TimeSpan interval)
            {
                return new NoOpDeferredRefreshScheduler();
            }

            public IImageViewerForcedRefreshScheduler CreateForced(Action<bool> refreshAction, System.Windows.Threading.Dispatcher dispatcher, System.Windows.Threading.DispatcherPriority priority, TimeSpan interval)
            {
                return new NoOpForcedRefreshScheduler();
            }
        }

        private sealed class NoOpDeferredRefreshScheduler : IImageViewerDeferredRefreshScheduler
        {
            public void StopScheduling()
            {
            }

            public void Request(bool immediate = false)
            {
            }

            public void BeginBatch()
            {
            }

            public void EndBatch(bool immediate = false)
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class NoOpForcedRefreshScheduler : IImageViewerForcedRefreshScheduler
        {
            public void StopScheduling()
            {
            }

            public void Request(bool force = false, bool immediate = false)
            {
            }

            public void BeginBatch()
            {
            }

            public void EndBatch(bool immediate = false)
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class NoOpLatestTaskScheduler : IImageViewerLatestTaskScheduler
        {
            public System.Threading.Tasks.Task ScheduleAsync(Func<System.Threading.CancellationToken, System.Threading.Tasks.Task> work, int delayMilliseconds = 0)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public void Cancel()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class NoOpPeriodicTaskScheduler : IImageViewerPeriodicTaskScheduler
        {
            public void Start()
            {
            }

            public void StopScheduling()
            {
            }

            public Task DrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public void Dispose()
            {
            }
        }
    }
}