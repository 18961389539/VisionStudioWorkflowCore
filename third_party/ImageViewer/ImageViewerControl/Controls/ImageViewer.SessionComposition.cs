using System;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerSessionComposition
    {
        public ImageViewerSessionComposition(
            ImageViewerSessionController sessionController,
            ImageViewerRoiPersistenceController roiPersistenceController,
            ImageViewerFileMenuCommandController fileMenuCommandController)
        {
            SessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
            RoiPersistenceController = roiPersistenceController ?? throw new ArgumentNullException(nameof(roiPersistenceController));
            FileMenuCommandController = fileMenuCommandController ?? throw new ArgumentNullException(nameof(fileMenuCommandController));
        }

        public ImageViewerSessionController SessionController { get; }

        public ImageViewerRoiPersistenceController RoiPersistenceController { get; }

        public ImageViewerFileMenuCommandController FileMenuCommandController { get; }
    }
}