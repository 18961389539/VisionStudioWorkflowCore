using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class CaliperWidthTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task MeasuresSubpixelBrightAndDarkWidthsInEitherDirection(bool dark, bool reverse, bool sixteenBit)
    {
        const double left = 30.25, right = 63.75;
        using var image = MakeStrip(left, right, dark, sixteenBit);
        var result = await Run(image, new() { ["polarity"] = dark ? "dark" : "bright",
            ["startX"] = reverse ? 100 : 5, ["endX"] = reverse ? 5 : 100 });
        using var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.True(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.InRange(result.Outputs["width"].Require<double>("width"), right - left - .15, right - left + .15);
        var first = result.Outputs["point1"].Require<VisionPoint2D>("point1");
        var second = result.Outputs["point2"].Require<VisionPoint2D>("point2");
        Assert.InRange(first.X, (reverse ? right : left) - .15, (reverse ? right : left) + .15);
        Assert.Equal(reverse, first.X > second.X);
        Assert.InRange(result.Outputs["center"].Require<VisionPoint2D>("center").X, 46.9, 47.1);
        Assert.Equal("px", result.Summary["unit"]);
        Assert.Equal(sixteenBit ? MatType.CV_16UC1 : MatType.CV_8UC1, output.Mat.Type());
        Assert.Contains(result.Overlays ?? [], overlay => overlay.Id == "caliper-width-label");
    }

    [Theory]
    [InlineData("firstfromstart", 20, 20, 24.5)]
    [InlineData("firstfromend", 20, 30, 79.5)]
    [InlineData("strongest", 20, 30, 79.5)]
    [InlineData("closestwidth", 21, 20, 24.5)]
    public async Task SelectsAmongMultiplePairsAfterWidthFiltering(string direction, double nominal, double width, double center)
    {
        using var mat = new Mat(64, 128, MatType.CV_8UC1, Scalar.All(20));
        using (var first = new Mat(mat, new Rect(15, 0, 20, 64))) first.SetTo(Scalar.All(120));
        using (var second = new Mat(mat, new Rect(65, 0, 30, 64))) second.SetTo(Scalar.All(220));
        using var image = VisionImage.Own(mat.Clone());
        var result = await Run(image, new() { ["direction"] = direction, ["nominalWidth"] = nominal });
        DisposeImage(result);
        Assert.True(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.Equal(2L, result.Outputs["pairCount"].Require<long>("pairCount"));
        Assert.InRange(result.Outputs["width"].Require<double>("width"), width - .1, width + .1);
        Assert.InRange(result.Outputs["center"].Require<VisionPoint2D>("center").X, center - .1, center + .1);

        var filtered = await Run(image, new() { ["minWidth"] = 18, ["maxWidth"] = 22, ["direction"] = direction, ["nominalWidth"] = nominal });
        DisposeImage(filtered);
        Assert.True(Found(filtered));
        Assert.Equal(1L, filtered.Outputs["pairCount"].Require<long>("pairCount"));
        Assert.InRange(filtered.Outputs["width"].Require<double>("width"), 19.9, 20.1);
        Assert.Equal(1, filtered.Summary["rejectedByWidth"]);
    }

    [Fact]
    public async Task AnyPolarityCanSelectTheDarkGapBetweenBrightStrips()
    {
        using var mat = new Mat(64, 128, MatType.CV_8UC1, Scalar.All(20));
        using (var first = new Mat(mat, new Rect(15, 0, 20, 64))) first.SetTo(Scalar.All(220));
        using (var second = new Mat(mat, new Rect(65, 0, 15, 64))) second.SetTo(Scalar.All(220));
        using var image = VisionImage.Own(mat.Clone());
        var result = await Run(image, new() { ["polarity"] = "any", ["minWidth"] = 28, ["maxWidth"] = 32 });
        DisposeImage(result);
        Assert.True(Found(result));
        Assert.InRange(result.Outputs["width"].Require<double>("width"), 29.9, 30.1);
        Assert.True(Convert.ToDouble(result.Summary["firstAmplitude"]) < 0);
        Assert.True(Convert.ToDouble(result.Summary["secondAmplitude"]) > 0);
    }

    [Fact]
    public async Task RoiHoleCannotBridgeTwoIndividuallyValidEdges()
    {
        using var image = MakeStrip(30, 70);
        var roi = new VisionRoi(VisionRoiType.Brush, Strokes:
        [new(VisionRoiStrokeMode.Add, 200, [new(64, 32)]),
            new(VisionRoiStrokeMode.Erase, 8, [new(50, 0), new(50, 63)])]);
        var result = await Run(image, inputs: new() { ["roi"] = VisionValue.Roi(roi) });
        DisposeImage(result);
        AssertNoWidth(result, "no-edge-pair-above-threshold");
    }

    [Fact]
    public async Task ConnectedLineAndRigidRotationOverrideStaleEndpointParameters()
    {
        using var mat = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(20));
        using (var strip = new Mat(mat, new Rect(0, 40, 128, 25))) strip.SetTo(Scalar.All(220));
        using var image = VisionImage.Own(mat.Clone());
        var result = await Run(image, new() { ["startX"] = "stale", ["endX"] = "stale" }, new()
        {
            ["searchLine"] = VisionValue.Line(new(new(5, 0), new(100, 0))),
            ["transform"] = VisionValue.Transform(new VisionTransform2D("ReferenceImage", "CurrentImage", "px", 0, -1, 64, 1, 0, 0, 0, 0, 1))
        });
        DisposeImage(result);
        Assert.True(Found(result));
        Assert.InRange(result.Outputs["width"].Require<double>("width"), 24.9, 25.1);
        var center = result.Outputs["center"].Require<VisionPoint2D>("center");
        Assert.Equal(64, center.X, 6);
        Assert.InRange(center.Y, 51.9, 52.1);
    }

    [Fact]
    public async Task ClipsSearchLineWithoutChangingMeasurementCoordinates()
    {
        using var image = MakeStrip(30, 70);
        var result = await Run(image, new() { ["startX"] = -100, ["endX"] = 250 });
        DisposeImage(result);
        Assert.True(Found(result));
        Assert.True(Convert.ToBoolean(result.Summary["lineClampedToImage"]));
        Assert.InRange(result.Outputs["point1"].Require<VisionPoint2D>("point1").X, 29.8, 30.2);
        Assert.InRange(result.Outputs["width"].Require<double>("width"), 39.8, 40.2);
    }

    [Theory]
    [InlineData("{}", "no-edge-pair-above-threshold")]
    [InlineData("{\"minWidth\":60}", "no-pair-within-width-range")]
    [InlineData("{\"startY\":100,\"endY\":100}", "search-line-outside-image")]
    [InlineData("{\"startX\":20,\"endX\":25}", "profile-has-insufficient-contiguous-samples")]
    public async Task MissingPairsOmitAllMeasurementValues(string json, string reason)
    {
        using var image = MakeStrip(30, 70);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        if (json == "{}") parameters["polarity"] = "dark";
        var result = await Run(image, parameters);
        DisposeImage(result);
        AssertNoWidth(result, reason);
    }

    [Theory]
    [InlineData("{\"polarity\":\"invalid\"}")]
    [InlineData("{\"direction\":\"invalid\"}")]
    [InlineData("{\"minWidth\":30,\"maxWidth\":20}")]
    [InlineData("{\"minWidth\":-1}")]
    [InlineData("{\"direction\":\"closestwidth\",\"nominalWidth\":0}")]
    [InlineData("{\"edgeThreshold\":256}")]
    public async Task InvalidSettingsFailDeterministically(string json)
    {
        using var image = MakeStrip(30, 70);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(image,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!));
    }

    [Fact]
    public async Task HonorsCancellationBeforeAllocatingOutput()
    {
        using var image = MakeStrip(30, 70);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CaliperWidthNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) }),
            Node(new()), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ShippedTemplateMeasuresTheSyntheticRectangleAtNominalWidth()
    {
        var generated = await new SyntheticImageNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            new("source", "image.synthetic", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                "{\"width\":640,\"height\":480,\"centerX\":320,\"centerY\":240,\"radius\":80}")), default);
        using var image = generated.Outputs["image"].Require<VisionImage>("image");
        var result = await Run(image, new() { ["startX"] = 10, ["startY"] = 100, ["endX"] = 220, ["endY"] = 100,
            ["sampleWidth"] = 7, ["edgeThreshold"] = 8, ["minWidth"] = 100, ["maxWidth"] = 130,
            ["direction"] = "closestwidth", ["nominalWidth"] = 115 });
        DisposeImage(result);
        Assert.True(Found(result));
        Assert.InRange(result.Outputs["width"].Require<double>("width"), 114, 116);
    }

    [Theory]
    [InlineData("{\"minWidth\":30,\"maxWidth\":20}", "maxWidth")]
    [InlineData("{\"direction\":\"closestwidth\",\"nominalWidth\":0}", "nominalWidth")]
    [InlineData("{\"startX\":5,\"endX\":5}", "search line")]
    public void CompilerRejectsInvalidMeasurementSettings(string json, string message)
    {
        using var registry = Registry();
        var measurement = Node(JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(registry).Compile(Workflow(measurement)));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void CompilerAcceptsWidthWorkflowAndChecksCropTransformFrames()
    {
        using var registry = Registry();
        var measurement = Node(new());
        Assert.Contains("width", new VisionWorkflowCompiler(registry).Compile(Workflow(measurement)).OrderedNodeIds);
        var crop = new NodeDefinition("crop", "image.crop", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"x\":10,\"y\":10,\"width\":100,\"height\":50}"));
        var workflow = Workflow(measurement);
        workflow = workflow with { Nodes = [.. workflow.Nodes, crop], Edges = [.. workflow.Edges,
            new("crop-exec", "source", "next", "crop", "exec"),
            new("crop-image", "source", "image", "crop", "image"),
            new("fixture", "crop", "transform", "width", "transform")] };
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(registry).Compile(workflow));
        Assert.Contains("frame", error.Message);
    }

    private static VisionNodeRegistry Registry()
    {
        var registry = new VisionNodeRegistry();
        foreach (var executor in new IVisionNodeExecutor[] { new SyntheticImageNode(), new CaliperWidthNode(), new ImageCropNode() })
            registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "test");
        return registry;
    }

    private static WorkflowDefinition Workflow(NodeDefinition measurement) => new("width-test", "Width test",
        [new("source", "image.synthetic", null, null, null), measurement],
        [new("exec", "source", "next", "width", "exec"), new("image", "source", "image", "width", "image")]);

    private static void AssertNoWidth(NodeExecutorResult result, string reason)
    {
        Assert.False(Found(result));
        Assert.Equal(reason, result.Summary["reason"]);
        foreach (var key in new[] { "width", "point1", "point2", "center", "strength" }) Assert.False(result.Outputs.ContainsKey(key), key);
    }

    private static bool Found(NodeExecutorResult result) => result.Outputs["found"].Require<bool>("found");
    private static void DisposeImage(NodeExecutorResult result) => result.Outputs["image"].Require<VisionImage>("image").Dispose();

    private static VisionImage MakeStrip(double left, double right, bool dark = false, bool sixteenBit = false)
    {
        var mat = new Mat(64, 128, sixteenBit ? MatType.CV_16UC1 : MatType.CV_8UC1);
        var columns = mat.Cols;
        var rows = mat.Rows;
        for (var x = 0; x < columns; x++)
        {
            var fraction = 1 / (1 + Math.Exp(-(x - left) * 2)) - 1 / (1 + Math.Exp(-(x - right) * 2));
            var value = 20 + 200 * (dark ? 1 - fraction : fraction);
            for (var y = 0; y < rows; y++)
                if (sixteenBit) mat.Set(y, x, (ushort)Math.Round(value * 257));
                else mat.Set(y, x, (byte)Math.Round(value));
        }
        return VisionImage.Own(mat);
    }

    private static async Task<NodeExecutorResult> Run(VisionImage image, Dictionary<string, object?>? parameters = null,
        Dictionary<string, VisionValue>? inputs = null)
    {
        inputs ??= new();
        inputs["image"] = VisionValue.Image(image);
        return await new CaliperWidthNode().ExecuteAsync(new NodeExecutionContext(inputs), Node(parameters ?? new()), CancellationToken.None);
    }

    private static NodeDefinition Node(Dictionary<string, object?> parameters)
    {
        var settings = new Dictionary<string, object?> { ["startX"] = 5, ["startY"] = 32, ["endX"] = 120, ["endY"] = 32, ["sampleWidth"] = 1 };
        foreach (var (key, value) in parameters) settings[key] = value;
        return new("width", "measure.caliperWidth", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(settings)));
    }
}
