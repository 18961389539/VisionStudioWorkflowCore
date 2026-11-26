using System;
using System.Windows;
using System.Windows.Media;
using ImageViewer.Abstractions;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal readonly record struct ImageViewerViewportState(double Scale, double TranslateX, double TranslateY)
    {
        public static ImageViewerViewportState Default { get; } = new(1.0, 0, 0);
    }

    internal static class ImageViewerViewportStateOperations
    {
        public static ImageViewerViewportState Normalize(ImageViewerViewportState requested, double minScale, double maxScale, bool allowBelowMinScale = false)
        {
            double requestedScale = Math.Max(requested.Scale, double.Epsilon);
            double minimumScale = allowBelowMinScale ? Math.Min(minScale, requestedScale) : minScale;

            return requested with
            {
                Scale = Math.Clamp(requestedScale, minimumScale, maxScale)
            };
        }

        public static ImageViewerViewportState ZoomAt(ImageViewerViewportState current, Point imagePoint, double zoomFactor, double minScale, double maxScale)
        {
            double newScale = Math.Clamp(current.Scale * zoomFactor, minScale, maxScale);
            double dx = imagePoint.X * (current.Scale - newScale);
            double dy = imagePoint.Y * (current.Scale - newScale);

            return new ImageViewerViewportState(newScale, current.TranslateX + dx, current.TranslateY + dy);
        }

        public static ImageViewerViewportState TranslateBy(ImageViewerViewportState current, Vector delta)
        {
            return new ImageViewerViewportState(current.Scale, current.TranslateX + delta.X, current.TranslateY + delta.Y);
        }
    }

    internal interface IImageViewerViewportHost
    {
        IImageViewerViewportService ViewportService { get; }

        ImageSource? ImageSource { get; }

        Size ViewportSize { get; }

        RoiBase? SelectedRoi { get; }

        ImageViewerViewportState ViewportState { get; set; }

        void BeginViewportOverlayBatch();

        void EndViewportOverlayBatch(bool immediate);

        void UpdatePixelGrid();

        void UpdateProfile();

        void UpdateInfoPanel();

        void UpdateRenderedImage();

        void RequestViewportOverlayRefresh();
    }
}
