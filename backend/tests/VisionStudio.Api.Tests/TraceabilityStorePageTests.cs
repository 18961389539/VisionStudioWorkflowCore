using Microsoft.Extensions.Options;
using VisionStudio.Api;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 追溯分页与统计的过滤口径（数据查询页审查第一批）：
/// 分页与统计共用同一组谓词（统计范围=列表范围）、真实总数、稳定排序、运行 ID 前缀直查。
/// </summary>
public sealed class TraceabilityStorePageTests
{
    private static async Task<(TraceabilityStore Store, SqliteMetadataDatabase Db)> CreateAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new TraceabilityStore(db, env, Options.Create(new TraceRetentionOptions()));
        return (store, db);
    }

    private static async Task SeedAsync(
        SqliteMetadataDatabase db,
        string runId,
        DateTimeOffset startedAt,
        string executionStatus = "Completed",
        string disposition = "OK",
        string jobId = "job-1",
        int? jobVersion = 2,
        string workflowName = "压装流程",
        string? errorCode = null)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO run_traces(run_id, started_at, source, job_id, job_version, workflow_id, workflow_name,
                       execution_status, disposition, total_duration_ms, node_count, overlay_count,
                       has_preview, error_code, node_reports_json, overlays_json)
VALUES($id, $started, 'job', $job, $ver, 'wf-1', $name, $status, $disp, 12.5, 2, 0, 0, $ec, '[]', '[]');
""";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$started", startedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ver", (object?)jobVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", workflowName);
        command.Parameters.AddWithValue("$status", executionStatus);
        command.Parameters.AddWithValue("$disp", disposition);
        command.Parameters.AddWithValue("$ec", (object?)errorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Page_returns_true_total_and_filters_by_time_window()
    {
        var (store, db) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db, "run-old", now.AddDays(-3));
        await SeedAsync(db, "run-new-1", now.AddMinutes(-2));
        await SeedAsync(db, "run-new-2", now.AddMinutes(-1));

        var page = await store.PageAsync(new TraceQuery(Offset: 0, Limit: 50, From: now.AddHours(-1)), CancellationToken.None);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
        // 新的在前（started_at DESC）。
        Assert.Equal("run-new-2", page.Items[0].RunId);
        Assert.Equal("run-new-1", page.Items[1].RunId);
    }

    [Fact]
    public async Task Page_filters_by_execution_status_and_disposition()
    {
        var (store, db) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db, "run-a", now.AddMinutes(-3), executionStatus: "Completed", disposition: "OK");
        await SeedAsync(db, "run-b", now.AddMinutes(-2), executionStatus: "Failed", disposition: "NG", errorCode: "E-FOCUS");
        await SeedAsync(db, "run-c", now.AddMinutes(-1), executionStatus: "Running", disposition: "PENDING");

        var failed = await store.PageAsync(new TraceQuery(0, 50, ExecutionStatus: "failed"), CancellationToken.None);
        Assert.Equal(1, failed.Total);
        Assert.Equal("run-b", failed.Items[0].RunId);

        var ng = await store.PageAsync(new TraceQuery(0, 50, Disposition: "ng"), CancellationToken.None);
        Assert.Equal(1, ng.Total);
        Assert.Equal("run-b", ng.Items[0].RunId);

        var running = await store.PageAsync(new TraceQuery(0, 50, ExecutionStatus: "Running"), CancellationToken.None);
        Assert.Equal(1, running.Total);
        Assert.Equal("run-c", running.Items[0].RunId);
    }

    [Fact]
    public async Task Page_run_id_prefix_direct_lookup()
    {
        var (store, db) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db, "3f2a0001-aaaa", now.AddMinutes(-3));
        await SeedAsync(db, "3f2a0002-bbbb", now.AddDays(-5));
        await SeedAsync(db, "9e1c0003-cccc", now.AddMinutes(-1));

        var page = await store.PageAsync(new TraceQuery(0, 50, RunIdPrefix: "3f2a"), CancellationToken.None);

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, item => Assert.StartsWith("3f2a", item.RunId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Page_orders_stably_for_identical_timestamps()
    {
        var (store, db) = await CreateAsync();
        var same = DateTimeOffset.UtcNow;
        await SeedAsync(db, "run-zzz", same);
        await SeedAsync(db, "run-aaa", same);
        await SeedAsync(db, "run-mmm", same);

        var page = await store.PageAsync(new TraceQuery(0, 50), CancellationToken.None);

        // 同一时间戳并列时按 run_id DESC：翻页次序确定，不会重复或漏行。
        Assert.Equal(new[] { "run-zzz", "run-mmm", "run-aaa" }, page.Items.Select(item => item.RunId).ToArray());
    }

    [Fact]
    public async Task Page_filters_by_workflow_keyword_and_job_version()
    {
        var (store, db) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db, "run-1", now.AddMinutes(-3), workflowName: "压装流程", jobVersion: 2);
        await SeedAsync(db, "run-2", now.AddMinutes(-2), workflowName: "检测流程", jobVersion: 3);
        await SeedAsync(db, "run-3", now.AddMinutes(-1), workflowName: "压装流程", jobVersion: 3);

        var byKeyword = await store.PageAsync(new TraceQuery(0, 50, WorkflowKeyword: "压装"), CancellationToken.None);
        Assert.Equal(2, byKeyword.Total);

        var byVersion = await store.PageAsync(new TraceQuery(0, 50, JobId: "job-1", JobVersion: 3), CancellationToken.None);
        Assert.Equal(2, byVersion.Total);
        Assert.All(byVersion.Items, item => Assert.Equal(3, item.JobVersion));
    }

    [Fact]
    public async Task Page_filters_by_error_code()
    {
        var (store, db) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db, "run-ok", now.AddMinutes(-2));
        await SeedAsync(db, "run-err", now.AddMinutes(-1), errorCode: "e-focus-lost");

        var page = await store.PageAsync(new TraceQuery(0, 50, ErrorCode: "E-FOCUS-LOST"), CancellationToken.None);
        Assert.Equal(1, page.Total);
        Assert.Equal("run-err", page.Items[0].RunId);
    }

    [Fact]
    public async Task Stats_shares_the_same_filters_as_the_list()
    {
        var (store, db) = await CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db, "run-old-ok", now.AddDays(-3), disposition: "OK");
        await SeedAsync(db, "run-new-ok", now.AddMinutes(-2), disposition: "OK");
        await SeedAsync(db, "run-new-ng", now.AddMinutes(-1), disposition: "NG", executionStatus: "Failed");

        // 统计窗口与列表窗口相同：只覆盖最近两条。
        var query = new TraceQuery(From: now.AddHours(-1));
        var page = await store.PageAsync(query with { Limit = 50 }, CancellationToken.None);
        var stats = await store.StatsAsync(query, CancellationToken.None);

        Assert.Equal(page.Total, stats.Total);
        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.Ok);
        Assert.Equal(1, stats.Ng);
        Assert.Equal(0, stats.Review);
        Assert.Equal(12.5, stats.AverageDurationMs, precision: 6);
    }
}
