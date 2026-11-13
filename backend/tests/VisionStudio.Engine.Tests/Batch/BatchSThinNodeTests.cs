using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class BatchSThinNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);

    [Fact]
    public async Task SkeletonHullBoundary_EmitShapeMetrics()
    {
        var region = Rect(8, 8, 24, 16, 64);
        var skel = await new RegionSkeletonNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.skeleton"), CancellationToken.None);
        Assert.Contains("region.skeleton", (string)skel.Outputs["diagnostics"].Value!);
        Assert.True((double)skel.Outputs["length"].Value! > 0);
        Assert.True((long)skel.Outputs["endpoints"].Value! >= 0);

        var hull = await new RegionConvexHullNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.convexHull"), CancellationToken.None);
        Assert.Contains("region.convexHull", (string)hull.Outputs["diagnostics"].Value!);
        Assert.True((long)hull.Outputs["vertexCount"].Value! >= 4);
        Assert.True((double)hull.Outputs["length"].Value! > 0);

        var boundary = await new RegionBoundaryNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.boundary", """{"boundaryMode":"inner"}"""), CancellationToken.None);
        Assert.Contains("region.boundary", (string)boundary.Outputs["diagnostics"].Value!);
        Assert.True((double)boundary.Outputs["length"].Value! > 0);
    }

    [Fact]
    public async Task MorphologyHats_EmitIntensityStats()
    {
        using var img = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(img, new Rect(18, 18, 8, 8), Scalar.All(220), -1);
        var top = await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.topHat", """{"kernelWidth":15,"kernelHeight":15}"""), CancellationToken.None);
        Assert.Contains("image.topHat", (string)top.Outputs["diagnostics"].Value!);
        Assert.True(top.Outputs.ContainsKey("meanIntensity"));
        Assert.True(top.Outputs.ContainsKey("maxIntensity"));
        Assert.True(top.Outputs.ContainsKey("nonZeroCount"));
        Assert.True((double)top.Outputs["maxIntensity"].Value! >= (double)top.Outputs["meanIntensity"].Value!);

        var bottom = await new ImageBottomHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.bottomHat", """{"kernelWidth":15,"kernelHeight":15}"""), CancellationToken.None);
        Assert.Contains("image.bottomHat", (string)bottom.Outputs["diagnostics"].Value!);
        Assert.True(bottom.Outputs.ContainsKey("stdIntensity"));

        var grad = await new ImageMorphGradientNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.morphGradient", """{"kernelWidth":5,"kernelHeight":5}"""), CancellationToken.None);
        Assert.Contains("image.morphGradient", (string)grad.Outputs["diagnostics"].Value!);
        Assert.True((long)grad.Outputs["nonZeroCount"].Value! >= 0);
    }

    [Fact]
    public async Task MedianBlur_BorderTypePadAndCrop()
    {
        using var img = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Circle(img, new Point(0, 0), 4, Scalar.All(255), -1);
        var result = await new MedianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.blur.median", """{"kernelSize":5,"borderType":"reflect"}"""), CancellationToken.None);
        Assert.Contains("image.blur.median", (string)result.Outputs["diagnostics"].Value!);
        Assert.Contains("reflect", (string)result.Outputs["diagnostics"].Value!);
        Assert.Equal(32, ((VisionImage)result.Outputs["image"].Value!).Width);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MedianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.blur.median", """{"kernelSize":5,"borderType":"mirror"}"""), CancellationToken.None));
    }
}
