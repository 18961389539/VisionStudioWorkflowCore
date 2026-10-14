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

    [Theory]
    [InlineData("""{"startX":64,"startY":64,"endX":64,"endY":64}""")]
    [InlineData("""{"startX":1000,"startY":1000,"endX":1100,"endY":1100}""")]
    public async Task CaliperLine_RejectsZeroLengthAfterClamp(string parameters)
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)), Node("measure.caliperLine", parameters), CancellationToken.None));
        Assert.Contains("too short", exception.Message);
        Assert.True(input.Mat.Rows > 0);
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
