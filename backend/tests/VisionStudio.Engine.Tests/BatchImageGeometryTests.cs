using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 图像几何/梯度/距离变换三个节点（2026-10-28）。刻意用像素级断言而不是"不抛异常"，
/// 因为镜像轴选错、梯度方向符号反了、距离变换取错侧，都会静默产出"看起来合理"的图。
/// </summary>
public sealed class BatchImageGeometryTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public async Task Flip_HorizontalMirrorsColumnsAndKeepsSize()
    {
        using var img = new Mat(2, 3, MatType.CV_8UC1);
        img.Set(0, 0, (byte)1); img.Set(0, 1, (byte)2); img.Set(0, 2, (byte)3);
        img.Set(1, 0, (byte)4); img.Set(1, 1, (byte)5); img.Set(1, 2, (byte)6);

        var result = await new ImageFlipNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img) }), Def("image.flip", """{"mode":"horizontal"}"""), CancellationToken.None);

        var mat = ((VisionImage)result.Outputs["image"].Value!).Mat;
        Assert.Equal(3, mat.Width);
        Assert.Equal(2, mat.Height);
        Assert.Equal((byte)3, mat.At<byte>(0, 0));
        Assert.Equal((byte)1, mat.At<byte>(0, 2));
        Assert.Equal((byte)6, mat.At<byte>(1, 0));
        Assert.Equal((byte)4, mat.At<byte>(1, 2));
        Assert.Contains("image.flip", (string)result.Outputs["diagnostics"].Value!);
        Assert.Equal(1, (int)result.Summary["flipCode"]!);
    }

    [Fact]
    public async Task Flip_VerticalAndBothDifferFromHorizontal()
    {
        using var img = new Mat(2, 3, MatType.CV_8UC1);
        img.Set(0, 0, (byte)1); img.Set(1, 0, (byte)4);

        var vertical = await new ImageFlipNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img) }), Def("image.flip", """{"mode":"vertical"}"""), CancellationToken.None);
        var vmat = ((VisionImage)vertical.Outputs["image"].Value!).Mat;
        Assert.Equal((byte)4, vmat.At<byte>(0, 0));
        Assert.Equal((byte)1, vmat.At<byte>(1, 0));

        var both = await new ImageFlipNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img) }), Def("image.flip", """{"mode":"both"}"""), CancellationToken.None);
        var bmat = ((VisionImage)both.Outputs["image"].Value!).Mat;
        Assert.Equal((byte)4, bmat.At<byte>(0, 2));
        Assert.Equal((byte)1, bmat.At<byte>(1, 2));
    }

    [Fact]
    public async Task Flip_RejectsUnknownModeAndEmptyImage()
    {
        using var img = new Mat(2, 3, MatType.CV_8UC1, Scalar.All(7));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageFlipNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(img) }), Def("image.flip", """{"mode":"diagonal"}"""), CancellationToken.None));

        using var empty = new Mat();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageFlipNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(empty) }), Def("image.flip"), CancellationToken.None));
    }

    [Fact]
    public async Task Gradient_StepEdgeGivesPositiveMagnitudeAndZeroDirection()
    {
        // 左暗右亮的竖直阶跃：dx 为正、dy 为 0，梯度方向应为 0 度。
        using var img = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(img, new Rect(16, 0, 16, 32), Scalar.All(200), -1);

        var result = await new ImageGradientNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img) }),
            Def("image.gradient", """{"operator":"sobel","kernelSize":3,"emitDirection":true}"""),
            CancellationToken.None);

        var magnitude = ((VisionImage)result.Outputs["image"].Value!).Mat;
        Assert.True((double)result.Outputs["maxMagnitude"].Value! > 100);
        var peak = 0d;
        for (var x = 12; x < 20; x++) peak = Math.Max(peak, magnitude.At<byte>(16, x));
        Assert.True(peak > 100, $"expected a strong edge near x=16, peak={peak}");
        Assert.True(magnitude.At<byte>(16, 2) < 5, "flat region must stay near zero");

        var direction = ((VisionImage)result.Outputs["direction"].Value!).Mat;
        Assert.Equal(MatType.CV_32FC1, direction.Type());
        var angleAtEdge = direction.At<float>(16, 16);
        Assert.True(Math.Abs(angleAtEdge) < 1.0 || Math.Abs(angleAtEdge - 360) < 1.0,
            $"expected 0/360 deg direction on a vertical step, got {angleAtEdge}");
        Assert.Equal("degree", result.Outputs["directionUnit"].Value);
    }

    [Fact]
    public async Task Gradient_ScharrRejectsNonThreeKernelAndStretchSaturates()
    {
        using var img = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(8, 0, 8, 16), Scalar.All(90), -1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageGradientNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(img) }),
                Def("image.gradient", """{"operator":"scharr","kernelSize":5}"""), CancellationToken.None));

        var stretched = await new ImageGradientNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img) }),
            Def("image.gradient", """{"operator":"scharr","kernelSize":3,"scaleMode":"stretch","emitDirection":false}"""),
            CancellationToken.None);
        Assert.False(stretched.Outputs.ContainsKey("direction"));
        var mat = ((VisionImage)stretched.Outputs["image"].Value!).Mat;
        Assert.Equal(MatType.CV_8UC1, mat.Type());
        var peak = 0;
        for (var x = 0; x < 16; x++) peak = Math.Max(peak, (int)mat.At<byte>(8, x));
        Assert.True(peak >= 250, $"stretch should push the P99 edge to near 255, peak={peak}");
    }

    [Fact]
    public async Task DistanceTransform_ForegroundMatchesHalfWidth()
    {
        using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(22, 22, 20, 20), Scalar.All(255), -1);

        var result = await new ImageDistanceTransformNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(mask) }),
            Def("image.distanceTransform", """{"distanceType":"l2","maskSize":5,"mode":"foreground"}"""),
            CancellationToken.None);

        var distance = ((VisionImage)result.Outputs["image"].Value!).Mat;
        Assert.Equal(MatType.CV_32FC1, distance.Type());
        Assert.Equal(400L, (long)result.Outputs["foregroundCount"].Value!);
        var centre = distance.At<float>(31, 31);
        Assert.InRange(centre, 9.0f, 11.0f);
        // 20x20 方块的最大内切半径出现在中心，约 10 像素。
        Assert.InRange((double)result.Outputs["maxDistance"].Value!, 9.5, 10.5);
        Assert.Contains("image.distanceTransform", (string)result.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task DistanceTransform_BackgroundMeasuresFreeSpaceAndRejectsColor()
    {
        using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(22, 22, 20, 20), Scalar.All(255), -1);

        var background = await new ImageDistanceTransformNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(mask) }),
            Def("image.distanceTransform", """{"distanceType":"l2","maskSize":3,"mode":"background"}"""),
            CancellationToken.None);
        var distance = ((VisionImage)background.Outputs["image"].Value!).Mat;
        // 反转后零像素是方块内部，所以方块中心距离为 0，远处背景到方块的距离最大。
        Assert.True(distance.At<float>(0, 0) > distance.At<float>(31, 31));
        Assert.True(distance.At<float>(31, 31) < 0.001f);

        using var color = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(10));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageDistanceTransformNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(color) }), Def("image.distanceTransform"), CancellationToken.None));
    }
}
