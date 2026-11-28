using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// P0 acceptance for the image-domain contracts: a byte conversion must report how much signal it
/// destroyed, and a depth reduction must refuse to silently truncate. Both used to return a
/// "looks fine" image while the measurement signal had already been clipped away.
/// </summary>
public sealed class P0ImageContractTests
{
    [Fact]
    public async Task FlatField_ByteOutput_ReportsSaturationAndHonoursRatioGate()
    {
        // 增益 50 与 200 各占一半：自动目标取增益均值 125，于是增益 50 的像素被放大到 500。
        using var image = Gray8(200, 200, 200, 200);
        using var bright = Gray8(50, 50, 200, 200);

        var reported = await RunFlatField(image, bright, """{"referenceMode":"gain","outputType":"byte"}""");
        using var _ = reported.Outputs["image"].Require<VisionImage>("image");

        Assert.Equal(2L, reported.Outputs["saturatedPixelCount"].Require<long>("saturatedPixelCount"));
        Assert.Equal(0.5, reported.Outputs["saturatedRatio"].Require<double>("saturatedRatio"), 6);
        // 500 → 255，裁掉 245：这是"信号被饱和"的可核对证据。
        Assert.Equal(245d, reported.Outputs["maxClipAmount"].Require<double>("maxClipAmount"), 3);
        Assert.Equal("byte", Convert.ToString(reported.Summary["outputType"]));

        // 门限必须让节点失败，而不是交出一张看似有效的图。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunFlatField(image, bright, """{"referenceMode":"gain","outputType":"byte","maxSaturatedRatio":0.25}"""));
        Assert.Contains("saturates 2 pixels", error.Message, StringComparison.Ordinal);
        Assert.Contains("maxSaturatedRatio=0.25", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlatField_PartialCorrection_RestoresUniformity()
    {
        // 图像恰为参考的一半：校正后每个像素都应回到同一灰度（均值 25/50*125=62.5）。
        using var image = Gray8(25, 50, 75, 100);
        using var bright = Gray8(50, 100, 150, 200);

        var status = await RunFlatField(image, bright, """{"referenceMode":"gain","outputType":"real"}""");
        using var _ = status.Outputs["image"].Require<VisionImage>("image");

        Assert.Equal(62.5, status.Outputs["correctedMean"].Require<double>("correctedMean"), 3);
        Assert.Equal(0d, status.Outputs["correctedStd"].Require<double>("correctedStd"), 3);
        Assert.Equal(0L, status.Outputs["saturatedPixelCount"].Require<long>("saturatedPixelCount"));
    }

    [Fact]
    public async Task DepthScale_RefusesSilentTruncation_AndReportsOverflowWhenAcknowledged()
    {
        using var image = Gray16(0, 300, 1000, 60000);

        // 默认 overflowPolicy=fail：3 个像素会被裁掉，必须拒绝而不是交出一张 8 位图。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunDepthScale(image, """{"inputBits":16,"outputBits":"8","scale":1}"""));
        Assert.Contains("would clip 3 pixels", error.Message, StringComparison.Ordinal);

        // 显式确认裁剪后才允许写出，并报告预计溢出量。
        var acknowledged = await RunDepthScale(image, """{"inputBits":16,"outputBits":"8","scale":1,"overflowPolicy":"saturate"}""");
        using var _ = acknowledged.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(3L, acknowledged.Outputs["overflowPixelCount"].Require<long>("overflowPixelCount"));
        Assert.Equal(0.75, acknowledged.Outputs["overflowRatio"].Require<double>("overflowRatio"), 6);
        Assert.Equal(59745d, acknowledged.Outputs["maxClipAmount"].Require<double>("maxClipAmount"), 3);
        Assert.Equal("saturate", Convert.ToString(acknowledged.Summary["overflowPolicy"]));

        // 自动缩放（255/65535）不应产生溢出：证明保护只对真正的裁剪生效，不误伤正常转换。
        var automatic = await RunDepthScale(image, """{"inputBits":16,"outputBits":"8"}""");
        using var __ = automatic.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0L, automatic.Outputs["overflowPixelCount"].Require<long>("overflowPixelCount"));
    }

    private static VisionImage Gray8(params byte[] values)
    {
        var mat = new Mat(1, values.Length, MatType.CV_8UC1);
        for (var x = 0; x < values.Length; x++) mat.Set(0, x, values[x]);
        return VisionImage.Own(mat);
    }

    private static VisionImage Gray16(params ushort[] values)
    {
        var mat = new Mat(1, values.Length, MatType.CV_16UC1);
        for (var x = 0; x < values.Length; x++) mat.Set(0, x, values[x]);
        return VisionImage.Own(mat);
    }

    private static async Task<NodeExecutorResult> RunFlatField(VisionImage image, VisionImage bright, string parameters)
        => await new FlatFieldCorrectNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(image),
                ["bright"] = VisionValue.Image(bright)
            }), Definition("image.flatFieldCorrect", parameters), CancellationToken.None);

    private static async Task<NodeExecutorResult> RunDepthScale(VisionImage image, string parameters)
        => await new DepthScaleNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) }),
            Definition("image.depthScale", parameters), CancellationToken.None);

    private static NodeDefinition Definition(string type, string parameters)
        => new("n", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters));
}
