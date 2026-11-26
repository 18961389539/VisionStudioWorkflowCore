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
        private sealed class CircularCaliperMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is CircularCaliperMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var caliper = (CircularCaliperMeasureRoi)roi;
                double distance = GeometryUtils.Distance(caliper.Center.ToWpfPoint(), point);
                double innerRadius = Math.Max(0, caliper.Radius - caliper.CaliperSearchRange);
                double outerRadius = caliper.Radius + caliper.CaliperSearchRange;
                return distance >= innerRadius && distance <= outerRadius;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var caliper = (CircularCaliperMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                foreach (var kvp in CreateBoxHandlePositions(caliper.Center.ToWpfPoint(), caliper.Radius, caliper.Radius))
                {
                    if (IsWithinHandle(point, kvp.Value, hSize))
                    {
                        return kvp.Key;
                    }
                }

                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var caliper = (CircularCaliperMeasureRoi)roi;
                caliper.Center = new Point(caliper.Center.X + dx, caliper.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var caliper = (CircularCaliperMeasureRoi)roi;
                if (handle != ResizeHandle.None)
                {
                    caliper.Radius = Math.Max(minimumRoiDimension, GeometryUtils.Distance(caliper.Center.ToWpfPoint(), currentPos));
                }
            }
        }

        private sealed class ArcCaliperMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is ArcCaliperMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var caliper = (ArcCaliperMeasureRoi)roi;
                double distance = GeometryUtils.Distance(caliper.Center.ToWpfPoint(), point);
                double innerRadius = Math.Max(0, caliper.Radius - caliper.CaliperSearchRange);
                double outerRadius = caliper.Radius + caliper.CaliperSearchRange;
                if (distance < innerRadius || distance > outerRadius)
                {
                    return false;
                }

                double angle = Math.Atan2(point.Y - caliper.Center.Y, point.X - caliper.Center.X) * 180 / Math.PI;
                return IsAngleWithinArc(angle, caliper.StartAngle, caliper.SweepAngle);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var caliper = (ArcCaliperMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                foreach (var kvp in CreateBoxHandlePositions(caliper.Center.ToWpfPoint(), caliper.Radius, caliper.Radius))
                {
                    if (IsWithinHandle(point, kvp.Value, hSize))
                    {
                        return kvp.Key;
                    }
                }

                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var caliper = (ArcCaliperMeasureRoi)roi;
                caliper.Center = new Point(caliper.Center.X + dx, caliper.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var caliper = (ArcCaliperMeasureRoi)roi;
                if (handle != ResizeHandle.None)
                {
                    caliper.Radius = Math.Max(minimumRoiDimension, GeometryUtils.Distance(caliper.Center.ToWpfPoint(), currentPos));
                }
            }

            private static bool IsAngleWithinArc(double angleDegrees, double startAngle, double sweepAngle)
            {
                double normalizedAngle = NormalizeAngle(angleDegrees);
                double normalizedStart = NormalizeAngle(startAngle);
                double normalizedEnd = NormalizeAngle(startAngle + sweepAngle);
                if (sweepAngle >= 0)
                {
                    return normalizedStart <= normalizedEnd
                        ? normalizedAngle >= normalizedStart && normalizedAngle <= normalizedEnd
                        : normalizedAngle >= normalizedStart || normalizedAngle <= normalizedEnd;
                }

                return normalizedEnd <= normalizedStart
                    ? normalizedAngle >= normalizedEnd && normalizedAngle <= normalizedStart
                    : normalizedAngle >= normalizedEnd || normalizedAngle <= normalizedStart;
            }

            private static double NormalizeAngle(double angleDegrees)
            {
                double angle = angleDegrees % 360;
                return angle < 0 ? angle + 360 : angle;
            }
        }
    }
}
