using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 通用 JLVision 算子节点的真执行验收：表里的每一个算子都必须在一张合成图上跑通并输出非空图像。
/// 这条用例是"接入"与"只写了一行调用"之间的分界线——加了算子却没验证，这里会直接红。
/// </summary>
public sealed class JlVisionOperatorSurfaceTests
{
    public static IEnumerable<object[]> ImageOperators()
        => JlImageOperations.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(ImageOperators))]
    public async Task ImageOperator_ExecutesOnSyntheticImage(string key)
    {
        using var source = InputFor(key);

        var result = await Run(new JLVisionImageOpNode(), source,
            JsonSerializer.Serialize(new { @operator = key, size = 3, size2 = 3, mult = 1, offset = 2, threshold = 128, count = 1, mode = "", lutJson = JsonSerializer.Serialize(Enumerable.Range(0, 256)) }));

        var image = result.Outputs["image"].Require<VisionImage>("image");
        Assert.False(image.Mat.Empty());
        Assert.Equal(key, Convert.ToString(result.Summary["operator"]));
        Assert.Equal("jlvision", Convert.ToString(result.Summary["algorithmBackend"]));
        Assert.True(Convert.ToInt32(result.Summary["outputWidth"]) > 0);
    }

    [Fact]
    public async Task ImageOperator_UnknownKey_ReportsKnownOperators()
    {
        using var source = GrayImage();
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(source.Clone())) };
        var node = new NodeDefinition("n1", "jlvision.imageOp", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"nope"}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new JLVisionImageOpNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None));

        Assert.Contains("Unknown JLVision image operator", error.Message, StringComparison.Ordinal);
        Assert.Contains("abs", error.Message, StringComparison.Ordinal);
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    [Fact]
    public async Task ImageOperator_SecondInput_IsUsed()
    {
        using var primary = GrayImage();
        using var second = new Mat(primary.Rows, primary.Cols, MatType.CV_8UC1, Scalar.All(10));
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(primary.Clone())),
            ["image2"] = VisionValue.Image(VisionImage.Own(second.Clone()))
        };
        var node = new NodeDefinition("n1", "jlvision.imageOp", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"min"}"""));

        var result = await new JLVisionImageOpNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
        Assert.True(Convert.ToBoolean(result.Summary["secondInputConnected"]));
        var image = result.Outputs["image"].Require<VisionImage>("image");
        // min(primary, 10) 必须把亮区压到 10
        Assert.Equal(10, image.Mat.At<byte>(image.Mat.Rows / 2, image.Mat.Cols / 2));
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    /// <summary>按算子声明的像素类型准备输入，避免用错类型掩盖真实能力。</summary>
    private static Mat InputFor(string key)
    {
        var op = JlImageOperations.All[key];
        return key switch
        {
            "channelsToImage" => ColorImage(),
            _ when op.Pixels == JlPixelKind.Color => ColorImage(),
            _ when op.Pixels == JlPixelKind.UnitReal => UnitRealImage(),
            _ when op.Pixels == JlPixelKind.Real => RealImage(),
            _ => GrayImage()
        };
    }

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Mat source, string json)
    {
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(source.Clone())) };
        try
        {
            var node = new NodeDefinition("n1", executor.Type, null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
            if (node.GetString("operator", "") is "compose3" or "rgb3ToGray")
            {
                inputs["image2"] = VisionValue.Image(VisionImage.Own(source.Clone()));
                inputs["image3"] = VisionValue.Image(VisionImage.Own(source.Clone()));
            }
            return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
        }
    }

    private static Mat GrayImage()
    {
        var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(image, new Rect(10, 12, 30, 18), Scalar.All(200), -1);
        Cv2.Circle(image, new Point(44, 44), 9, Scalar.All(150), -1);
        Cv2.Line(image, new Point(2, 60), new Point(62, 30), Scalar.All(90), 2);
        return image;
    }

    private static Mat RealImage()
    {
        var image = new Mat(64, 64, MatType.CV_32FC1, Scalar.All(0.25));
        Cv2.Rectangle(image, new Rect(10, 12, 30, 18), Scalar.All(0.8), -1);
        Cv2.Circle(image, new Point(44, 44), 9, Scalar.All(0.6), -1);
        return image;
    }

    private static Mat UnitRealImage()
    {
        var image = new Mat(64, 64, MatType.CV_32FC1, Scalar.All(0.1));
        Cv2.Rectangle(image, new Rect(10, 12, 30, 18), Scalar.All(-0.5), -1);
        Cv2.Circle(image, new Point(44, 44), 9, Scalar.All(0.5), -1);
        return image;
    }

    private static Mat ColorImage()
    {
        var image = new Mat(64, 64, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Cv2.Rectangle(image, new Rect(10, 12, 30, 18), new Scalar(200, 180, 160), -1);
        Cv2.Circle(image, new Point(44, 44), 9, new Scalar(90, 150, 210), -1);
        return image;
    }
}
