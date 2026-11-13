using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class FeatureRegionNodeTests : IDisposable
{
    private readonly List<IDisposable> _outputs = [];
    public void Dispose() { foreach (var item in _outputs) item.Dispose(); }
    private async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string json = "{}", CancellationToken token = default)
    {
        var result = await executor.ExecuteAsync(new(inputs), new("n", executor.Type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)), token);
        _outputs.AddRange(result.Outputs.Values.Select(v => v.Value).OfType<IDisposable>());
        return result;
    }
    private static double Number(NodeExecutorResult r, string key) => r.Outputs[key].Require<double>(key);
    private static VisionRegion Region(NodeExecutorResult r) => r.Outputs["region"].Require<VisionRegion>("region");
    private static Mat Pixels(NodeExecutorResult r) => r.Outputs["image"].Require<VisionImage>("image").Mat;
    private static VisionRegion Rect(int x, int y, int w, int h, int width = 32, int height = 32) => new(width, height,
        [new(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);
    private static Dictionary<string, VisionValue> Input(VisionRegion region) => new() { ["region"] = VisionValue.Region(region) };
    private static IVisionNodeExecutor Executor(string type) => type switch
    {
        "image.topHat" => new ImageTopHatNode(), "image.bottomHat" => new ImageBottomHatNode(), "image.morphGradient" => new ImageMorphGradientNode(),
        "measure.grayHistogram" => new GrayHistogramNode(), "region.fromRoi" => new RegionFromRoiNode(), "region.convexHull" => new RegionConvexHullNode(),
        "region.boundary" => new RegionBoundaryNode(), "region.skeleton" => new RegionSkeletonNode(), _ => throw new ArgumentException(type)
    };

    [Theory]
    [InlineData("image.topHat", 200, 80)]
    [InlineData("image.bottomHat", 40, 80)]
    [InlineData("image.morphGradient", 200, 80)]
    public async Task GrayMorphologyExtractsKnownLocalContrast(string type, byte feature, byte expected)
    {
        using var source = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(120)));
        using (var patch = new Mat(source.Mat, new Rect(14, 14, 3, 3))) patch.SetTo(Scalar.All(feature));
        var result = await Run(Executor(type), new() { ["image"] = VisionValue.Image(source) }, """{"kernelWidth":7,"kernelHeight":7}""");
        Assert.Equal(expected, Pixels(result).At<byte>(15, 15)); Assert.Equal(0, Pixels(result).At<byte>(5, 5));
        Assert.Equal(feature, source.Mat.At<byte>(15, 15)); Assert.Equal(MatType.CV_8UC1, Pixels(result).Type());
        Assert.Equal("jlvision", result.Summary["algorithmBackend"]);
    }
    [Theory]
    [InlineData("image.topHat")]
    [InlineData("image.bottomHat")]
    [InlineData("image.morphGradient")]
    public async Task UnitKernelAndConstantColorProduceZero(string type)
    {
        using var source = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC3, new Scalar(10, 90, 200)));
        var result = await Run(Executor(type), new() { ["image"] = VisionValue.Image(source) }, """{"kernelWidth":1,"kernelHeight":1}""");
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) Assert.Equal(0, Pixels(result).At<byte>(y, x));
    }
    [Fact]
    public async Task HistogramReportsExactDistributionAndNearestRankQuantiles()
    {
        using var source = VisionImage.Own(new Mat(2, 2, MatType.CV_8UC1, Scalar.All(10)));
        source.Mat.Set(1, 0, (byte)30); source.Mat.Set(1, 1, (byte)30);
        var result = await Run(new GrayHistogramNode(), new() { ["image"] = VisionValue.Image(source) }, """{"lowPercent":0,"highPercent":100}""");
        var bins = JsonSerializer.Deserialize<long[]>(result.Outputs["binsJson"].Require<string>("binsJson"))!;
        var cdf = JsonSerializer.Deserialize<double[]>(result.Outputs["cdfJson"].Require<string>("cdfJson"))!;
        Assert.Equal(256, bins.Length); Assert.Equal(2, bins[10]); Assert.Equal(2, bins[30]); Assert.Equal(4, bins.Sum());
        Assert.Equal(.5, cdf[10]); Assert.Equal(1, cdf[255]); Assert.Equal(4L, result.Outputs["pixelCount"].Require<long>("pixelCount"));
        Assert.Equal(20, Number(result, "mean")); Assert.Equal(10, Number(result, "stdDev")); Assert.Equal(1, Number(result, "entropy"));
        Assert.Equal(10, Number(result, "median")); Assert.Equal(10, Number(result, "percentileLow")); Assert.Equal(30, Number(result, "percentileHigh"));
    }
    [Fact]
    public async Task HistogramIntersectsDomainAndUnionWithoutDoubleCounting()
    {
        using var source = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(40)));
        var reduced = await Run(new ReduceDomainNode(), new() { ["image"] = VisionValue.Image(source), ["region"] = VisionValue.Region(Rect(4, 4, 8, 8)) });
        var overlap = new VisionRegion(32, 32, [.. Rect(8, 8, 8, 8).Components, .. Rect(8, 8, 4, 4).Components]);
        var result = await Run(new GrayHistogramNode(), new() { ["domainImage"] = reduced.Outputs["domainImage"], ["region"] = VisionValue.Region(overlap) });
        Assert.Equal(16L, result.Outputs["pixelCount"].Require<long>("pixelCount")); Assert.Equal(40, Number(result, "mean")); Assert.Equal(0, Number(result, "entropy"));
    }
    [Fact]
    public async Task EmptyHistogramOmitsScalarMeasurementsAndReturnsZeroArrays()
    {
        using var source = VisionImage.Own(new Mat(4, 4, MatType.CV_8UC3, Scalar.All(50)));
        var result = await Run(new GrayHistogramNode(), new() { ["image"] = VisionValue.Image(source), ["region"] = VisionValue.Region(new(4, 4, [])) });
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("mean"));
        Assert.All(JsonSerializer.Deserialize<long[]>(result.Outputs["binsJson"].Require<string>("binsJson"))!, n => Assert.Equal(0, n));
        Assert.All(JsonSerializer.Deserialize<double[]>(result.Outputs["cdfJson"].Require<string>("cdfJson"))!, n => Assert.Equal(0, n));
    }
    [Theory]
    [InlineData(-3, -2, 6, 5, 9)]
    [InlineData(40, 40, 5, 5, 0)]
    [InlineData(1.2, 2.2, 3, 2, 6)]
    [InlineData(4, 4, 0, 8, 0)]
    public async Task RectangleRoiUsesHalfOpenPixelCentresAndTrueClipping(double x, double y, double w, double h, double area)
    {
        var roi = new VisionRoi(VisionRoiType.Rectangle, x, y, w, h);
        var result = await Run(new RegionFromRoiNode(), new() { ["roi"] = VisionValue.Roi(roi) }, """{"canvasWidth":32,"canvasHeight":32}""");
        Assert.Equal(area, Number(result, "area")); Assert.Equal(area > 0, result.Outputs["found"].Require<bool>("found"));
    }
    [Fact]
    public async Task RoiParameterCirclePolygonAndConnectedOverrideAreSupported()
    {
        using var source = VisionImage.Own(new Mat(16, 20, MatType.CV_8UC1, Scalar.All(200)));
        var circle = await Run(new RegionFromRoiNode(), new() { ["image"] = VisionValue.Image(source) }, """{"roi":{"type":"circle","x":10,"y":8,"radius":3}}""");
        Assert.Equal(20, Region(circle).Width); Assert.Equal(16, Region(circle).Height); Assert.Equal(255, Pixels(circle).At<byte>(8, 10)); Assert.Equal(0, Pixels(circle).At<byte>(8, 0));
        var polygon = new VisionRoi(VisionRoiType.Polygon, Points: [new(2, 2), new(8, 2), new(2, 8)]);
        var result = await Run(new RegionFromRoiNode(), new() { ["roi"] = VisionValue.Roi(polygon) }, """{"roi":{"type":"rectangle","width":1,"height":1}}""");
        Assert.Equal(255, Pixels(result).At<byte>(3, 3)); Assert.Equal(0, Pixels(result).At<byte>(8, 8)); Assert.True(Number(result, "area") > 1);
    }
    [Fact]
    public async Task HullRemovesConcavityAndUnionFirstBridgesObjects()
    {
        var concave = new VisionRegion(32, 32, [new([new(2, 2, 6), new(3, 2, 2), new(4, 2, 2), new(5, 2, 6)])]);
        var hull = await Run(new RegionConvexHullNode(), Input(concave));
        Assert.Equal(20, Number(hull, "area")); Assert.Equal(255, Pixels(hull).At<byte>(3, 4));
        var objects = new VisionRegion(32, 32, [.. Rect(2, 2, 3, 3).Components, .. Rect(8, 2, 3, 3).Components]);
        var separate = await Run(new RegionConvexHullNode(), Input(objects)); var union = await Run(new RegionConvexHullNode(), Input(objects), """{"unionFirst":true}""");
        Assert.Equal(18, Number(separate, "area")); Assert.Equal(27, Number(union, "area")); Assert.Equal(2, Region(separate).Components.Count); Assert.Single(Region(union).Components);
    }
    [Fact]
    public async Task BoundaryIncludesHoleEdgesAndClipsOuterPixels()
    {
        var shape = new VisionRegion(16, 16, [new(Enumerable.Range(2, 10).SelectMany(row => row is >= 5 and <= 8
            ? new[] { new VisionRegionRun(row, 2, 4), new VisionRegionRun(row, 9, 11) } : [new VisionRegionRun(row, 2, 11)]).ToArray())]);
        var inner = await Run(new RegionBoundaryNode(), Input(shape));
        Assert.Equal(255, Pixels(inner).At<byte>(5, 4)); Assert.Equal(255, Pixels(inner).At<byte>(2, 2)); Assert.Equal(0, Pixels(inner).At<byte>(3, 3));
        var outer = await Run(new RegionBoundaryNode(), Input(Rect(0, 0, 4, 4)), """{"boundaryMode":"outer"}""");
        Assert.Equal(0, Pixels(outer).At<byte>(1, 1)); Assert.Equal(255, Pixels(outer).At<byte>(2, 4));
    }
    [Fact]
    public async Task SkeletonIsNonemptyThinSubsetAndPreservesInput()
    {
        var input = Rect(4, 4, 21, 7); var result = await Run(new RegionSkeletonNode(), Input(input));
        Assert.InRange(Number(result, "area"), 1, 146); Assert.Single(Region(result).Components);
        foreach (var run in Region(result).Components.SelectMany(c => c.Runs)) { Assert.InRange(run.Row, 4, 10); Assert.InRange(run.StartColumn, 4, 24); Assert.InRange(run.EndColumn, 4, 24); }
        Assert.Equal(147, input.Components[0].Runs.Sum(r => r.EndColumn - r.StartColumn + 1));
    }
    [Theory]
    [InlineData("region.convexHull")]
    [InlineData("region.boundary")]
    [InlineData("region.skeleton")]
    public async Task EmptyShapeRemainsEmpty(string type)
    {
        var result = await Run(Executor(type), Input(new VisionRegion(32, 32, [])));
        Assert.Empty(Region(result).Components); Assert.Equal(0, Number(result, "area")); Assert.False(result.Outputs["found"].Require<bool>("found"));
    }
    [Fact]
    public async Task InvalidKernelsHistogramInputsAndRoiFailClearly()
    {
        using var source = VisionImage.Own(new Mat(4, 4, MatType.CV_8UC1, Scalar.All(0)));
        var image = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImageTopHatNode(), image, """{"kernelWidth":2}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new GrayHistogramNode(), image, """{"lowPercent":99,"highPercent":20}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new GrayHistogramNode(), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new GrayHistogramNode(), new() { ["image"] = image["image"], ["region"] = VisionValue.Region(Rect(0, 0, 1, 1)) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromRoiNode(), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromRoiNode(), [], """{"roi":{"type":"rectangle","width":-1,"height":2}}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromRoiNode(), [], """{"canvasWidth":16384,"canvasHeight":16384,"roi":{"type":"circle","radius":2}}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromRoiNode(), new() { ["roi"] = VisionValue.Roi(new(VisionRoiType.Circle, double.NaN, 0, Radius: 3)) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionBoundaryNode(), Input(Rect(2, 2, 4, 4)), """{"boundaryMode":"wrong"}"""));
        using var floatImage = VisionImage.Own(new Mat(4, 4, MatType.CV_32FC1, Scalar.All(0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImageTopHatNode(), new() { ["image"] = VisionValue.Image(floatImage) }));
    }
    [Theory]
    [InlineData("image.topHat")]
    [InlineData("image.bottomHat")]
    [InlineData("image.morphGradient")]
    [InlineData("measure.grayHistogram")]
    [InlineData("region.fromRoi")]
    [InlineData("region.convexHull")]
    [InlineData("region.boundary")]
    [InlineData("region.skeleton")]
    public async Task CancellationPrecedesInputsAndNativeWork(string type)
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Executor(type), [], token: cts.Token));
    }
}
