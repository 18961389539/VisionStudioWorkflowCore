using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class ColorTextNodeTests : IDisposable
{
    private readonly List<IDisposable> _outputs = [];
    public void Dispose() { foreach (var output in _outputs) output.Dispose(); }
    private async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, object? parameters = null, CancellationToken token = default)
    {
        var node = new NodeDefinition("test", executor.Type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters ?? new { })));
        var result = await executor.ExecuteAsync(new(inputs), node, token);
        _outputs.AddRange(result.Outputs.Values.Select(v => v.Value).OfType<IDisposable>());
        return result;
    }
    private static Dictionary<string, VisionValue> Image(VisionImage image) => new() { ["image"] = VisionValue.Image(image) };
    private static Dictionary<string, VisionValue> Text(string text) => new() { ["text"] = VisionValue.String(text) };
    private static VisionRegion Rect(int x, int y, int w, int h, int width = 16, int height = 16) => new(width, height,
        [new(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);
    private static float Pixel(NodeExecutorResult result, string port) => result.Outputs[port].Require<VisionImage>(port).Mat.At<float>(0, 0);

    [Theory]
    [InlineData(255, 0, 0, 0)] [InlineData(0, 255, 0, 120)] [InlineData(0, 0, 255, 240)]
    public async Task HsvUsesDegreesAndNormalizedSaturationValue(int r, int g, int b, double hue)
    {
        using var source = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC3, new Scalar(b, g, r)));
        var result = await Run(new ImageColorConvertNode(), Image(source), new { space = "hsv" });
        Assert.InRange(Math.Abs(Pixel(result, "channel1") - hue), 0, .01);
        Assert.InRange(Pixel(result, "channel2"), .999, 1.001); Assert.InRange(Pixel(result, "channel3"), .999, 1.001);
        source.Mat.SetTo(Scalar.All(0)); Assert.True(Pixel(result, "channel3") > .99);
    }

    [Theory]
    [InlineData(255, 0, 0, 53.2408, 80.0925, 67.2032)]
    [InlineData(0, 255, 0, 87.7347, -86.1827, 83.1793)]
    [InlineData(0, 0, 255, 32.2970, 79.1875, -107.8602)]
    [InlineData(255, 255, 255, 100, 0, 0)]
    [InlineData(128, 128, 128, 53.5850, 0, 0)]
    [InlineData(10, 10, 10, 2.7417, 0, 0)]
    [InlineData(11, 11, 11, 3.0229, 0, 0)]
    public async Task LabMatchesIndependentSrgbD65Swatches(int r, int g, int b, double l, double a, double labB)
    {
        using var source = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC3, new Scalar(b, g, r)));
        var result = await Run(new ImageColorConvertNode(), Image(source), new { space = "lab" });
        Assert.InRange(Math.Abs(Pixel(result, "channel1") - l), 0, .08);
        Assert.InRange(Math.Abs(Pixel(result, "channel2") - a), 0, .08);
        Assert.InRange(Math.Abs(Pixel(result, "channel3") - labB), 0, .08);
    }

    [Fact]
    public async Task RgbHandlesBgraOrderReplicatesGrayAndRejectsFloatInput()
    {
        using var bgra = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC4, new Scalar(10, 100, 200, 30)));
        var result = await Run(new ImageColorConvertNode(), Image(bgra), new { space = "rgb" });
        Assert.InRange(Pixel(result, "channel1"), .784, .785); Assert.InRange(Pixel(result, "channel3"), .039, .040);
        using var gray = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC1, Scalar.All(128)));
        result = await Run(new ImageColorConvertNode(), Image(gray), new { space = "rgb" });
        Assert.Equal(Pixel(result, "channel1"), Pixel(result, "channel2")); Assert.Equal(Pixel(result, "channel2"), Pixel(result, "channel3"));
        using var floating = VisionImage.Own(new Mat(8, 8, MatType.CV_32FC1, Scalar.All(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImageColorConvertNode(), Image(floating)));
    }

    [Fact]
    public async Task HueWrapSelectsBothSidesOfRedAndExcludesAchromaticPixelsBySaturation()
    {
        using var image = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC3, Scalar.All(128)));
        using (var left = new Mat(image.Mat, new Rect(0, 0, 4, 16))) left.SetTo(new Scalar(10, 0, 255));
        using (var right = new Mat(image.Mat, new Rect(12, 0, 4, 16))) right.SetTo(new Scalar(0, 10, 255));
        var result = await Run(new RegionFromColorRangeNode(), Image(image), new { space = "hsv", min1 = 350, max1 = 10, min2 = .8, max2 = 1, min3 = .8, max3 = 1 });
        Assert.Equal(128d, result.Outputs["area"].Require<double>("area"));
        var input = Image(image); input["region"] = VisionValue.Region(Rect(0, 0, 8, 16));
        result = await Run(new RegionFromColorRangeNode(), input, new { space = "hsv", min1 = 350, max1 = 10, min2 = .8, max2 = 1, min3 = .8, max3 = 1 });
        Assert.Equal(64d, result.Outputs["area"].Require<double>("area"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromColorRangeNode(), Image(image), new { space = "rgb", min1 = .9, max1 = .1 }));
    }

    [Fact]
    public async Task ColorDifferenceUsesPixelwiseCie76AndConstantOrImageReference()
    {
        using var source = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC3, new Scalar(0, 0, 255)));
        var result = await Run(new ColorDifferenceNode(), Image(source), new { referenceRed = 0, referenceGreen = 255, referenceBlue = 0 });
        Assert.InRange(result.Outputs["deltaE"].Require<double>("deltaE"), 170.4, 170.8);
        var input = Image(source); input["reference"] = VisionValue.Image(source);
        result = await Run(new ColorDifferenceNode(), input);
        Assert.InRange(result.Outputs["deltaE"].Require<double>("deltaE"), 0, .0001);
        input["region"] = VisionValue.Region(new(16, 16, []));
        result = await Run(new ColorDifferenceNode(), input);
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("deltaE"));
        var threshold = await Run(new RegionFromThresholdNode(), new() { ["domainImage"] = result.Outputs["domainImage"] }, new { minGray = 0, maxGray = 500 });
        Assert.Equal(0d, threshold.Outputs["area"].Require<double>("area"));
    }

    [Fact]
    public async Task ColorDifferenceAveragesPixelDistancesRatherThanMeanColors()
    {
        using var source = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC1, Scalar.All(0)));
        using (var half = new Mat(source.Mat, new Rect(8, 0, 8, 16))) half.SetTo(Scalar.All(255));
        var result = await Run(new ColorDifferenceNode(), Image(source), new { referenceRed = 128, referenceGreen = 128, referenceBlue = 128 });
        Assert.InRange(result.Outputs["deltaE"].Require<double>("deltaE"), 49.99, 50.01);
        Assert.InRange(result.Outputs["maxDeltaE"].Require<double>("maxDeltaE"), 53.50, 53.67);
        Assert.InRange(result.Outputs["stdDev"].Require<double>("stdDev"), 3.5, 3.7);
        var threshold = await Run(new RegionFromThresholdNode(), new() { ["domainImage"] = result.Outputs["domainImage"] }, new { minGray = 50, maxGray = 100 });
        Assert.Equal(128d, threshold.Outputs["area"].Require<double>("area"));
    }

    [Fact]
    public async Task ColorRangeIntersectsScopedImageAndOptionalRegionWithoutCountingDomainExterior()
    {
        using var image = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC3, new Scalar(0, 0, 255)));
        var scoped = await Run(new ReduceDomainNode(), new() { ["image"] = VisionValue.Image(image), ["region"] = VisionValue.Region(Rect(0, 0, 8, 16)) });
        var selected = await Run(new RegionFromColorRangeNode(), new() { ["domainImage"] = scoped.Outputs["domainImage"], ["region"] = VisionValue.Region(Rect(4, 0, 8, 16)) }, new { space = "rgb", min1 = 1, max1 = 1, min2 = 0, max2 = 0, min3 = 0, max3 = 0 });
        Assert.Equal(64d, selected.Outputs["area"].Require<double>("area"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromColorRangeNode(), new() { ["image"] = VisionValue.Image(image), ["region"] = VisionValue.Region(Rect(0, 0, 4, 4, 32, 32)) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromColorRangeNode(), new() { ["image"] = VisionValue.Image(image), ["domainImage"] = scoped.Outputs["domainImage"] }));
        using var mismatch = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC3, Scalar.All(0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ColorDifferenceNode(), new() { ["image"] = VisionValue.Image(image), ["reference"] = VisionValue.Image(mismatch) }));
    }

    [Fact]
    public async Task PercentileContrastClipsOutliersToTheRequestedRange()
    {
        using var source = VisionImage.Own(new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0)));
        for (var i = 0; i < 100; i++) source.Mat.Set(i / 10, i % 10, (byte)(50 + i));
        source.Mat.Set(0, 0, (byte)0); source.Mat.Set(9, 9, (byte)255);
        var result = await Run(new ContrastNormalizeNode(), Image(source), new { tailPercent = 10, outputMin = 20, outputMax = 220 });
        Assert.True(result.Outputs["inputMin"].Require<double>("inputMin") > 0); Assert.True(result.Outputs["inputMax"].Require<double>("inputMax") < 255);
        var image = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(20, image.Mat.At<byte>(0, 0)); Assert.Equal(220, image.Mat.At<byte>(9, 9));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ContrastNormalizeNode(), Image(source), new { outputMin = 20.5 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new IlluminationNormalizeNode(), Image(source), new { windowSize = 4 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ImageSharpenNode(), Image(source), new { amount = -1 }));
    }

    [Fact]
    public async Task ContrastStretchUsesRequestedBoundsAndLeavesUniformImagesUnchanged()
    {
        using var source = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC1, Scalar.All(50)));
        using (var right = new Mat(source.Mat, new Rect(8, 0, 8, 16))) right.SetTo(Scalar.All(150));
        var result = await Run(new ContrastNormalizeNode(), Image(source), new { outputMin = 20, outputMax = 220 });
        var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(20, output.Mat.At<byte>(5, 5)); Assert.Equal(220, output.Mat.At<byte>(5, 12)); Assert.Equal(50, source.Mat.At<byte>(5, 5));
        source.Mat.SetTo(Scalar.All(80)); result = await Run(new ContrastNormalizeNode(), Image(source));
        Assert.Equal(80, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(5, 5));
    }

    [Fact]
    public async Task IlluminationNormalizationFlattensUniformFieldAndHandlesBlack()
    {
        using var source = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(64)));
        var result = await Run(new IlluminationNormalizeNode(), Image(source), new { windowSize = 11, targetMean = 128 });
        Assert.Equal(128, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(16, 16));
        source.Mat.SetTo(Scalar.All(0)); result = await Run(new IlluminationNormalizeNode(), Image(source));
        Assert.Equal(0, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(16, 16));
    }

    [Fact]
    public async Task SharpenMatchesUnsharpFormulaAndZeroAmountPassesThrough()
    {
        using var source = VisionImage.Own(new Mat(9, 9, MatType.CV_8UC1, Scalar.All(100))); source.Mat.Set(4, 4, (byte)180);
        var result = await Run(new ImageSharpenNode(), Image(source), new { windowSize = 3, amount = 1 });
        Assert.InRange(result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(4, 4), 250, 252);
        result = await Run(new ImageSharpenNode(), Image(source), new { amount = 0 });
        Assert.Equal(0, Cv2.Norm(source.Mat, result.Outputs["image"].Require<VisionImage>("image").Mat, NormTypes.L2));
    }

    [Theory]
    [InlineData("equals", "LOT-42", true)] [InlineData("contains", "OT-4", true)]
    [InlineData("startsWith", "LOT", true)] [InlineData("endsWith", "42", true)] [InlineData("equals", "lot-42", false)]
    public async Task TextComparisonSupportsConfiguredModes(string mode, string expected, bool pass)
    {
        var result = await Run(new TextMatchNode(), Text("  LOT-42 \r\n"), new { mode, expected });
        Assert.Equal(pass, result.Outputs["pass"].Require<bool>("pass"));
    }

    [Fact]
    public async Task TextComparisonUsesConnectedExpectedAndOrdinalCaseRules()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var input = Text("FILE"); input["expected"] = VisionValue.String("file");
            var result = await Run(new TextMatchNode(), input, new { expected = "wrong", ignoreCase = true });
            Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        var empty = await Run(new TextMatchNode(), Text(""), new { expected = "", mode = "contains" });
        Assert.False(empty.Outputs["pass"].Require<bool>("pass"));
    }

    [Fact]
    public async Task TextFallbackIsOverriddenByOcrAndRegexEmptyPolicyIsExplicit()
    {
        var result = await Run(new TextMatchNode(), new(), new { text = "LOT-42", expected = "LOT-42" });
        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        result = await Run(new TextMatchNode(), Text("OCR-17"), new { text = "LOT-42", expected = "LOT-42" });
        Assert.False(result.Outputs["pass"].Require<bool>("pass")); Assert.Equal("OCR-17", result.Outputs["text"].Require<string>("text"));
        result = await Run(new TextRegexNode(), Text(""), new { pattern = ".*" }); Assert.False(result.Outputs["pass"].Require<bool>("pass"));
        result = await Run(new TextRegexNode(), Text(""), new { pattern = ".*", allowEmpty = true }); Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TextMatchNode(), Text(new string('A', 65537)), new { expected = "A" }));
    }

    [Fact]
    public async Task RegexValidatesEntireCodeAndExposesNamedGroups()
    {
        var result = await Run(new TextRegexNode(), Text(" LOT-20261015-A42 "), new { pattern = @"LOT-(?<date>[0-9]{8})-(?<batch>[A-Z][0-9]{2})" });
        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        var groups = JsonSerializer.Deserialize<Dictionary<string, string>>(result.Outputs["groupsJson"].Require<string>("groupsJson"))!;
        Assert.Equal("20261015", groups["date"]); Assert.Equal("A42", groups["batch"]);
        result = await Run(new TextRegexNode(), Text("prefix ABC123 suffix"), new { pattern = "[A-Z]{3}[0-9]{3}" });
        Assert.False(result.Outputs["pass"].Require<bool>("pass"));
        result = await Run(new TextRegexNode(), Text("prefix ABC123 suffix"), new { pattern = "[A-Z]{3}[0-9]{3}", fullMatch = false });
        Assert.Equal("ABC123", result.Outputs["matchedText"].Require<string>("matchedText"));
    }

    [Fact]
    public async Task RegexFullMatchPreservesAlternationAndExtendedModeTrailingComments()
    {
        var result = await Run(new TextRegexNode(), Text("AB"), new { pattern = "A|AB" });
        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        result = await Run(new TextRegexNode(), Text("LOT-42"), new { pattern = "(?x) LOT - [0-9]+ # batch code" });
        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        result = await Run(new TextRegexNode(), Text("ABC\n"), new { pattern = "ABC", trim = false });
        Assert.False(result.Outputs["pass"].Require<bool>("pass"));
    }

    [Fact]
    public async Task RegexRejectsBadPatternsAndTimesOutPathologicalMatching()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TextRegexNode(), Text("ABC"), new { pattern = "[" }));
        var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TextRegexNode(), Text(new string('a', 10000) + "!"), new { pattern = "(a+)+$", timeoutMs = 1 }));
        Assert.Contains("exceeded", timeout.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TextRegexNode(), Text(new string('a', 65537))));
    }

    [Fact]
    public async Task AllEightNodesHonorPreCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        IVisionNodeExecutor[] nodes = [new ImageColorConvertNode(), new RegionFromColorRangeNode(), new ColorDifferenceNode(), new ContrastNormalizeNode(), new IlluminationNormalizeNode(), new ImageSharpenNode(), new TextMatchNode(), new TextRegexNode()];
        foreach (var node in nodes) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(node, new(), token: cancellation.Token));
    }
}
