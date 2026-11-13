using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class ContourThicknessTests
{
    [Theory]
    [InlineData("shortest", false)] [InlineData("innernormal", false)]
    [InlineData("shortest", true)] [InlineData("innernormal", true)]
    public async Task MeasuresConcentricPolygonalRingsInEitherWinding(string mode, bool reverse)
    {
        var inner = Circle(10); var outer = Circle(15);
        if (reverse) { Array.Reverse(inner); Array.Reverse(outer); }
        var result = await Run(inner, outer, new { measurementMode = mode, sampleStep = 1, minThickness = 4.9, maxThickness = 5.1 });
        Assert.True(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.InRange(result.Outputs["minThickness"].Require<double>("minThickness"), 4.97, 5.03);
        Assert.InRange(result.Outputs["meanThickness"].Require<double>("meanThickness"), 4.97, 5.03);
        Assert.InRange(result.Outputs["maxThickness"].Require<double>("maxThickness"), 4.97, 5.03);
        var line = result.Outputs["minLine"].Require<VisionLine2D>("minLine");
        Assert.InRange(Math.Abs(Math.Sqrt(Math.Pow(line.End.X - line.Start.X, 2) + Math.Pow(line.End.Y - line.Start.Y, 2))
            - result.Outputs["minThickness"].Require<double>("minThickness")), 0, 1e-5);
    }

    [Theory]
    [InlineData("shortest")] [InlineData("innernormal")]
    public async Task DetectsNarrowOuterDentEvenWithCoarseAdditionalSampling(string mode)
    {
        var inner = Circle(10); var outer = Circle(20);
        outer[0] = new(112, 100);
        var result = await Run(inner, outer, new { measurementMode = mode, sampleStep = 100, minThickness = 3 });
        Assert.True(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.InRange(result.Outputs["minThickness"].Require<double>("minThickness"), 1.99, 2.01);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(result.Outputs["thinSampleCount"].Require<long>("thinSampleCount") > 0);
        Assert.Contains(result.Overlays ?? [], overlay => overlay.Stroke == "#f5222d");
        using var samples = JsonDocument.Parse(result.Outputs["samplesJson"].Require<string>("samplesJson"));
        Assert.Contains(samples.RootElement.EnumerateArray(), sample => sample.GetProperty("Thickness").GetDouble() < 3);
        Assert.Equal(72, samples.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ShortestAndNormalModesHaveDifferentDocumentedExtremaAtCorners()
    {
        var inner = Rectangle(30, 30, 70, 70); var outer = Rectangle(20, 20, 80, 80);
        var shortest = await Run(inner, outer, new { sampleStep = 10 });
        var normal = await Run(inner, outer, new { sampleStep = 10, measurementMode = "innernormal" });
        Assert.True(Found(shortest)); Assert.True(Found(normal));
        Assert.Equal(10, shortest.Outputs["minThickness"].Require<double>("minThickness"), 6);
        Assert.InRange(shortest.Outputs["maxThickness"].Require<double>("maxThickness"), 14.14, 14.15);
        Assert.InRange(normal.Outputs["maxThickness"].Require<double>("maxThickness"), 14.14, 14.15);
        using var json = JsonDocument.Parse(normal.Outputs["samplesJson"].Require<string>("samplesJson"));
        Assert.InRange(json.RootElement[0].GetProperty("Thickness").GetDouble(), 14.14, 14.15);
        Assert.All(json.RootElement.EnumerateArray(), sample => Assert.Equal("inner", sample.GetProperty("Boundary").GetString()));
    }

    [Fact]
    public async Task ExactMinimumFindsAnInnerProtrusionBetweenCoarseOuterSamples()
    {
        VisionPoint2D[] inner = [new(20,20),new(60,20),new(95,45),new(60,80),new(20,80)];
        var result = await Run(inner, Rectangle(0, 0, 100, 100), new { sampleStep = 100, minThickness = 10 });
        Assert.True(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.Equal(5, result.Outputs["minThickness"].Require<double>("minThickness"), 6);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal(0L, result.Outputs["thinSampleCount"].Require<long>("thinSampleCount"));
        Assert.Contains(result.Overlays ?? [], overlay => overlay.Id == "thickness-minimum" && overlay.Stroke == "#f5222d");
    }

    [Fact]
    public async Task ShortestConnectionsCannotLeaveAConcaveOuterShell()
    {
        VisionPoint2D[] outer = [new(0,0),new(100,0),new(100,100),new(70,100),new(70,30),new(30,30),new(30,100),new(0,100)];
        Missing(await Run(Rectangle(10, 50, 20, 80), outer, new { sampleStep = 10 }), "shortest-pairing-failed");
    }

    [Theory]
    [InlineData("innernormal", "normal-pairing-failed")]
    public async Task RejectsConnectionsThatCrossAnotherLobeOfAConcaveHole(string mode, string reason)
    {
        VisionPoint2D[] inner = [new(20,20),new(80,20),new(80,80),new(60,80),new(60,40),new(40,40),new(40,80),new(20,80)];
        var result = await Run(inner, Rectangle(0, 0, 100, 100), new { measurementMode = mode, sampleStep = 10 });
        Missing(result, reason);
        Assert.True(Convert.ToInt32(result.Summary["invalidSamples"]) > 0);
    }

    [Theory]
    [InlineData("missing")] [InlineData("open")] [InlineData("outside")]
    [InlineData("crossing")] [InlineData("touching")] [InlineData("self")]
    public async Task InvalidBoundariesNeverEmitMeasurementValues(string kind)
    {
        var inner = Rectangle(30, 30, 70, 70); var outer = Rectangle(0, 0, 100, 100);
        if (kind == "missing") inner = [];
        if (kind == "outside") inner = Rectangle(120, 30, 140, 70);
        if (kind == "crossing") inner = Rectangle(90, 30, 110, 70);
        if (kind == "touching") inner = Rectangle(0, 30, 30, 70);
        if (kind == "self") inner = [new(30,30),new(70,70),new(30,70),new(70,30)];
        var expected = kind switch { "missing" => "missing-or-insufficient-contour", "open" => "open-contour",
            "outside" => "inner-contour-not-contained", "self" => "self-intersecting-contour", _ => "contours-intersect-or-touch" };
        var inputs = Inputs(inner, outer);
        if (kind == "open") inputs["innerContours"] = VisionValue.Contours([Contour(inner) with { Closed = false }]);
        Missing(await Execute(inputs), expected);
    }

    [Fact]
    public async Task RejectsSamplingOverflowInsteadOfSilentlyDroppingVertices()
    {
        var result = await Run(Circle(10), Circle(20), new { maxSamplesPerBoundary = 8 });
        Missing(result, "sample-budget-exceeded");
        Assert.True(result.Outputs["contained"].Require<bool>("contained"));
    }

    [Fact]
    public async Task CapsQuadraticWorkBeforeEnteringNativeGeometry()
    {
        var result = await Run(Circle(10, 8000), Circle(20, 8000));
        Missing(result, "geometry-work-budget-exceeded");
    }

    [Fact]
    public async Task ArcWeightedStatisticsDoNotGiveDenseVerticesEqualWeight()
    {
        var coarse = Rectangle(30, 30, 70, 70);
        var dense = new List<VisionPoint2D>();
        for (var x = 30; x < 70; x++) dense.Add(new(x, 30));
        dense.AddRange(coarse.Skip(1));
        var outer = Rectangle(20, 20, 90, 90);
        var a = await Run(coarse, outer, new { measurementMode = "innernormal", sampleStep = 1 });
        var b = await Run(dense.ToArray(), outer, new { measurementMode = "innernormal", sampleStep = 1 });
        Assert.True(Found(a)); Assert.True(Found(b));
        Assert.Equal(a.Outputs["meanThickness"].Require<double>("meanThickness"), b.Outputs["meanThickness"].Require<double>("meanThickness"), 6);
    }

    [Theory]
    [InlineData("{\"measurementMode\":\"invalid\"}")]
    [InlineData("{\"minThickness\":10,\"maxThickness\":5}")]
    [InlineData("{\"sampleStep\":0}")]
    [InlineData("{\"maxSamplesPerBoundary\":8.5}")]
    [InlineData("{\"innerIndex\":-1}")]
    public async Task RejectsInvalidParameters(string json)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(Inputs(Circle(10), Circle(20)), JsonSerializer.Deserialize<JsonElement>(json)));
    }

    [Fact]
    public async Task PreservesAnExplicitOpenContourAcrossSerializationAndPointCreation()
    {
        var contour = Contour(Rectangle(0, 0, 20, 20)) with { Closed = false };
        Assert.False(JsonSerializer.Deserialize<VisionContour>(JsonSerializer.Serialize(contour))!.Closed);
        var produced = await new ContourFromPointsNode().ExecuteAsync(new(new Dictionary<string, VisionValue>()),
            Definition("contour.fromPoints", new { pointsJson = "[[0,0],[20,0],[20,20]]", closed = false }), default);
        Assert.False(Assert.Single(produced.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours")).Closed);
    }

    [Fact]
    public async Task OptionalImageIsClonedAndCancellationIsHonored()
    {
        using var image = VisionImage.Own(new Mat(220, 220, MatType.CV_8UC1, Scalar.All(20)));
        var inputs = Inputs(Circle(10), Circle(20)); inputs["image"] = VisionValue.Image(image);
        var result = await Execute(inputs);
        using var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.NotSame(image, output); Assert.Equal(image.Width, output.Width); Assert.Equal(image.Height, output.Height);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ContourThicknessNode().ExecuteAsync(new(inputs),
            Definition("measure.contourThickness", new { }), cancelled.Token).AsTask());
    }

    [Fact]
    public async Task RoiCanvasDoesNotInheritThePreviousNativeImageExtent()
    {
        using var smallerImage = new JLVisionLib.JlImage("byte", 128, 128);
        var result = await new RegionFromRoiNode().ExecuteAsync(new(new Dictionary<string, VisionValue>()),
            Definition("region.fromRoi", new { canvasWidth = 400, canvasHeight = 400,
                roi = new { type = "Rectangle", x = 100, y = 100, width = 200, height = 200 } }), default);
        using var mask = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(40000d, Convert.ToDouble(result.Summary["area"]));
        Assert.Equal(400, mask.Width); Assert.Equal(400, mask.Height);
    }

    private static void Missing(NodeExecutorResult result, string reason)
    {
        Assert.False(Found(result)); Assert.Equal(reason, result.Summary["reason"]);
        foreach (var key in new[] { "minThickness", "maxThickness", "meanThickness", "withinTolerance", "minLine", "minInnerPoint", "minOuterPoint" })
            Assert.False(result.Outputs.ContainsKey(key), key);
    }
    private static bool Found(NodeExecutorResult result) => result.Outputs["found"].Require<bool>("found");
    private static VisionPoint2D[] Circle(double radius, int count = 72) => Enumerable.Range(0, count)
        .Select(i => new VisionPoint2D(100 + radius * Math.Cos(2 * Math.PI * i / count), 100 + radius * Math.Sin(2 * Math.PI * i / count))).ToArray();
    private static VisionPoint2D[] Rectangle(double left, double top, double right, double bottom) => [new(left,top),new(right,top),new(right,bottom),new(left,bottom)];
    private static VisionContour Contour(VisionPoint2D[] points) => new(0, points, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));
    private static Dictionary<string, VisionValue> Inputs(VisionPoint2D[] inner, VisionPoint2D[] outer) => new()
        { ["innerContours"] = VisionValue.Contours([Contour(inner)]), ["outerContours"] = VisionValue.Contours([Contour(outer)]) };
    private static Task<NodeExecutorResult> Run(VisionPoint2D[] inner, VisionPoint2D[] outer, object? parameters = null) => Execute(Inputs(inner, outer), parameters);
    private static async Task<NodeExecutorResult> Execute(Dictionary<string, VisionValue> inputs, object? parameters = null)
        => await new ContourThicknessNode().ExecuteAsync(new(inputs), Definition("measure.contourThickness", parameters ?? new { }), default);
    private static NodeDefinition Definition(string type, object parameters) => new("thickness", type, null, null,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters)));
}
