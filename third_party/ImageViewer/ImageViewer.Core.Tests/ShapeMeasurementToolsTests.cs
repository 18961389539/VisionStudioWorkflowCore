using ImageViewer.Core.Measurements;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests;

public class ShapeMeasurementToolsTests
{
    [Fact]
    public void MeasureContour_ReturnsPolygonMetricsAndSubtractsExplicitHole()
    {
        PointD[] outer = [new(0, 0), new(10, 0), new(10, 8), new(0, 8), new(0, 0)];
        IReadOnlyList<PointD>[] holes = [[new(2, 2), new(4, 2), new(4, 4), new(2, 4)]];

        ContourShapeMetrics result = ShapeMeasurementTools.MeasureContour(outer, holes);

        Assert.Equal(76, result.Area, 10);
        Assert.Equal(2 * (10 + 8) + 8, result.Perimeter, 10);
        Assert.Equal(5.105263157894737, result.Centroid.X, 10);
        Assert.Equal(4.052631578947368, result.Centroid.Y, 10);
        Assert.Equal(8, result.MinimumFeretDiameter, 10);
        Assert.Equal(Math.Sqrt(164), result.MaximumFeretDiameter, 10);
        Assert.Equal(Math.Sqrt(164) / 8, result.AspectRatio, 10);
        Assert.Equal(0.05, result.HoleAreaFraction, 10);
        Assert.Equal(4, result.ConvexHull.Count);
    }

    [Fact]
    public void MeasureContour_IsIndependentOfWindingAndClosingDuplicate()
    {
        PointD[] clockwise = [new(0, 0), new(0, 4), new(6, 4), new(6, 0)];
        ContourShapeMetrics result = ShapeMeasurementTools.MeasureContour(clockwise);

        Assert.Equal(24, result.Area);
        Assert.Equal(new PointD(3, 2), result.Centroid);
        Assert.Equal(20, result.Perimeter);
        Assert.Equal((4 * Math.PI * 24) / 400, result.Circularity, 10);
    }

    [Fact]
    public void FitMinimumAreaRectangle_RecoversRotatedRectangleAndEnclosesContour()
    {
        PointD[] contour = CreateRotatedRectangle(new PointD(14, -3), 12, 4, 0.37);

        OrientedRectangleFit fit = ShapeMeasurementTools.FitMinimumAreaRectangle(contour);

        Assert.Equal(14, fit.Center.X, 8);
        Assert.Equal(-3, fit.Center.Y, 8);
        Assert.Equal(12, fit.Width, 8);
        Assert.Equal(4, fit.Height, 8);
        Assert.Equal(0.37, fit.AngleRadians, 8);
        Assert.Equal(48, fit.Area, 8);
        Assert.Equal(4, fit.Corners.Count);
        foreach (PointD point in contour)
        {
            VectorD relative = point - fit.Center;
            double alongWidth = (relative.X * Math.Cos(fit.AngleRadians)) + (relative.Y * Math.Sin(fit.AngleRadians));
            double alongHeight = (-relative.X * Math.Sin(fit.AngleRadians)) + (relative.Y * Math.Cos(fit.AngleRadians));
            Assert.True(Math.Abs(alongWidth) <= fit.Width / 2 + 1e-8);
            Assert.True(Math.Abs(alongHeight) <= fit.Height / 2 + 1e-8);
        }
    }

    [Fact]
    public void FitMinimumAreaRectangle_HandlesLineAsDegenerateRectangle()
    {
        OrientedRectangleFit fit = ShapeMeasurementTools.FitMinimumAreaRectangle([new(4, 2), new(1, 2), new(4, 2)]);

        Assert.Equal(3, fit.Width);
        Assert.Equal(0, fit.Height);
        Assert.Equal(new PointD(2.5, 2), fit.Center);
        Assert.Equal(0, fit.Area);
    }

