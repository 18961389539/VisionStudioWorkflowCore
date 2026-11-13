using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class GeometryNumericStabilityTests
{
    private static NodeDefinition Node(string type, object? parameters = null) => new("numeric", type, null, null,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters ?? new { })));
    private static NodeExecutionContext Inputs(params (string Name, VisionValue Value)[] values)
        => new(values.ToDictionary(value => value.Name, value => value.Value));

    [Theory]
    [InlineData(1e200)]
    [InlineData(1e-200)]
    public async Task PixelDistance_PreservesRepresentableLargeAndTinyDistances(double scale)
    {
        var result = await new DistanceNode().ExecuteAsync(Inputs(
            ("pointA", VisionValue.Point(new(0, 0))), ("pointB", VisionValue.Point(new(3 * scale, 4 * scale)))),
            Node("geometry.distance"), default);

        var distance = result.Outputs["distance"].Require<double>("distance");
        Assert.True(double.IsFinite(distance));
        Assert.InRange(distance / (5 * scale), 1 - 1e-14, 1 + 1e-14);
    }

    [Theory]
    [InlineData(1e200)]
    [InlineData(1e-200)]
    public async Task CoordinateDistance_PreservesRepresentableLargeAndTinyDistances(double scale)
    {
        var result = await new CoordinateDistanceNode().ExecuteAsync(Inputs(
            ("pointA", VisionValue.CoordinatePoint(new(0, 0, "World", "mm"))),
            ("pointB", VisionValue.CoordinatePoint(new(3 * scale, 4 * scale, "World", "mm")))),
            Node("coordinate.distance"), default);

        var distance = result.Outputs["distance"].Require<double>("distance");
        Assert.True(double.IsFinite(distance));
        Assert.InRange(distance / (5 * scale), 1 - 1e-14, 1 + 1e-14);
        Assert.Equal("mm", result.Outputs["measurement"].Require<VisionMeasurement>("measurement").Unit);
    }

    [Theory]
    [InlineData(1e308, 1.1e308)]
    [InlineData(-1e308, -1.1e308)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public async Task PixelDistance_ReturnsFiniteMidpointsWithoutAddingTheEndpoints(double start, double end)
    {
        var result = await new DistanceNode().ExecuteAsync(Inputs(
            ("pointA", VisionValue.Point(new(start, start))), ("pointB", VisionValue.Point(new(end, end)))),
            Node("geometry.distance"), default);

        var midpoint = result.Outputs["midX"].Require<double>("midX");
        Assert.True(double.IsFinite(midpoint));
        Assert.InRange(midpoint, Math.Min(start, end), Math.Max(start, end));
        Assert.Equal(midpoint, result.Outputs["midY"].Require<double>("midY"));
    }

    [Theory]
    [InlineData(1e200)]
    [InlineData(1e-200)]
    public void LineLengths_UseStableNormsInPixelAndCoordinateTypes(double scale)
    {
        var pixel = new VisionLine2D(new(0, 0), new(3 * scale, 4 * scale));
        var coordinate = new VisionCoordinateLine2D(new(0, 0, "World", "mm"), new(3 * scale, 4 * scale, "World", "mm"));
        Assert.InRange(pixel.Length / (5 * scale), 1 - 1e-14, 1 + 1e-14);
        Assert.InRange(coordinate.Length / (5 * scale), 1 - 1e-14, 1 + 1e-14);
    }

    [Theory]
    [InlineData(1e12)]
    [InlineData(-1e12)]
    [InlineData(1e15)]
    public async Task Intersection_IsStableAfterLargeTranslations(double origin)
    {
        var a = new VisionLine2D(new(origin, origin), new(origin + 10, origin + 10));
        var b = new VisionLine2D(new(origin, origin + 10), new(origin + 10, origin));
        var result = await new IntersectionNode().ExecuteAsync(Inputs(
            ("lineA", VisionValue.Line(a)), ("lineB", VisionValue.Line(b))), Node("geometry.intersection", new { requireWithinSegments = true }), default);

        Assert.Equal(origin + 5, result.Outputs["x"].Require<double>("x"));
        Assert.Equal(origin + 5, result.Outputs["y"].Require<double>("y"));
        Assert.True(result.Outputs["withinSegments"].Require<bool>("withinSegments"));
    }

    [Fact]
    public async Task Intersection_HandlesLongLinesWithoutOverflowingTheirDeterminant()
    {
        var result = await new IntersectionNode().ExecuteAsync(Inputs(
            ("lineA", VisionValue.Line(new(new(-1e200, 0), new(1e200, 0)))),
            ("lineB", VisionValue.Line(new(new(0, -1e200), new(0, 1e200))))), Node("geometry.intersection"), default);
        Assert.Equal(new VisionPoint2D(0, 0), result.Outputs["point"].Require<VisionPoint2D>("point"));
        Assert.True(result.Outputs["withinSegments"].Require<bool>("withinSegments"));
    }

    [Fact]
    public async Task Intersection_PreservesTheAbsoluteParallelTolerance()
    {
        var inputs = Inputs(("lineA", VisionValue.Line(new(new(0, 0), new(1e-4, 0)))),
            ("lineB", VisionValue.Line(new(new(0, 0), new(0, 1e-4)))));
        var result = await new IntersectionNode().ExecuteAsync(inputs, Node("geometry.intersection"), default);
        Assert.True(result.Outputs["withinSegments"].Require<bool>("withinSegments"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new IntersectionNode().ExecuteAsync(
            inputs, Node("geometry.intersection", new { parallelTolerance = 1e-7 }), default));
    }

    [Fact]
    public async Task Angle_AcceptsRepresentableLongLines()
    {
        var result = await new AngleNode().ExecuteAsync(Inputs(
            ("lineA", VisionValue.Line(new(new(0, 0), new(1e200, 0)))),
            ("lineB", VisionValue.Line(new(new(0, 0), new(0, 1e200))))), Node("geometry.angle"), default);
        Assert.Equal(90d, result.Outputs["angle"].Require<double>("angle"));
    }
}
