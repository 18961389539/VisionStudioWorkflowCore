using System;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerModeCommandHostAdapter : IImageViewerModeCommandHost
    {
        private readonly ImageViewer _owner;

        public ImageViewerModeCommandHostAdapter(ImageViewer owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public void StartRectangleMode() => _owner.StartRoiMode();

        public void StartEllipseMode() => _owner.StartEllipseRoiMode();

        public void StartCircleMode() => _owner.StartCircleRoiMode();

        public void StartPolygonMode() => _owner.StartPolygonRoiMode();

        public void StartPolylineMode() => _owner.StartPolylineRoiMode(freehand: false);

        public void StartFreehandMode() => _owner.StartPolylineRoiMode(freehand: true);

        public void StartPointAnnotationMode() => _owner.StartPointAnnotationMode();

        public void StartTextAnnotationMode() => _owner.StartTextAnnotationMode();

        public void StartLineMeasureMode() => _owner.StartLineMeasureMode();

        public void StartAngleMeasureMode() => _owner.StartAngleMeasureMode();
    }
}
