using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Nodes;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 真实 SQLite 结果落库测试：迁移建表、单条写入、按数据集/业务键读取、主键冲突。
/// </summary>
public sealed class ResultRecordStoreTests
{
    [Fact]
    public async Task MigrationCreatesResultRecordTable()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();

        var status = await db.GetSchemaStatusAsync();
        Assert.Equal(SqliteSchemaMigrationRunner.CurrentVersion, status.CurrentVersion);
        Assert.Contains(status.History, entry => entry.Name.Contains("result records", StringComparison.OrdinalIgnoreCase));

        var table = await ScalarAsync(db, "SELECT name FROM sqlite_master WHERE type='table' AND name='result_records';");
        Assert.Equal("result_records", table);
    }

    [Fact]
    public async Task AppendThenReadBackByDatasetAndKey()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new SqliteResultRecordStore(db);

        var created = DateTimeOffset.UtcNow;
        await store.AppendAsync(new ResultRecord("rec-1", "caliper-width", "SN-1", "{\"type\":\"Double\",\"value\":1.5}", "run-1", "scope-1", "node-1", created));
        await store.AppendAsync(new ResultRecord("rec-2", "caliper-width", "SN-2", "{\"type\":\"Double\",\"value\":2.5}", "run-1", "scope-1", "node-1", created.AddMilliseconds(5)));
        await store.AppendAsync(new ResultRecord("rec-3", "other", null, "{}", "run-2", null, "node-2", created));

        var byDataset = await store.ReadAsync("caliper-width");
        Assert.Equal(2, byDataset.Count);
        // 新的在前。
        Assert.Equal("rec-2", byDataset[0].Id);
        Assert.Equal("SN-2", byDataset[0].RecordKey);
        Assert.Equal("{\"type\":\"Double\",\"value\":2.5}", byDataset[0].PayloadJson);
        Assert.Equal("run-1", byDataset[0].RunId);
        Assert.Equal("scope-1", byDataset[0].WorkflowScope);

        var byKey = await store.ReadByKeyAsync("SN-1");
        var single = Assert.Single(byKey);
        Assert.Equal("rec-1", single.Id);
        Assert.Equal("caliper-width", single.Dataset);
    }

    [Fact]
    public async Task DuplicateRecordIdIsRejectedSoRetriesCannotDoubleWrite()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new SqliteResultRecordStore(db);
        var record = new ResultRecord("dup", "d", null, "{}", "run", null, "node", DateTimeOffset.UtcNow);

        await store.AppendAsync(record);
        // 主键冲突：同一条记录重复写入必须失败，而不是静默产生两行。
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.AppendAsync(record));
        Assert.Single(await store.ReadAsync("d"));
    }

    [Fact]
    public async Task NodeRetriesReuseOneRecordPerDatasetAndOperation()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new SqliteResultRecordStore(db);
        var node = new DatabaseWriteNode(store);

        Task<NodeExecutorResult> Run(string dataset, string key, string value)
        {
            var definition = new NodeDefinition("db", "output.database", null, null,
                new Dictionary<string, JsonElement>
                {
                    ["dataset"] = JsonSerializer.SerializeToElement(dataset),
                    ["idempotencyKey"] = JsonSerializer.SerializeToElement("configured-fallback")
                });
            var context = new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["value"] = VisionValue.String(value),
                ["key"] = VisionValue.String("SN-1"),
                ["idempotencyKey"] = VisionValue.String(key)
            });
            return node.ExecuteAsync(context, definition, CancellationToken.None).AsTask();
        }

        var results = await Task.WhenAll(Enumerable.Range(1, 8)
            .Select(length => Task.Run(() => Run("d", " inspection-1 ", new string('x', length)))));
        var firstRecord = Assert.Single(await store.ReadAsync("d"));
        Assert.Single(results, result => !result.Outputs["reused"].Require<bool>("reused"));
        Assert.All(results, result =>
        {
            Assert.Equal(firstRecord.Id, result.Outputs["recordId"].Require<string>("recordId"));
            Assert.Equal(firstRecord.PayloadBytes, result.Outputs["payloadBytes"].Require<long>("payloadBytes"));
        });

        var otherDataset = await Run("other", "inspection-1", "new");
        var otherOperation = await Run("d", "inspection-2", "new");
        var blankKey = await Run("d", " ", "manual");
        Assert.NotEqual(firstRecord.Id, otherDataset.Outputs["recordId"].Require<string>("recordId"));
        Assert.NotEqual(firstRecord.Id, otherOperation.Outputs["recordId"].Require<string>("recordId"));
        Assert.False(blankKey.Outputs["reused"].Require<bool>("reused"));
        Assert.Equal(4, (await store.ReadByKeyAsync("SN-1")).Count);
    }

    [Theory]
    [InlineData("reuse")]
    [InlineData("warn")]
    [InlineData("fail")]
    public async Task NodeAppliesConflictPolicyWithoutReplacingThePersistedRecord(string policy)
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new SqliteResultRecordStore(db);
        var node = new DatabaseWriteNode(store);
        var definition = new NodeDefinition("db", node.Type, null, null, new Dictionary<string, JsonElement>
        {
            ["dataset"] = JsonSerializer.SerializeToElement("inspection"),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement("operation-1"),
            ["idempotencyConflictPolicy"] = JsonSerializer.SerializeToElement(policy)
        });

        Task<NodeExecutorResult> Run(string value)
            => node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["value"] = VisionValue.String(value),
                ["key"] = VisionValue.String("SN-1")
            }), definition, CancellationToken.None).AsTask();

        var first = await Run("accepted");
        var recordId = first.Outputs["recordId"].Require<string>("recordId");
        Assert.False(first.Outputs["reused"].Require<bool>("reused"));
        var retry = await Run("accepted");
        Assert.True(retry.Outputs["reused"].Require<bool>("reused"));
        Assert.Equal(recordId, retry.Outputs["recordId"].Require<string>("recordId"));
        Assert.False(retry.Outputs.ContainsKey("warningCount"));

        if (policy == "fail")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("rejected"));
            Assert.Contains("idempotency conflict", error.Message);
            Assert.Contains(recordId, error.Message);
            Assert.DoesNotContain("operation-1", error.Message);
        }
        else
        {
            var different = await Run("rejected");
            Assert.True(different.Outputs["reused"].Require<bool>("reused"));
            Assert.Equal(recordId, different.Outputs["recordId"].Require<string>("recordId"));
            if (policy == "warn")
            {
                Assert.True(different.Outputs["idempotencyConflict"].Require<bool>("idempotencyConflict"));
                Assert.Equal(1L, different.Outputs["warningCount"].Require<long>("warningCount"));
            }
        }

        var stored = Assert.Single(await store.ReadAsync("inspection"));
        Assert.Equal(recordId, stored.Id);
        Assert.Contains("accepted", stored.PayloadJson);
    }

    [Fact]
    public async Task CheckedWritesCompareOnlyBusinessKeyAndExactSerializedPayload()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new SqliteResultRecordStore(db);
        var original = new ResultRecord("first", "inspection", null, """{"type":"String","value":"OK"}""",
            "run-1", "scope-1", "node-1", DateTimeOffset.UtcNow);
        await store.AppendIdempotentAsync(original, "operation");
        var retry = original with { Id = "retry", RunId = "run-2", WorkflowScope = null,
            NodeId = "node-2", CreatedAt = original.CreatedAt.AddSeconds(1) };

        Assert.Equal((original.Id, original.PayloadBytes, true, false),
            await store.AppendCheckedIdempotentAsync(retry, "operation"));
        Assert.Equal((original.Id, original.PayloadBytes, true, true),
            await store.AppendCheckedIdempotentAsync(retry with { RecordKey = "SN-1" }, "operation"));
        Assert.Equal((original.Id, original.PayloadBytes, true, true),
            await store.AppendCheckedIdempotentAsync(retry with { PayloadJson = """{"type":"String","value":"ok"}""" }, "operation"));
        Assert.Equal(original, Assert.Single(await store.ReadAsync(original.Dataset)));
    }

    [Fact]
    public async Task ConcurrentCheckedWritesReportConflictsAgainstTheSingleWinner()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new SqliteResultRecordStore(db);
        var contenders = Enumerable.Range(0, 8)
            .Select(index => new ResultRecord($"record-{index}", "inspection", "SN-1",
                $$"""{"type":"Integer","value":{{index % 2}}}""", $"run-{index}", null, "db", DateTimeOffset.UtcNow))
            .ToArray();

        var results = await Task.WhenAll(contenders.Select(record => Task.Run(async () =>
            (Submitted: record, Outcome: await store.AppendCheckedIdempotentAsync(record, "operation")))));

        var stored = Assert.Single(await store.ReadAsync("inspection"));
        Assert.Single(results, result => !result.Outcome.Reused);
        Assert.All(results, result =>
        {
            Assert.Equal(stored.Id, result.Outcome.RecordId);
            Assert.Equal(stored.PayloadBytes, result.Outcome.PayloadBytes);
            Assert.Equal(result.Submitted.PayloadJson != stored.PayloadJson, result.Outcome.Conflict);
        });
    }

    private static async Task<string?> ScalarAsync(SqliteMetadataDatabase db, string sql)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }
}
