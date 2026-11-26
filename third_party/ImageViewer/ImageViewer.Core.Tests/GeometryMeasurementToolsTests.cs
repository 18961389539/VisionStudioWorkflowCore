using ImageViewer.Core.Measurements;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests;

public class GeometryMeasurementToolsTests
{
    [Fact]
    public void ConstructionMethodsReturnExpectedExactGeometry()
    {
        GeometryMeasurementResult midpoint = GeometryMeasurementTools.Midpoint(new(0, 2), new(4, 6));
        Assert.True(midpoint.IsValid);
        Assert.Equal(new PointD(2, 4), midpoint.FirstWitness);

        GeometryMeasurementResult crossing = GeometryMeasurementTools.IntersectLines(new(0, 0), new(2, 2), new(0, 2), new(2, 0));
        Assert.True(crossing.IsValid);
        Assert.Equal(new PointD(1, 1), crossing.FirstWitness);

        GeometryMeasurementResult projection = GeometryMeasurementTools.ProjectPointToSegment(new(3, 2), new(0, 0), new(4, 0));
        Assert.Equal(2, projection.Value);
        Assert.Equal(new PointD(3, 0), projection.FirstWitness);

        GeometryMeasurementResult bisector = GeometryMeasurementTools.AngleBisector(new(0, 0), new(1, 0), new(0, 1));
        Assert.Equal(Math.PI / 2, bisector.Value, 12);
        Assert.Equal(Math.Sqrt(0.5), bisector.SecondWitness!.Value.X, 12);
        Assert.Equal(Math.Sqrt(0.5), bisector.SecondWitness.Value.Y, 12);
    }

    [Fact]
    public void SegmentCircleAndCircleCircleDistancesIncludeWitnesses()
    {
        GeometryMeasurementResult segmentCircle = GeometryMeasurementTools.SegmentCircleBoundaryDistance(new(0, 0), new(2, 0), new(1, 3), 1);
        Assert.Equal(2, segmentCircle.Value, 12);
        Assert.Equal(new PointD(1, 0), segmentCircle.FirstWitness);
        Assert.Equal(new PointD(1, 2), segmentCircle.SecondWitness);

        GeometryMeasurementResult circles = GeometryMeasurementTools.CircleCircleBoundaryDistance(new(0, 0), 1, new(5, 0), 2);
        Assert.Equal(2, circles.Value);
        Assert.Equal(new PointD(1, 0), circles.FirstWitness);
        Assert.Equal(new PointD(3, 0), circles.SecondWitness);

        GeometryMeasurementResult intersecting = GeometryMeasurementTools.CircleCircleBoundaryDistance(new(0, 0), 2, new(2, 0), 2);
        Assert.Equal(0, intersecting.Value);
        Assert.Equal(intersecting.FirstWitness, intersecting.SecondWitness);

        GeometryMeasurementResult nested = GeometryMeasurementTools.CircleCircleBoundaryDistance(new(0, 0), 5, new(1, 0), 2);
        Assert.Equal(2, nested.Value);
        Assert.Equal(new PointD(5, 0), nested.FirstWitness);
        Assert.Equal(new PointD(3, 0), nested.SecondWitness);
    }

