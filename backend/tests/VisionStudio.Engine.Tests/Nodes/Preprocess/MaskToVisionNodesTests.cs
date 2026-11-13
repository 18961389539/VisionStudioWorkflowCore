using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 掩码 → 区域 / 轮廓测试：连通域拆分、阈值与取反语义、层级保留、预算与过滤。
/// </summary>
public sealed class MaskToVisionNodesTests
{
    [Fact]
    public async Task RegionFromMaskSplitsConnectedComponents()
    {
        // 两个互不相连的 10x10 方块。
        using var mask = Mask((64, 40, new Rect(4, 4, 10, 10)), (64, 40, new Rect(30, 20, 10, 10)));

        var result = await Run(new RegionFromMaskNode(), mask, Node());

        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(200L, result.Outputs["area"].Require<long>("area"));
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        var region = result.Outputs["region"].Require<VisionRegion>("region");
        Assert.Equal(2, region.Components.Count);
        Assert.Equal(64, region.Width);
        Assert.Equal(40, region.Height);
        result.Outputs["image"].Require<VisionImage>("image").Dispose();
    }

    [Fact]
    public async Task RegionFromMaskInvertSwapsForegroundAndBackground()
    {
        using var mask = Mask((20, 20, new Rect(5, 5, 10, 10)));

        var normal = await Run(new RegionFromMaskNode(), mask, Node());
        Assert.Equal(100L, normal.Outputs["area"].Require<long>("area"));
        normal.Outputs["image"].Require<VisionImage>("image").Dispose();

        var inverted = await Run(new RegionFromMaskNode(), mask, Node(("invert", "true")));
        Assert.Equal(300L, inverted.Outputs["area"].Require<long>("area"));
        inverted.Outputs["image"].Require<VisionImage>("image").Dispose();
    }

    [Fact]
    public async Task RegionFromMaskThresholdAboveContentYieldsEmptyRegion()
    {
        // 掩码内容是 128，阈值 200 时应当没有任何像素命中。
        using var mask = Mask([(20, 20, new Rect(0, 0, 20, 20))], 128);

        var result = await Run(new RegionFromMaskNode(), mask, Node(("threshold", "200")));
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0L, result.Outputs["count"].Require<long>("count"));
        result.Outputs["image"].Require<VisionImage>("image").Dispose();
    }

    [Fact]
    public async Task ContourFromMaskExtractsOneContourPerBlob()
    {
        using var mask = Mask((64, 40, new Rect(4, 4, 10, 10)), (64, 40, new Rect(30, 20, 10, 10)));

        var result = await Run(new ContourFromMaskNode(), mask, Node());

        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Equal(2, contours.Count);
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        // OpenCV 轮廓沿像素中心描边，10x10 实心块的轮廓面积是 (10-1)^2 = 81，不是 100。
        Assert.All(contours, contour => Assert.InRange(contour.Area, 80, 101));
        Assert.All(contours, contour => { Assert.Equal(10, contour.BoundingBox.Width); Assert.Equal(10, contour.BoundingBox.Height); });
        Assert.All(contours, contour => Assert.False(contour.IsHole));
        Assert.All(contours, contour => Assert.Equal(-1, contour.ParentIndex));
    }

    [Fact]
    public async Task ContourFromMaskMinAreaDropsSpeckle()
    {
        // 一个大方块 + 一个 2x2 噪点。
        using var mask = Mask((64, 40, new Rect(4, 4, 12, 12)), (64, 40, new Rect(40, 30, 2, 2)));

        var all = await Run(new ContourFromMaskNode(), mask, Node());
        Assert.Equal(2, all.Outputs["count"].Require<long>("count"));

        var filtered = await Run(new ContourFromMaskNode(), mask, Node(("minArea", "10")));
        Assert.Equal(1, filtered.Outputs["count"].Require<long>("count"));
    }

    [Fact]
    public async Task ContourFromMaskKeepsHoleHierarchy()
    {
        // 圆环：外圆 40x40，内圆挖空。
        using var mat = new Mat(60, 60, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(mat, new Point(30, 30), 20, Scalar.All(255), -1);
        Cv2.Circle(mat, new Point(30, 30), 8, Scalar.All(0), -1);
        using var mask = VisionImage.Own(mat.Clone());

        var external = await Run(new ContourFromMaskNode(), mask, Node());
        Assert.Equal(1, external.Outputs["count"].Require<long>("count"));

        var withHoles = await Run(new ContourFromMaskNode(), mask, Node(("retrieval", "all")));
        Assert.Equal(2, withHoles.Outputs["count"].Require<long>("count"));
        var contours = withHoles.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        var hole = contours.Single(contour => contour.IsHole);
        Assert.Equal(0, hole.ParentIndex);
    }

    [Fact]
    public async Task ContourFromMaskRejectsBudgetOverflowInsteadOfTruncating()
    {
        using var mask = Mask((64, 40, new Rect(4, 4, 10, 10)), (64, 40, new Rect(30, 20, 10, 10)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ContourFromMaskNode(), mask, Node(("maxContours", "1"))));
        Assert.Contains("maxContours", error.Message);
    }

    [Fact]
    public async Task BothNodesShareThresholdSemantics()
    {
        // 掩码灰度 200：阈值 250 时两个出口都应当判空（255 掩码在阈值 250 下仍是前景）。
        using var mask = Mask([(32, 32, new Rect(8, 8, 16, 16))], 200);

        var region = await Run(new RegionFromMaskNode(), mask, Node(("threshold", "250")));
        var contour = await Run(new ContourFromMaskNode(), mask, Node(("threshold", "250")));
        // 同一个阈值下两个出口必须一致：都没有命中。
        Assert.False(region.Outputs["found"].Require<bool>("found"));
        Assert.False(contour.Outputs["found"].Require<bool>("found"));
        region.Outputs["image"].Require<VisionImage>("image").Dispose();
    }

    // ===== 夹具 =====

    private static VisionImage Mask(params (int Width, int Height, Rect Rect)[] rects)
        => Mask(rects, 255);

    private static VisionImage Mask((int Width, int Height, Rect Rect)[] rects, byte value)
    {
        var width = rects.Max(item => item.Width);
        var height = rects.Max(item => item.Height);
        var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        foreach (var (_, _, rect) in rects)
            Cv2.Rectangle(mat, rect, Scalar.All(value), -1);
        return VisionImage.Own(mat);
    }

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, VisionImage mask, NodeDefinition node)
    {
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(mask) };
        return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private static NodeDefinition Node(params (string Key, string Value)[] parameters)
    {
        var payload = parameters.ToDictionary(x => x.Key, x => (object)x.Value);
        return new NodeDefinition("n1", "test", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)));
    }
}
