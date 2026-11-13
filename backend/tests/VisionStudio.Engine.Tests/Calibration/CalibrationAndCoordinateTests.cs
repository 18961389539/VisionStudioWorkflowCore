using VisionStudio.Engine;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class CalibrationAndCoordinateTests
{
    [Fact]
    public void SolveHomography_ReconstructsKnownAffineMapping()
    {
        var pairs = new List<CalibrationPair>();
        foreach (var y in new[] { 100d, 240d, 380d })
        foreach (var x in new[] { 100d, 320d, 540d })
            pairs.Add(new CalibrationPair(x, y, x * 0.1 + 5, y * 0.1 - 10));

        var t = CalibrationMath.SolveHomography(pairs, "ImagePixel", "Workpiece", "mm");
        var q = t.Apply(new VisionPoint2D(320, 240));

        Assert.Equal("ImagePixel", t.SourceFrame);
        Assert.Equal("Workpiece", t.TargetFrame);
        Assert.Equal("px", t.SourceUnit);
        Assert.Equal("mm", t.TargetUnit);
        Assert.InRange(Math.Abs(q.X - 37), 0, 1e-8);
        Assert.InRange(Math.Abs(q.Y - 14), 0, 1e-8);
    }

    [Fact]
    public void SolveHomography_RejectsDegenerateGeometry()
    {
        var pairs = new[]
        {
            new CalibrationPair(0, 0, 0, 0),
            new CalibrationPair(1, 0, 1, 0),
            new CalibrationPair(2, 0, 2, 0),
            new CalibrationPair(3, 0, 3, 0)
        };

        Assert.Throws<InvalidOperationException>(() =>
            CalibrationMath.SolveHomography(pairs, "ImagePixel", "Workpiece", "mm"));
    }

    [Fact]
    public void ComposeAndInverse_CloseWithinNumericTolerance()
    {
        var a = VisionTransform2DMath.Rigid("Workpiece", "Fixture", "mm", 100, 50, 5);
        var b = VisionTransform2DMath.Rigid("Fixture", "RobotBase", "mm", 500, 200, -2);
        var composed = VisionTransform2DMath.Compose(a, b);
        var inverse = VisionTransform2DMath.Inverse(composed);

        var p = new VisionCoordinatePoint2D(22.5, 14.25, "Workpiece", "mm");
        var robot = VisionTransform2DMath.Apply(composed, p);
        var roundTrip = VisionTransform2DMath.Apply(inverse, robot);

        Assert.InRange(Math.Abs(roundTrip.X - p.X), 0, 1e-9);
        Assert.InRange(Math.Abs(roundTrip.Y - p.Y), 0, 1e-9);
        Assert.Equal("RobotBase", robot.Frame);
        Assert.Equal("mm", robot.Unit);
    }

    [Fact]
    public void Compose_RejectsFrameMismatch()
    {
        var a = VisionTransform2DMath.Rigid("A", "B", "mm", 0, 0, 0);
        var b = VisionTransform2DMath.Rigid("C", "D", "mm", 0, 0, 0);
        Assert.Throws<InvalidOperationException>(() => VisionTransform2DMath.Compose(a, b));
    }
}
