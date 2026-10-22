using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 阈值节点的真执行验收：每个分割算子都要在一张合成图上产出区域（允许为空，但必须成功返回）。
/// </summary>
public sealed class JlVisionOperatorSurfaceThresholdTests
{
    public static IEnumerable<object[]> Operators()
        => JlThresholdOperations.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Operators))]
    public async Task ThresholdOperator_ProducesRegion(string key)
    {
        using var image = GrayImage();
        using var second = new Mat(image.Rows, image.Cols, MatType.CV_8UC1, Scalar.All(60));
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(image.Clone())),
            ["image2"] = VisionValue.Image(VisionImage.Own(second.Clone()))
        };
        var node = new NodeDefinition("n1", "jlvision.threshold", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"operator":"{{key}}","min":60,"max":200,"threshold":128,"size":15,"size2":15,"mult":1,"count":20,"mode":"","mode2":"dark"}"""));

        var result = await new JLVisionThresholdNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);

        var region = result.Outputs["region"].Require<VisionRegion>("region");
        Assert.Equal(image.Width, region.Width);
        Assert.Equal(key, Convert.ToString(result.Summary["operator"]));
        Assert.True(Convert.ToInt64(result.Outputs["area"].Require<long>("area")) >= 0);
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    [Fact]
    public async Task ThresholdOperator_GlobalThreshold_SelectsBrightRectangle()
    {
        using var image = GrayImage();
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(image.Clone())) };
        var node = new NodeDefinition("n1", "jlvision.threshold", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"threshold","min":150,"max":255}"""));

        var result = await new JLVisionThresholdNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);

        var area = Convert.ToInt64(result.Outputs["area"].Require<long>("area"));
        // 合成图里亮矩形 40x26 = 1040 像素，圆 14 半径约 615 像素，合计约 1655。
        Assert.InRange(area, 1500, 1800);
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    [Fact]
    public async Task ThresholdOperator_MissingSecondImage_IsRejected()
    {
        using var image = GrayImage();
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(image.Clone())) };
        var node = new NodeDefinition("n1", "jlvision.threshold", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"dynThreshold"}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new JLVisionThresholdNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None));
        Assert.Contains("requires an image2 input", error.Message, StringComparison.Ordinal);
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    private static Mat GrayImage()
    {
        var image = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(image, new Rect(14, 16, 40, 26), Scalar.All(200), -1);
        Cv2.Circle(image, new Point(68, 66), 14, Scalar.All(180), -1);
        return image;
    }
}
