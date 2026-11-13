using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class SqliteStorageTests
{
    [Fact]
    public async Task JobAndCalibrationVersions_AreStoredInSingleSqliteDatabase_NotVersionFileFanout()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        var calibrations = new CalibrationAssetStore(db, new CalibrationWorkspaceService());

        await jobs.CreateAsync(new CreateJobRequest("job-db", "DB Job", null, Workflow(1), "v1"), default);
        await jobs.AddVersionAsync("job-db", new SaveJobVersionRequest(Workflow(2), "v2"), default);
        await calibrations.CreateAsync(new CreateCalibrationAssetRequest("cal-db", "DB Cal", null, Workspace(0), "v1"), default);
        await calibrations.AddVersionAsync("cal-db", new SaveCalibrationVersionRequest(Workspace(0.001), "v2"), default);

        Assert.True(File.Exists(db.DatabasePath));
        Assert.False(Directory.Exists(Path.Combine(env.ContentRootPath, "data", "jobs", "job-db", "versions")));
        Assert.False(Directory.Exists(Path.Combine(env.ContentRootPath, "data", "calibrations", "cal-db", "versions")));

        await using var connection = await db.OpenConnectionAsync();
        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM job_versions WHERE job_id='job-db';"));
        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_versions WHERE asset_id='cal-db';"));
    }

    [Fact]
    public async Task ProductionTrace_ReferencesImmutableJobWorkflow_InsteadOfDuplicatingWorkflowJson()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        var traces = Store(db, env, new TraceRetentionOptions { OkPreviewSampleEvery = 1000 });
        var workflow = Workflow(42);
        await jobs.CreateAsync(new CreateJobRequest("job-trace", "Trace Job", null, workflow, "v1"), default);
        var manifest = Manifest("job-trace");
        var published = await jobs.PublishAsync("job-trace", 1, "publish", manifest, default);
        var hash = published.Versions.Single(v => v.Version == 1).WorkflowHash;

        await traces.RecordAsync(
            Result("production-run", "OK", Array.Empty<byte>()),
            workflow,
            new RunTraceContext("ProductionRuntime", "job-trace", 1, hash, DependencyManifestHash: manifest.ManifestHash),
            default);

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT workflow_json FROM run_traces WHERE run_id='production-run';";
            var stored = await command.ExecuteScalarAsync();
            Assert.True(stored is null || stored is DBNull);
        }

        var resolved = await traces.GetWorkflowSnapshotAsync("production-run", default);
        Assert.NotNull(resolved);
        Assert.Equal(workflow.Id, resolved!.Id);
        Assert.Equal(42, resolved.Nodes.Single().Parameters["revision"].GetInt32());
        var resolvedManifest = await traces.GetDependencyManifestAsync("production-run", default);
        Assert.NotNull(resolvedManifest);
        Assert.Equal(manifest.ManifestHash, resolvedManifest!.ManifestHash);
    }

    [Fact]
    public async Task TraceStats_UsesSqlAggregation_AndIsNotCappedByListLimit()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await using var connection = await db.OpenConnectionAsync();
        await using var tx = connection.BeginTransaction(deferred: false);
        const int total = 620;
        for (var i = 0; i < total; i++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO run_traces(run_id,started_at,source,job_id,job_version,workflow_id,workflow_name,workflow_hash,execution_status,disposition,total_duration_ms,node_count,overlay_count,has_preview,preview_relative_path,preview_bytes,error,note,node_reports_json,workflow_json,overlays_json)
VALUES($run,$started,'Test','job-a',1,'wf','WF','hash','Complete',$disp,2.0,1,0,0,NULL,0,NULL,NULL,'[]','{}','[]');
""";
            command.Parameters.AddWithValue("$run", $"run-{i:D4}");
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.AddMinutes(-i / 100.0).ToString("O"));
            command.Parameters.AddWithValue("$disp", i % 2 == 0 ? "OK" : "NG");
            await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();

        var store = Store(db, env, new TraceRetentionOptions());
        var stats = await store.StatsAsync(7, default);
        var list = await store.ListAsync(500, null, null, default);

        Assert.Equal(total, stats.Total);
        Assert.Equal(310, stats.Ok);
        Assert.Equal(310, stats.Ng);
        Assert.Equal(500, list.Count);
    }

    [Fact]
    public async Task TraceArtifacts_SampleOkPreviews_KeepNg_AndRetentionCanRemoveArtifactsWithoutDeletingMetadata()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var options = new TraceRetentionOptions
        {
            OkPreviewSampleEvery = 1000,
            OkArtifactRetentionDays = 7,
            NgArtifactRetentionDays = 0,
            ErrorArtifactRetentionDays = 90,
            ReviewArtifactRetentionDays = 30,
            MetadataRetentionDays = 3650,
            CleanupIntervalMinutes = 60
        };
        var store = Store(db, env, options);
        var workflow = Workflow(1);
        var okRunId = FindNonSampledRunId(options.OkPreviewSampleEvery);
        var jpeg = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };

        var ok = await store.RecordAsync(Result(okRunId, "OK", jpeg), workflow, new RunTraceContext("ProductionRuntime"), default);
        var ng = await store.RecordAsync(Result("ng-run", "NG", jpeg), workflow, new RunTraceContext("ProductionRuntime"), default);
        Assert.False(ok.HasPreview);
        Assert.True(ng.HasPreview);
        Assert.NotNull(store.FindPreviewPath("ng-run"));

        var changed = await store.CleanupAsync(default);
        var reread = await store.GetAsync("ng-run", default);
        Assert.True(changed >= 1);
        Assert.NotNull(reread);
        Assert.False(reread!.HasPreview);
        Assert.Null(store.FindPreviewPath("ng-run"));
        Assert.NotNull(await store.GetAsync(okRunId, default));
    }


    [Fact]
    public async Task V047ReplayInput_IsPersistedAsDedicatedPngArtifact_AndSchemaIsV11()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = Store(db, env, new TraceRetentionOptions { OkReplaySampleEvery = 1, OkPreviewSampleEvery = 1000 });
        var workflow = Workflow(1);
        var replay = new ReplayInputArtifact(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, "n1", 10, 10);
        var result = Result("replay-artifact", "OK", Array.Empty<byte>()) with { ReplayInput = replay };

        var trace = await store.RecordAsync(result, workflow, new RunTraceContext("AdHoc"), default);
        Assert.True(trace.HasReplayInput);
        Assert.Equal("n1", trace.ReplaySourceNodeId);
        Assert.NotNull(store.FindReplayInputPath("replay-artifact"));
        // schema 版本随迁移演进（当前 20）：断言跟随实现常量而非硬编码快照
        Assert.Equal(db.CurrentSchemaVersion, (await db.GetSchemaStatusAsync()).CurrentVersion);

        await using var connection = await db.OpenConnectionAsync();
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT has_replay_input FROM run_traces WHERE run_id='replay-artifact';"));
    }

    [Fact]
    public async Task LegacyJobFiles_AreImportedOnce_AndLeftUntouched()
    {
        using var env = new TempWebHostEnvironment();
        var legacyDir = Path.Combine(env.ContentRootPath, "data", "jobs", "legacy-job");
        var versionsDir = Path.Combine(legacyDir, "versions");
        Directory.CreateDirectory(versionsDir);
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var workflow = Workflow(7);
        var snapshot = new JobVersionSnapshot("legacy-job", 1, WorkflowFingerprint.Compute(workflow), now, "legacy", workflow);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "job.json"), JsonSerializer.Serialize(new
        {
            id = "legacy-job", name = "Legacy", description = "from V0.19", createdAt = now, updatedAt = now,
            latestVersion = 1, publishedVersion = 1,
            publicationHistory = new[] { new { version = 1, action = "Publish", at = now } }
        }, WebJson));
        await File.WriteAllTextAsync(Path.Combine(versionsDir, "v000001.json"), JsonSerializer.Serialize(snapshot, WebJson));

        var db = new SqliteMetadataDatabase(env);
        var migration = new LegacyStorageMigrationService(db, env, NullLogger<LegacyStorageMigrationService>.Instance);
        await migration.MigrateAsync(default);
        await migration.MigrateAsync(default);

        var store = new JobStore(db);
        var imported = await store.GetAsync("legacy-job", default);
        Assert.NotNull(imported);
        Assert.Equal(1, imported!.PublishedVersion);
        Assert.Single(imported.Versions);
        Assert.Single(imported.PublicationHistory);
        Assert.True(File.Exists(Path.Combine(legacyDir, "job.json")));
        Assert.True(File.Exists(Path.Combine(versionsDir, "v000001.json")));
    }

    private static TraceabilityStore Store(SqliteMetadataDatabase db, TempWebHostEnvironment env, TraceRetentionOptions options)
        => new(db, env, Options.Create(options));

    private static WorkflowRunResult Result(string runId, string disposition, byte[] jpeg)
        => new(runId, true, 1.0, true, 1, 1, [new NodeRunReport("n1", "test", true, 0.1, new Dictionary<string, object?>())], [], jpeg, QualityDisposition: disposition);

    private static string FindNonSampledRunId(int every)
    {
        for (var i = 0; i < 10000; i++)
        {
            var id = $"ok-{i}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            if (BitConverter.ToUInt32(hash, 0) % (uint)every != 0) return id;
        }
        throw new InvalidOperationException("Could not find deterministic non-sampled run id.");
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static WorkflowDefinition Workflow(int revision)
        => new("sqlite-test", "SQLite Test", [new NodeDefinition("n1", "test.sqlite", "Test", null, new Dictionary<string, JsonElement> { ["revision"] = JsonSerializer.SerializeToElement(revision) })], []);

    private static RuntimeDependencyManifest Manifest(string seed)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return new RuntimeDependencyManifest(
            1,
            hash,
            DateTimeOffset.UtcNow,
            new EngineRuntimeDependency("VisionStudio.Engine", "test", "test", hash),
            [], [], [], [], []);
    }

    private static CalibrationWorkspaceRequest Workspace(double offset)
    {
        var points = new List<CalibrationWorkspacePoint>();
        var index = 1;
        foreach (var y in new[] { 100d, 240d, 380d })
        foreach (var x in new[] { 100d, 320d, 540d })
            points.Add(new CalibrationWorkspacePoint(index++, x + offset, y - offset, x * 0.1 + 5, y * 0.1 - 10));
        return new CalibrationWorkspaceRequest("ImagePixel", "Workpiece", "mm", points);
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
