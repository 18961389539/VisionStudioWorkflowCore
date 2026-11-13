using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Behavioral smoke tests for region, morphology, and value nodes.</summary>
public sealed class NodeBehaviorSmokeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);

    [Fact]
    public async Task MorphologyHats_EmitDiagnostics()
    {
        using var img = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(12, 12, 16, 16), Scalar.All(220), -1);
        foreach (var (node, type) in new (IVisionNodeExecutor, string)[]
                 {
                     (new ImageTopHatNode(), "image.topHat"),
                     (new ImageBottomHatNode(), "image.bottomHat"),
                     (new ImageMorphGradientNode(), "image.morphGradient"),
                 })
        {
            var result = await node.ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def(type, """{"kernelWidth":5,"kernelHeight":5,"shape":"rect"}"""), CancellationToken.None);
            Assert.Contains(type, result.Outputs["diagnostics"].Require<string>("diagnostics"));
        }
    }

    [Fact]
    public async Task RegionShape_BoundaryHullSkeleton_Diagnostics()
    {
        var region = Rect(6, 6, 24, 24);
        foreach (var (node, type) in new (IVisionNodeExecutor, string)[]
                 {
                     (new RegionBoundaryNode(), "region.boundary"),
                     (new RegionConvexHullNode(), "region.convexHull"),
                     (new RegionSkeletonNode(), "region.skeleton"),
                 })
        {
            var result = await node.ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
                Def(type), CancellationToken.None);
            Assert.Contains(type, result.Outputs["diagnostics"].Require<string>("diagnostics"));
        }
    }

    [Fact]
    public async Task RegionFilterAndBoolean_Gates()
    {
        var region = Rect(4, 4, 20, 20);
        var filtered = await new RegionFilterNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.filter", """{"feature":"area","min":1,"max":999999}"""), CancellationToken.None);
        Assert.Contains("region.filter", filtered.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new RegionFilterNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
                Def("region.filter", """{"feature":"nope"}"""), CancellationToken.None));

        var other = Rect(10, 10, 20, 20);
        var union = await new RegionBooleanNode().ExecuteAsync(
            Ctx(new() { ["a"] = VisionValue.Region(region), ["b"] = VisionValue.Region(other) }),
            Def("region.boolean", """{"operation":"union"}"""), CancellationToken.None);
        Assert.Contains("region.boolean", union.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task ValueArithmetic_EmitsFiniteFlagAndDiagnostics()
    {
        var r = await new ValueArithmeticNode().ExecuteAsync(
            Ctx(new() { ["a"] = VisionValue.Double(3), ["b"] = VisionValue.Double(4) }),
            Def("value.arithmetic", """{"op":"add"}"""), CancellationToken.None);
        Assert.Equal(7d, (double)r.Outputs["result"].Value!);
        Assert.True((bool)r.Outputs["isFinite"].Value!);
        Assert.Contains("value.arithmetic", (string)r.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task PoseToTransform_EmitsDiagnostics()
    {
        var pose = new VisionCoordinatePose2D(1, 2, 15, "RobotBase", "mm");
        var r = await new PoseToTransformNode().ExecuteAsync(
            Ctx(new() { ["pose"] = VisionValue.CoordinatePose(pose) }),
            Def("coordinate.poseToTransform", """{"sourceFrame":"Tool"}"""), CancellationToken.None);
        Assert.Contains("coordinate.poseToTransform", (string)r.Outputs["diagnostics"].Value!);
        Assert.Equal(1d, (double)r.Outputs["x"].Value!);
    }
}
