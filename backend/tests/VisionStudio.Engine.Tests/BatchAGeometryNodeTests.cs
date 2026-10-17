using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Batch A node tests. These need OpenCV/JLVision natives; on this Linux box they
/// fail with DllNotFound like all pre-existing image tests and must be re-run on Windows.</summary>
public sealed class BatchAGeometryNodeTests
{
    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Mat source, string json = "{}")
    {
        var vision = VisionImage.Own(source.Clone());
        var node = new NodeDefinition("test", executor.Type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        try { return await executor.ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(vision) }), node, CancellationToken.None); }
        finally { vision.Dispose(); }
    }

    [Fact]
    public async Task LineFinderReturnsMultipleFittedLinesWithResiduals()
    {
        using var source = new Mat(200, 300, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Line(source, new Point(20, 40), new Point(280, 40), Scalar.All(255), 3);
        Cv2.Line(source, new Point(20, 160), new Point(280, 100), Scalar.All(255), 3);
        var result = await Run(new LineNode(), source, """{"minLength":50,"maxLines":5}""");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.True(result.Outputs["count"].Require<long>("count") >= 2);
        Assert.True(result.Outputs["rmsError"].Require<double>("rmsError") < 2.5);
        Assert.Contains("inlierPoints", result.Outputs["lines"].Require<string>("lines"));
    }

    [Fact]
    public async Task LineFinderReportsNotFoundInsteadOfThrowing()
    {
        using var source = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        var result = await Run(new LineNode(), source);
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0, result.Outputs["count"].Require<long>("count"));
        Assert.False(result.Outputs.ContainsKey("line"));
    }

    [Fact]
    public async Task CircleFinderRefinesCircleAndReportsRms()
    {
        using var source = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(source, new Point(100, 95), 45, Scalar.All(255), 3);
        var result = await Run(new CircleNode(), source, """{"minRadius":20,"maxRadius":90}""");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        var circle = result.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.InRange(circle.X, 96, 104); Assert.InRange(circle.Y, 91, 99); Assert.InRange(circle.Radius, 41, 49);
    }

    [Fact]
    public async Task CircleFinderReportsNotFoundInsteadOfThrowing()
    {
        using var source = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        var result = await Run(new CircleNode(), source);
        Assert.False(result.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task EdgeNodeReportsChains()
    {
        using var source = new Mat(120, 160, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(source, new Rect(30, 25, 80, 60), Scalar.All(255), -1);
        var result = await Run(new EdgeNode(), source);
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.True(result.Outputs["chainCount"].Require<long>("chainCount") >= 1);
        Assert.True(result.Outputs["longestChain"].Require<long>("longestChain") > 100);
    }

    [Fact]
    public async Task EdgeNodeRejectsInvertedThresholds()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new EdgeNode(), source, """{"lowThreshold":200,"highThreshold":100}"""));
    }
}
