using System;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerControlCompositionRoot
    {
        public static ImageViewerControlComposition Create(ImageViewer owner, ImageViewerDependencies dependencies)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(dependencies);

            var coreAssembler = new ImageViewer.CoreControllerAssembler(owner, dependencies);
            var analysisAssembler = new ImageViewer.AnalysisCompositionAssembler(owner);
            var sessionAssembler = new ImageViewer.SessionCompositionAssembler(owner);
            var interactionAssembler = new ImageViewer.InteractionCompositionAssembler(owner);
            var commandAssembler = new ImageViewer.CommandControllerAssembler(owner);

            IImageViewStateController imageViewStateController = coreAssembler.CreateImageViewStateController();
            RoiSelectionStateController roiSelectionStateController = coreAssembler.CreateRoiSelectionStateController();
            ViewModelController viewModelController = coreAssembler.CreateViewModelController(roiSelectionStateController);
            ViewportController viewportController = coreAssembler.CreateViewportController();
            ImageViewerDialogWorkflowService dialogWorkflowService = coreAssembler.CreateDialogWorkflowService(roiSelectionStateController, viewportController);
            ImageViewerAnalysisComposition analysisComposition = analysisAssembler.CreateAnalysisComposition(dialogWorkflowService);
            ImageViewerSessionComposition sessionComposition = sessionAssembler.CreateSessionComposition(dialogWorkflowService, viewportController);
            ImageSourceController imageSourceController = coreAssembler.CreateImageSourceController(imageViewStateController);
            RoiEditController roiEditController = coreAssembler.CreateRoiEditController(dialogWorkflowService);
            CalibrationController calibrationController = coreAssembler.CreateCalibrationController(dialogWorkflowService);
            DroppedContentController droppedContentController = coreAssembler.CreateDroppedContentController(sessionComposition.SessionController);
            ImageViewerInteractionComposition interactionComposition = interactionAssembler.CreateInteractionComposition(viewportController, roiEditController, sessionComposition.SessionController, analysisComposition.AnalysisController);
            ImageViewerFeatureMenuCommandController featureMenuCommandController = commandAssembler.CreateFeatureMenuCommandController(dialogWorkflowService);
            ExternalImageSourceBindingController externalImageSourceBindingController = coreAssembler.CreateExternalImageSourceBindingController();

            return new ImageViewerControlComposition(
                dialogWorkflowService,
                imageViewStateController,
                imageSourceController,
                roiSelectionStateController,
                interactionComposition.InteractionController,
                viewModelController,
                roiEditController,
                commandAssembler.CreateRoiMenuCommandController(dialogWorkflowService, roiEditController, calibrationController),
                sessionComposition.FileMenuCommandController,
                featureMenuCommandController,
                commandAssembler.CreateViewCommandController(viewportController),
                commandAssembler.CreateModeCommandController(),
                viewportController,
                sessionComposition.SessionController,
                sessionComposition.RoiPersistenceController,
                calibrationController,
                droppedContentController,
                interactionComposition.ContextMenuController,
                analysisComposition.AnalysisController,
                analysisComposition.AnalysisCommandController,
                externalImageSourceBindingController);
        }
    }
}
