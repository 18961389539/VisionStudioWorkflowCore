using System;
using System.Linq;
using ImageViewer.Dialogs;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests;

public sealed class MeasurementPreviewGeometryTests
{
    [Fact]
    public void GetSourceOutline_LineMeasure_ReturnsBothEndpoints()
    {
        var line = new LineMeasureRoi { P1 = new PointD(1, 2), P2 = new PointD(3, 4) };

        IReadOnlyList<PointD> outline = MeasurementPreviewGeometry.GetSourceOutline(line);

        Assert.Equal([new PointD(1, 2), new PointD(3, 4)], outline);
    }

    [Fact]
    public void GetSourceOutline_ClosedPolygon_ReturnsVertices()
    {
        var polygon = new PolygonRoi { IsClosed = true };
        polygon.Points.Add(new PointD(0, 0));
        polygon.Points.Add(new PointD(4, 0));
        polygon.Points.Add(new PointD(2, 3));

        IReadOnlyList<PointD> outline = MeasurementPreviewGeometry.GetSourceOutline(polygon);

        Assert.Equal(3, outline.Count);
        Assert.Equal(new PointD(2, 3), outline[2]);
    }

    [Fact]
    public void GetSourceOutline_OpenPolygon_ReturnsEmpty()
    {
        var polygon = new PolygonRoi { IsClosed = false };
        polygon.Points.Add(new PointD(0, 0));
        polygon.Points.Add(new PointD(4, 0));

        Assert.Empty(MeasurementPreviewGeometry.GetSourceOutline(polygon));
    }

    [Fact]
    public void GetSourceOutline_Polyline_ReturnsVertices()
    {
        var polyline = new PolylineRoi();
        polyline.Points.Add(new PointD(1, 1));
        polyline.Points.Add(new PointD(2, 5));

        IReadOnlyList<PointD> outline = MeasurementPreviewGeometry.GetSourceOutline(polyline);

        Assert.Equal([new PointD(1, 1), new PointD(2, 5)], outline);
    }

    [Fact]
    public void GetSourceOutline_RotatedRect_ReturnsFourCorners()
    {
        var rectangle = new RotatedRect { Center = new PointD(10, 20), Width = 4, Height = 2, Angle = 0 };

        IReadOnlyList<PointD> outline = MeasurementPreviewGeometry.GetSourceOutline(rectangle);

        Assert.Equal(
            [new PointD(8, 19), new PointD(12, 19), new PointD(12, 21), new PointD(8, 21)],
            outline);
    }

    [Fact]
    public void GetSourceOutline_RotatedRect_AppliesRotation()
    {
        var rectangle = new RotatedRect { Center = new PointD(10, 20), Width = 4, Height = 2, Angle = Math.PI / 2 };

        IReadOnlyList<PointD> outline = MeasurementPreviewGeometry.GetSourceOutline(rectangle);

        Assert.Equal(new PointD(11, 18), outline[0]);
        Assert.Equal(new PointD(11, 22), outline[1]);
        Assert.Equal(new PointD(9, 22), outline[2]);
        Assert.Equal(new PointD(9, 18), outline[3]);
    }

    [Fact]
    public void GetSourceOutline_Circle_SamplesSixtyFourPointsOnRadius()
    {
        var circle = new CircleRoi { Center = new PointD(5, 6), Radius = 3 };

        IReadOnlyList<PointD> outline = MeasurementPreviewGeometry.GetSourceOutline(circle);

        Assert.Equal(64, outline.Count);
        Assert.Equal(new PointD(8, 6), outline[0]);
        Assert.All(outline, point => Assert.Equal(3, point.DistanceTo(circle.Center), 9));
    }

    [Fact]
    public void GetSourceOutline_Ellipse_UsesBothRadii()
    {
        var ellipse = new EllipseRoi { Center = new PointD(5, 6), RadiusX = 4, RadiusY = 2, Angle = 0 };

        IReadOnlyList<PointD> outline = MeasurementPreviewGeometry.GetSourceOutline(ellipse);

        Assert.Equal(64, outline.Count);
        Assert.Equal(new PointD(9, 6), outline[0]);
        Assert.All(outline, point =>
        {
            double x = (point.X - ellipse.Center.X) / ellipse.RadiusX;
            double y = (point.Y - ellipse.Center.Y) / ellipse.RadiusY;
            Assert.Equal(1, (x * x) + (y * y), 9);
        });
    }

    [Fact]
    public void GetSourceOutline_UnsupportedRoi_ReturnsEmpty()
    {
        Assert.Empty(MeasurementPreviewGeometry.GetSourceOutline(new PointAnnotationRoi()));
    }
}
