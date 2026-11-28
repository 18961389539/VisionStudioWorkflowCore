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

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public async Task RegionFromMaskEnforcesComponentBudget(int columns, bool overLimit)
    {
        using var mask = VisionImage.Own(new Mat(199, 2 * columns - 1, MatType.CV_8UC1, Scalar.All(0)));
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < columns; x++)
                mask.Mat.Set(2 * y, 2 * x, (byte)255);

        if (overLimit)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromMaskNode(), mask, Node()));
            Assert.Contains("component budget", error.Message);
            Assert.Contains("10100 components", error.Message);
            Assert.Contains("10000", error.Message);
            Assert.Contains("\"reason\":\"component-budget\"", error.Message);
            Assert.Contains("\"componentCount\":10100", error.Message);
            Assert.Contains("\"componentLimit\":10000", error.Message);
        }
        else
        {
            var result = await Run(new RegionFromMaskNode(), mask, Node());
            using var image = result.Outputs["image"].Require<VisionImage>("image");
            Assert.Equal(10_000L, result.Outputs["count"].Require<long>("count"));
            Assert.Equal(10_000L, result.Outputs["area"].Require<long>("area"));
        }
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public async Task RegionFromMaskEnforcesTotalRunBudget(int height, bool overLimit)
    {
        using var mask = VisionImage.Own(new Mat(height, 1999, MatType.CV_8UC1, Scalar.All(0)));
        for (var x = 0; x < 1000; x++)
            Cv2.Line(mask.Mat, new Point(2 * x, 0), new Point(2 * x, height - 1), Scalar.All(255));

        if (overLimit)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionFromMaskNode(), mask, Node()));
            Assert.Contains("1000000 run limit", error.Message);
            Assert.Contains("\"reason\":\"run-budget\"", error.Message);
            Assert.Contains("\"runCount\":1000001", error.Message);
            Assert.Contains("\"runLimit\":1000000", error.Message);
        }
        else
        {
            var result = await Run(new RegionFromMaskNode(), mask, Node());
            using var image = result.Outputs["image"].Require<VisionImage>("image");
            Assert.Equal(1_000L, result.Outputs["count"].Require<long>("count"));
            Assert.Equal(1_000_000L, result.Outputs["area"].Require<long>("area"));
        }
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
    public async Task ContourFromMaskClassifiesNestedIslandsByHierarchyDepth()
    {
        using var mask = VisionImage.Own(new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0)));
        Cv2.Circle(mask.Mat, new Point(40, 40), 30, Scalar.All(255), -1);
        Cv2.Circle(mask.Mat, new Point(40, 40), 20, Scalar.All(0), -1);
        Cv2.Circle(mask.Mat, new Point(40, 40), 10, Scalar.All(255), -1);

        var result = await Run(new ContourFromMaskNode(), mask, Node(("retrieval", "all")));
        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Equal(3, contours.Count);
        var outer = Assert.Single(contours, contour => contour.ParentIndex == -1);
        var hole = Assert.Single(contours, contour => contour.ParentIndex == outer.Index);
        var island = Assert.Single(contours, contour => contour.ParentIndex == hole.Index);
        Assert.False(outer.IsHole);
        Assert.True(hole.IsHole);
        Assert.False(island.IsHole);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbabilityMasksUseInclusiveThresholdAtOriginalDepth(bool useDouble)
    {
        using var mask = VisionImage.Own(new Mat(18, 32, useDouble ? MatType.CV_64FC1 : MatType.CV_32FC1, Scalar.All(0.2)));
        Cv2.Rectangle(mask.Mat, new Rect(2, 4, 5, 5), Scalar.All(0.69), -1);
        Cv2.Rectangle(mask.Mat, new Rect(10, 4, 5, 5), Scalar.All(0.7), -1);
        Cv2.Rectangle(mask.Mat, new Rect(20, 4, 5, 5), Scalar.All(1), -1);
        var parameters = Node(("maskMode", "probability"), ("probabilityThreshold", "0.7"));

        var region = await Run(new RegionFromMaskNode(), mask, parameters);
        using var binary = region.Outputs["image"].Require<VisionImage>("image");
        var contour = await Run(new ContourFromMaskNode(), mask, parameters);
        Assert.Equal(50L, region.Outputs["area"].Require<long>("area"));
        Assert.Equal(2L, region.Outputs["count"].Require<long>("count"));
        Assert.Equal(2L, contour.Outputs["count"].Require<long>("count"));
        Assert.Equal((byte)0, binary.Mat.At<byte>(5, 3));
        Assert.Equal((byte)255, binary.Mat.At<byte>(5, 11));
        Assert.Equal((byte)255, binary.Mat.At<byte>(5, 21));
        Assert.Equal("probability", contour.Summary["maskMode"]);

        var invertedParameters = Node(("maskMode", "probability"), ("probabilityThreshold", "0.7"), ("invert", "true"), ("retrieval", "all"));
        var inverted = await Run(new RegionFromMaskNode(), mask, invertedParameters);
        using var invertedBinary = inverted.Outputs["image"].Require<VisionImage>("image");
        var invertedContours = await Run(new ContourFromMaskNode(), mask, invertedParameters);
        Assert.Equal(18L * 32 - 50, inverted.Outputs["area"].Require<long>("area"));
        Assert.Equal(3L, invertedContours.Outputs["count"].Require<long>("count"));
        Assert.Equal((byte)0, invertedBinary.Mat.At<byte>(5, 11));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    public async Task BothProbabilityNodesRejectInvalidPixels(double probability)
    {
        using var mask = VisionImage.Own(new Mat(4, 4, MatType.CV_64FC1, Scalar.All(probability)));
        var parameters = Node(("maskMode", "probability"));
        foreach (var executor in new IVisionNodeExecutor[] { new RegionFromMaskNode(), new ContourFromMaskNode() })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(executor, mask, parameters));
            Assert.Contains("finite and within 0..1", error.Message);
            if (executor is ContourFromMaskNode)
                Assert.Contains("stage=mask-validation", error.Message);
        }
    }

    [Fact]
    public async Task ProbabilityModeRejectsByteInputAndInvalidThreshold()
    {
        using var mask = Mask((4, 4, new Rect(0, 0, 4, 4)));
        var executor = new RegionFromMaskNode();
        var badImage = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(executor, mask, Node(("maskMode", "probability"))));
        Assert.Contains("float32 or float64", badImage.Message);
        var badThreshold = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(executor, mask, Node(("maskMode", "probability"), ("probabilityThreshold", "1.01"))));
        Assert.Contains("probabilityThreshold", badThreshold.Message);
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
