using System.Text.Json;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class GeometryToleranceNodeTests
{
    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, object? parameters = null, CancellationToken token = default)
        => await executor.ExecuteAsync(new(inputs), new("test", executor.Type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters ?? new { }))), token);
    private static VisionContour Contour(params VisionPoint2D[] points) => new(0, points, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));
    private static Dictionary<string, VisionValue> Points(params VisionPoint2D[] points) => new() { ["contours"] = VisionValue.Contours([Contour(points)]) };
    private static VisionPoint2D[] CirclePoints(double radius, double variation = 0, double arc = 360, int count = 60) => Enumerable.Range(0, count).Select(i =>
    {
        var angle = arc == 360 ? 2 * Math.PI * i / count : arc * Math.PI / 180 * i / (count - 1);
        var r = radius + (i % 2 == 0 ? variation : -variation);
        return new VisionPoint2D(100 + r * Math.Cos(angle), 120 + r * Math.Sin(angle));
    }).ToArray();

    [Fact]
    public async Task FromPointsPreservesSubpixelsMetadataAndConnectedInputPrecedence()
    {
        var json = "[[10.5,20.5],[20.5,20.5],[20.5,30.5],[10.5,30.5],[10.5,20.5]]";
        var result = await Run(new ContourFromPointsNode(), new() { ["pointsJson"] = VisionValue.String(json) }, new { pointsJson = "invalid", closed = true });
        var contour = Assert.Single(result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours"));
        Assert.Equal(4, contour.Points.Count); Assert.Equal(new VisionPoint2D(10.5, 20.5), contour.Points[0]);
        Assert.Equal(100, contour.Area, 8); Assert.Equal(40, contour.Perimeter, 8);
        Assert.Equal(10, contour.BoundingBox.Width, 8);
    }

    [Fact]
    public async Task OpenPointPathKeepsExplicitReturnToStartAndUsesOpenLength()
    {
        var result = await Run(new ContourFromPointsNode(), new(), new { pointsJson = "[[0,0],[0,0],[10,0],[0,0]]", closed = false });
        var contour = Assert.Single(result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours"));
        Assert.Equal(3, contour.Points.Count); Assert.Equal(20, contour.Perimeter, 8); Assert.Equal(0, contour.Area);
    }

    [Theory]
    [InlineData("[]")] [InlineData("[[1,2]]")] [InlineData("[[1,2],[1,2]]")]
    public async Task InsufficientPointJsonIsNormalMissing(string pointsJson)
    {
        var result = await Run(new ContourFromPointsNode(), new(), new { pointsJson });
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.Empty(result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours"));
    }

    [Fact]
    public async Task CircularWallUsesAnalyticExtremaAndRadialSampleIntersections()
    {
        var result = await Run(new WallThicknessNode(), new() { ["inner"] = VisionValue.Circle(new(2, 0, 5)), ["outer"] = VisionValue.Circle(new(0, 0, 10)) }, new { sampleCount = 8, startAngleDeg = 13 });
        Assert.True(result.Outputs["found"].Require<bool>("found")); Assert.Equal(3d, result.Outputs["minThickness"].Require<double>("minThickness"));
        Assert.Equal(7d, result.Outputs["maxThickness"].Require<double>("maxThickness")); Assert.Equal(4d, result.Outputs["variation"].Require<double>("variation"));
        using var json = JsonDocument.Parse(result.Outputs["samplesJson"].Require<string>("samplesJson"));
        foreach (var sample in json.RootElement.EnumerateArray())
        {
            var outer = sample.GetProperty("Outer"); var x = outer.GetProperty("X").GetDouble(); var y = outer.GetProperty("Y").GetDouble();
            Assert.InRange(Math.Abs(x * x + y * y - 100), 0, 1e-8);
            Assert.InRange(sample.GetProperty("Thickness").GetDouble(), 3, 7);
        }
    }

    [Fact]
    public async Task ConcentricWallHasConstantThicknessAndTangencyMayBeZero()
    {
        var input = new Dictionary<string, VisionValue> { ["inner"] = VisionValue.Circle(new(0, 0, 5)), ["outer"] = VisionValue.Circle(new(0, 0, 10)) };
        var result = await Run(new WallThicknessNode(), input);
        Assert.Equal(5d, result.Outputs["meanThickness"].Require<double>("meanThickness")); Assert.Equal(0d, result.Outputs["stdDev"].Require<double>("stdDev"));
        input["inner"] = VisionValue.Circle(new(5, 0, 5)); result = await Run(new WallThicknessNode(), input);
        Assert.True(result.Outputs["found"].Require<bool>("found")); Assert.Equal(0d, result.Outputs["minThickness"].Require<double>("minThickness"));
    }

    [Fact]
    public async Task NoncontainedWallOmitsMeasurementsAndInvalidRadiiAreRejected()
    {
        var inputs = new Dictionary<string, VisionValue> { ["inner"] = VisionValue.Circle(new(20, 0, 5)), ["outer"] = VisionValue.Circle(new(0, 0, 10)) };
        var result = await Run(new WallThicknessNode(), inputs);
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs["contained"].Require<bool>("contained")); Assert.False(result.Outputs.ContainsKey("minThickness"));
        inputs["inner"] = VisionValue.Circle(new(0, 0, -1)); await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new WallThicknessNode(), inputs));
    }

    [Fact]
    public async Task ConcentricityReturnsCentreVectorAndDoubledOffset()
    {
        var result = await Run(new ConcentricityNode(), new() { ["circleA"] = VisionValue.Circle(new(0, 0, 3)), ["circleB"] = VisionValue.Circle(new(3, 4, 20)) });
        Assert.Equal(5d, result.Outputs["centerDistance"].Require<double>("centerDistance")); Assert.Equal(10d, result.Outputs["diametralOffset"].Require<double>("diametralOffset"));
        Assert.Equal(new VisionPoint2D(3, 4), result.Outputs["centerOffset"].Require<VisionPoint2D>("centerOffset"));
    }

    [Fact]
    public async Task RoundnessOfIdealAndAlternatingRadiusSamplesUsesFittedCircle()
    {
        var result = await Run(new RoundnessNode(), Points(CirclePoints(20)));
        Assert.True(result.Outputs["found"].Require<bool>("found")); Assert.InRange(result.Outputs["deviation"].Require<double>("deviation"), 0, 1e-5);
        Assert.InRange(result.Outputs["radius"].Require<double>("radius"), 19.99999, 20.00001);
        result = await Run(new RoundnessNode(), Points(CirclePoints(20, 1)));
        Assert.InRange(result.Outputs["deviation"].Require<double>("deviation"), 1.999, 2.001); Assert.InRange(result.Outputs["rms"].Require<double>("rms"), .999, 1.001);
    }

    [Fact]
    public async Task ShortArcFailsCoverageAndCollinearCircleSamplesFailClearly()
    {
        var result = await Run(new RoundnessNode(), Points(CirclePoints(20, arc: 60)));
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("deviation")); Assert.InRange(result.Outputs["coverageDeg"].Require<double>("coverageDeg"), 59, 61);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoundnessNode(), Points(Enumerable.Range(0, 6).Select(i => new VisionPoint2D(i, 0)).ToArray())));
    }

    [Fact]
    public async Task StraightnessUsesSignedResidualRangeAndLeastSquaresRms()
    {
        var result = await Run(new StraightnessNode(), Points(new(0, 1), new(1, -1), new(2, 1), new(3, -1), new(4, 1), new(5, -1), new(6, 1)));
        Assert.True(result.Outputs["found"].Require<bool>("found")); Assert.InRange(result.Outputs["deviation"].Require<double>("deviation"), 1.99999, 2.00001);
        Assert.InRange(result.Outputs["rms"].Require<double>("rms"), .98, 1);
        result = await Run(new StraightnessNode(), Points(new(10, 0), new(10, 3), new(10, 7)));
        Assert.InRange(result.Outputs["deviation"].Require<double>("deviation"), 0, 1e-7);
    }

    [Theory]
    [InlineData("roundness")] [InlineData("straightness")]
    public async Task MissingContourIndexOmitsFitMeasurements(string kind)
    {
        IVisionNodeExecutor executor = kind == "roundness" ? new RoundnessNode() : new StraightnessNode();
        var result = await Run(executor, Points(CirclePoints(10)), new { index = 1 });
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("deviation"));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 5)]
    public async Task ContourDeviationSupportsDirectedAndSymmetricSegmentDistances(bool symmetric, double deviation)
    {
        var input = Points(new(5, 0), new(15, 0)); input["referenceContours"] = VisionValue.Contours([Contour(new(0, 0), new(20, 0))]);
        var result = await Run(new ContourDeviationNode(), input, new { closed = false, symmetric, sampleStep = 1 });
        Assert.Equal(deviation, result.Outputs["deviation"].Require<double>("deviation"), 8); Assert.Equal(0, result.Outputs["forwardMax"].Require<double>("forwardMax"), 8);
        Assert.Equal(symmetric, result.Outputs.ContainsKey("reverseMax"));
    }

    [Fact]
    public async Task ContourDeviationMeasuresToSegmentsRatherThanOnlyVertices()
    {
        var input = Points(new(5, 3), new(15, 3)); input["referenceContours"] = VisionValue.Contours([Contour(new(0, 0), new(20, 0))]);
        var result = await Run(new ContourDeviationNode(), input, new { closed = false, symmetric = false, sampleStep = 1 });
        Assert.Equal(3, result.Outputs["deviation"].Require<double>("deviation"), 8); Assert.Equal(3, result.Outputs["mean"].Require<double>("mean"), 8);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ContourDeviationNode(), input, new { closed = false, sampleStep = .01, maxSamples = 10 }));
    }

    [Fact]
    public async Task ContourDeviationEmptyReferenceIsNormalMissing()
    {
        var input = Points(new(0, 0), new(10, 0)); input["referenceContours"] = VisionValue.Contours([]);
        var result = await Run(new ContourDeviationNode(), input);
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("deviation"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LineOrientationMetricsAreInvariantToEndpointReversal(bool reverse)
    {
        var a = new VisionLine2D(new(0, 0), new(10, 0)); var b = new VisionLine2D(new(1, 2), new(11, 3));
        if (reverse) { a = new(a.End, a.Start); b = new(b.End, b.Start); }
        var input = new Dictionary<string, VisionValue> { ["a"] = VisionValue.Line(a), ["b"] = VisionValue.Line(b) };
        var parallel = await Run(new ParallelismNode(), input); Assert.Equal(1, parallel.Outputs["deviation"].Require<double>("deviation"), 8);
        Assert.Equal(Math.Atan(.1) * 180 / Math.PI, parallel.Outputs["angleError"].Require<double>("angleError"), 8);
        var perpendicular = await Run(new PerpendicularityNode(), input); Assert.Equal(10, perpendicular.Outputs["deviation"].Require<double>("deviation"), 8);
    }

    [Fact]
    public async Task PerfectParallelAndPerpendicularLinesHaveZeroCorrespondingError()
    {
        var input = new Dictionary<string, VisionValue> { ["a"] = VisionValue.Line(new(new(0, 0), new(10, 0))), ["b"] = VisionValue.Line(new(new(0, 5), new(20, 5))) };
        Assert.Equal(0, (await Run(new ParallelismNode(), input)).Outputs["deviation"].Require<double>("deviation"));
        input["b"] = VisionValue.Line(new(new(2, 0), new(2, 10)));
        Assert.Equal(0, (await Run(new PerpendicularityNode(), input)).Outputs["deviation"].Require<double>("deviation"));
        input["b"] = VisionValue.Line(new(new(0, 0), new(0, 0))); await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ParallelismNode(), input));
    }

    [Fact]
    public async Task InvalidPointsJsonCoordinatesAndBudgetsFailBeforeNativeGeometry()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ContourFromPointsNode(), new(), new { pointsJson = "[[1,2,3]]" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ContourFromPointsNode(), new(), new { pointsJson = "invalid" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new StraightnessNode(), Points(new(0, 0), new(1, double.NaN), new(2, 0))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new StraightnessNode(), Points(Enumerable.Range(0, 20001).Select(i => new VisionPoint2D(i, 0)).ToArray())));
    }

    [Fact]
    public async Task AllEightNodesHonorPreCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        IVisionNodeExecutor[] nodes = [new ContourFromPointsNode(), new WallThicknessNode(), new ConcentricityNode(), new RoundnessNode(), new StraightnessNode(), new ContourDeviationNode(), new ParallelismNode(), new PerpendicularityNode()];
        foreach (var node in nodes) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(node, new(), token: cancellation.Token));
    }
}
