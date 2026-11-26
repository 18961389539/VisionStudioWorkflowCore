using System.Windows;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerViewportService
    {
        (double Scale, double TranslateX, double TranslateY)? FitToViewport(Size viewport, Size content, double padding);

        (double Scale, double TranslateX, double TranslateY)? ZoomToBounds(Size viewport, Rect bounds, double padding, double minScale, double maxScale);

        Point ClampTranslation(Size viewport, Size content, double scale, Point translation, double maxOverPan = 5000);
    }
}
