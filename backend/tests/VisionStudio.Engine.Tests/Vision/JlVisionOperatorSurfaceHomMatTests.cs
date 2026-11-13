using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 齐次变换节点的真执行验收：每个 JlHomMat2D 算子都要产出可解析的 3x3 矩阵，
/// 并用「旋转 90° 后平移」这类可手算的例子核对数值。
/// </summary>
public sealed class JlVisionOperatorSurfaceHomMatTests
{
    public static IEnumerable<object[]> Operators()
        => JlHomMatOperations.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Operators))]
    public async Task HomMatOperator_ProducesMatrix(string key)
    {
        var result = await Run(key,
            """{"operator":"__KEY__","tx":10,"ty":20,"phi":0.5,"theta":0.2,"sx":2,"sy":2,"px":1,"py":0,"qx":30,"qy":40,"mode":"x"}"""
                .Replace("__KEY__", key));

        Assert.Equal(key, Convert.ToString(result.Summary["operator"]));
        var matrix = Convert.ToString(result.Outputs["matrix"].Require<string>("matrix"))!;
        // determinant / toAffinePar / transPoint / transPixel / 投影变换只产出数值，没有矩阵。
        if (key is "determinant" or "toAffinePar" or "transPoint" or "transPixel"
            or "projectiveTransPixel" or "projectiveTransPoint2d")
        {
            Assert.Equal("null", matrix);
            Assert.True(double.IsFinite(result.Outputs["value"].Require<double>("value")));
            return;
        }
        var values = JsonSerializer.Deserialize<double[][]>(matrix)!;
        Assert.Equal(3, values.Length);
        Assert.Equal(3, values[0].Length);
        // 仿射矩阵最后一行的形状必须固定，否则平台 Transform2D 会拿错值。
        Assert.Equal(0d, values[2][0], 9);
        Assert.Equal(1d, values[2][2], 9);
    }

    [Fact]
    public async Task HomMatOperator_Rotate90_MapsPointAsExpected()
    {
        // 绕原点旋转 90°，点 (10, 0) 应落到 (0, 10)
        var result = await Run("transPoint",
            """{"operator":"transPoint","phi":1.5707963267948966,"px":10,"py":0}""");

        var values = JsonSerializer.Deserialize<Dictionary<string, double>>(
            Convert.ToString(result.Outputs["values"].Require<string>("values"))!)!;
        Assert.Equal(0d, values["column"], 3);
        Assert.Equal(10d, values["row"], 3);
    }

    [Fact]
    public async Task HomMatOperator_Translate_ProducesTransform2D()
    {
        var result = await Run("translate", """{"operator":"translate","tx":5,"ty":7}""");

        var transform = result.Outputs["transform"].Require<VisionTransform2D>("transform");
        Assert.Equal(5d, transform.H02, 6);
        Assert.Equal(7d, transform.H12, 6);
        Assert.Equal(1d, transform.H00, 6);
    }

    private static async Task<NodeExecutorResult> Run(string key, string json)
    {
        var node = new NodeDefinition("n1", "jlvision.homMat", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await new JLVisionHomMatNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);
    }
}
