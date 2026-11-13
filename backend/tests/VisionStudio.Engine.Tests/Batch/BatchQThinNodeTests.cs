using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Tests;

public sealed class BatchQThinNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    private static VisionContour Contour(params VisionPoint2D[] pts)
        => new(0, pts, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));

    private static VisionPoint2D[] CirclePoints(double r, double noise = 0, double arc = 360, int n = 36)
    {
        var pts = new List<VisionPoint2D>();
        var span = Math.Min(360, Math.Max(1, arc));
        for (var i = 0; i < n; i++)
        {
            var a = span * i / n * Math.PI / 180;
            var rr = r + (noise == 0 ? 0 : ((i % 2 == 0) ? noise : -noise));
            pts.Add(new VisionPoint2D(50 + rr * Math.Cos(a), 50 + rr * Math.Sin(a)));
        }
        return pts.ToArray();
    }

    [Fact]
    public async Task RoundnessStraightnessContourDeviation_EmitDiagnostics()
    {
        var round = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20))]) }),
            Def("measure.roundness"), CancellationToken.None);
        Assert.True((bool)round.Outputs["found"].Value!);
        Assert.Contains("measure.roundness", (string)round.Outputs["diagnostics"].Value!);

        var missing = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20, arc: 40, n: 8))]) }),
            Def("measure.roundness", """{"minCoverageDeg":90}"""), CancellationToken.None);
        Assert.False((bool)missing.Outputs["found"].Value!);
        Assert.Contains("measure.roundness", (string)missing.Outputs["diagnostics"].Value!);

        var straight = await new StraightnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(
                new(0, 1), new(1, -1), new(2, 1), new(3, -1), new(4, 1), new(5, -1), new(6, 1))]) }),
            Def("measure.straightness"), CancellationToken.None);
        Assert.True((bool)straight.Outputs["found"].Value!);
        Assert.Contains("measure.straightness", (string)straight.Outputs["diagnostics"].Value!);

        var measured = Contour(new(0, 0), new(10, 0), new(10, 10));
        var reference = Contour(new(0, 1), new(10, 1), new(10, 11));
        var dev = await new ContourDeviationNode().ExecuteAsync(
            Ctx(new()
            {
                ["contours"] = VisionValue.Contours([measured]),
                ["referenceContours"] = VisionValue.Contours([reference])
            }),
            Def("measure.contourDeviation", """{"closed":false,"sampleStep":1}"""), CancellationToken.None);
        Assert.True((bool)dev.Outputs["found"].Value!);
        Assert.Contains("measure.contourDeviation", (string)dev.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task TextValidateAndColorConvert_EmitDiagnostics()
    {
        var text = await new TextValidationNode().ExecuteAsync(
            Ctx(new() { ["text"] = VisionValue.String("LOT-42") }),
            Def("text.validate", """{"mode":"equals","expected":"LOT-42"}"""), CancellationToken.None);
        Assert.True((bool)text.Outputs["pass"].Value!);
        Assert.Contains("text.validate", (string)text.Outputs["diagnostics"].Value!);

        using var src = new Mat(8, 8, MatType.CV_8UC3, new Scalar(40, 80, 160));
        var color = await new ImageColorConvertNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(src) }),
            Def("image.colorConvert", """{"space":"hsv"}"""), CancellationToken.None);
        Assert.Contains("image.colorConvert", (string)color.Outputs["diagnostics"].Value!);
        Assert.True(color.Outputs.ContainsKey("channel1"));
    }

    [Fact]
    public async Task ContourFilter_ValidatesAndEmitsDiagnostics()
    {
        var contour = Contour(
            new(0, 0), new(100, 0), new(100, 100), new(0, 100));
        // ContourFilter uses Area from contour metadata; set a large area via constructor fields if needed.
        // Use Contour with computed Area like other tests - check VisionContour structure.
        var big = new VisionContour(
            0, [new(0, 0), new(100, 0), new(100, 80), new(0, 80)], -1, false,
            8000, 360, 0.7, new(0, 0, 100, 80), new(50, 40, 60));
        var result = await new ContourFilterNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([big]) }),
            Def("contour.filter", """{"minArea":100}"""), CancellationToken.None);
        Assert.Equal(1L, (long)result.Outputs["count"].Value!);
        Assert.Contains("contour.filter", (string)result.Outputs["diagnostics"].Value!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourFilterNode().ExecuteAsync(
                Ctx(new() { ["contours"] = VisionValue.Contours([big]) }),
                Def("contour.filter", """{"maxCount":0}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourFilterNode().ExecuteAsync(
                Ctx(new() { ["contours"] = VisionValue.Contours([big]) }),
                Def("contour.filter", """{"hierarchy":"bogus"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task MeasureBlob_EmitsDiagnostics()
    {
        using var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(10, 10, 30, 30), Scalar.All(255), -1);
        var found = await new LargestBlobNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("measure.blob", """{"minArea":50}"""), CancellationToken.None);
        Assert.True((bool)found.Outputs["found"].Value!);
        Assert.Contains("measure.blobs", (string)found.Outputs["diagnostics"].Value!);

        using var empty = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0));
        var none = await new LargestBlobNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(empty) }),
            Def("measure.blob", """{"minArea":50}"""), CancellationToken.None);
        Assert.False((bool)none.Outputs["found"].Value!);
        Assert.Contains("measure.blobs", (string)none.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task RobotExecuteTarget_ValidatesAndEmitsDiagnostics()
    {
        await using var manager = new RobotManager();
        manager.Register(new VirtualAbbRobotAdapter());
        var node = new RobotExecuteTargetNode(manager);
        var target = new VisionRobotTarget2D(10, 20, 0, "RobotBase", "mm", "ABB", "BatchQ");
        var result = await node.ExecuteAsync(
            Ctx(new() { ["target"] = VisionValue.RobotTarget(target) }),
            Def("robot.executeTarget", """{"robotId":"virtual-abb-1","action":"Handshake","timeoutMs":2000}"""),
            CancellationToken.None);
        Assert.Contains("robot.executeTarget", (string)result.Outputs["diagnostics"].Value!);
        Assert.True(result.Outputs.ContainsKey("commandId"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(
                Ctx(new() { ["target"] = VisionValue.RobotTarget(target) }),
                Def("robot.executeTarget", """{"robotId":"  ","action":"Handshake"}"""),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(
                Ctx(new() { ["target"] = VisionValue.RobotTarget(target) }),
                Def("robot.executeTarget", """{"robotId":"virtual-abb-1","timeoutMs":1}"""),
                CancellationToken.None));
    }
}
