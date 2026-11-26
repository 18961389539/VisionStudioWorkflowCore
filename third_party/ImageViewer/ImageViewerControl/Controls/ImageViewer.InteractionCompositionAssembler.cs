using System;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        internal sealed class InteractionCompositionAssembler
        {
            private readonly ImageViewer _owner;

            public InteractionCompositionAssembler(ImageViewer owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public ImageViewerInteractionComposition CreateInteractionComposition(
                ViewportController viewportController,
                RoiEditController roiEditController,
                ImageViewerSessionController sessionController,
                ImageViewerAnalysisCoordinator analysisController)
            {
                ArgumentNullException.ThrowIfNull(viewportController);
                ArgumentNullException.ThrowIfNull(roiEditController);
                ArgumentNullException.ThrowIfNull(sessionController);
                ArgumentNullException.ThrowIfNull(analysisController);

                return new ImageViewerInteractionComposition(
                    CreateInteractionController(viewportController, roiEditController),
                    CreateContextMenuController(sessionController, analysisController));
            }

            private InteractionController CreateInteractionController(
                ViewportController viewportController,
                RoiEditController roiEditController)
            {
                return new InteractionController(
                    new InteractionControllerHost(_owner, viewportController),
                    new PointerInteractionFlow(new PointerInteractionHost(_owner, viewportController)),
                    new SelectionInteractionFlow(new SelectionInteractionHost(_owner)),
                    new EditInteractionFlow(new EditInteractionHost(_owner, roiEditController)));
            }

            private ContextMenuController CreateContextMenuController(
                ImageViewerSessionController sessionController,
                ImageViewerAnalysisCoordinator analysisController)
            {
                return new ContextMenuController(
                    new ImageViewerContextMenuHost(
                        _owner,
                        _owner.drawRoiMenuItem,
                        sessionController,
                        analysisController,
                        _owner.RefreshRoiDrawingMenuItems,
                        () => _owner.GetAnalysisBitmapSource() != null));
            }
        }
    }
}