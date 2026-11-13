using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 矩阵节点的真执行验收：每个线性代数算子都要在 3x3 可逆矩阵上跑通并给出可解析的 JSON 结果。
/// </summary>
public sealed class JlVisionOperatorSurfaceMatrixTests
{
    public static IEnumerable<object[]> Operators()
        => JlMatrixOperations.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Operators))]
    public async Task MatrixOperator_Executes(string key)
    {
        var result = await Run(key,
            """{"operator":"__KEY__","matrixA":"[[4,1,0],[1,3,1],[0,1,2]]","matrixB":"[[2,0.5,1],[1,2,0.5],[0.5,1,2]]","scalar":2,"row":0,"column":0,"rows":2,"columns":2,"mode":""}"""
                .Replace("__KEY__", key));

        Assert.Equal(key, Convert.ToString(result.Summary["operator"]));
        var matrix = Convert.ToString(result.Outputs["matrix"].Require<string>("matrix"));
        Assert.False(string.IsNullOrWhiteSpace(matrix));
        Assert.False(double.IsNaN(result.Outputs["value"].Require<double>("value")));
    }

    [Fact]
    public async Task MatrixOperator_Determinant_MatchesAnalyticValue()
    {
        // det([[4,1,0],[1,3,1],[0,1,2]]) = 4*(6-1) - 1*(2-0) = 20 - 2 = 18
        var result = await Run("determinant",
            """{"operator":"determinant","matrixA":"[[4,1,0],[1,3,1],[0,1,2]]","mode":"general"}""");

        Assert.Equal(18d, result.Outputs["value"].Require<double>("value"), 6);
    }

    [Fact]
    public async Task MatrixOperator_InvertTimesOriginal_IsIdentity()
    {
        var inverted = await Run("invert",
            """{"operator":"invert","matrixA":"[[4,1,0],[1,3,1],[0,1,2]]","scalar":1e-9,"mode":"general"}""");
        var inverse = Convert.ToString(inverted.Outputs["matrix"].Require<string>("matrix"))!;

        var product = await Run("mult",
            $$"""{"operator":"mult","matrixA":"[[4,1,0],[1,3,1],[0,1,2]]","matrixB":{{JsonSerializer.Serialize(inverse)}},"mode":"algebraic"}""");
        var text = Convert.ToString(product.Outputs["matrix"].Require<string>("matrix"))!;
        var values = JsonSerializer.Deserialize<double[][]>(text)!;

        Assert.Equal(1d, values[0][0], 6);
        Assert.Equal(1d, values[1][1], 6);
        Assert.Equal(1d, values[2][2], 6);
        Assert.Equal(0d, values[0][1], 6);
        Assert.Equal(0d, values[2][0], 6);
    }

    [Fact]
    public async Task MatrixOperator_RaggedMatrix_IsRejectedBeforeNativeCall()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run("transpose", """{"operator":"transpose","matrixA":"[[1,2],[3]]"}"""));
        Assert.Contains("ragged", error.Message, StringComparison.Ordinal);
    }

    private static async Task<NodeExecutorResult> Run(string key, string json)
    {
        var node = new NodeDefinition("n1", "jlvision.matrixOp", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await new JLVisionMatrixNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);
    }
}
