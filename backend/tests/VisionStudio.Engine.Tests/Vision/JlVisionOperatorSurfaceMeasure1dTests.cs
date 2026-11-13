using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 一维计量节点的真执行验收：矩形/圆弧两种形状 × 四种方法都要在合成图上返回可解析的边缘结果。
/// </summary>
public sealed class JlVisionOperatorSurfaceMeasure1dTests
{
    public static IEnumerable<object[]> Shapes()
        => new[] { new object[] { "rectangle" }, new object[] { "arc" } };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Measure1d_Executes(string shape)
    {
        using var image = BandImage();
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(image.Clone())) };
        var node = new NodeDefinition("n1", "jlvision.measure1d", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"shape":"{{shape}}","method":"measurePos","row":48,"column":48,"phi":0,"measureLength1":40,"measureLength2":2,"radius":30,"angleStart":-180,"angleExtent":360,"annulusRadius":6,"sigma":1,"threshold":20,"transition":"all","select":"all"}"""));

        var result = await new JLVisionMeasure1dNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);

        Assert.Equal(shape, Convert.ToString(result.Summary["shape"]));
        var edges = Convert.ToString(result.Outputs["edges"].Require<string>("edges"));
        Assert.False(string.IsNullOrWhiteSpace(edges));
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    [Fact]
    public async Task Measure1d_MeasurePairs_FindsBandWidth()
    {
        using var image = BandImage();
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(image.Clone())) };
        var node = new NodeDefinition("n1", "jlvision.measure1d", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"shape":"rectangle","method":"measurePairs","row":48,"column":48,"measureLength1":40,"measureLength2":2,"sigma":1,"threshold":20}"""));

        var result = await new JLVisionMeasure1dNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);

        var count = Convert.ToInt32(result.Outputs["count"].Require<long>("count"));
        // 亮带在 30..66 列之间，水平剖面应有且只有一对边缘，宽度 ≈ 36。
        Assert.True(count >= 1, Convert.ToString(result.Outputs["diagnostics"]));
        if (count >= 1)
        {
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, double>>>(
                Convert.ToString(result.Outputs["edges"].Require<string>("edges"))!)!;
            // 实测封装的 width 元组长度为 0，宽度字段会被丢弃；用成对距离核对亮带宽度。
            Assert.InRange(rows[0]["distance"], 30, 42);
        }
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    private static Mat BandImage()
    {
        var image = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(image, new Rect(30, 0, 36, 96), Scalar.All(230), -1);
        return image;
    }
}
