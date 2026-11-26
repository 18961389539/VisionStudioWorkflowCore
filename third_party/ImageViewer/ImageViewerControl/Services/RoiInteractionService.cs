using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Utils;
using ImageViewer.ViewModels;

namespace ImageViewer.Services
{
    public sealed partial class RoiInteractionService
    {
        private readonly RoiPluginRegistry _pluginRegistry;

        public RoiInteractionService(RoiPluginRegistry pluginRegistry)
        {
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
        }

        internal static IReadOnlyDictionary<Type, IRoiBehavior> CreateBuiltInBehaviorMap()
        {
            return new Dictionary<Type, IRoiBehavior>
            {
                [typeof(RotatedRect)] = new RotatedRectBehavior(),
                [typeof(EllipseRoi)] = new EllipseRoiBehavior(),
                [typeof(FittedEllipseRoi)] = new EllipseRoiBehavior(),
                [typeof(CircleRoi)] = new CircleRoiBehavior(),
                [typeof(RingRoi)] = new RingRoiBehavior(),
                [typeof(CircularCaliperMeasureRoi)] = new CircularCaliperMeasureBehavior(),
                [typeof(ArcCaliperMeasureRoi)] = new ArcCaliperMeasureBehavior(),
                [typeof(PolygonRoi)] = new PolygonRoiBehavior(),
                [typeof(PolylineRoi)] = new PolylineRoiBehavior(),
                [typeof(PointAnnotationRoi)] = new PointAnnotationBehavior(),
                [typeof(PointCoordinateMeasureRoi)] = new PointCoordinateMeasureBehavior(),
                [typeof(TextAnnotationRoi)] = new TextAnnotationBehavior(),
                [typeof(ArrowAnnotationRoi)] = new LineMeasureBehavior(),
                [typeof(LineMeasureRoi)] = new LineMeasureBehavior(),
                [typeof(LineCaliperMeasureRoi)] = new LineCaliperMeasureBehavior(),
                [typeof(CaliperMeasureRoi)] = new CaliperMeasureBehavior(),
                [typeof(AngleMeasureRoi)] = new AngleMeasureBehavior(),
                [typeof(ArcMeasureRoi)] = new ArcMeasureBehavior(),
                [typeof(PointToLineDistanceRoi)] = new PointToLineDistanceBehavior(),
                [typeof(PointToCircleDistanceRoi)] = new PointToCircleDistanceBehavior(),
                [typeof(ParallelismMeasureRoi)] = new ParallelismMeasureBehavior(),
                [typeof(PerpendicularityMeasureRoi)] = new PerpendicularityMeasureBehavior(),
                [typeof(ConcentricityMeasureRoi)] = new ConcentricityMeasureBehavior(),
                [typeof(CenterDistanceMeasureRoi)] = new CenterDistanceMeasureBehavior(),
                [typeof(ThreePointCircleMeasureRoi)] = new ThreePointCircleMeasureBehavior(),
                [typeof(BlobAnalysisRoi)] = new BlobAnalysisBehavior()
            };
        }

        public RoiBase? HitTest(ImageViewerViewModel viewModel, Point point, double scale, double hitTestTolerance)
        {
            foreach (var plugin in _pluginRegistry.GetPluginsInHitTestOrder())
            {
                foreach (var roi in plugin.GetRois(viewModel).Reverse())
                {
                    if (!roi.IsVisible)
                    {
                        continue;
                    }

                    if (plugin.Behavior.HitTest(roi, point, scale, hitTestTolerance))
                    {
                        return roi;
                    }
                }

            }

            return null;
        }

