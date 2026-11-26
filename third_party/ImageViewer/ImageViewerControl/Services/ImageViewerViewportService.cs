using System;
using System.Windows;
using ImageViewer.Abstractions;

namespace ImageViewer.Services
{
    public sealed class ImageViewerViewportService : IImageViewerViewportService
    {
        public (double Scale, double TranslateX, double TranslateY)? FitToViewport(Size viewport, Size content, double padding)
        {
            if (!IsValid(viewport) || !IsValid(content))
            {
                return null;
            }

            double availableWidth = Math.Max(1, viewport.Width - padding * 2);
            double availableHeight = Math.Max(1, viewport.Height - padding * 2);
            double scale = Math.Min(availableWidth / content.Width, availableHeight / content.Height);
            double translateX = (viewport.Width - content.Width * scale) / 2;
            double translateY = (viewport.Height - content.Height * scale) / 2;
            return (scale, translateX, translateY);
        }

        public (double Scale, double TranslateX, double TranslateY)? ZoomToBounds(Size viewport, Rect bounds, double padding, double minScale, double maxScale)
        {
            if (!IsValid(viewport) || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            double availableWidth = Math.Max(1, viewport.Width - padding * 2);
            double availableHeight = Math.Max(1, viewport.Height - padding * 2);
            double scale = Math.Min(availableWidth / bounds.Width, availableHeight / bounds.Height);
            scale = Math.Clamp(scale, minScale, maxScale);
            double translateX = viewport.Width / 2 - (bounds.X + bounds.Width / 2) * scale;
            double translateY = viewport.Height / 2 - (bounds.Y + bounds.Height / 2) * scale;
            return (scale, translateX, translateY);
        }

        public Point ClampTranslation(Size viewport, Size content, double scale, Point translation, double maxOverPan = 50000)
        {
            if (!IsValid(viewport) || !IsValid(content) || scale <= 0)
            {
                return translation;
            }

            double scaledWidth = content.Width * scale;
            double scaledHeight = content.Height * scale;

            double minX = scaledWidth <= viewport.Width ? (viewport.Width - scaledWidth) / 2 : viewport.Width - scaledWidth;
            double maxX = scaledWidth <= viewport.Width ? minX : 0;
            double minY = scaledHeight <= viewport.Height ? (viewport.Height - scaledHeight) / 2 : viewport.Height - scaledHeight;
            double maxY = scaledHeight <= viewport.Height ? minY : 0;

            // 允许最大超出（正负方向），防止传入负数导致反向放大
            double margin = Math.Max(0, maxOverPan);

            return new Point(
                Math.Clamp(translation.X, minX - margin, maxX + margin),
                Math.Clamp(translation.Y, minY - margin, maxY + margin));
        }

        private static bool IsValid(Size size)
        {
            return !double.IsNaN(size.Width) && !double.IsNaN(size.Height) && size.Width > 0 && size.Height > 0;
        }
    }
}
