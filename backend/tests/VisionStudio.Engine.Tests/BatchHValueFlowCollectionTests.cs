using System.Text.Json;
using VisionStudio.Engine.Nodes;
namespace VisionStudio.Engine.Tests;
public sealed class BatchHValueFlowCollectionTests
{
    private static NodeDefinition Def(string type, string json="{}") => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string,VisionValue> inputs) => new(inputs);
    private static VisionDetection Det(double area, double conf=0.9) => new(0, area, 0, 10, 10, area, 40, 0.8, new VisionCircle(area,0,5), new VisionRectangle2D(0,0,10,10), conf, 1);

    public static IEnumerable<object[]> InvalidCollectionIntegerParameters()
    {
        (string Type, string Parameter)[] parameters =
        [
            ("collection.detections.at", "index"), ("collection.detections.sort", "topN"),
            ("collection.detections.filter", "classId"), ("flow.forEach", "maxItems")
        ];
        foreach (var (type, parameter) in parameters)
            foreach (var value in new[] { "0.4", "-0.4", "1.5", "\"2\"", "null", "true", "{}", "[]", "1e400", "2147483648" })
                yield return [type, parameter, value];
    }

    [Theory]
    [MemberData(nameof(InvalidCollectionIntegerParameters))]
    public async Task CollectionIntegerParameters_RejectInvalidValuesInsteadOfRoundingOrDefaulting(string type, string parameter, string value)
    {
        var inputs = Ctx(new() { ["detections"] = VisionValue.Detections(new[] { Det(10), Det(20), Det(30) }) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectionExecutor(type).ExecuteAsync(inputs, Def(type, $"{{\"{parameter}\":{value}}}"), CancellationToken.None));
        Assert.Contains(parameter, error.Message);
        Assert.Contains("integer", error.Message);
    }

    [Theory]
    [InlineData("collection.detections.at", "index", "1.0", 1, 20d)]
    [InlineData("collection.detections.at", "index", "199", 0, 0d)]
    [InlineData("collection.detections.sort", "topN", "1e0", 1, 30d)]
    [InlineData("collection.detections.sort", "topN", "0.0", 3, 30d)]
    [InlineData("collection.detections.filter", "classId", "2.0", 1, 30d)]
    [InlineData("collection.detections.filter", "classId", "-1.0", 3, 10d)]
    [InlineData("flow.forEach", "maxItems", "5e2", 3, 10d)]
    [InlineData("flow.forEach", "maxItems", "3.0", 3, 10d)]
    public async Task CollectionIntegerParameters_AcceptWholeJsonNumbers(string type, string parameter, string value, int expectedCount, double expectedArea)
    {
        var result = await CollectionExecutor(type).ExecuteAsync(
            Ctx(new() { ["detections"] = VisionValue.Detections(new[] { Det(10) with { ClassId = 0 }, Det(20) with { ClassId = 1 }, Det(30) with { ClassId = 2 } }) }),
            Def(type, $"{{\"{parameter}\":{value}}}"), CancellationToken.None);
        if (type == "collection.detections.at")
        {
            Assert.Equal(expectedCount > 0, result.Outputs["found"].Require<bool>("found"));
            Assert.Equal(JsonSerializer.Deserialize<double>(value), (double)result.Outputs["index"].Require<long>("index"));
            if (expectedCount > 0) Assert.Equal(expectedArea, result.Outputs["area"].Require<double>("area"));
            else Assert.False(result.Outputs.ContainsKey("area"));
        }
        else
        {
            Assert.Equal(expectedCount, result.Outputs["count"].Require<long>("count"));
            var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
            Assert.Equal(expectedCount, detections.Count);
            Assert.Equal(expectedArea, detections[0].Area);
            Assert.Equal(Enumerable.Range(0, expectedCount), detections.Select(detection => detection.Index));
        }
        using var diagnostics = JsonDocument.Parse(result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(diagnostics.RootElement.GetProperty("valid").GetBoolean());
    }

    [Theory]
    [InlineData("collection.detections.at")]
    [InlineData("collection.detections.sort")]
    [InlineData("collection.detections.filter")]
    [InlineData("flow.forEach")]
    public async Task CollectionIntegerParameters_PreserveOmittedDefaults(string type)
    {
        var result = await CollectionExecutor(type).ExecuteAsync(
            Ctx(new() { ["detections"] = VisionValue.Detections(new[] { Det(10), Det(20), Det(30) }) }),
            Def(type), CancellationToken.None);
        if (type == "collection.detections.at")
        {
            Assert.True(result.Outputs["found"].Require<bool>("found"));
            Assert.Equal(0L, result.Outputs["index"].Require<long>("index"));
            Assert.Equal(10d, result.Outputs["x"].Require<double>("x"));
        }
        else
        {
            Assert.Equal(3L, result.Outputs["count"].Require<long>("count"));
            if (type == "collection.detections.sort")
                Assert.Equal(new[] { 30d, 20d, 10d }, result.Outputs["detections"]
                    .Require<IReadOnlyList<VisionDetection>>("detections").Select(detection => detection.Area));
        }
    }

    private static IVisionNodeExecutor CollectionExecutor(string type) => type switch
    {
        "collection.detections.at" => new DetectionAtIndexNode(),
        "collection.detections.sort" => new DetectionSortNode(),
        "collection.detections.filter" => new DetectionFilterNode(),
        "flow.forEach" => new DetectionForEachNode(),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    [Fact] public async Task ValueNumberArithmetic_StrictOps()
    {
        var n=await new ValueNumberNode().ExecuteAsync(Ctx(new()),Def("value.number","{\"value\":2.5}"),CancellationToken.None);
        Assert.Equal(3L,(long)n.Outputs["integer"].Value!); Assert.False((bool)n.Outputs["isInteger"].Value!);
        var a=await new ValueArithmeticNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Double(6)},{"b",VisionValue.Double(3)}}),Def("value.arithmetic","{\"op\":\"divide\"}"),CancellationToken.None);
        Assert.Equal(2d,(double)a.Outputs["result"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ValueArithmeticNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Double(1)},{"b",VisionValue.Double(1)}}),Def("value.arithmetic","{\"op\":\"mod\"}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ValueArithmeticNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Double(1)},{"b",VisionValue.Double(0)}}),Def("value.arithmetic","{\"op\":\"divide\"}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ValueNumberNode().ExecuteAsync(Ctx(new()),Def("value.number","{\"value\":1e30}"),CancellationToken.None));
    }
    [Fact] public async Task ValueRangeMarginAndLogicStrict()
    {
        var r=await new ValueRangeNode().ExecuteAsync(Ctx(new(){{"value",VisionValue.Double(150)}}),Def("value.range","{\"min\":0,\"max\":100}"),CancellationToken.None);
        Assert.False((bool)r.Outputs["pass"].Value!); Assert.Equal(-50d,(double)r.Outputs["margin"].Value!,6);
        var inside=await new ValueRangeNode().ExecuteAsync(Ctx(new(){{"value",VisionValue.Double(50)}}),Def("value.range","{\"min\":100,\"max\":0}"),CancellationToken.None);
        Assert.True((bool)inside.Outputs["inside"].Value!); Assert.Contains("boundsSwapped",(string)inside.Outputs["diagnostics"].Value!);
        var logic=await new LogicBooleanNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Boolean(true)}}),Def("logic.boolean","{\"op\":\"not\"}"),CancellationToken.None);
        Assert.False((bool)logic.Outputs["result"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new LogicBooleanNode().ExecuteAsync(Ctx(new(){{"a",VisionValue.Boolean(true)},{"b",VisionValue.Boolean(true)}}),Def("logic.boolean","{\"op\":\"nand\"}"),CancellationToken.None));
    }
    [Fact] public async Task FlowNodes_DiagnosticsAndStrictOperator()
    {
        var f=await new IfConditionNode().ExecuteAsync(Ctx(new(){{"value",VisionValue.Double(10)}}),Def("flow.if","{\"operator\":\">\",\"threshold\":5}"),CancellationToken.None);
        Assert.True((bool)f.Outputs["condition"].Value!); Assert.Equal(5d,(double)f.Outputs["margin"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new IfConditionNode().ExecuteAsync(Ctx(new(){{"value",VisionValue.Double(1)}}),Def("flow.if","{\"operator\":\"~=\"}"),CancellationToken.None));
        var b=await new BooleanIfConditionNode().ExecuteAsync(Ctx(new(){{"condition",VisionValue.Boolean(false)}}),Def("flow.ifBoolean"),CancellationToken.None);
        Assert.Equal("false",(string)b.Outputs["branch"].Value!);
        var s=await new SwitchNode().ExecuteAsync(Ctx(new(){{"value",VisionValue.String("NG")}}),Def("flow.switch","{\"casesJson\":\"[\\\"OK\\\",\\\"NG\\\"]\"}"),CancellationToken.None);
        Assert.True((bool)s.Outputs["matched"].Value!); Assert.Equal("case2",(string)s.Outputs["selected"].Value!);
        var p=await new ParallelMarkerNode().ExecuteAsync(Ctx(new()),Def("flow.parallel"),CancellationToken.None); Assert.Contains("parallel",(string)p.Outputs["diagnostics"].Value!);
        var j=await new JoinNode().ExecuteAsync(Ctx(new()),Def("flow.join"),CancellationToken.None); Assert.Contains("join",(string)j.Outputs["diagnostics"].Value!);
        var m=await new FlowMergeNode().ExecuteAsync(Ctx(new()),Def("flow.merge"),CancellationToken.None); Assert.Contains("merge",(string)m.Outputs["diagnostics"].Value!);
        var res=await new ResultDispositionNode().ExecuteAsync(Ctx(new(){{"pass",VisionValue.Boolean(false)}}),Def("flow.result"),CancellationToken.None); Assert.Equal("NG",(string)res.Outputs["disposition"].Value!);
        var d=await new DelayNode().ExecuteAsync(Ctx(new()),Def("flow.delay","{\"delayMs\":1}"),CancellationToken.None); Assert.Contains("requestedMs",(string)d.Outputs["diagnostics"].Value!);
    }
    [Fact] public async Task CollectionNodes_CountsDiagnosticsAndConfidenceSort()
    {
        var list=new List<VisionDetection>{Det(10,0.5),Det(100,0.9),Det(50,0.7)};
        var filtered=await new DetectionFilterNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(list)}}),Def("collection.detections.filter","{\"minArea\":20}"),CancellationToken.None);
        Assert.Equal(2L,(long)filtered.Outputs["count"].Value!); Assert.Equal(1L,(long)filtered.Outputs["rejectedCount"].Value!);
        var sorted=await new DetectionSortNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(list)}}),Def("collection.detections.sort","{\"sortBy\":\"confidence\",\"descending\":false}"),CancellationToken.None);
        Assert.Equal(3L,(long)sorted.Outputs["count"].Value!);
        var sortedList=(IReadOnlyList<VisionDetection>)sorted.Outputs["detections"].Value!; Assert.Equal(0.5,sortedList[0].Confidence!.Value,6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new DetectionSortNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(list)}}),Def("collection.detections.sort","{\"sortBy\":\"bogus\"}"),CancellationToken.None));
        var at=await new DetectionAtIndexNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(list)}}),Def("collection.detections.at","{\"index\":9}"),CancellationToken.None);
        Assert.False((bool)at.Outputs["found"].Value!); Assert.Contains("detections.at",(string)at.Outputs["diagnostics"].Value!);
        var count=await new DetectionCountNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(list)}}),Def("collection.detections.count"),CancellationToken.None);
        Assert.Equal(3L,(long)count.Outputs["count"].Value!); Assert.False((bool)count.Outputs["isEmpty"].Value!);
        var empty=await new DetectionCountNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(new List<VisionDetection>())}}),Def("collection.detections.count"),CancellationToken.None);
        Assert.True((bool)empty.Outputs["isEmpty"].Value!);
        var each=await new DetectionForEachNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(list)}}),Def("flow.forEach","{\"maxItems\":100}"),CancellationToken.None);
        Assert.Equal(3L,(long)each.Outputs["count"].Value!);
    }
    [Fact] public async Task ForEach_RejectsOverLimitAndLoopEndDiagnostics()
    {
        var list=new List<VisionDetection>{Det(10),Det(20),Det(30)};
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new DetectionForEachNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(list)}}),Def("flow.forEach","{\"maxItems\":2}"),CancellationToken.None));
        var end=await new LoopEndNode().ExecuteAsync(Ctx(new()),Def("flow.loopEnd"),CancellationToken.None);
        Assert.Contains("loopEnd",(string)end.Outputs["diagnostics"].Value!);
        var bad=new List<VisionDetection>{Det(double.NaN)};
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new DetectionCountNode().ExecuteAsync(Ctx(new(){{"detections",VisionValue.Detections(bad)}}),Def("collection.detections.count"),CancellationToken.None));
    }
}