        public ResizeHandle GetHandleAt(RoiBase? roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
        {
            if (roi == null || roi.IsLocked)
            {
                return ResizeHandle.None;
            }

            return GetBehavior(roi)?.GetHandleAt(roi, point, scale, handleSize, handleHitPadding, infoTextOffset, polygonVertexHitPadding) ?? ResizeHandle.None;
        }

        public int GetPolygonPointIndexAt(RoiBase? roi, Point point, double scale, double handleSize, double polygonVertexHitPadding)
        {
            if (roi == null || roi.IsLocked)
            {
                return -1;
            }

            return GetBehavior(roi)?.GetVertexIndexAt(roi, point, scale, handleSize, polygonVertexHitPadding) ?? -1;
        }

        public int GetPolygonSegmentAt(RoiBase? roi, Point point, double scale, double hitTestTolerance)
        {
            if (roi == null || roi.IsLocked)
            {
                return -1;
            }

            return GetBehavior(roi)?.GetSegmentIndexAt(roi, point, scale, hitTestTolerance) ?? -1;
        }

        public void MoveRoi(RoiBase roi, double dx, double dy)
        {
            if (roi.IsLocked)
            {
                return;
            }

            GetBehavior(roi)?.Move(roi, dx, dy);
        }

        public void ResizeRoi(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
        {
            if (roi.IsLocked)
            {
                return;
            }

            GetBehavior(roi)?.Resize(roi, handle, dx, dy, currentPos, minimumRoiDimension);
        }

        private IRoiBehavior? GetBehavior(RoiBase roi)
        {
            return _pluginRegistry.FindByRoi(roi)?.Behavior;
        }

        private static Dictionary<ResizeHandle, Point> CreateBoxHandlePositions(Point center, double halfWidth, double halfHeight)
        {
            return new Dictionary<ResizeHandle, Point>
            {
                { ResizeHandle.TopLeft, new Point(center.X - halfWidth, center.Y - halfHeight) },
                { ResizeHandle.TopCenter, new Point(center.X, center.Y - halfHeight) },
                { ResizeHandle.TopRight, new Point(center.X + halfWidth, center.Y - halfHeight) },
                { ResizeHandle.MiddleRight, new Point(center.X + halfWidth, center.Y) },
                { ResizeHandle.BottomRight, new Point(center.X + halfWidth, center.Y + halfHeight) },
                { ResizeHandle.BottomCenter, new Point(center.X, center.Y + halfHeight) },
                { ResizeHandle.BottomLeft, new Point(center.X - halfWidth, center.Y + halfHeight) },
                { ResizeHandle.MiddleLeft, new Point(center.X - halfWidth, center.Y) }
            };
        }

        private static bool IsNear(Point p1, Point p2, double threshold)
        {
            return Math.Abs(p1.X - p2.X) < threshold / 2 && Math.Abs(p1.Y - p2.Y) < threshold / 2;
        }

        private static bool IsWithinHandle(Point point, Point handlePoint, double size)
        {
            return point.X >= handlePoint.X - size / 2 && point.X <= handlePoint.X + size / 2 &&
                   point.Y >= handlePoint.Y - size / 2 && point.Y <= handlePoint.Y + size / 2;
        }

        private static void UpdateBounds(ResizeHandle handle, double dx, double dy, ref double left, ref double top, ref double right, ref double bottom, double minimumRoiDimension)
        {
            switch (handle)
            {
                case ResizeHandle.TopLeft:
                    left += dx;
                    top += dy;
                    break;
                case ResizeHandle.TopCenter:
                    top += dy;
                    break;
                case ResizeHandle.TopRight:
                    right += dx;
                    top += dy;
                    break;
                case ResizeHandle.MiddleRight:
                    right += dx;
                    break;
                case ResizeHandle.BottomRight:
                    right += dx;
                    bottom += dy;
                    break;
                case ResizeHandle.BottomCenter:
                    bottom += dy;
                    break;
                case ResizeHandle.BottomLeft:
                    left += dx;
                    bottom += dy;
                    break;
                case ResizeHandle.MiddleLeft:
                    left += dx;
                    break;
            }

            if (right - left < minimumRoiDimension)
            {
                if (dx > 0) right = left + minimumRoiDimension;
                else left = right - minimumRoiDimension;
            }

            if (bottom - top < minimumRoiDimension)
            {
                if (dy > 0) bottom = top + minimumRoiDimension;
                else top = bottom - minimumRoiDimension;
            }
        }
    }
}
