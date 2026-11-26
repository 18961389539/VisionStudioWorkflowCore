using System;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerInteractionComposition
    {
        public ImageViewerInteractionComposition(
            IImageViewerInteractionController interactionController,
            IImageViewerContextMenuController contextMenuController)
        {
            InteractionController = interactionController ?? throw new ArgumentNullException(nameof(interactionController));
            ContextMenuController = contextMenuController ?? throw new ArgumentNullException(nameof(contextMenuController));
        }

        public IImageViewerInteractionController InteractionController { get; }

        public IImageViewerContextMenuController ContextMenuController { get; }
    }
}