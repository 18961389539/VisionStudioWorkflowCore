using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Tests;

public sealed class BatchPThinNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);

    [Fact]
    public async Task RegionIntensity_EmitsDiagnostics()
    {
        using var image = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(50));
        Cv2.Rectangle(image, new Rect(5, 5, 10, 10), Scalar.All(200), -1);
        var region = Rect(5, 5, 10, 10, 32);
        var result = await new RegionIntensityNode().ExecuteAsync(Ctx(new()
        {
            ["image"] = Img(image),
            ["region"] = VisionValue.Region(region)
        }), Def("measure.regionIntensity"), CancellationToken.None);
        Assert.True((bool)result.Outputs["found"].Value!);
        Assert.Contains("measure.regionIntensity", (string)result.Outputs["diagnostics"].Value!);
        Assert.True((double)result.Outputs["mean"].Value! > 100);
    }

    [Fact]
    public async Task RegionPropertiesAndToDetections_Diagnostics()
    {
        var region = new VisionRegion(64, 64, [Rect(2, 2, 8, 8).Components[0], Rect(20, 20, 5, 5).Components[0]]);
        var props = await new RegionPropertiesNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.properties"), CancellationToken.None);
        Assert.True((bool)props.Outputs["found"].Value!);
        Assert.Contains("region.properties", (string)props.Outputs["diagnostics"].Value!);
        Assert.Equal(2L, (long)props.Outputs["count"].Value!);

        var dets = await new RegionToDetectionsNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.toDetections", """{"maxCount":1}"""), CancellationToken.None);
        Assert.Equal(1L, (long)dets.Outputs["count"].Value!);
        Assert.Contains("region.toDetections", (string)dets.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new RegionToDetectionsNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
                Def("region.toDetections", """{"maxCount":-1}"""), CancellationToken.None));
    }

    [Fact]
    public async Task SkeletonHullBoundary_UseTypedDiagnostics()
    {
        var region = Rect(4, 4, 20, 10);
        var skel = await new RegionSkeletonNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.skeleton"), CancellationToken.None);
        Assert.Contains("region.skeleton", (string)skel.Outputs["diagnostics"].Value!);
        var hull = await new RegionConvexHullNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.convexHull"), CancellationToken.None);
        Assert.Contains("region.convexHull", (string)hull.Outputs["diagnostics"].Value!);
        var boundary = await new RegionBoundaryNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.boundary", """{"boundaryMode":"inner"}"""), CancellationToken.None);
        Assert.Contains("region.boundary", (string)boundary.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task MorphologyHats_UseNodeTypeInDiagnostics()
    {
        using var img = new Mat(30, 30, MatType.CV_8UC1, Scalar.All(10));
        Cv2.Rectangle(img, new Rect(12, 12, 5, 5), Scalar.All(200), -1);
        var top = await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.topHat", """{"kernelWidth":15,"kernelHeight":15}"""), CancellationToken.None);
        Assert.Contains("image.topHat", (string)top.Outputs["diagnostics"].Value!);
        var bottom = await new ImageBottomHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.bottomHat", """{"kernelWidth":15,"kernelHeight":15}"""), CancellationToken.None);
        Assert.Contains("image.bottomHat", (string)bottom.Outputs["diagnostics"].Value!);
        var grad = await new ImageMorphGradientNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.morphGradient", """{"kernelWidth":3,"kernelHeight":3}"""), CancellationToken.None);
        Assert.Contains("image.morphGradient", (string)grad.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task ContrastNormalizeAndWallThickness_Diagnostics()
    {
        using var img = new Mat(20, 20, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(img, new Rect(2, 2, 10, 10), Scalar.All(200), -1);
        var contrast = await new ContrastNormalizeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.contrastNormalize"), CancellationToken.None);
        Assert.Contains("image.contrastNormalize", (string)contrast.Outputs["diagnostics"].Value!);

        var wall = await new WallThicknessNode().ExecuteAsync(Ctx(new()
        {
            ["inner"] = VisionValue.Circle(new VisionCircle(50, 50, 20)),
            ["outer"] = VisionValue.Circle(new VisionCircle(50, 50, 30))
        }), Def("measure.wallThickness", """{"sampleCount":36,"nominalThickness":10,"tolerance":0.5}"""), CancellationToken.None);
        Assert.True((bool)wall.Outputs["found"].Value!);
        Assert.True((bool)wall.Outputs["withinTolerance"].Value!);
        Assert.Contains("measure.wallThickness", (string)wall.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task RobotCurrentPose_EmitsDiagnostics()
    {
        await using var manager = new RobotManager();
        manager.Register(new VirtualAbbRobotAdapter());
        var node = new RobotCurrentPoseNode(manager);
        var result = await node.ExecuteAsync(Ctx(new()), Def("robot.currentPose", """{"robotId":"virtual-abb-1"}"""), CancellationToken.None);
        Assert.Contains("robot.currentPose", (string)result.Outputs["diagnostics"].Value!);
        Assert.Equal("Connected", result.Outputs["connection"].Value);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(Ctx(new()), Def("robot.currentPose", """{"robotId":"  "}"""), CancellationToken.None));
    }
}
