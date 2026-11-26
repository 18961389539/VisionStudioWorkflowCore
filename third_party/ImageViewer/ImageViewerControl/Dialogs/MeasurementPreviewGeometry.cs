using System;
using System.Collections.Generic;
using System.Linq;
using ImageViewer.Models;

namespace ImageViewer.Dialogs;

/// <summary>
/// Builds ROI outlines for the advanced-measurement preview.
/// Chinese: 预览轮廓的纯几何构造从对话框抽离，便于直接单测。
/// English: Pure outline construction extracted from the dialog so it can be tested without WPF.
/// </summary>
internal static class MeasurementPreviewGeometry
{
    public static IReadOnlyList<PointD> GetSourceOutline(RoiBase roi)
    {
        switch (roi)
        {
            case LineMeasureRoi line:
                return [line.P1, line.P2];
            case PolygonRoi { IsClosed: true } polygon:
                return polygon.Points.ToArray();
            case PolylineRoi polyline:
                return polyline.Points.ToArray();
            case RotatedRect rectangle:
                return RectangleCorners(rectangle.Center, rectangle.Width, rectangle.Height, rectangle.Angle);
            case EllipseRoi ellipse:
                return Enumerable.Range(0, 64).Select(index =>
                {
                    double angle = index * Math.Tau / 64;
                    double x = ellipse.RadiusX * Math.Cos(angle);
                    double y = ellipse.RadiusY * Math.Sin(angle);
                    double cos = Math.Cos(ellipse.Angle);
                    double sin = Math.Sin(ellipse.Angle);
                    return new PointD(ellipse.Center.X + (cos * x) - (sin * y), ellipse.Center.Y + (sin * x) + (cos * y));
                }).ToArray();
            case CircleRoi circle:
                return Enumerable.Range(0, 64).Select(index =>
                {
                    double angle = index * Math.Tau / 64;
                    return new PointD(circle.Center.X + (circle.Radius * Math.Cos(angle)), circle.Center.Y + (circle.Radius * Math.Sin(angle)));
                }).ToArray();
            default:
                return Array.Empty<PointD>();
        }
    }

    private static PointD[] RectangleCorners(PointD center, double width, double height, double angle)
    {
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double ux = cos * width / 2;
        double uy = sin * width / 2;
        double vx = -sin * height / 2;
        double vy = cos * height / 2;
        return
        [
            new(center.X - ux - vx, center.Y - uy - vy),
            new(center.X + ux - vx, center.Y + uy - vy),
            new(center.X + ux + vx, center.Y + uy + vy),
            new(center.X - ux + vx, center.Y - uy + vy)
        ];
    }

}
