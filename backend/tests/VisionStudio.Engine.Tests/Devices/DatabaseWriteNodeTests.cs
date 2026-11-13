using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 结果落库节点（output.database）测试：负载序列化、业务键优先级、体积上限、缺存储时拒绝执行。
/// 真实 SQLite 落库由 Api 侧的 ResultRecordStoreTests 覆盖。
/// </summary>
public sealed class DatabaseWriteNodeTests
{
    [Fact]
    public async Task AppendsRecordWithSerializedPayloadAndRunContext()
    {
        var store = new CapturingStore();
        var node = new DatabaseWriteNode(store);

        var result = await node.ExecuteAsync(
            Ctx(("value", VisionValue.NumberList([1.5, 2.5])), ("key", VisionValue.String("SN-42"))),
            Def(("dataset", "caliper-width")), CancellationToken.None);

        var record = Assert.Single(store.Records);
        Assert.Equal("caliper-width", record.Dataset);
        Assert.Equal("SN-42", record.RecordKey);
        Assert.Equal("node-1", record.NodeId);
        Assert.Equal("run-9", record.RunId);
        Assert.Equal("scope-7", record.WorkflowScope);
        // 负载形态与 output.exportResult 的 JSON 一致：{type,value}。
        Assert.Contains("\"type\":\"NumberList\"", record.PayloadJson);
        Assert.Contains("1.5", record.PayloadJson);
        Assert.Equal(record.Id, result.Outputs["recordId"].Require<string>("recordId"));
        Assert.Equal("caliper-width", result.Outputs["dataset"].Require<string>("dataset"));
        Assert.Equal(record.PayloadBytes, result.Outputs["payloadBytes"].Require<long>("payloadBytes"));
    }

    [Fact]
    public async Task KeyPortOverridesParameterAndBlankKeyBecomesNull()
    {
        var store = new CapturingStore();
        var node = new DatabaseWriteNode(store);

        await node.ExecuteAsync(Ctx(("value", VisionValue.Double(3)), ("key", VisionValue.String("  "))),
            Def(("dataset", "d"), ("key", "from-parameter")), CancellationToken.None);

        // 端口接线优先；只含空白时归一为 null，而不是存一个空字符串。
        Assert.Null(Assert.Single(store.Records).RecordKey);

        store.Records.Clear();
        await node.ExecuteAsync(Ctx(("value", VisionValue.Double(3))), Def(("dataset", "d"), ("key", "param-key")), CancellationToken.None);
        Assert.Equal("param-key", Assert.Single(store.Records).RecordKey);
    }

    [Fact]
    public async Task ImageValueDegradesToSizeInsteadOfRawPixels()
    {
        var store = new CapturingStore();
        using var image = VisionImage.Own(new Mat(6, 8, MatType.CV_8UC1, Scalar.All(1)));

        await new DatabaseWriteNode(store).ExecuteAsync(
            Ctx(("value", VisionValue.Image(image))), Def(("dataset", "d")), CancellationToken.None);

        var payload = Assert.Single(store.Records).PayloadJson;
        Assert.Contains("\"type\":\"Image\"", payload);
        Assert.Contains("\"Width\":8", payload);
        Assert.Contains("\"Height\":6", payload);
    }

    [Fact]
    public async Task MissingStoreFailsInsteadOfSilentlyDroppingResults()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseWriteNode().ExecuteAsync(Ctx(("value", VisionValue.Double(1))), Def(), CancellationToken.None).AsTask());
        Assert.Contains("requires a host result store", error.Message);
        Assert.Contains("output.exportResult", error.Message);
    }

    [Fact]
    public async Task RejectsEmptyDatasetOversizedPayloadAndLongKey()
    {
        var store = new CapturingStore();
        var node = new DatabaseWriteNode(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ExecuteAsync(Ctx(("value", VisionValue.Double(1))), Def(("dataset", "   ")), CancellationToken.None).AsTask());

        var big = new string('x', 4096);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ExecuteAsync(Ctx(("value", VisionValue.String(big))), Def(("dataset", "d"), ("maxPayloadBytes", "64")), CancellationToken.None).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ExecuteAsync(Ctx(("value", VisionValue.Double(1)), ("key", VisionValue.String(new string('k', 257)))), Def(("dataset", "d")), CancellationToken.None).AsTask());

        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task StoreFailureIsWrappedWithRecordIdForReconciliation()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseWriteNode(new FailingStore()).ExecuteAsync(Ctx(("value", VisionValue.Double(1))), Def(("dataset", "d")), CancellationToken.None).AsTask());
        Assert.Contains("failed to append record", error.Message);
        Assert.Contains("dataset 'd'", error.Message);
    }

    // ===== 夹具 =====

    private sealed class CapturingStore : IResultRecordStore
    {
        public List<ResultRecord> Records { get; } = [];
        public Task AppendAsync(ResultRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStore : IResultRecordStore
    {
        public Task AppendAsync(ResultRecord record, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("disk I/O error");
    }

    private static NodeExecutionContext Ctx(params (string Port, VisionValue Value)[] inputs)
        => new(inputs.ToDictionary(x => x.Port, x => x.Value, StringComparer.OrdinalIgnoreCase), workflowScope: "scope-7", runId: "run-9");

    private static NodeDefinition Def(params (string Key, string Value)[] parameters)
    {
        var payload = parameters.ToDictionary(x => x.Key, x => (object)x.Value);
        return new NodeDefinition("node-1", "output.database", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)));
    }
}
