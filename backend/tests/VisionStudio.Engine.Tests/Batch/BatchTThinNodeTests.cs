using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class BatchTThinNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);
    private static VisionContour Contour(params VisionPoint2D[] pts)
        => new(0, pts, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));
    private static VisionPoint2D[] CirclePoints(double r, int n = 36)
        => Enumerable.Range(0, n).Select(i =>
        {
            var a = 360.0 * i / n * Math.PI / 180;
            return new VisionPoint2D(50 + r * Math.Cos(a), 50 + r * Math.Sin(a));
        }).ToArray();

    [Fact]
    public async Task Point3D_EmitsSphericalMetrics()
    {
        var result = await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.point3D", """{"x":3,"y":4,"z":12,"frame":"Workpiece","unit":"mm"}"""), CancellationToken.None);
        Assert.Equal(13d, (double)result.Outputs["magnitude"].Value!, 9);
        Assert.Equal(5d, (double)result.Outputs["xyMagnitude"].Value!, 9);
        Assert.True(result.Outputs.ContainsKey("azimuthDeg"));
        Assert.True(result.Outputs.ContainsKey("elevationDeg"));
    }

    [Fact]
    public async Task GaussianChannelGrayscale_Harden()
    {
        using var img = new Mat(24, 24, MatType.CV_8UC3, new Scalar(10, 40, 200));
        var gauss = await new GaussianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.blur.gaussian", """{"kernelSize":5,"borderType":"reflect"}"""), CancellationToken.None);
        Assert.Contains("reflect", (string)gauss.Outputs["diagnostics"].Value!);

        var channel = await new ChannelExtractNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.channel", """{"channel":"red"}"""), CancellationToken.None);
        Assert.True(channel.Outputs.ContainsKey("meanIntensity"));
        Assert.Contains("image.channel", (string)channel.Outputs["diagnostics"].Value!);

        var gray = await new GrayscaleNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.grayscale", """{"mode":"average"}"""), CancellationToken.None);
        Assert.True(gray.Outputs.ContainsKey("maxIntensity"));
        Assert.Contains("average", (string)gray.Outputs["diagnostics"].Value!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new GaussianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.blur.gaussian", """{"borderType":"bogus"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task DynamicThresholdAndRegionIntensity_Diagnostics()
    {
        using var img = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(img, new Rect(10, 10, 12, 12), Scalar.All(220), -1);
        var dyn = await new DynamicThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.dynamicThreshold", """{"offset":20,"polarity":"bright","windowSize":15}"""), CancellationToken.None);
        Assert.Contains("image.dynamicThreshold", (string)dyn.Outputs["diagnostics"].Value!);

        var region = Rect(10, 10, 12, 12, 40);
        var intensity = await new RegionIntensityNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img), ["region"] = VisionValue.Region(region) }),
            Def("measure.regionIntensity", """{"meanNominal":200,"tolerance":50}"""), CancellationToken.None);
        Assert.True((bool)intensity.Outputs["found"].Value!);
        Assert.True(intensity.Outputs.ContainsKey("cv"));
        Assert.True(intensity.Outputs.ContainsKey("pixelCount"));
        Assert.True((bool)intensity.Outputs["withinTolerance"].Value!);
    }

    [Fact]
    public async Task RegionProperties_SelectLargestAndMetrics()
    {
        var a = Rect(2, 2, 6, 6, 64).Components[0];
        var b = Rect(20, 20, 16, 10, 64).Components[0];
        var region = new VisionRegion(64, 64, [a, b]);
        var result = await new RegionPropertiesNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.properties", """{"selectBy":"largest"}"""), CancellationToken.None);
        Assert.True((bool)result.Outputs["found"].Value!);
        Assert.Equal(1L, (long)result.Outputs["selectedIndex"].Value!);
        Assert.True((double)result.Outputs["aspectRatio"].Value! > 0);
        Assert.True((double)result.Outputs["equivalentDiameter"].Value! > 0);
    }

    [Fact]
    public async Task RoundnessStraightnessWall_ExtraMetrics()
    {
        var round = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20))]) }),
            Def("measure.roundness"), CancellationToken.None);
        Assert.True(round.Outputs.ContainsKey("outOfRoundPct"));
        Assert.True(round.Outputs.ContainsKey("relativeRms"));

        var straight = await new StraightnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(new(0, 0), new(10, 0), new(20, 0), new(30, 0))]) }),
            Def("measure.straightness"), CancellationToken.None);
        Assert.True(straight.Outputs.ContainsKey("angleDeg"));
        Assert.True(Math.Abs((double)straight.Outputs["angleDeg"].Value!) < 1);

        var wall = await new WallThicknessNode().ExecuteAsync(
            Ctx(new()
            {
                ["inner"] = VisionValue.Circle(new VisionCircle(50, 50, 20)),
                ["outer"] = VisionValue.Circle(new VisionCircle(50, 50, 30))
            }),
            Def("measure.wallThickness", """{"sampleCount":36}"""), CancellationToken.None);
        Assert.True((bool)wall.Outputs["found"].Value!);
        Assert.True(wall.Outputs.ContainsKey("uniformity"));
        Assert.True(wall.Outputs.ContainsKey("cv"));
    }
}