    [Fact]
    public void FiniteGeometryDistanceReturnsConsistentWitnessesForDisjointAndContainedShapes()
    {
        GeometryMeasurementResult segments = GeometryMeasurementTools.SegmentSegmentDistance(
            new(0, 0), new(4, 0), new(2, 2), new(2, 4));
        Assert.Equal(2, segments.Value);
        Assert.Equal(new PointD(2, 0), segments.FirstWitness);
        Assert.Equal(new PointD(2, 2), segments.SecondWitness);

        GeometryMeasurementResult containedSegment = GeometryMeasurementTools.SegmentCircleBoundaryDistance(
            new(1, -1), new(1, 1), new(0, 0), 3);
        Assert.Equal(3 - Math.Sqrt(2), containedSegment.Value, 12);
        Assert.Equal(new PointD(1, -1), containedSegment.FirstWitness);
        Assert.Equal(3 / Math.Sqrt(2), containedSegment.SecondWitness!.Value.X, 12);
        Assert.Equal(-3 / Math.Sqrt(2), containedSegment.SecondWitness.Value.Y, 12);

        GeometryMeasurementResult crossingSegment = GeometryMeasurementTools.SegmentCircleBoundaryDistance(
            new(-2, 0), new(2, 0), new(0, 0), 1);
        Assert.Equal(0, crossingSegment.Value);
        Assert.Equal(new PointD(-1, 0), crossingSegment.FirstWitness);
    }

    [Fact]
    public void PolylinePitchAndPositionMethodsMeasureExpectedValues()
    {
        GeometryMeasurementResult polyline = GeometryMeasurementTools.PolylinePolylineDistance(
            [new(0, 0), new(2, 0)], [new(1, 3), new(1, 4)]);
        Assert.Equal(3, polyline.Value);
        Assert.Equal(new PointD(1, 0), polyline.FirstWitness);
        Assert.Equal(new PointD(1, 3), polyline.SecondWitness);

        IReadOnlyList<GeometryMeasurementResult> pitches = GeometryMeasurementTools.AdjacentPitch([new(0, 0), new(3, 0), new(3, 4)]);
        Assert.Collection(pitches,
            pitch => Assert.Equal(3, pitch.Value),
            pitch => Assert.Equal(4, pitch.Value));
        Assert.Equal(4, GeometryMeasurementTools.ProjectedCenterDistance(new(1, 1), new(4, 5), new(0, 2)).Value);
        Assert.Equal(5, GeometryMeasurementTools.PositionDeviation(new(3, 4), new(0, 0)).Value);
        Assert.Equal(10, GeometryMeasurementTools.DiametricalPosition(new(3, 4), new(0, 0)).Value);
    }

    [Fact]
    public void LeastSquaresCircleFitRecoversExactCircleAndRejectsCollinearPoints()
    {
        PointD[] ring = [new(3, 2), new(1, 4), new(-1, 2), new(1, 0)];
        DistributionCircleFit fit = GeometryMeasurementTools.FitDistributionCircle(ring);
        Assert.True(fit.IsValid);
        Assert.Equal(new PointD(1, 2), fit.Center);
        Assert.Equal(2, fit.Radius, 10);
        Assert.Equal(0, fit.RootMeanSquareResidual, 10);

        Assert.False(GeometryMeasurementTools.FitDistributionCircle([new(0, 0), new(1, 1), new(2, 2)]).IsValid);
    }

    [Fact]
    public void DegenerateAndNonFiniteInputsReturnExplicitInvalidResults()
    {
        Assert.False(GeometryMeasurementTools.IntersectLines(new(0, 0), new(1, 0), new(0, 1), new(1, 1)).IsValid);
        Assert.False(GeometryMeasurementTools.ProjectPointToLine(new(1, 1), new(0, 0), new(0, 0)).IsValid);
        Assert.False(GeometryMeasurementTools.SegmentSegmentDistance(new(0, 0), new(0, 0), new(1, 0), new(2, 0)).IsValid);
        Assert.False(GeometryMeasurementTools.SegmentCircleBoundaryDistance(new(0, 0), new(1, 0), new(0, 0), 0).IsValid);
        Assert.False(GeometryMeasurementTools.ProjectedCenterDistance(new(0, 0), new(1, 1), new(0, 0)).IsValid);
        Assert.False(GeometryMeasurementTools.PositionDeviation(new(double.NaN, 0), new(0, 0)).IsValid);
        Assert.False(GeometryMeasurementTools.AngleBisector(new(0, 0), new(1, 0), new(-1, 0)).IsValid);
    }
}
