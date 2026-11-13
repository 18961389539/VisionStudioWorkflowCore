using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 几何节点的真执行验收：表里每个 JlMisc 算子都要用一组标量参数算出有限数值。
/// </summary>
public sealed class JlVisionOperatorSurfaceGeometryTests
{
    public static IEnumerable<object[]> Operators()
        => JlGeometryOperations.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Operators))]
    public async Task GeometryOperator_ComputesFiniteValue(string key)
    {
        var node = new NodeDefinition("n1", "jlvision.geometry", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"operator":"{{key}}","row1":0,"column1":0,"row2":30,"column2":40,"row3":0,"column3":40,"row4":30,"column4":0,"radius1":12,"radius2":8,"phi":0,"angle":1.5708,"size":1,"mode":""}"""));

        var result = await new JLVisionGeometryNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        var value = result.Outputs["value"].Require<double>("value");
        Assert.False(double.IsNaN(value), $"{key} returned NaN");
        Assert.False(double.IsInfinity(value), $"{key} returned Infinity");
        Assert.Equal(key, Convert.ToString(result.Summary["operator"]));
    }

    [Fact]
    public async Task GeometryOperator_PointToPointDistance_MatchesPythagoras()
    {
        var node = new NodeDefinition("n1", "jlvision.geometry", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"distancePp","row1":0,"column1":0,"row2":30,"column2":40}"""));

        var result = await new JLVisionGeometryNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        Assert.Equal(50d, result.Outputs["value"].Require<double>("value"), 6);
    }

    [Fact]
    public async Task GeometryOperator_LineIntersection_ReturnsCrossingPoint()
    {
        // 线 A：(0,0) → (30,40)；线 B：(0,40) → (30,0) 交于 (15,20)
        var node = new NodeDefinition("n1", "jlvision.geometry", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"intersectionLl","row1":0,"column1":0,"row2":30,"column2":40,"row3":0,"column3":40,"row4":30,"column4":0}"""));

        var result = await new JLVisionGeometryNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        var values = Convert.ToString(result.Outputs["values"].Require<string>("values"))!;
        var payload = JsonSerializer.Deserialize<Dictionary<string, double>>(values)!;
        Assert.Equal(15d, payload["row"], 3);
        Assert.Equal(20d, payload["column"], 3);
        Assert.Equal(0d, payload["isParallel"], 6);
    }
}
