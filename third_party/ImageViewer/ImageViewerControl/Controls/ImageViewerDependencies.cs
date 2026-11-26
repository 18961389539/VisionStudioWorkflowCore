using System;
using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Dialogs;
using ImageViewer.Plugins;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public sealed class ImageViewerDependencies
    {
        public ImageViewerDependencies(
            RoiPluginRegistry pluginRegistry,
            ImageViewerRuntimeServices runtimeServices)
            : this(pluginRegistry, runtimeServices, ImageViewerHostDefaults.CreateHostServices(), compositionHooks: null)
        {
        }

        public ImageViewerDependencies(
            RoiPluginRegistry pluginRegistry,
            ImageViewerRuntimeServices runtimeServices,
            ImageViewerHostServices hostServices)
            : this(pluginRegistry, runtimeServices, hostServices, compositionHooks: null)
        {
        }

        public ImageViewerDependencies(
            RoiPluginRegistry pluginRegistry,
            IImageViewerDialogService dialogService,
            IImageViewerFileDialogService fileDialogService,
            IImageViewerLogger logger,
            IImageViewerViewportService viewportService,
            IImageViewerSessionService sessionService,
            IImageViewerRecentProjectService recentProjectService,
            IImageViewerProjectPackageService projectPackageService,
            IImageViewerRenderService renderService,
            ISelectedRoiDetectionService selectedRoiDetectionService)
            : this(
                pluginRegistry,
                new ImageViewerRuntimeServices(
                    dialogService,
                    fileDialogService,
                    logger,
                    viewportService,
                    sessionService,
                    recentProjectService,
                    projectPackageService,
                    renderService,
                    selectedRoiDetectionService),
                compositionHooks: null)
        {
        }

        internal ImageViewerDependencies(
            RoiPluginRegistry pluginRegistry,
            ImageViewerRuntimeServices runtimeServices,
            ImageViewerCompositionHooks? compositionHooks)
            : this(pluginRegistry, runtimeServices, ImageViewerHostDefaults.CreateHostServices(), compositionHooks)
        {
        }

        internal ImageViewerDependencies(
            RoiPluginRegistry pluginRegistry,
            ImageViewerRuntimeServices runtimeServices,
            ImageViewerHostServices hostServices,
            ImageViewerCompositionHooks? compositionHooks)
        {
            PluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            RuntimeServices = runtimeServices ?? throw new ArgumentNullException(nameof(runtimeServices));
            HostServices = hostServices ?? throw new ArgumentNullException(nameof(hostServices));
            CompositionHooks = compositionHooks ?? ImageViewerCompositionHooks.CreateDefault();
        }

        public RoiPluginRegistry PluginRegistry { get; }
        public ImageViewerRuntimeServices RuntimeServices { get; }
        public ImageViewerHostServices HostServices { get; }
        public IImageViewerDialogService DialogService => RuntimeServices.DialogService;
        public IImageViewerFileDialogService FileDialogService => RuntimeServices.FileDialogService;
        public IImageViewerLogger Logger => RuntimeServices.Logger;
        public IImageViewerViewportService ViewportService => RuntimeServices.ViewportService;
        public IImageViewerSessionService SessionService => RuntimeServices.SessionService;
        public IImageViewerRecentProjectService RecentProjectService => RuntimeServices.RecentProjectService;
        public IImageViewerProjectPackageService ProjectPackageService => RuntimeServices.ProjectPackageService;
        public IImageViewerRenderService RenderService => RuntimeServices.RenderService;
        public ISelectedRoiDetectionService SelectedRoiDetectionService => RuntimeServices.SelectedRoiDetectionService;

        internal ImageViewerCompositionHooks CompositionHooks { get; }

        internal IImageViewerDialogWorkflowAdapter CreateDialogWorkflowAdapter(Func<Window?> ownerWindowProvider)
        {
            ArgumentNullException.ThrowIfNull(ownerWindowProvider);
            return CompositionHooks.DialogWorkflowAdapterFactory(ownerWindowProvider, RuntimeServices);
        }

        internal ImageViewerDialogWorkflowService CreateDialogWorkflowService(ImageViewerDialogWorkflowDependencies dependencies, IImageViewerDialogWorkflowAdapter adapter)
        {
            ArgumentNullException.ThrowIfNull(dependencies);
            ArgumentNullException.ThrowIfNull(adapter);
            return CompositionHooks.DialogWorkflowServiceFactory(dependencies, adapter);
        }

        internal ImageViewerControlComposition CreateControlComposition(ImageViewer owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            return CompositionHooks.ControlCompositionFactory(owner, this);
        }
    }
}
