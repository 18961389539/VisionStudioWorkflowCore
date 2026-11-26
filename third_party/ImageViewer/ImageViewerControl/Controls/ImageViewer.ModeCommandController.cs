using System;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerModeCommandController : ImageViewerCommandControllerBase<IImageViewerModeCommandHost>
    {
        public ImageViewerModeCommandController(IImageViewerModeCommandHost host)
            : base(host)
        {
        }

        public void Execute(ImageViewerModeCommand command)
        {
            switch (command)
            {
                case ImageViewerModeCommand.Rectangle:
                    Host.StartRectangleMode();
                    break;
                case ImageViewerModeCommand.Ellipse:
                    Host.StartEllipseMode();
                    break;
                case ImageViewerModeCommand.Circle:
                    Host.StartCircleMode();
                    break;
                case ImageViewerModeCommand.Polygon:
                    Host.StartPolygonMode();
                    break;
                case ImageViewerModeCommand.Polyline:
                    Host.StartPolylineMode();
                    break;
                case ImageViewerModeCommand.Freehand:
                    Host.StartFreehandMode();
                    break;
                case ImageViewerModeCommand.PointAnnotation:
                    Host.StartPointAnnotationMode();
                    break;
                case ImageViewerModeCommand.TextAnnotation:
                    Host.StartTextAnnotationMode();
                    break;
                case ImageViewerModeCommand.LineMeasure:
                    Host.StartLineMeasureMode();
                    break;
                case ImageViewerModeCommand.AngleMeasure:
                    Host.StartAngleMeasureMode();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }
    }
}