using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;

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

    private static async Task<string?> ScalarAsync(SqliteMetadataDatabase db, string sql)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }
}