    [Fact]
    public void TryFitSlot_RecoversCapsuleDimensionsAndRejectsRectangle()
    {
        PointD[] capsule = CreateCapsule(new PointD(5, -8), length: 20, width: 6, angle: 0.42, arcSteps: 24);

        bool succeeded = ShapeMeasurementTools.TryFitSlot(capsule, out SlotShapeFit? fit, out string? reason);

        Assert.True(succeeded, reason);
        Assert.NotNull(fit);
        Assert.Equal(new PointD(5, -8), fit.Center, new PointDComparer(1e-7));
        Assert.Equal(20, fit.Length, 1e-6);
        Assert.Equal(6, fit.Width, 1e-6);
        Assert.Equal(3, fit.EndRadius, 1e-6);
        Assert.Equal(0.42, fit.AngleRadians, 1e-6);
        Assert.True(fit.Quality > 0.99);

        bool rectangleAccepted = ShapeMeasurementTools.TryFitSlot(CreateRotatedRectangle(new PointD(0, 0), 12, 4, 0.2), out _, out reason);
        Assert.False(rectangleAccepted);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void MeasureContour_RejectsDegenerateArea()
    {
        PointD[] points = [new(0, 0), new(1, 1), new(0, 0)];

        Assert.Throws<ArgumentException>(() => ShapeMeasurementTools.MeasureContour(points));
    }

    [Fact]
    public void MeasureContour_RejectsHoleOutsideOuterContour()
    {
        PointD[] outer = [new(0, 0), new(5, 0), new(5, 5), new(0, 5)];
        IReadOnlyList<PointD>[] holes = [[new(4, 4), new(7, 4), new(7, 7), new(4, 7)]];

        Assert.Throws<ArgumentException>(() => ShapeMeasurementTools.MeasureContour(outer, holes));
    }

    [Fact]
    public void MeasureContour_RejectsHoleEdgesThatLeaveConcaveOuterContour()
    {
        PointD[] outer = [new(0, 0), new(10, 0), new(10, 3), new(3, 3), new(3, 7), new(10, 7), new(10, 10), new(0, 10)];
        IReadOnlyList<PointD>[] holes = [[new(1, 2), new(9, 2), new(9, 8), new(1, 8)]];

        Assert.All(holes[0], point => Assert.True(point.X is >= 0 and <= 10 && point.Y is >= 0 and <= 10));
        Assert.Throws<ArgumentException>(() => ShapeMeasurementTools.MeasureContour(outer, holes));
    }

    [Fact]
    public void MeasureContour_RejectsIntersectingAndNestedHoles()
    {
        PointD[] outer = [new(0, 0), new(20, 0), new(20, 20), new(0, 20)];
        IReadOnlyList<PointD>[] overlapping =
        [
            [new(2, 2), new(10, 2), new(10, 10), new(2, 10)],
            [new(8, 8), new(16, 8), new(16, 16), new(8, 16)]
        ];
        IReadOnlyList<PointD>[] nested =
        [
            [new(2, 2), new(18, 2), new(18, 18), new(2, 18)],
            [new(5, 5), new(10, 5), new(10, 10), new(5, 10)]
        ];

        Assert.Throws<ArgumentException>(() => ShapeMeasurementTools.MeasureContour(outer, overlapping));
        Assert.Throws<ArgumentException>(() => ShapeMeasurementTools.MeasureContour(outer, nested));
    }

    [Fact]
    public void MeasureContour_RejectsSelfIntersectingOuterOrHole()
    {
        PointD[] bowTie = [new(0, 0), new(4, 4), new(0, 4), new(4, 0)];
        PointD[] square = [new(-1, -1), new(5, -1), new(5, 5), new(-1, 5)];
        IReadOnlyList<PointD>[] selfIntersectingHole = [bowTie];

        Assert.Throws<ArgumentException>(() => ShapeMeasurementTools.MeasureContour(bowTie));
        Assert.Throws<ArgumentException>(() => ShapeMeasurementTools.MeasureContour(square, selfIntersectingHole));
    }

    private static PointD[] CreateRotatedRectangle(PointD center, double width, double height, double angle)
    {
        double ux = Math.Cos(angle);
        double uy = Math.Sin(angle);
        double vx = -uy;
        double vy = ux;
        return
        [
            new(center.X - (width / 2 * ux) - (height / 2 * vx), center.Y - (width / 2 * uy) - (height / 2 * vy)),
            new(center.X + (width / 2 * ux) - (height / 2 * vx), center.Y + (width / 2 * uy) - (height / 2 * vy)),
            new(center.X + (width / 2 * ux) + (height / 2 * vx), center.Y + (width / 2 * uy) + (height / 2 * vy)),
            new(center.X - (width / 2 * ux) + (height / 2 * vx), center.Y - (width / 2 * uy) + (height / 2 * vy))
        ];
    }

    private static PointD[] CreateCapsule(PointD center, double length, double width, double angle, int arcSteps)
    {
        double radius = width / 2;
        double halfLine = (length - width) / 2;
        double ux = Math.Cos(angle);
        double uy = Math.Sin(angle);
        double vx = -uy;
        double vy = ux;
        var points = new List<PointD>();

        void Add(double localU, double localV) => points.Add(new PointD(center.X + (localU * ux) + (localV * vx), center.Y + (localU * uy) + (localV * vy)));

        Add(-halfLine, radius);
        Add(halfLine, radius);
        for (int step = 1; step <= arcSteps; step++)
        {
            double arcAngle = (Math.PI / 2) - (Math.PI * step / arcSteps);
            Add(halfLine + (radius * Math.Cos(arcAngle)), radius * Math.Sin(arcAngle));
        }

        Add(-halfLine, -radius);
        for (int step = 1; step < arcSteps; step++)
        {
            double arcAngle = -(Math.PI / 2) - (Math.PI * step / arcSteps);
            Add(-halfLine + (radius * Math.Cos(arcAngle)), radius * Math.Sin(arcAngle));
        }

        return points.ToArray();
    }

    private sealed class PointDComparer(double tolerance) : IEqualityComparer<PointD>
    {
        public bool Equals(PointD first, PointD second) => first.DistanceTo(second) <= tolerance;

        public int GetHashCode(PointD point) => HashCode.Combine(Math.Round(point.X / tolerance), Math.Round(point.Y / tolerance));
    }
}
