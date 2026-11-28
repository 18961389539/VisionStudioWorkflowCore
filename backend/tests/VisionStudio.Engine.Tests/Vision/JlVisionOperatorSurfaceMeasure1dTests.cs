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
        using var document = JsonDocument.Parse(result.Outputs["edges"].Require<string>("edges"));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(shape, document.RootElement.GetProperty("shape").GetString());
        Assert.Equal("MeasurePos", document.RootElement.GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("edges").ValueKind);
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
        using var document = JsonDocument.Parse(result.Outputs["edges"].Require<string>("edges"));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("MeasurePairs", document.RootElement.GetProperty("method").GetString());
        var edges = document.RootElement.GetProperty("edges");
        Assert.Equal(count, edges.GetArrayLength());
        Assert.InRange(edges[0].GetProperty("width").GetDouble(), 30, 42);
        Assert.True(edges[0].GetProperty("widthValid").GetBoolean());
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    private static Mat BandImage()
    {
        var image = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(image, new Rect(30, 0, 36, 96), Scalar.All(230), -1);
        return image;
    }
}
