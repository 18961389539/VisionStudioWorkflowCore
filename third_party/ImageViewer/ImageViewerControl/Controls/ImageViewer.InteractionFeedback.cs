using System;
using System.Windows;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private Point SnapPoint(Point point)
        {
            if (!EnableSnapToGrid || GridSpacing <= 0)
            {
                return point;
            }

            return new Point(
                Math.Round(point.X / GridSpacing) * GridSpacing,
                Math.Round(point.Y / GridSpacing) * GridSpacing);
        }

        private void UpdateCrosshair(double x, double y)
        {
            crosshairH.X1 = 0;
            crosshairH.X2 = ActualWidth;
            crosshairH.Y1 = y;
            crosshairH.Y2 = y;

            crosshairV.X1 = x;
            crosshairV.X2 = x;
            crosshairV.Y1 = 0;
            crosshairV.Y2 = ActualHeight;
        }
    }
}