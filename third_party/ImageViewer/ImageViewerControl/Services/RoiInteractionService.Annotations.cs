using System;
using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    public sealed partial class RoiInteractionService
    {
        private sealed class PointAnnotationBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is PointAnnotationRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var annotation = (PointAnnotationRoi)roi;
                return GeometryUtils.Distance(annotation.Position.ToWpfPoint(), point) <= 8 / scale;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var point = (PointAnnotationRoi)roi;
                point.Position = new Point(point.Position.X + dx, point.Position.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
            }
        }

        private sealed class PointCoordinateMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is PointCoordinateMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var measure = (PointCoordinateMeasureRoi)roi;
                return GeometryUtils.Distance(measure.Position.ToWpfPoint(), point) <= Math.Max(8, hitTestTolerance) / scale;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
                => ResizeHandle.None;

            public void Move(RoiBase roi, double dx, double dy)
            {
                var measure = (PointCoordinateMeasureRoi)roi;
                measure.Position = new Point(measure.Position.X + dx, measure.Position.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
            }
        }

        private sealed class TextAnnotationBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is TextAnnotationRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var text = (TextAnnotationRoi)roi;
                return GeometryUtils.Distance(text.Position.ToWpfPoint(), point) <= 14 / scale;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var text = (TextAnnotationRoi)roi;
                text.Position = new Point(text.Position.X + dx, text.Position.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
            }
        }
    }
}
