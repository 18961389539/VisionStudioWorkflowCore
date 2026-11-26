using System;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerControlComposition : IDisposable
    {
        private bool _isAttached;
        private bool _isDisposed;

        public ImageViewerControlComposition(
            ImageViewerDialogWorkflowService dialogWorkflowService,
            IImageViewStateController imageViewStateController,
            ImageSourceController imageSourceController,
            RoiSelectionStateController roiSelectionStateController,
            IImageViewerInteractionController interactionController,
            ViewModelController viewModelController,
            RoiEditController roiEditController,
            ImageViewerRoiMenuCommandController roiMenuCommandController,
            ImageViewerFileMenuCommandController fileMenuCommandController,
            ImageViewerFeatureMenuCommandController featureMenuCommandController,
            ImageViewerViewCommandController viewCommandController,
            ImageViewerModeCommandController modeCommandController,
            ViewportController viewportController,
            ImageViewerSessionController sessionController,
            ImageViewerRoiPersistenceController roiPersistenceController,
            CalibrationController calibrationController,
            DroppedContentController droppedContentController,
            IImageViewerContextMenuController contextMenuController,
            ImageViewerAnalysisCoordinator analysisController,
            ImageViewerAnalysisCommandController analysisCommandController,
            ExternalImageSourceBindingController externalImageSourceBindingController)
        {
            DialogWorkflowService = dialogWorkflowService ?? throw new ArgumentNullException(nameof(dialogWorkflowService));
            ImageViewStateController = imageViewStateController ?? throw new ArgumentNullException(nameof(imageViewStateController));
            ImageSourceController = imageSourceController ?? throw new ArgumentNullException(nameof(imageSourceController));
            RoiSelectionStateController = roiSelectionStateController ?? throw new ArgumentNullException(nameof(roiSelectionStateController));
            InteractionController = interactionController ?? throw new ArgumentNullException(nameof(interactionController));
            ViewModelController = viewModelController ?? throw new ArgumentNullException(nameof(viewModelController));
            RoiEditController = roiEditController ?? throw new ArgumentNullException(nameof(roiEditController));
            RoiMenuCommandController = roiMenuCommandController ?? throw new ArgumentNullException(nameof(roiMenuCommandController));
            FileMenuCommandController = fileMenuCommandController ?? throw new ArgumentNullException(nameof(fileMenuCommandController));
            FeatureMenuCommandController = featureMenuCommandController ?? throw new ArgumentNullException(nameof(featureMenuCommandController));
            ViewCommandController = viewCommandController ?? throw new ArgumentNullException(nameof(viewCommandController));
            ModeCommandController = modeCommandController ?? throw new ArgumentNullException(nameof(modeCommandController));
            ViewportController = viewportController ?? throw new ArgumentNullException(nameof(viewportController));
            SessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
            RoiPersistenceController = roiPersistenceController ?? throw new ArgumentNullException(nameof(roiPersistenceController));
            CalibrationController = calibrationController ?? throw new ArgumentNullException(nameof(calibrationController));
            DroppedContentController = droppedContentController ?? throw new ArgumentNullException(nameof(droppedContentController));
            ContextMenuController = contextMenuController ?? throw new ArgumentNullException(nameof(contextMenuController));
            AnalysisController = analysisController ?? throw new ArgumentNullException(nameof(analysisController));
            AnalysisCommandController = analysisCommandController ?? throw new ArgumentNullException(nameof(analysisCommandController));
            ExternalImageSourceBindingController = externalImageSourceBindingController ?? throw new ArgumentNullException(nameof(externalImageSourceBindingController));
        }

        public ImageViewerDialogWorkflowService DialogWorkflowService { get; }
        public IImageViewStateController ImageViewStateController { get; }
        public ImageSourceController ImageSourceController { get; }
        public RoiSelectionStateController RoiSelectionStateController { get; }
        public IImageViewerInteractionController InteractionController { get; }
        public ViewModelController ViewModelController { get; }
        public RoiEditController RoiEditController { get; }
        public ImageViewerRoiMenuCommandController RoiMenuCommandController { get; }
        public ImageViewerFileMenuCommandController FileMenuCommandController { get; }
        public ImageViewerFeatureMenuCommandController FeatureMenuCommandController { get; }
        public ImageViewerViewCommandController ViewCommandController { get; }
        public ImageViewerModeCommandController ModeCommandController { get; }
        public ViewportController ViewportController { get; }
        public ImageViewerSessionController SessionController { get; }
        public ImageViewerRoiPersistenceController RoiPersistenceController { get; }
        public CalibrationController CalibrationController { get; }
        public DroppedContentController DroppedContentController { get; }
        public IImageViewerContextMenuController ContextMenuController { get; }
        public ImageViewerAnalysisCoordinator AnalysisController { get; }
        public ImageViewerAnalysisCommandController AnalysisCommandController { get; }
        public ExternalImageSourceBindingController ExternalImageSourceBindingController { get; }

        public void Attach()
        {
            if (_isAttached || _isDisposed)
            {
                return;
            }

            ExternalImageSourceBindingController.Attach();
            ViewModelController.Attach();
            _isAttached = true;
        }

        public void Detach()
        {
            if (!_isAttached)
            {
                return;
            }

            ViewModelController.Dispose();
            ExternalImageSourceBindingController.Dispose();
            _isAttached = false;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            AnalysisController.CancelPendingWork();
            Detach();
            SessionController.Dispose();
        }
    }
}