using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests;

public sealed class CaliperDetectionResultTests
{
    [Fact]
    public void LineResult_UsesCoreGeometryForMeasuredSegment()
    {
        var result = new LineCaliperDetectionResult(
            new PointD(0, 1),
            new PointD(10, 1),
            new PointD(0, 2),
            new PointD(10, 2),
            [],
            [],
            [],
            [],
            new DetectedLineSegment(new PointD(0, 2), new PointD(10, 2)),
            80,
            0.1,
            0.2,
            5,
            0,
            90);

        Assert.Equal(new PointD(0, 2), result.MeasuredSegment.Start);
        Assert.Equal(new PointD(10, 2), result.MeasuredSegment.End);
        Assert.True(result.FittedGeometry.IsValid);
    }

    [Fact]
    public void DualEdgeResult_ExposesBothCoreFitGeometries()
    {
        var result = new LineMeasureGradientDetectionResult(
            new PointD(0, 0),
            new PointD(10, 2),
            [], [], [], [], [], [], [],
            new DetectedLineSegment(new PointD(0, 1), new PointD(10, 1)),
            new DetectedLineSegment(new PointD(0, 3), new PointD(10, 3)),
            90, 90, 0.1, 0.1, 0.2, 0.2, 4, 0, 0, 0, 95, []);

        Assert.True(result.FittedEdge1Geometry.IsValid);
        Assert.True(result.FittedEdge2Geometry.IsValid);
        Assert.Equal(2, result.MeasuredSegment.End.Y - result.MeasuredSegment.Start.Y);
    }
}
