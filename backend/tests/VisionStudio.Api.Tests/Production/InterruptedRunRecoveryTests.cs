using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace VisionStudio.Api.Tests;

/// <summary>
/// A run left at 'Running'/'Queued' by a previous host must not survive startup: the runner lived in
/// process memory, so the row has no owner. Leaving it behind keeps the UI polling forever and blocks
/// every operation guarded by "no run is active" (for example deleting the dataset).
/// </summary>
public sealed class InterruptedRunRecoveryTests : IDisposable
{
    private const string Start = "2026-01-01T00:00:00.0000000Z";

    private readonly string _root;
    private readonly SqliteMetadataDatabase _database;
    private readonly InterruptedRunRecovery _recovery;

    public InterruptedRunRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vs-run-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new SqliteMetadataDatabase(new FakeEnv(_root));
        _recovery = new InterruptedRunRecovery(_database, NullLogger<InterruptedRunRecovery>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp directory best effort */ }
    }

    [Fact]
    public async Task Startup_MarksUnfinishedRunsFromPreviousHostAsInterrupted()
    {
        await SeedAsync();

        var recovered = await _recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(3, recovered);
        Assert.Equal(InterruptedRunRecovery.Status, await ScalarAsync("SELECT status FROM validation_runs WHERE run_id='v-running';"));
        Assert.Equal(InterruptedRunRecovery.Status, await ScalarAsync("SELECT status FROM camera_sync_commissioning_tests WHERE test_id='c-queued';"));
        Assert.Equal(InterruptedRunRecovery.Status, await ScalarAsync("SELECT status FROM plugin_benchmark_runs WHERE run_id='b-running';"));
        Assert.Contains("Host restarted", await ScalarAsync("SELECT error FROM validation_runs WHERE run_id='v-running';"));
        Assert.Equal(Start, await ScalarAsync("SELECT started_at FROM validation_runs WHERE run_id='v-running';"));
        Assert.NotEqual(Start, await ScalarAsync("SELECT completed_at FROM validation_runs WHERE run_id='v-running';"));
    }

    [Fact]
    public async Task Startup_LeavesFinishedRunsAndTheirTimestampsUntouched()
    {
        await SeedAsync();

        await _recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal("Completed", await ScalarAsync("SELECT status FROM validation_runs WHERE run_id='v-done';"));
        Assert.Equal("2026-01-01T00:05:00.0000000Z", await ScalarAsync("SELECT completed_at FROM validation_runs WHERE run_id='v-done';"));
        Assert.Equal(string.Empty, await ScalarAsync("SELECT COALESCE(error,'') FROM validation_runs WHERE run_id='v-done';"));
    }

    [Fact]
    public async Task Recovery_IsIdempotent()
    {
        await SeedAsync();

        Assert.Equal(3, await _recovery.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, await _recovery.RecoverAsync(CancellationToken.None));
    }

    private async Task SeedAsync()
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        await ExecuteAsync(connection, $"""
            INSERT INTO validation_datasets(id,name,description,created_at,updated_at)
            VALUES('ds','Dataset','', '{Start}','{Start}');
            """);
        await ExecuteAsync(connection, $"""
            INSERT INTO validation_runs(run_id,dataset_id,status,source_workflow_run_id,workflow_hash,candidate_workflow_json,requested_count,completed_count,started_at)
            VALUES('v-running','ds','Running','src','hash','workflow-json',2,1,'{Start}');
            """);
        await ExecuteAsync(connection, $"""
            INSERT INTO validation_runs(run_id,dataset_id,status,source_workflow_run_id,workflow_hash,candidate_workflow_json,requested_count,completed_count,started_at,completed_at)
            VALUES('v-done','ds','Completed','src','hash','workflow-json',2,2,'{Start}','2026-01-01T00:05:00.0000000Z');
            """);
        await ExecuteAsync(connection, $"""
            INSERT INTO camera_sync_commissioning_tests(test_id,group_id,group_config_hash,scheduled,requested_iterations,completed_iterations,frame_timeout_ms,delay_ms,minimum_samples,status,started_at)
            VALUES('c-queued','group','hash',0,3,0,1000,0,1,'Queued','{Start}');
            """);
        await ExecuteAsync(connection, $"""
            INSERT INTO plugin_benchmark_runs(run_id,dataset_id,validation_run_id,plugin_id,plugin_version,workflow_hash,status,pool_sizes_json,requested_count,warmup_count,workload_concurrency,policy_json,started_at)
            VALUES('b-running','ds','v-done','plugin','1.0.0','hash','Running','[1,2]',10,1,1,'policy-json','{Start}');
            """);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private sealed class FakeEnv(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
