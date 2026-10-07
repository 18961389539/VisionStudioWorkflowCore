using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>
/// One-shot upgrade path from V0.19 file metadata to V0.20 SQLite metadata.
/// Legacy files are intentionally left untouched so rollback to an older binary remains possible.
/// </summary>
public sealed class LegacyStorageMigrationService
{
    private const string MigrationKey = "v20-file-metadata-to-sqlite";
    private readonly SqliteMetadataDatabase _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LegacyStorageMigrationService> _logger;
    private int _migrationErrors;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public LegacyStorageMigrationService(SqliteMetadataDatabase db, IWebHostEnvironment env, ILogger<LegacyStorageMigrationService> logger)
    {
        _db = db;
        _env = env;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        if (await IsCompletedAsync(connection, ct)) return;

        _migrationErrors = 0;
        var jobs = await MigrateJobsAsync(connection, ct);
        var calibrations = await MigrateCalibrationsAsync(connection, ct);
        var traces = await MigrateTracesAsync(connection, ct);

        if (_migrationErrors > 0)
        {
            _logger.LogWarning("V0.20 legacy migration imported partial data but had {Errors} errors. Completion marker was not written; startup will retry safely.", _migrationErrors);
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO migration_state(key,completed_at,detail) VALUES($key,$at,$detail);";
        command.Parameters.AddWithValue("$key", MigrationKey);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$detail", $"jobs={jobs}; calibrations={calibrations}; traces={traces}");
        await command.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("V0.20 legacy storage migration finished: {Jobs} jobs, {Calibrations} calibrations, {Traces} traces.", jobs, calibrations, traces);
    }

    private static async Task<bool> IsCompletedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM migration_state WHERE key=$key;";
        command.Parameters.AddWithValue("$key", MigrationKey);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<int> MigrateJobsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var root = Path.Combine(_env.ContentRootPath, "data", "jobs");
        if (!Directory.Exists(root)) return 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var metadataPath = Path.Combine(dir, "job.json");
            if (!File.Exists(metadataPath)) continue;
            try
            {
                var meta = await ReadAsync<LegacyJobMetadata>(metadataPath, ct);
                if (meta is null) continue;
                await using var tx = connection.BeginTransaction(deferred: false);
                await ExecAsync(connection, tx,
                    "INSERT OR IGNORE INTO jobs(id,name,description,created_at,updated_at,latest_version,published_version) VALUES($id,$name,$description,$created,$updated,$latest,$published);",
                    ct, ("$id", meta.Id), ("$name", meta.Name), ("$description", meta.Description), ("$created", Iso(meta.CreatedAt)),
                    ("$updated", Iso(meta.UpdatedAt)), ("$latest", meta.LatestVersion), ("$published", meta.PublishedVersion));

                var versionsDir = Path.Combine(dir, "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(versionsDir, "v*.json").OrderBy(x => x))
                    {
                        var snapshot = await ReadAsync<JobVersionSnapshot>(file, ct);
                        if (snapshot is null) continue;
                        await ExecAsync(connection, tx,
                            "INSERT OR IGNORE INTO job_versions(job_id,version,workflow_hash,created_at,note,workflow_json) VALUES($job,$version,$hash,$created,$note,$workflow);",
                            ct, ("$job", snapshot.JobId), ("$version", snapshot.Version), ("$hash", snapshot.WorkflowHash),
                            ("$created", Iso(snapshot.CreatedAt)), ("$note", snapshot.Note), ("$workflow", JsonSerializer.Serialize(snapshot.Workflow, _json)));
                    }
                }

                foreach (var evt in meta.PublicationHistory)
                    await ExecAsync(connection, tx,
                        "INSERT INTO job_publication_history(job_id,version,action,at) SELECT $id,$version,$action,$at WHERE NOT EXISTS (SELECT 1 FROM job_publication_history WHERE job_id=$id AND version=$version AND action=$action AND at=$at);",
                        ct, ("$id", meta.Id), ("$version", evt.Version), ("$action", evt.Action), ("$at", Iso(evt.At)));
                await tx.CommitAsync(ct);
                count++;
            }
            catch (Exception ex) { Interlocked.Increment(ref _migrationErrors); _logger.LogWarning(ex, "Could not migrate legacy job directory {Directory}.", dir); }
        }
        return count;
    }

    private async Task<int> MigrateCalibrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var root = Path.Combine(_env.ContentRootPath, "data", "calibrations");
        if (!Directory.Exists(root)) return 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var metadataPath = Path.Combine(dir, "asset.json");
            if (!File.Exists(metadataPath)) continue;
            try
            {
                var meta = await ReadAsync<LegacyCalibrationMetadata>(metadataPath, ct);
                if (meta is null) continue;
                await using var tx = connection.BeginTransaction(deferred: false);
                await ExecAsync(connection, tx,
                    "INSERT OR IGNORE INTO calibration_assets(id,name,description,created_at,updated_at,latest_version,published_version) VALUES($id,$name,$description,$created,$updated,$latest,$published);",
                    ct, ("$id", meta.Id), ("$name", meta.Name), ("$description", meta.Description), ("$created", Iso(meta.CreatedAt)),
                    ("$updated", Iso(meta.UpdatedAt)), ("$latest", meta.LatestVersion), ("$published", meta.PublishedVersion));

                var versionsDir = Path.Combine(dir, "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(versionsDir, "v*.json").OrderBy(x => x))
                    {
                        var snapshot = await ReadAsync<CalibrationVersionSnapshot>(file, ct);
                        if (snapshot is null) continue;
                        await ExecAsync(connection, tx,
                            "INSERT OR IGNORE INTO calibration_versions(asset_id,version,snapshot_hash,created_at,note,workspace_json,result_json) VALUES($id,$version,$hash,$created,$note,$workspace,$result);",
                            ct, ("$id", snapshot.AssetId), ("$version", snapshot.Version), ("$hash", snapshot.SnapshotHash),
                            ("$created", Iso(snapshot.CreatedAt)), ("$note", snapshot.Note),
                            ("$workspace", JsonSerializer.Serialize(snapshot.Workspace, _json)), ("$result", JsonSerializer.Serialize(snapshot.Result, _json)));
                    }
                }

                foreach (var evt in meta.PublicationHistory)
                    await ExecAsync(connection, tx,
                        "INSERT INTO calibration_publication_history(asset_id,version,action,at) SELECT $id,$version,$action,$at WHERE NOT EXISTS (SELECT 1 FROM calibration_publication_history WHERE asset_id=$id AND version=$version AND action=$action AND at=$at);",
                        ct, ("$id", meta.Id), ("$version", evt.Version), ("$action", evt.Action), ("$at", Iso(evt.At)));
                await tx.CommitAsync(ct);
                count++;
            }
            catch (Exception ex) { Interlocked.Increment(ref _migrationErrors); _logger.LogWarning(ex, "Could not migrate legacy calibration directory {Directory}.", dir); }
        }
        return count;
    }

    private async Task<int> MigrateTracesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var root = Path.Combine(_env.ContentRootPath, "data", "traces");
        if (!Directory.Exists(root)) return 0;
        var artifactRoot = Path.Combine(_env.ContentRootPath, "data", "artifacts");
        var count = 0;
        foreach (var metadataPath in Directory.EnumerateFiles(root, "metadata.json", SearchOption.AllDirectories))
        {
            try
            {
                var record = await ReadAsync<RunTraceRecord>(metadataPath, ct);
                if (record is null) continue;
                var dir = Path.GetDirectoryName(metadataPath)!;
                var workflowPath = Path.Combine(dir, "workflow.json");
                var overlaysPath = Path.Combine(dir, "overlays.json");
                var previewPath = Path.Combine(dir, "preview.jpg");
                var workflowJson = File.Exists(workflowPath)
                    ? await File.ReadAllTextAsync(workflowPath, ct)
                    : JsonSerializer.Serialize(new WorkflowDefinition(record.WorkflowId, record.WorkflowName, [], []), _json);
                var overlaysJson = File.Exists(overlaysPath) ? await File.ReadAllTextAsync(overlaysPath, ct) : "[]";

                string? previewRelative = null;
                if (File.Exists(previewPath))
                {
                    previewRelative = Path.Combine("traces", record.StartedAt.UtcDateTime.ToString("yyyy-MM-dd"), Sanitize(record.RunId), "preview.jpg");
                    var destination = Path.Combine(artifactRoot, previewRelative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!File.Exists(destination)) File.Copy(previewPath, destination);
                }

                await using var command = connection.CreateCommand();
                command.CommandText = """
INSERT OR IGNORE INTO run_traces(run_id,started_at,source,job_id,job_version,workflow_id,workflow_name,workflow_hash,execution_status,disposition,total_duration_ms,node_count,overlay_count,has_preview,preview_relative_path,preview_bytes,error,note,node_reports_json,workflow_json,overlays_json)
VALUES($run,$started,$source,$job,$jobVersion,$workflowId,$workflowName,$hash,$status,$disposition,$duration,$nodeCount,$overlayCount,$hasPreview,$preview,$previewBytes,$error,$note,$nodes,$workflow,$overlays);
""";
                Add(command, "$run", record.RunId); Add(command, "$started", Iso(record.StartedAt)); Add(command, "$source", record.Source);
                Add(command, "$job", record.JobId); Add(command, "$jobVersion", record.JobVersion); Add(command, "$workflowId", record.WorkflowId); Add(command, "$workflowName", record.WorkflowName);
                Add(command, "$hash", record.WorkflowHash); Add(command, "$status", record.ExecutionStatus); Add(command, "$disposition", record.Disposition); Add(command, "$duration", record.TotalDurationMs);
                Add(command, "$nodeCount", record.NodeCount); Add(command, "$overlayCount", record.OverlayCount); Add(command, "$hasPreview", previewRelative is null ? 0 : 1); Add(command, "$preview", previewRelative); Add(command, "$previewBytes", File.Exists(previewPath) ? new FileInfo(previewPath).Length : 0);
                Add(command, "$error", record.Error); Add(command, "$note", record.Note); Add(command, "$nodes", JsonSerializer.Serialize(record.NodeReports, _json)); Add(command, "$workflow", workflowJson); Add(command, "$overlays", overlaysJson);
                count += await command.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex) { Interlocked.Increment(ref _migrationErrors); _logger.LogWarning(ex, "Could not migrate legacy trace {MetadataPath}.", metadataPath); }
        }
        return count;
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
    }

    private static async Task ExecAsync(SqliteConnection connection, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) Add(command, name, value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private sealed record LegacyJobMetadata(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int? PublishedVersion, IReadOnlyList<PublicationEvent> PublicationHistory);
    private sealed record LegacyCalibrationMetadata(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int? PublishedVersion, IReadOnlyList<CalibrationPublicationEvent> PublicationHistory);
}
