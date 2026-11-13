using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Raise &lt;70 region shape + gray-morphology nodes: F-port exercise, robustness gates, diagnostics richness.
/// </summary>
public sealed class BatchRaise70RegionMorphTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);
    private static VisionRegion EmptyCanvas(int canvas = 32)
        => new(canvas, canvas, Array.Empty<VisionRegionComponent>());

    [Fact]
    public async Task Skeleton_ExercisesTopologyPorts_AndRejectsBadCanvas()
    {
        var region = Rect(6, 6, 28, 10, 64);
        var skel = await new RegionSkeletonNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.skeleton"), CancellationToken.None);
        var found = skel.Outputs["found"].Require<bool>("found");
        var count = skel.Outputs["count"].Require<long>("count");
        var area = skel.Outputs["area"].Require<double>("area");
        var length = skel.Outputs["length"].Require<double>("length");
        var endpoints = skel.Outputs["endpoints"].Require<long>("endpoints");
        var junctions = skel.Outputs["junctions"].Require<long>("junctions");
        var vertexCount = skel.Outputs["vertexCount"].Require<long>("vertexCount");
        var diag = skel.Outputs["diagnostics"].Require<string>("diagnostics");
        Assert.True(found, "found");
        Assert.True(count >= 1, $"count={count}");
        Assert.True(area > 0, $"area={area}");
        Assert.True(length > 0, $"length={length}");
        Assert.True(endpoints >= 0, $"endpoints={endpoints}");
        Assert.True(junctions >= 0, $"junctions={junctions}");
        Assert.Equal(0L, vertexCount);
        Assert.Contains("region.skeleton", diag);
        Assert.Contains("inputPixelArea", diag);
        Assert.Equal("skeleton", skel.Summary["operation"]?.ToString());
        Assert.True(skel.Summary.ContainsKey("areaRatio"), "missing areaRatio");
        var ratio = Convert.ToDouble(skel.Summary["areaRatio"]);
        Assert.True(ratio > 0 && ratio <= 1.0001, $"areaRatio={ratio}");

        var empty = await new RegionSkeletonNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(EmptyCanvas()) }),
            Def("region.skeleton"), CancellationToken.None);
        Assert.False(empty.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0d, empty.Outputs["length"].Require<double>("length"));
        Assert.Equal(true, empty.Summary["emptyInput"]);
    }


    [Fact]
    public async Task ConvexHull_ExercisesVertexAndLength_UnionFirstBridges()
    {
        var a = Rect(4, 4, 8, 8, 64);
        var b = Rect(40, 40, 8, 8, 64);
        var two = new VisionRegion(64, 64, [.. a.Components, .. b.Components]);

        var separate = await new RegionConvexHullNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(two) }),
            Def("region.convexHull", """{"unionFirst":false}"""), CancellationToken.None);
        Assert.True(separate.Outputs["found"].Require<bool>("found"));
        Assert.Equal(2L, separate.Outputs["count"].Require<long>("count"));
        Assert.True(separate.Outputs["vertexCount"].Require<long>("vertexCount") >= 8);
        Assert.True(separate.Outputs["length"].Require<double>("length") > 0);
        Assert.Contains("region.convexHull", separate.Outputs["diagnostics"].Require<string>("diagnostics"));

        var united = await new RegionConvexHullNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(two) }),
            Def("region.convexHull", """{"unionFirst":true}"""), CancellationToken.None);
        Assert.True(united.Outputs["found"].Require<bool>("found"));
        Assert.Equal(1L, united.Outputs["count"].Require<long>("count"));
        Assert.True(united.Outputs["area"].Require<double>("area") > separate.Outputs["area"].Require<double>("area"));
        Assert.True(Convert.ToDouble(united.Summary["areaRatio"]) >= 1.0 - 1e-6);
    }

    [Fact]
    public async Task Boundary_InnerOuter_LengthAndBadMode()
    {
        var region = Rect(10, 10, 20, 16, 64);
        var inner = await new RegionBoundaryNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.boundary", """{"boundaryMode":"inner"}"""), CancellationToken.None);
        var outer = await new RegionBoundaryNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.boundary", """{"boundaryMode":"outer"}"""), CancellationToken.None);
        Assert.True(inner.Outputs["found"].Require<bool>("found"));
        Assert.True(outer.Outputs["found"].Require<bool>("found"));
        Assert.True(inner.Outputs["length"].Require<double>("length") > 0);
        Assert.True(outer.Outputs["length"].Require<double>("length") > 0);
        Assert.Contains("\"boundaryMode\":\"inner\"", inner.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("\"boundaryMode\":\"outer\"", outer.Outputs["diagnostics"].Require<string>("diagnostics"));
        // Outer ring is outside the filled rect → typically more pixels than inner for this size.
        Assert.True(outer.Outputs["length"].Require<double>("length") >= inner.Outputs["length"].Require<double>("length") - 1);

        var err = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new RegionBoundaryNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
                Def("region.boundary", """{"boundaryMode":"middle"}"""), CancellationToken.None));
        Assert.Contains("boundaryMode", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TopHat_BrightBlob_RaisesMax_AndRejectsBadKernels()
    {
        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(img, new Rect(28, 28, 6, 6), Scalar.All(240), -1);
        var top = await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.topHat", """{"kernelWidth":15,"kernelHeight":15}"""), CancellationToken.None);
        Assert.Contains("image.topHat", top.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(top.Outputs["maxIntensity"].Require<double>("maxIntensity") > top.Outputs["meanIntensity"].Require<double>("meanIntensity"));
        Assert.True(top.Outputs["nonZeroCount"].Require<long>("nonZeroCount") > 0);
        Assert.True(top.Outputs["stdIntensity"].Require<double>("stdIntensity") >= 0);
        Assert.True(top.Outputs["minIntensity"].Require<double>("minIntensity") >= 0);
        Assert.Contains("peakX", top.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("kernelArea", top.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Equal(false, top.Summary["emptyResult"]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.topHat", """{"kernelWidth":14,"kernelHeight":15}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.topHat", """{"kernelWidth":0,"kernelHeight":15}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.topHat", """{"kernelWidth":99,"kernelHeight":99}"""), CancellationToken.None));
        using var empty = new Mat();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(empty) }),
                Def("image.topHat"), CancellationToken.None));
    }

    [Fact]
    public async Task BottomHat_AndMorphGradient_FPortsAndDiagnostics()
    {
        using var img = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(180));
        Cv2.Rectangle(img, new Rect(18, 18, 8, 8), Scalar.All(20), -1); // dark pit for bottom-hat
        var bottom = await new ImageBottomHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.bottomHat", """{"kernelWidth":15,"kernelHeight":15}"""), CancellationToken.None);
        Assert.Contains("image.bottomHat", bottom.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(bottom.Outputs["maxIntensity"].Require<double>("maxIntensity") >= bottom.Outputs["meanIntensity"].Require<double>("meanIntensity"));
        Assert.True(bottom.Outputs["nonZeroCount"].Require<long>("nonZeroCount") >= 0);
        Assert.Equal("bottomHat", bottom.Summary["operation"]?.ToString());

        using var edge = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(edge, new Rect(10, 10, 20, 20), Scalar.All(255), -1);
        var grad = await new ImageMorphGradientNode().ExecuteAsync(Ctx(new() { ["image"] = Img(edge) }),
            Def("image.morphGradient", """{"kernelWidth":5,"kernelHeight":5}"""), CancellationToken.None);
        Assert.Contains("image.morphGradient", grad.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(grad.Outputs["nonZeroCount"].Require<long>("nonZeroCount") > 0);
        Assert.True(grad.Outputs["maxIntensity"].Require<double>("maxIntensity") > 0);
        Assert.Equal("gradient", grad.Summary["operation"]?.ToString());
        Assert.Contains("energy", grad.Outputs["diagnostics"].Require<string>("diagnostics"));
    }
}
