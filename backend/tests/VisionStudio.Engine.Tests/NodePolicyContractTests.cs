using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Behavioral tests for controls that used to be declared but ignored.</summary>
public sealed class NodePolicyContractTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Theory]
    [InlineData("bt601", 76)]
    [InlineData("bt709", 54)]
    [InlineData("avg", 85)]
    public async Task Grayscale_UsesSelectedLumaWeights(string weights, int expected)
    {
        using var source = new Mat(1, 1, MatType.CV_8UC3, new Scalar(0, 0, 255));
        var result = await new GrayscaleNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(source) }),
            Def("image.grayscale", $$"""{"mode":"luminance","weights":"{{weights}}"}"""),
            CancellationToken.None);
        var image = result.Outputs["image"].Require<VisionImage>("image");
        try
        {
            Assert.InRange(image.Mat.At<byte>(0, 0), expected - 2, expected + 2);
        }
        finally { image.Dispose(); }
    }

    [Theory]
    [InlineData("bgr", 0.117647f, 0.078431f, 0.039216f)]
    [InlineData("rgb", 0.039216f, 0.078431f, 0.117647f)]
    public async Task ColorConvert_HonorsChannelOrder(string order, float r, float g, float b)
    {
        using var source = new Mat(1, 1, MatType.CV_8UC3, new Scalar(10, 20, 30));
        var result = await new ImageColorConvertNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(source) }),
            Def("image.colorConvert", $$"""{"space":"rgb","channelOrder":"{{order}}"}"""),
            CancellationToken.None);
        var c1 = result.Outputs["channel1"].Require<VisionImage>("channel1");
        var c2 = result.Outputs["channel2"].Require<VisionImage>("channel2");
        var c3 = result.Outputs["channel3"].Require<VisionImage>("channel3");
        try
        {
            Assert.InRange(c1.Mat.At<float>(0, 0), r - 0.001f, r + 0.001f);
            Assert.InRange(c2.Mat.At<float>(0, 0), g - 0.001f, g + 0.001f);
            Assert.InRange(c3.Mat.At<float>(0, 0), b - 0.001f, b + 0.001f);
        }
        finally { c1.Dispose(); c2.Dispose(); c3.Dispose(); }
    }

    [Theory]
    [InlineData("away", 3)]
    [InlineData("even", 2)]
    public async Task NumberConstant_HonorsRoundingMode(string rounding, int expected)
    {
        var result = await new ValueNumberNode().ExecuteAsync(
            Ctx(new()),
            Def("value.number", $$"""{"value":2.5,"rounding":"{{rounding}}"}"""),
            CancellationToken.None);
        Assert.Equal(expected, Convert.ToInt32(result.Outputs["integer"].Value));
    }

    [Theory]
    [InlineData("inclusive", true)]
    [InlineData("exclusive", false)]
    public async Task RangeCheck_HonorsBoundPolicy(string policy, bool expected)
    {
        var result = await new ValueRangeNode().ExecuteAsync(
            Ctx(new() { ["value"] = VisionValue.Double(1) }),
            Def("value.range", $$"""{"min":0,"max":1,"boundPolicy":"{{policy}}"}"""),
            CancellationToken.None);
        Assert.Equal(expected, (bool)result.Outputs["pass"].Value!);
    }
}
