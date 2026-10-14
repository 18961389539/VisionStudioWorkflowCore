using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class DefectNodeTests : IDisposable
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
    private static VisionImage Image(byte value = 100, int width = 32, int height = 32) => VisionImage.Own(new Mat(height, width, MatType.CV_8UC1, Scalar.All(value)));
    private static VisionRegion Rect(int x, int y, int width, int height) => new(32, 32, [new VisionRegionComponent(Enumerable.Range(y, height).Select(row => new VisionRegionRun(row, x, x + width - 1)).ToArray())]);
    private static void Patch(VisionImage image, Rect rectangle, byte value) { using var patch = new Mat(image.Mat, rectangle); patch.SetTo(Scalar.All(value)); }
    private static Dictionary<string, VisionValue> Pair(VisionImage image, VisionImage reference) => new() { ["image"] = VisionValue.Image(image), ["reference"] = VisionValue.Image(reference) };

    [Theory]
    [InlineData("byte")]
    [InlineData("real")]
    public async Task FlatFieldRemovesSpatialGainAndLeavesInputsOwned(string outputType)
    {
        using var source = Image(); using var dark = Image(10); using var bright = Image();
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
        {
            var gain = 20 + 2 * x;
            bright.Mat.Set(y, x, (byte)(10 + gain)); source.Mat.Set(y, x, (byte)(10 + gain / 2));
        }
        var result = await Run(new FlatFieldCorrectNode(), new() { ["image"] = VisionValue.Image(source), ["dark"] = VisionValue.Image(dark), ["bright"] = VisionValue.Image(bright) }, JsonSerializer.Serialize(new { outputType, targetMean = 100 }));
        var output = result.Outputs["image"].Require<VisionImage>("image");
        if (outputType == "byte") { Assert.Equal(50, output.Mat.At<byte>(0, 0)); Assert.Equal(50, output.Mat.At<byte>(20, 31)); }
        else { Assert.InRange(output.Mat.At<float>(0, 0), 49.99f, 50.01f); Assert.InRange(output.Mat.At<float>(20, 31), 49.99f, 50.01f); }
        Assert.Equal(20, source.Mat.At<byte>(0, 0)); Assert.Equal(30, bright.Mat.At<byte>(0, 0));
    }

    [Fact]
    public async Task FlatFieldAutoTargetAndInvalidReferenceAreExplicit()
    {
        using var source = Image(60); using var dark = Image(10); using var bright = Image(110);
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source), ["dark"] = VisionValue.Image(dark), ["bright"] = VisionValue.Image(bright) };
        var result = await Run(new FlatFieldCorrectNode(), inputs);
        Assert.Equal(100d, result.Summary["targetMean"]); Assert.Equal(50, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0));
        bright.Mat.Set(0, 0, (byte)10);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new FlatFieldCorrectNode(), inputs));
        Assert.Contains("gain", error.Message); Assert.Equal(60, source.Mat.At<byte>(0, 0));
    }

    [Fact]
    public async Task FlatFieldRealOutputAvoidsByteClipping()
    {
        using var source = Image(200); using var dark = Image(0); using var bright = Image(10);
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source), ["dark"] = VisionValue.Image(dark), ["bright"] = VisionValue.Image(bright) };
        var real = await Run(new FlatFieldCorrectNode(), inputs, """{"targetMean":100,"outputType":"real"}""");
        var clipped = await Run(new FlatFieldCorrectNode(), inputs, """{"targetMean":100,"outputType":"byte"}""");
        Assert.InRange(real.Outputs["image"].Require<VisionImage>("image").Mat.At<float>(0, 0), 1999f, 2001f);
        Assert.Equal(255, clipped.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0));
    }

    [Theory]
    [InlineData("bright", 1d)]
    [InlineData("dark", 1d)]
    [InlineData("both", 2d)]
    public async Task DynamicThresholdSelectsBrightAndDarkDeviations(string polarity, double area)
    {
        using var source = Image(); using var background = Image(); source.Mat.Set(8, 8, (byte)120); source.Mat.Set(20, 20, (byte)80);
        var result = await Run(new DynamicThresholdNode(), new() { ["image"] = VisionValue.Image(source), ["background"] = VisionValue.Image(background) }, JsonSerializer.Serialize(new { polarity, offset = 10 }));
        Assert.Equal(area, result.Outputs["area"].Require<double>("area")); Assert.Equal("input", result.Summary["backgroundSource"]);
    }

    [Fact]
    public async Task DynamicLocalMeanAndScopedDomainRespectInspectionArea()
    {
        using var source = Image(); source.Mat.Set(8, 8, (byte)150); source.Mat.Set(20, 20, (byte)50);
        using var scoped = new VisionDomainImage(source.CloneOwned(), Rect(5, 5, 8, 8));
        var result = await Run(new DynamicThresholdNode(), new() { ["domainImage"] = VisionValue.DomainImage(scoped) }, """{"polarity":"both","offset":20,"windowSize":7}""");
        Assert.Equal(1d, result.Outputs["area"].Require<double>("area")); Assert.Equal("localMean", result.Summary["backgroundSource"]);
        Assert.Equal(0, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(20, 20));
    }

    [Theory]
    [InlineData("absolute", 20f)]
    [InlineData("signed", -20f)]
    public async Task DifferenceKeepsFloatRangeAndNegativeChanges(string mode, float expected)
    {
        using var source = Image(80); using var reference = Image(100);
        var result = await Run(new ImageDifferenceNode(), Pair(source, reference), JsonSerializer.Serialize(new { mode }));
        var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(MatType.CV_32FC1, output.Mat.Type()); Assert.Equal(expected, output.Mat.At<float>(0, 0));
        source.Dispose(); reference.Dispose(); Assert.Equal(expected, output.Mat.At<float>(20, 20));
    }

    [Theory]
    [InlineData("bright", 1L, 16d)]
    [InlineData("dark", 1L, 16d)]
    [InlineData("both", 2L, 32d)]
    public async Task ReferenceCompareDetectsAddedAndMissingMaterialAndFiltersSpecks(string polarity, long count, double area)
    {
        using var source = Image(); using var reference = Image();
        Patch(source, new Rect(5, 5, 4, 4), 120); Patch(source, new Rect(20, 20, 4, 4), 80); source.Mat.Set(1, 1, (byte)150);
        var result = await Run(new ReferenceCompareNode(), Pair(source, reference), JsonSerializer.Serialize(new { polarity, threshold = 20, minArea = 2 }));
        Assert.Equal(count, result.Outputs["count"].Require<long>("count")); Assert.Equal(area, result.Outputs["area"].Require<double>("area"));
        Assert.Equal(0, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(1, 1));
    }

    [Fact]
    public async Task IdenticalReferenceAndEmptyDomainAreNormalNoDefectResults()
    {
        using var source = Image(); using var reference = Image();
        var result = await Run(new ReferenceCompareNode(), Pair(source, reference));
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        using var scoped = new VisionDomainImage(source.CloneOwned(), new(32, 32, []));
        var empty = await Run(new ReferenceCompareNode(), new() { ["domainImage"] = VisionValue.DomainImage(scoped), ["reference"] = VisionValue.Image(reference) });
        Assert.False(empty.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task MinimumDefectAreaAboveFrameAreaReturnsNoDefects()
    {
        using var source = Image(200); using var reference = Image(0);
        var result = await Run(new ReferenceCompareNode(), Pair(source, reference), """{"threshold":15,"minArea":100000}""");
        Assert.False(result.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task IntensityUsesUnionPixelsAndPopulationDeviation()
    {
        using var image = Image(0); image.Mat.Set(5, 5, (byte)10); image.Mat.Set(5, 6, (byte)30);
        var region = new VisionRegion(32, 32, [new([new(5, 5, 6)]), new([new(5, 6, 6)])]);
        var result = await Run(new RegionIntensityNode(), new() { ["image"] = VisionValue.Image(image), ["region"] = VisionValue.Region(region) });
        Assert.Equal(2d, result.Outputs["area"].Require<double>("area"));
        Assert.Equal(20d, result.Outputs["mean"].Require<double>("mean")); Assert.Equal(10d, result.Outputs["stdDev"].Require<double>("stdDev"));
        Assert.Equal(10d, result.Outputs["min"].Require<double>("min")); Assert.Equal(30d, result.Outputs["max"].Require<double>("max"));
    }

    [Fact]
    public async Task EmptyIntensityOmitsStatisticsAndIntersectsExistingDomain()
    {
        using var source = Image(80); using var scoped = new VisionDomainImage(source.CloneOwned(), Rect(0, 0, 4, 4));
        var inputs = new Dictionary<string, VisionValue> { ["domainImage"] = VisionValue.DomainImage(scoped), ["region"] = VisionValue.Region(Rect(20, 20, 4, 4)) };
        var empty = await Run(new RegionIntensityNode(), inputs);
        Assert.False(empty.Outputs["found"].Require<bool>("found")); Assert.False(empty.Outputs.ContainsKey("mean"));
        inputs["region"] = VisionValue.Region(Rect(2, 2, 4, 4));
        var overlap = await Run(new RegionIntensityNode(), inputs);
        Assert.Equal(4d, overlap.Outputs["area"].Require<double>("area")); Assert.Equal(80d, overlap.Outputs["mean"].Require<double>("mean"));
    }

    [Fact]
    public async Task InvalidDimensionsThresholdsAndNanAreRejected()
    {
        using var source = Image(); using var smaller = Image(width: 16); using var reference = Image();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImageDifferenceNode(), Pair(source, smaller)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ReferenceCompareNode(), Pair(source, reference), """{"threshold":0}"""));
        using var nan = VisionImage.Own(new Mat(32, 32, MatType.CV_32FC1, Scalar.All(0))); nan.Mat.Set(0, 0, float.NaN);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImageDifferenceNode(), Pair(nan, reference)));
    }

    [Theory]
    [InlineData("image.flatFieldCorrect")]
    [InlineData("image.dynamicThreshold")]
    [InlineData("image.difference")]
    [InlineData("defect.referenceCompare")]
    [InlineData("measure.regionIntensity")]
    public async Task CancellationPrecedesProcessing(string type)
    {
        IVisionNodeExecutor executor = type switch { "image.flatFieldCorrect" => new FlatFieldCorrectNode(), "image.dynamicThreshold" => new DynamicThresholdNode(), "image.difference" => new ImageDifferenceNode(), "defect.referenceCompare" => new ReferenceCompareNode(), _ => new RegionIntensityNode() };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(executor, new(), token: cancellation.Token));
    }
}
