using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class RegionNodeTests : IDisposable
{
    private readonly List<IDisposable> _outputs = new();
    public void Dispose() { foreach (var output in _outputs) output.Dispose(); }
    private async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string json = "{}", CancellationToken token = default)
    {
        var node = new NodeDefinition("test", executor.Type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        var result = await executor.ExecuteAsync(new(inputs), node, token);
        _outputs.AddRange(result.Outputs.Values.Select(v => v.Value).OfType<IDisposable>());
        return result;
    }
    private static Dictionary<string, VisionValue> Input(VisionRegion region) => new() { ["region"] = VisionValue.Region(region) };
    private static VisionRegion Region(NodeExecutorResult result) => result.Outputs["region"].Require<VisionRegion>("region");
    private static VisionRegion Rect(int x, int y, int w, int h, int width = 32, int height = 32) => new(width, height,
        [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);

    [Fact]
    public async Task ThresholdIsInclusiveAndRetainsHolesUntilFilled()
    {
        using var image = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0)));
        using (var outer = new Mat(image.Mat, new Rect(4, 4, 12, 12))) outer.SetTo(Scalar.All(128));
        using (var hole = new Mat(image.Mat, new Rect(8, 8, 4, 4))) hole.SetTo(Scalar.All(0));
        var threshold = await Run(new RegionFromThresholdNode(), new() { ["image"] = VisionValue.Image(image) }, """{"minGray":128,"maxGray":128}""");
        Assert.Equal(128d, threshold.Outputs["area"].Require<double>("area"));
        Assert.Equal(0, threshold.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(9, 9));
        var fill = await Run(new RegionFillHolesNode(), Input(Region(threshold)));
        Assert.Equal(144d, fill.Outputs["area"].Require<double>("area"));
        Assert.Equal(255, fill.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(9, 9));
        Assert.Equal(0, image.Mat.At<byte>(9, 9));
    }

    [Fact]
    public async Task UnifiedRangeSegmentationSupportsGrayColorAndRegionClipping()
    {
        using var gray = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC1, Scalar.All(0)));
        using (var inner = new Mat(gray.Mat, new Rect(2, 2, 4, 4))) inner.SetTo(Scalar.All(200));
        using (var outside = new Mat(gray.Mat, new Rect(10, 10, 4, 4))) outside.SetTo(Scalar.All(200));
        var grayInputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(gray),
            ["region"] = VisionValue.Region(new VisionRegion(16, 16, [new VisionRegionComponent(Enumerable.Range(3, 2).Select(y => new VisionRegionRun(y, 3, 4)).ToArray())]))
        };
        var grayResult = await Run(new RegionFromRangeNode(), grayInputs, """{"mode":"gray","minGray":200,"maxGray":200}""");
        Assert.Equal(4d, grayResult.Outputs["area"].Require<double>("area"));

        using var color = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC3, new Scalar(0, 0, 0)));
        using (var red = new Mat(color.Mat, new Rect(4, 4, 6, 6))) red.SetTo(new Scalar(0, 0, 255));
        var colorInputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(color),
            ["region"] = VisionValue.Region(new VisionRegion(16, 16, [new VisionRegionComponent(Enumerable.Range(6, 3).Select(y => new VisionRegionRun(y, 6, 8)).ToArray())]))
        };
        var colorResult = await Run(new RegionFromRangeNode(), colorInputs,
            """{"mode":"color","space":"hsv","min1":350,"max1":10,"min2":0.8,"max2":1,"min3":0.9,"max3":1}""");
        Assert.Equal(9d, colorResult.Outputs["area"].Require<double>("area"));
        Assert.Equal("hsv", colorResult.Summary["space"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromRangeNode(), grayInputs, """{"mode":"binary"}"""));
    }

    [Fact]
    public async Task ConnectivitySplitsBeforePerObjectFiltering()
    {
        var merged = new VisionRegion(32, 32, [new VisionRegionComponent([new(2, 2, 4), new(3, 2, 4), new(20, 20, 20)])]);
        var connected = await Run(new RegionConnectedComponentsNode(), Input(merged));
        Assert.Equal(2L, connected.Outputs["count"].Require<long>("count"));
        var selected = await Run(new RegionFilterNode(), Input(Region(connected)), """{"feature":"area","min":2,"max":10}""");
        Assert.Single(Region(selected).Components); Assert.Equal(6d, selected.Outputs["area"].Require<double>("area"));
        Assert.Equal(7, merged.Components[0].Runs.Sum(r => r.EndColumn - r.StartColumn + 1));
    }

    [Theory]
    [InlineData("union", 24d)]
    [InlineData("intersection", 8d)]
    [InlineData("difference", 8d)]
    [InlineData("xor", 16d)]
    public async Task BooleanUsesSetSemanticsAndExpectedDirection(string operation, double area)
    {
        var result = await Run(new RegionBooleanNode(), new() { ["a"] = VisionValue.Region(Rect(2, 2, 4, 4)), ["b"] = VisionValue.Region(Rect(4, 2, 4, 4)) }, JsonSerializer.Serialize(new { operation }));
        Assert.Equal(area, result.Outputs["area"].Require<double>("area"));
    }

    [Theory]
    [InlineData("region.connectedComponents")]
    [InlineData("region.filter")]
    [InlineData("region.fillHoles")]
    public async Task EmptyRegionsAreNormalBusinessResults(string type)
    {
        IVisionNodeExecutor executor = type switch { "region.filter" => new RegionFilterNode(), "region.fillHoles" => new RegionFillHolesNode(), _ => new RegionConnectedComponentsNode() };
        var result = await Run(executor, Input(new VisionRegion(32, 32, [])));
        Assert.Empty(Region(result).Components); Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0d, result.Outputs["area"].Require<double>("area"));
        Assert.Equal(0, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0));
    }

    [Fact]
    public async Task DomainPreservesPixelsAndDarkThresholdDoesNotIncludeOutside()
    {
        using var source = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0)));
        var scoped = await Run(new ReduceDomainNode(), new() { ["image"] = VisionValue.Image(source), ["region"] = VisionValue.Region(Rect(8, 8, 8, 8)) });
        var domain = scoped.Outputs["domainImage"].Require<VisionDomainImage>("domainImage");
        source.Mat.SetTo(Scalar.All(255)); source.Dispose();
        Assert.Equal(0, domain.Image.Mat.At<byte>(0, 0));
        var threshold = await Run(new RegionFromThresholdNode(), new() { ["domainImage"] = scoped.Outputs["domainImage"] }, """{"minGray":0,"maxGray":0}""");
        Assert.Equal(64d, threshold.Outputs["area"].Require<double>("area"));
        Assert.False(VisionNodeDispatcher.AreCompatible(VisionDataType.DomainImage, VisionDataType.Image));
        var second = await Run(new ReduceDomainNode(), new() { ["domainImage"] = scoped.Outputs["domainImage"], ["region"] = VisionValue.Region(Rect(12, 12, 8, 8)) });
        Assert.Equal(16d, second.Outputs["area"].Require<double>("area"));
    }

    [Fact]
    public async Task EmptyDomainNeverTurnsFullFrameIntoForeground()
    {
        using var source = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0)));
        var scoped = await Run(new ReduceDomainNode(), new() { ["image"] = VisionValue.Image(source), ["region"] = VisionValue.Region(new(32, 32, [])) });
        var result = await Run(new RegionFromThresholdNode(), new() { ["domainImage"] = scoped.Outputs["domainImage"] }, """{"minGray":0,"maxGray":255}""");
        Assert.Empty(Region(result).Components);
    }

    [Fact]
    public async Task DimensionMismatchAndInvalidRangesFailClearly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionBooleanNode(), new() { ["a"] = VisionValue.Region(Rect(0, 0, 2, 2)), ["b"] = VisionValue.Region(Rect(0, 0, 2, 2, 64, 64)) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFilterNode(), Input(Rect(0, 0, 2, 2)), """{"min":8,"max":4}"""));
        using var image = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromThresholdNode(), new() { ["image"] = VisionValue.Image(image) }, """{"minGray":200,"maxGray":100}"""));
    }

    [Fact]
    public void RegionIsImmutableAndRoundTripsThroughWorkerJson()
    {
        var runs = new[] { new VisionRegionRun(3, 4, 7) };
        var original = new VisionRegion(32, 32, [new(runs)]); runs[0] = new(0, 0, 0);
        var decoded = PluginWorkerValueCodec.Decode(PluginWorkerValueCodec.Encode(VisionValue.Region(original))).Require<VisionRegion>("region");
        Assert.Equal(new VisionRegionRun(3, 4, 7), decoded.Components[0].Runs[0]);
        Assert.Throws<ArgumentException>(() => new VisionRegion(32, 32, [new([new(33, 0, 1)])]));
        Assert.Throws<ArgumentException>(() => new VisionRegionComponent([new(2, 4, 7), new(2, 6, 9)]));
    }

    [Fact]
    public async Task CancellationStopsBeforeNativeProcessing()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(new RegionConnectedComponentsNode(), Input(Rect(0, 0, 2, 2)), token: cancellation.Token));
    }

    [Theory]
    [InlineData("area", 16d, 16d)]
    [InlineData("width", 4d, 4d)]
    [InlineData("height", 4d, 4d)]
    [InlineData("circularity", 0d, 1d)]
    [InlineData("rectangularity", 0d, 1d)]
    [InlineData("anisometry", 1d, 2d)]
    public async Task EveryExposedNativeFeatureWorks(string feature, double min, double max)
    {
        var result = await Run(new RegionFilterNode(), Input(Rect(4, 4, 4, 4)), JsonSerializer.Serialize(new { feature, min, max }));
        Assert.Single(Region(result).Components);
    }
}
