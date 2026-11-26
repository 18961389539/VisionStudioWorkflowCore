using ImageViewer.Abstractions;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private ImageViewerViewModel ViewModel => _hostState.ViewModel;

        private Services.RoiInteractionService RoiInteraction => _hostState.RoiInteraction;

        private Rendering.RoiRenderService RoiRenderer => _hostState.RoiRenderer;

        private ImageViewerDialogWorkflowService _dialogWorkflowService => _controlComposition.DialogWorkflowService;

        private IImageViewStateController _imageViewStateController => _controlComposition.ImageViewStateController;

        private ImageSourceController _imageSourceController => _controlComposition.ImageSourceController;

        private RoiSelectionStateController _roiSelectionStateController => _controlComposition.RoiSelectionStateController;

        private IImageViewerInteractionController _interactionController => _controlComposition.InteractionController;

        private ViewModelController _viewModelController => _controlComposition.ViewModelController;

        private RoiEditController _roiEditController => _controlComposition.RoiEditController;

        private ImageViewerRoiMenuCommandController _roiMenuCommandController => _controlComposition.RoiMenuCommandController;

        private ImageViewerFileMenuCommandController _fileMenuCommandController => _controlComposition.FileMenuCommandController;

        private ImageViewerFeatureMenuCommandController _featureMenuCommandController => _controlComposition.FeatureMenuCommandController;

        private ImageViewerViewCommandController _viewCommandController => _controlComposition.ViewCommandController;

        private ImageViewerModeCommandController _modeCommandController => _controlComposition.ModeCommandController;

        private ViewportController _viewportController => _controlComposition.ViewportController;

        private ImageViewerSessionController _sessionController => _controlComposition.SessionController;

        private ImageViewerRoiPersistenceController _roiPersistenceController => _controlComposition.RoiPersistenceController;

        private CalibrationController _calibrationController => _controlComposition.CalibrationController;

        private DroppedContentController _droppedContentController => _controlComposition.DroppedContentController;

        private IImageViewerContextMenuController _contextMenuController => _controlComposition.ContextMenuController;

        private ImageViewerAnalysisCoordinator _analysisController => _controlComposition.AnalysisController;

        private ImageViewerAnalysisCommandController _analysisCommandController => _controlComposition.AnalysisCommandController;

        private ExternalImageSourceBindingController _externalImageSourceBindingController => _controlComposition.ExternalImageSourceBindingController;

        internal ImageViewerRuntimeServices RuntimeServices => _hostState.RuntimeServices;

        internal ImageViewerHostServices HostServices => _hostState.HostServices;

        public ImageViewerViewModel ViewerState => _hostState.ViewModel;

        public IImageViewerDialogService DialogService => RuntimeServices.DialogService;

        public IImageViewerFileDialogService FileDialogService => RuntimeServices.FileDialogService;

        public IImageViewerLogger Logger => RuntimeServices.Logger;

        public IImageViewerViewportService ViewportService => RuntimeServices.ViewportService;

        public IImageViewerSessionService SessionService => RuntimeServices.SessionService;

        public IImageViewerRecentProjectService RecentProjectService => RuntimeServices.RecentProjectService;

        public IImageViewerProjectPackageService ProjectPackageService => RuntimeServices.ProjectPackageService;

        public IImageViewerRenderService RenderService => RuntimeServices.RenderService;
    }
}