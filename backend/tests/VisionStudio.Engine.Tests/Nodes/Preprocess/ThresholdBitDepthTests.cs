using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 批次 B 回归：16 位输入的位深映射（image.threshold 的 inputBitDepth）。
/// </summary>
public sealed class ThresholdBitDepthTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public async Task UInt16Threshold_AutoKeepsLegacyFullRangeMapping()
    {
        // 12 位相机典型值 1024 / 3072：auto 按满量程 1/257 压到 4 / 12，两者都低于阈值 128。
        using var img = new Mat(100, 200, MatType.CV_16UC1, Scalar.All(1024));
        Cv2.Rectangle(img, new Rect(100, 0, 100, 100), Scalar.All(3072), -1);

        var result = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"manual","threshold":128}"""), CancellationToken.None);

        Assert.Equal(0d, Convert.ToDouble(result.Outputs["foregroundRatio"].Value), 9);
        using var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0, Cv2.CountNonZero(output.Mat));
        Assert.Equal("auto", Convert.ToString(result.Summary["inputBitDepth"]));
    }

    [Fact]
    public async Task UInt16Threshold_TwelveBitDepthKeepsSensorContrast()
    {
        // 同一张图，声明 12 位后 1024→64、3072→191，阈值 128 恰好分开两半。
        using var img = new Mat(100, 200, MatType.CV_16UC1, Scalar.All(1024));
        Cv2.Rectangle(img, new Rect(100, 0, 100, 100), Scalar.All(3072), -1);

        var result = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"manual","threshold":128,"inputBitDepth":"12"}"""), CancellationToken.None);

        Assert.Equal(0.5d, Convert.ToDouble(result.Outputs["foregroundRatio"].Value), 9);
        using var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0, Cv2.CountNonZero(new Mat(output.Mat, new Rect(0, 0, 100, 100))));
        Assert.Equal(100 * 100, Cv2.CountNonZero(new Mat(output.Mat, new Rect(100, 0, 100, 100))));
        // 12 位映射系数 255/4095，不再是 1/257。
        Assert.Equal(255d / 4095d, Convert.ToDouble(result.Summary["byteScale"]), 12);
    }

    [Fact]
    public async Task InputBitDepth_RejectsValuesOutsideEightToSixteen()
    {
        // 目录里只提供 auto/8/10/12/14/16；手改 JSON 时运行期仍按 8..16 校验，越界与非法文本都失败。
        using var img = new Mat(64, 64, MatType.CV_16UC1, Scalar.All(1024));
        foreach (var invalid in new[] { "20", "7", "abc" })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                    Def("image.threshold", $$"""{"mode":"manual","threshold":128,"inputBitDepth":"{{invalid}}"}"""), CancellationToken.None));
            Assert.Contains("inputBitDepth", error.Message);
            Assert.Contains("8..16", error.Message);
        }
    }

}
