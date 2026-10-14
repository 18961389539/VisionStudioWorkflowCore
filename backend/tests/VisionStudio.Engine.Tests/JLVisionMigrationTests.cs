using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

[CollectionDefinition("Native algorithm migration", DisableParallelization = true)]
public sealed class NativeAlgorithmMigrationCollection;

[Collection("Native algorithm migration")]
public sealed class JLVisionMigrationTests
{
    [Fact]
    public void RuntimeChecksActualNativeOperatorsAndInferenceDependency()
    {
        var diagnostics = VisionAlgorithmRuntime.Initialize();
        Assert.Equal("JLVision", diagnostics.TraditionalBackend);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.NativeVersion));
        Assert.Equal("ONNX Runtime", diagnostics.InferenceBackend);
    }

    [Fact]
    public async Task ColorBridgeKeepsBgrOrderAndDetachesInputLifetime()
    {
        using var pixels = new Mat(8, 10, MatType.CV_8UC4, new Scalar(50, 100, 200, 0));
        using var input = VisionImage.Own(pixels.Clone());
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) };
        var gray = await new GrayscaleNode().ExecuteAsync(new(inputs), Node("image.grayscale", "{}"), default);
        using var grayImage = (VisionImage)gray.Outputs["image"].Value!;
        Assert.InRange(grayImage.Mat.At<byte>(2, 2), 123, 125);
        var red = await new ChannelExtractNode().ExecuteAsync(new(inputs), Node("image.channel", """{"channel":"red"}"""), default);
        using var redImage = (VisionImage)red.Outputs["image"].Value!;
        Assert.Equal(200, redImage.Mat.At<byte>(2, 2));
        input.Dispose();
        Assert.Equal(200, redImage.Mat.At<byte>(2, 2));
        Assert.Equal(50, pixels.At<Vec4b>(2, 2).Item0);
    }

    [Fact]
    public async Task MorphologyRetainsGrayLevelsRatherThanBinarizing()
    {
        using var pixels = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(40)); pixels.Set(8, 8, (byte)160);
        using var input = VisionImage.Own(pixels.Clone());
        var result = await new MorphologyNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) }), Node("image.morphology", """{"operation":"dilate","kernelSize":3}"""), default);
        using var output = (VisionImage)result.Outputs["image"].Value!;
        Assert.Equal(160, output.Mat.At<byte>(8, 7)); Assert.Equal(40, output.Mat.At<byte>(2, 2)); Assert.Equal(40, input.Mat.At<byte>(8, 7));
    }

    [Fact]
    public async Task ExplicitGaussianSigmaUsesNativeFrequencyFilteringWithoutWraparound()
    {
        using var pixels = new Mat(48, 64, MatType.CV_8UC1, Scalar.All(0)); pixels.Set(24, 0, (byte)255);
        using var input = VisionImage.Own(pixels.Clone());
        var result = await new GaussianBlurNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) }), Node("image.blur.gaussian", """{"kernelSize":9,"sigmaX":2,"sigmaY":1}"""), default);
        using var output = (VisionImage)result.Outputs["image"].Value!;
        Assert.InRange(output.Mat.At<byte>(24, 0), 1, 254);
        Assert.Equal(0, output.Mat.At<byte>(24, 63)); Assert.Equal(255, input.Mat.At<byte>(24, 0));
    }

    [Fact]
    public async Task NativeMatcherReusesModelsAndInvalidatesChangedTemplatePixels()
    {
        VisionAlgorithmRuntime.ClearModelCache();
        using var template = new Mat(32, 32, MatType.CV_8UC1);
        var rng = new Random(1139); var bytes = new byte[1024]; rng.NextBytes(bytes); System.Runtime.InteropServices.Marshal.Copy(bytes, 0, template.Data, bytes.Length);
        using var scene = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(0)); using (var patch = new Mat(scene, new Rect(40, 40, 32, 32))) template.CopyTo(patch);
        var first = await Match(template, scene, "{}"); var second = await Match(template, scene, "{}");
        Assert.False(Convert.ToBoolean(first.Summary["modelCacheHit"])); Assert.True(Convert.ToBoolean(second.Summary["modelCacheHit"]));
        template.Set(0, 0, (byte)(template.At<byte>(0, 0) ^ 255));
        var changed = await Match(template, scene, "{}"); Assert.False(Convert.ToBoolean(changed.Summary["modelCacheHit"]));
        VisionAlgorithmRuntime.ClearModelCache();
    }

    [Fact]
    public async Task SsdModeChecksBrightnessRatherThanSilentlyUsingNccScore()
    {
        using var template = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(30)); Cv2.Rectangle(template, new Rect(8, 8, 16, 16), Scalar.All(80), -1);
        using var scene = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(0));
        using (var patch = new Mat(scene, new Rect(30, 30, 32, 32))) template.ConvertTo(patch, MatType.CV_8UC1, 1, 50);
        var ncc = await Match(template, scene, """{"method":"ccoeff-normed","minScore":0.1}""");
        var ssd = await Match(template, scene, """{"method":"sqdiff-normed","minScore":0.1}""");
        Assert.True(ncc.Outputs["found"].Require<bool>("found"));
        Assert.True(ncc.Outputs["score"].Require<double>("score") > .99);
        Assert.True(ssd.Outputs["score"].Require<double>("score") < .9);
    }

    [Theory]
    [InlineData("external", 1)]
    [InlineData("all", 3)]
    public async Task NestedIslandPreservesContourRetrievalSemantics(string mode, int expected)
    {
        using var pixels = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(pixels, new Rect(10, 10, 108, 108), Scalar.All(255), -1);
        Cv2.Rectangle(pixels, new Rect(25, 25, 78, 78), Scalar.All(0), -1);
        Cv2.Rectangle(pixels, new Rect(45, 45, 38, 38), Scalar.All(255), -1);
        using var input = VisionImage.Own(pixels.Clone());
        var result = await new ContourExtractNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) }),
            Node("contour.extract", JsonSerializer.Serialize(new { mode, binarize = "direct" })), default);
        using var output = (VisionImage)result.Outputs["image"].Value!;
        Assert.Equal(expected, result.Outputs["count"].Require<long>("count"));
    }

    private static async Task<NodeExecutorResult> Match(Mat template, Mat scene, string parameters)
    {
        using var image = VisionImage.Own(scene.Clone()); using var pattern = VisionImage.Own(template.Clone());
        return await new TemplateMatchNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image), ["template"] = VisionValue.Image(pattern) }), Node("match.template", parameters), default);
    }
    private static NodeDefinition Node(string type, string parameters) => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters));
}
