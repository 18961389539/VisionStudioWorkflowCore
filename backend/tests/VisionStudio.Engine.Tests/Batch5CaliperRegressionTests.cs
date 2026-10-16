using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class Batch5CaliperRegressionTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-0.7)]
    public async Task CaliperLine_FitsMeasuredDiagonalEdge(double slope)
    {
        using var mat = MakeEdgeImage(slope);
        using var input = VisionImage.Own(mat.Clone());
        using var registry = new VisionNodeRegistry();
        var executor = new CaliperLineNode();
        registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var node = Node(executor.Type, """{"startX":25,"startY":64,"endX":103,"endY":64,"edgeLength":58,"caliperCount":9,"sampleWidth":1,"minContrast":30}""");

        var result = await dispatcher.ExecuteAsync(node, Inputs(input), null, CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var fitted = result.Outputs["line"].Require<VisionLine2D>("line");
        var fittedSlope = (fitted.End.Y - fitted.Start.Y) / (fitted.End.X - fitted.Start.X);
        Assert.InRange(Math.Abs(fittedSlope - 1d / slope), 0, 0.12);
        Assert.True(Convert.ToInt32(result.Summary["sampleCount"]) >= 3);
        Assert.True(Convert.ToDouble(result.Summary["fitError"]) < 1.5);
        Assert.True(result.Outputs["image"].Require<VisionImage>("image").Mat.Rows > 0);
        Assert.True(input.Mat.Rows > 0);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_NoEdgeAndRoiClippedSpan_ReturnFoundFalseWithImage()
    {
        using var flat = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80));
        using var input = VisionImage.Own(flat.Clone());
        var executor = new CaliperLineNode();
        var none = await executor.ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node(executor.Type, """{"startX":20,"startY":64,"endX":100,"endY":64,"sampleWidth":1}"""), CancellationToken.None);
        Assert.False(Convert.ToBoolean(none.Summary["found"]));
        Assert.Equal("no-edge-above-threshold", none.Summary["reason"]);
        Assert.True(none.Outputs.ContainsKey("image"));
        (none.Outputs["image"].Value as IDisposable)?.Dispose();

        using var edge = MakeEdgeImage(1);
        using var clippedInput = VisionImage.Own(edge.Clone());
        var clipped = await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(clippedInput),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Rectangle, 0, 62, 128, 3))
        }), Node(executor.Type, """{"startX":25,"startY":64,"endX":103,"endY":64,"edgeLength":58,"caliperCount":9,"sampleWidth":1,"minContrast":30}"""), CancellationToken.None);
        Assert.False(Convert.ToBoolean(clipped.Summary["found"]));
        Assert.True(clipped.Outputs.ContainsKey("image"));
        (clipped.Outputs["image"].Value as IDisposable)?.Dispose();
        Assert.True(clippedInput.Mat.Rows > 0);
    }

    [Fact]
    public async Task CaliperLine_RejectsZeroLengthSearchLine()
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)),
                Node("measure.caliperLine", """{"startX":64,"startY":64,"endX":64,"endY":64}"""), CancellationToken.None));
        Assert.Contains("too short", exception.Message);
        Assert.True(input.Mat.Rows > 0);
    }

    [Fact]
    public async Task CaliperLine_FullyOutsideSearchRegionReturnsNotFound()
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node("measure.caliperLine", """{"startX":1000,"startY":1000,"endX":1100,"endY":1100}"""), CancellationToken.None);
        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("search-region-outside-image", result.Summary["reason"]);
        Assert.Equal(0L, result.Outputs["sampleCount"].Require<long>("sampleCount"));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeSubpixel_FitErrorUsesOverdeterminedWindow()
    {
        using var mat = new Mat(64, 128, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(mat, new Rect(64, 0, 64, 64), Scalar.All(220), -1);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new EdgeSubpixelNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("feature.edgeSubpixel", """{"startX":30,"startY":32,"endX":98,"endY":32,"sampleWidth":1,"minContrast":20}"""),
            CancellationToken.None);

        var error = result.Outputs["fitError"].Require<double>("fitError");
        Assert.True(double.IsFinite(error));
        Assert.True(error > 0, "A five-sample least-squares fit should report a non-zero residual for this non-parabolic peak.");
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeSubpixel_ClipsSearchSegmentWithoutChangingItsDirection()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new EdgeSubpixelNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("feature.edgeSubpixel", """{"startX":-20,"startY":160,"endX":140,"endY":80,"sampleWidth":1,"minContrast":30,"refine":"none"}"""),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        Assert.True(Convert.ToBoolean(result.Summary["lineClampedToImage"]));
        var clipped = result.Outputs["searchLine"].Require<VisionLine2D>("searchLine");
        Assert.InRange(clipped.Start.X, 45.9, 46.1);
        Assert.InRange(clipped.Start.Y, 126.9, 127.1);
        Assert.InRange(clipped.End.X, 126.9, 127.1);
        Assert.InRange(clipped.End.Y, 86.4, 86.6);
        var point = result.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 84.6, 86.6);
        Assert.InRange(point.Y, 106.2, 108.2);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeSubpixel_FullyOutsideSearchLineReturnsNotFound()
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new EdgeSubpixelNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("feature.edgeSubpixel", """{"startX":-40,"startY":-40,"endX":-20,"endY":-20}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("search-line-outside-image", result.Summary["reason"]);
        Assert.False(result.Outputs.ContainsKey("point"));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperMeasurement_ClipsTheSearchLineWithoutChangingTheMeasuredPosition()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        using var registry = new VisionNodeRegistry();
        var executor = new CaliperNode();
        registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "test");
        var result = await new VisionNodeDispatcher(registry).ExecuteAsync(
            Node(executor.Type, """{"startX":-20,"startY":160,"endX":140,"endY":80,"sampleWidth":1,"edgeThreshold":30}"""),
            Inputs(input), null, CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var point = result.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 84.6, 86.6);
        Assert.InRange(point.Y, 106.2, 108.2);
        Assert.True(Convert.ToBoolean(result.Summary["lineClampedToImage"]));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CropTransform_MapsReferenceCaliperLineIntoCropCoordinates()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        using var registry = new VisionNodeRegistry();
        var cropExecutor = new ImageCropNode();
        var caliperExecutor = new CaliperNode();
        registry.Register(BuiltInNodeCatalog.Require(cropExecutor.Type), cropExecutor, "test");
        registry.Register(BuiltInNodeCatalog.Require(caliperExecutor.Type), caliperExecutor, "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var cropped = await dispatcher.ExecuteAsync(
            Node("image.crop", """{"x":10,"y":10,"width":108,"height":108}"""), Inputs(input), null, CancellationToken.None);
        using var cropImage = (VisionImage)cropped.Outputs["image"].Require<IVisionImage>("image");
        using var cropMask = (VisionImage)cropped.Outputs["roiMask"].Require<IVisionImage>("roiMask");

        var measurement = await dispatcher.ExecuteAsync(
            Node("measure.caliper", """{"startX":-20,"startY":160,"endX":140,"endY":80,"sampleWidth":1,"edgeThreshold":30}"""),
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(cropImage),
                ["transform"] = cropped.Outputs["transform"]
            }, null, CancellationToken.None);

        Assert.True(Convert.ToBoolean(measurement.Summary["found"]));
        var point = measurement.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 74.6, 76.6);
        Assert.InRange(point.Y, 96.2, 98.2);
        using var outputImage = (VisionImage)measurement.Outputs["image"].Require<IVisionImage>("image");
        Assert.Equal((108, 108), (outputImage.Width, outputImage.Height));
    }

    [Fact]
    public async Task Crop_UsesConnectedCorrectedRoiAheadOfLocalSelection()
    {
        using var source = new Mat(60, 60, MatType.CV_8UC1, Scalar.All(90));
        using var image = VisionImage.Own(source.Clone());
        using var registry = new VisionNodeRegistry();
        var roiExecutor = new RoiTransformNode();
        var cropExecutor = new ImageCropNode();
        registry.Register(BuiltInNodeCatalog.Require(roiExecutor.Type), roiExecutor, "test");
        registry.Register(BuiltInNodeCatalog.Require(cropExecutor.Type), cropExecutor, "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var corrected = await dispatcher.ExecuteAsync(
            Node("roi.transform", """{"roi":{"type":"Rectangle","x":20,"y":10,"width":10,"height":12},"baseX":0,"baseY":0,"currentX":5,"currentY":7}"""),
            new Dictionary<string, VisionValue>(), null, CancellationToken.None);
        var cropped = await dispatcher.ExecuteAsync(
            Node("image.crop", """{"x":2,"y":3,"width":5,"height":4}"""),
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image), ["roi"] = corrected.Outputs["roi"] },
            null, CancellationToken.None);
        using var cropImage = (VisionImage)cropped.Outputs["image"].Require<IVisionImage>("image");

        Assert.Equal((10, 12), (cropImage.Width, cropImage.Height));
        Assert.Equal("Rectangle", cropped.Summary["roiType"]);
        Assert.Equal(25, Convert.ToInt32(cropped.Summary["cropOriginX"]));
        Assert.Equal(17, Convert.ToInt32(cropped.Summary["cropOriginY"]));
        var transform = cropped.Outputs["transform"].Require<VisionTransform2D>("transform");
        Assert.Equal(new VisionPoint2D(0, 0), transform.Apply(new VisionPoint2D(25, 17)));
        (cropped.Outputs["roiMask"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_ClipsProfilesAndPreservesExpectedEdgeAngle()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new CaliperLineNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("measure.caliperLine", """{"startX":-20,"startY":160,"endX":140,"endY":80,"edgeLength":100,"caliperCount":9,"sampleWidth":1,"minContrast":30}"""),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var fitted = result.Outputs["line"].Require<VisionLine2D>("line");
        var slope = (fitted.End.Y - fitted.Start.Y) / (fitted.End.X - fitted.Start.X);
        Assert.InRange(slope, 1.9, 2.1);
        Assert.True(Convert.ToInt32(result.Summary["profilesClippedToImage"]) > 0);
        Assert.True((result.Overlays?.Count(overlay => overlay.Id.StartsWith("caliper-line-profile-", StringComparison.Ordinal)) ?? 0) >= 3);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeNodes_ImageOutputsAreDispatcherValidAndInputSurvives()
    {
        using var mat = MakeEdgeImage(1);
        foreach (IVisionNodeExecutor executor in new IVisionNodeExecutor[] { new EdgeSubpixelNode(), new CaliperLineNode(), new CaliperCircleNode() })
        {
            using var input = VisionImage.Own(mat.Clone());
            using var registry = new VisionNodeRegistry();
            registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "test");
            var result = await new VisionNodeDispatcher(registry).ExecuteAsync(Node(executor.Type, "{}"), Inputs(input), null, CancellationToken.None);
            Assert.True(result.Outputs["image"].Require<VisionImage>("image").Mat.Rows > 0);
            Assert.True(input.Mat.Rows > 0);
            (result.Outputs["image"].Value as IDisposable)?.Dispose();
        }
    }

    private static Mat MakeEdgeImage(double slope)
    {
        var image = new Mat(128, 128, MatType.CV_8UC1);
        var rows = image.Rows;
        var cols = image.Cols;
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
            image.Set(y, x, x >= 64 + slope * (y - 64) ? (byte)220 : (byte)25);
        return image;
    }

    private static Dictionary<string, VisionValue> Inputs(VisionImage image) => new() { ["image"] = VisionValue.Image(image) };
    private static NodeDefinition Node(string type, string json) => new("n1", type, null, null,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
}
