using System.Windows;
using ImageViewer.Controls;
using ImageViewer.Models;

namespace ImageViewer.Abstractions
{
    public interface IRoiBehavior
    {
        bool CanHandle(RoiBase roi);

        bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance);

        ResizeHandle GetHandleAt(
            RoiBase roi,
            Point point,
            double scale,
            double handleSize,
            double handleHitPadding,
            double infoTextOffset,
            double polygonVertexHitPadding);

        int GetVertexIndexAt(RoiBase roi, Point point, double scale, double handleSize, double polygonVertexHitPadding) => -1;

        int GetSegmentIndexAt(RoiBase roi, Point point, double scale, double hitTestTolerance) => -1;

        void Move(RoiBase roi, double dx, double dy);

        void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension);
    }
}
