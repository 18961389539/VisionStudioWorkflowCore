using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record RunTraceContext(
    string Source,
    string? JobId = null,
    int? JobVersion = null,
    string? WorkflowHash = null,
    string? DebugMode = null,
    string? DependencyManifestHash = null,
    string? Note = null);

public sealed record RunTraceRecord(
    string RunId,
    DateTimeOffset StartedAt,
    string Source,
    string? JobId,
    int? JobVersion,
    string WorkflowId,
    string WorkflowName,
    string? WorkflowHash,
    string? DependencyManifestHash,
    string ExecutionStatus,
    string Disposition,
    double TotalDurationMs,
    int NodeCount,
    int OverlayCount,
    bool HasPreview,
    bool HasReplayInput,
    string? ReplaySourceNodeId,
    string? Error,
    string? Note,
    IReadOnlyList<NodeRunReport> NodeReports,
    string? ErrorCode = null);

public sealed record UpdateDispositionRequest(string Disposition, string? Note = null);

public sealed record TraceStats(
    int Total,
    int Ok,
    int Ng,
    int Review,
    int Errors,
    double AverageDurationMs);

public sealed record TracePage(
    int Offset,
    int Limit,
    int Total,
    IReadOnlyList<RunTraceRecord> Items);

public sealed class TraceRetentionOptions
{
    public int MetadataRetentionDays { get; set; } = 3650;
    public int OkArtifactRetentionDays { get; set; } = 7;
    public int NgArtifactRetentionDays { get; set; } = 90;
    public int ErrorArtifactRetentionDays { get; set; } = 90;
    public int ReviewArtifactRetentionDays { get; set; } = 30;
    public int OkPreviewSampleEvery { get; set; } = 20;
    public int OkReplaySampleEvery { get; set; } = 20;
    public int CleanupIntervalMinutes { get; set; } = 60;
}

public sealed record TraceStorageStatus(
    string DatabaseFile,
    long DatabaseBytes,
    int TraceCount,
    int PreviewCount,
    int ReplayInputCount,
    long ArtifactBytes,
    TraceRetentionOptions Retention,
    StorageCapacityStatus? Capacity = null,
    SchemaMigrationStatus? Schema = null);

/// <summary>
/// SQLite-backed trace index and structured run payload store.
/// Node reports, exact workflow snapshots and overlays live in SQLite JSON columns;
/// binary preview and replay-input artifacts live on disk. No recursive directory scan is used for list/stats/get.
/// </summary>
public sealed class TraceabilityStore
{
    private readonly SqliteMetadataDatabase _db;
    private readonly string _artifactRoot;
    private readonly TraceRetentionOptions _retention;
    private readonly StorageCapacityService? _capacity;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention, StorageCapacityService capacity)
        : this(db, env, retention)
    {
        _capacity = capacity;
    }

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention)
    {
        _db = db;
        _artifactRoot = Path.Combine(env.ContentRootPath, "data", "artifacts");
        Directory.CreateDirectory(_artifactRoot);
        _retention = retention.Value;
    }

    public TraceabilityStore(IWebHostEnvironment env)
        : this(new SqliteMetadataDatabase(env), env, Options.Create(new TraceRetentionOptions())) { }

    /// <summary>
    /// Records a run start before execution so an interrupted host still leaves trace evidence.
    /// <see cref="RecordAsync"/> later updates this same row; production cycles skip the start record
    /// and insert their full row at finalization.
    /// </summary>
    public async Task BeginAsync(string runId, DateTimeOffset startedAt, WorkflowDefinition workflow, RunTraceContext context, CancellationToken ct)
    {
        // Ad-hoc/debug runs embed the workflow immediately so even an interrupted row stays resolvable.
        var embeddedWorkflow = string.IsNullOrWhiteSpace(context.JobId) || context.JobVersion is null
            ? JsonSerializer.Serialize(workflow, _json)
            : null;

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT OR IGNORE INTO run_traces(
    run_id,started_at,source,job_id,job_version,workflow_id,workflow_name,workflow_hash,dependency_manifest_hash,
    execution_status,disposition,total_duration_ms,node_count,overlay_count,has_preview,preview_relative_path,preview_bytes,
    error,note,node_reports_json,workflow_json,overlays_json,error_code)
VALUES(
    $run,$started,$source,$job,$jobVersion,$workflowId,$workflowName,$hash,$dependencyManifest,
    'Running','PENDING',0,0,0,0,NULL,0,
    NULL,$note,'[]',$workflow,'[]',NULL);
""";
        Add(command, "$run", runId);
        Add(command, "$started", Iso(startedAt));
        Add(command, "$source", context.Source);
        Add(command, "$job", context.JobId);
        Add(command, "$jobVersion", context.JobVersion);
        Add(command, "$workflowId", workflow.Id);
        Add(command, "$workflowName", workflow.Name);
        Add(command, "$hash", context.WorkflowHash);
        Add(command, "$dependencyManifest", context.DependencyManifestHash);
        Add(command, "$note", context.Note);
        Add(command, "$workflow", embeddedWorkflow);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Finalizes a run trace: inserts the full record when no start row exists (production cycles),
    /// otherwise updates the row created by <see cref="BeginAsync"/>. Preview/replay artifacts are
    /// attached afterwards and never overwrite the searchable record.
    /// </summary>
    public async Task<RunTraceRecord> RecordAsync(
        WorkflowRunResult result,
        WorkflowDefinition workflow,
        RunTraceContext context,
        CancellationToken ct)
    {
        var startedAt = result.StartedAt ?? DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(result.TotalDurationMs);
        var disposition = result.Success
            ? (result.QualityDisposition ?? (context.Source == "Debug" ? "REVIEW" : "OK"))
            : "ERROR";
        // Cancellation is an expected lifecycle outcome (client disconnect, controlled stop) and must stay
        // distinguishable from node/host faults in the durable record.
        var executionStatus = result.Success
            ? "Complete"
            : result.ErrorCode == VisionRunErrorCodes.Cancelled ? "Cancelled" : "Failed";
        var wantsPreview = result.PreviewJpeg is { Length: > 0 } && ShouldPersistPreview(result.RunId, disposition);

        var record = new RunTraceRecord(
            result.RunId,
            startedAt,
            context.Source,
            context.JobId,
            context.JobVersion,
            workflow.Id,
            workflow.Name,
            context.WorkflowHash,
            context.DependencyManifestHash,
            executionStatus,
            disposition,
            result.TotalDurationMs,
            result.NodeReports.Count,
            result.Overlays.Count,
            false,
            false,
            null,
            result.Error,
            context.Note,
            result.NodeReports,
            result.ErrorCode);

        // Commit the searchable/auditable record first. Binary artifact failure must not erase the run itself.
        await using (var connection = await _db.OpenConnectionAsync(ct))
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO run_traces(
    run_id,started_at,source,job_id,job_version,workflow_id,workflow_name,workflow_hash,dependency_manifest_hash,
    execution_status,disposition,total_duration_ms,node_count,overlay_count,has_preview,preview_relative_path,preview_bytes,
    error,note,node_reports_json,workflow_json,overlays_json,error_code)
VALUES(
    $run,$started,$source,$job,$jobVersion,$workflowId,$workflowName,$hash,$dependencyManifest,
    $status,$disposition,$duration,$nodeCount,$overlayCount,0,NULL,0,
    $error,$note,$nodes,$workflow,$overlays,$errorCode)
ON CONFLICT(run_id) DO UPDATE SET
    started_at=excluded.started_at,
    source=excluded.source,
    job_id=excluded.job_id,
    job_version=excluded.job_version,
    workflow_id=excluded.workflow_id,
    workflow_name=excluded.workflow_name,
    workflow_hash=excluded.workflow_hash,
    dependency_manifest_hash=excluded.dependency_manifest_hash,
    execution_status=excluded.execution_status,
    disposition=excluded.disposition,
    total_duration_ms=excluded.total_duration_ms,
    node_count=excluded.node_count,
    overlay_count=excluded.overlay_count,
    error=excluded.error,
    note=excluded.note,
    node_reports_json=excluded.node_reports_json,
    workflow_json=excluded.workflow_json,
    overlays_json=excluded.overlays_json,
    error_code=excluded.error_code;
""";
            Add(command, "$run", record.RunId);
            Add(command, "$started", Iso(record.StartedAt));
            Add(command, "$source", record.Source);
            Add(command, "$job", record.JobId);
            Add(command, "$jobVersion", record.JobVersion);
            Add(command, "$workflowId", record.WorkflowId);
            Add(command, "$workflowName", record.WorkflowName);
            Add(command, "$hash", record.WorkflowHash);
            Add(command, "$dependencyManifest", record.DependencyManifestHash);
            Add(command, "$status", record.ExecutionStatus);
            Add(command, "$disposition", record.Disposition);
            Add(command, "$duration", record.TotalDurationMs);
            Add(command, "$nodeCount", record.NodeCount);
            Add(command, "$overlayCount", record.OverlayCount);
            Add(command, "$error", record.Error);
            Add(command, "$note", record.Note);
            Add(command, "$nodes", JsonSerializer.Serialize(record.NodeReports, _json));
            // Production/job-bound traces reference the immutable job version instead of duplicating the full workflow per cycle.
            // Ad-hoc/debug traces have no immutable job snapshot to resolve later, so they embed the workflow JSON.
            var embeddedWorkflow = string.IsNullOrWhiteSpace(record.JobId) || record.JobVersion is null
                ? JsonSerializer.Serialize(workflow, _json)
                : null;
            Add(command, "$workflow", embeddedWorkflow);
            Add(command, "$overlays", JsonSerializer.Serialize(result.Overlays, _json));
            Add(command, "$errorCode", record.ErrorCode);
            await command.ExecuteNonQueryAsync(ct);
            await InsertNodeObservationsAsync(connection, transaction, result.RunId, result.NodeReports, ct);
            await transaction.CommitAsync(ct);
        }

        if (result.ReplayInput is { Png.Length: > 0 } replay && ShouldPersistReplayInput(result.RunId, disposition, record.Source))
            record = await PersistReplayInputAsync(record, replay, startedAt, ct);

        if (!wantsPreview || result.PreviewJpeg is not { Length: > 0 } jpeg) return record;

        if (_capacity is not null && !_capacity.CanPersistArtifact(jpeg.LongLength))
        {
            const string capacityNote = "Preview artifact skipped because storage capacity guard is active.";
            try
            {
                await using var capacityConnection = await _db.OpenConnectionAsync(CancellationToken.None);
                await using var capacityUpdate = capacityConnection.CreateCommand();
                capacityUpdate.CommandText = "UPDATE run_traces SET note=COALESCE(note || ' | ','') || $note WHERE run_id=$run;";
                capacityUpdate.Parameters.AddWithValue("$note", capacityNote);
                capacityUpdate.Parameters.AddWithValue("$run", result.RunId);
                await capacityUpdate.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { }
            return record with { Note = capacityNote };
        }

        var capacityReservation = _capacity is not null ? jpeg.LongLength : 0L;
        var previewRelativePath = PreviewRelativePath(startedAt, result.RunId);
        var absolute = ArtifactPath(previewRelativePath);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            temp = absolute + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, jpeg, ct);
            File.Move(temp, absolute, true);
            temp = null;

            await using var connection = await _db.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_traces SET has_preview=1,preview_relative_path=$path,preview_bytes=$bytes WHERE run_id=$run;";
            update.Parameters.AddWithValue("$path", previewRelativePath);
            update.Parameters.AddWithValue("$bytes", jpeg.LongLength);
            update.Parameters.AddWithValue("$run", result.RunId);
            await update.ExecuteNonQueryAsync(ct);
            return record with { HasPreview = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            try { if (File.Exists(absolute)) File.Delete(absolute); } catch { }
            var note = $"Preview artifact persistence failed: {ex.Message}";
            try
            {
                await using var connection = await _db.OpenConnectionAsync(CancellationToken.None);
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE run_traces SET note=COALESCE(note || ' | ','') || $note WHERE run_id=$run;";
                update.Parameters.AddWithValue("$note", note);
                update.Parameters.AddWithValue("$run", result.RunId);
                await update.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { /* the searchable run row already exists; artifact failure must not erase it */ }
            return record with { Note = note };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp))
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            if (capacityReservation > 0) _capacity?.ReleaseArtifactReservation(capacityReservation);
        }
    }

    /// <summary>
    /// Marks start records that never reached finalization as unknown. Startup reconciliation passes
    /// <see cref="TimeSpan.Zero"/> (every "Running" row belongs to a previous process); periodic maintenance
    /// passes a maximum run age so an active run is never touched.
    /// </summary>
    public async Task<int> SweepStaleRunsAsync(TimeSpan maximumAge, CancellationToken ct)
    {
        var cutoff = Iso(DateTimeOffset.UtcNow - maximumAge);
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE run_traces
SET execution_status='Unknown',
    error=COALESCE(error,'No final record was written; the run outcome is unknown.'),
    note=CASE WHEN note IS NULL OR note='' THEN 'Marked unknown: no finalization was recorded.'
              ELSE note || ' | Marked unknown: no finalization was recorded.' END
WHERE execution_status='Running' AND started_at < $cutoff;
""";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RunTraceRecord>> ListAsync(int take, string? jobId, string? disposition, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(jobId)) { where.Add("job_id=$job"); command.Parameters.AddWithValue("$job", jobId); }
        if (!string.IsNullOrWhiteSpace(disposition)) { where.Add("disposition=$disposition"); command.Parameters.AddWithValue("$disposition", disposition.ToUpperInvariant()); }
        command.CommandText = $"SELECT {TraceColumns} FROM run_traces {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY started_at DESC LIMIT $take;";
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 500));
        var result = new List<RunTraceRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadRecord(reader));
        return result;
    }

    public async Task<TracePage> PageAsync(int offset, int limit, string? jobId, string? disposition, CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await _db.OpenConnectionAsync(ct);
        var where = new List<string>();
        await using var count = connection.CreateCommand();
        await using var query = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            where.Add("job_id=$job");
            count.Parameters.AddWithValue("$job", jobId);
            query.Parameters.AddWithValue("$job", jobId);
        }
        if (!string.IsNullOrWhiteSpace(disposition))
        {
            where.Add("disposition=$disposition");
            count.Parameters.AddWithValue("$disposition", disposition.ToUpperInvariant());
            query.Parameters.AddWithValue("$disposition", disposition.ToUpperInvariant());
        }
        var whereSql = where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
        count.CommandText = $"SELECT COUNT(*) FROM run_traces {whereSql};";
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

        query.CommandText = $"SELECT {TraceColumns} FROM run_traces {whereSql} ORDER BY started_at DESC LIMIT $limit OFFSET $offset;";
        query.Parameters.AddWithValue("$limit", limit);
        query.Parameters.AddWithValue("$offset", offset);
        var items = new List<RunTraceRecord>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ReadRecord(reader));
        return new TracePage(offset, limit, total, items);
    }

    public async Task<RunTraceRecord?> GetAsync(string runId, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TraceColumns} FROM run_traces WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<RunTraceRecord> UpdateDispositionAsync(string runId, UpdateDispositionRequest request, CancellationToken ct)
    {
        var allowed = new HashSet<string>(["OK", "NG", "REVIEW", "ERROR"], StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains(request.Disposition)) throw new ApiValidationException("Disposition must be OK, NG, REVIEW or ERROR.");

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE run_traces SET disposition=$disposition,note=$note WHERE run_id=$run;";
            command.Parameters.AddWithValue("$disposition", request.Disposition.ToUpperInvariant());
            Add(command, "$note", request.Note);
            command.Parameters.AddWithValue("$run", runId);
            if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiNotFoundException($"Trace '{runId}' was not found.");
        }
        await transaction.CommitAsync(ct);
        return await GetAsync(runId, ct) ?? throw new InvalidDataException($"Trace '{runId}' could not be read.");
    }

    public async Task<TraceStats> StatsAsync(int days, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*),
       SUM(CASE WHEN UPPER(disposition)='OK' THEN 1 ELSE 0 END),
       SUM(CASE WHEN UPPER(disposition)='NG' THEN 1 ELSE 0 END),
       SUM(CASE WHEN UPPER(disposition)='REVIEW' THEN 1 ELSE 0 END),
       SUM(CASE WHEN UPPER(disposition)='ERROR' THEN 1 ELSE 0 END),
       COALESCE(AVG(total_duration_ms),0)
FROM run_traces WHERE started_at >= $cutoff;
""";
        command.Parameters.AddWithValue("$cutoff", Iso(cutoff));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new TraceStats(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3), reader.IsDBNull(4) ? 0 : reader.GetInt32(4), reader.GetDouble(5));
    }


    public async Task<WorkflowDefinition?> GetWorkflowSnapshotAsync(string runId, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COALESCE(t.workflow_json, j.workflow_json)
FROM run_traces t
LEFT JOIN job_versions j ON j.job_id=t.job_id AND j.version=t.job_version
WHERE t.run_id=$run;
""";
        command.Parameters.AddWithValue("$run", runId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WorkflowDefinition>(json, _json);
    }

    public async Task<RuntimeDependencyManifest?> GetDependencyManifestAsync(string runId, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT m.manifest_json
FROM run_traces t
LEFT JOIN runtime_dependency_manifests m ON m.manifest_hash=t.dependency_manifest_hash
WHERE t.run_id=$run;
""";
        command.Parameters.AddWithValue("$run", runId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<RuntimeDependencyManifest>(json, _json);
    }

    public async Task<IReadOnlyList<VisionOverlay>?> GetOverlaysAsync(string runId, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT overlays_json FROM run_traces WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<VisionOverlay>>(json, _json);
    }

    public string? FindPreviewPath(string runId)
    {
        using var connection = _db.OpenConnectionAsync().GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT preview_relative_path FROM run_traces WHERE run_id=$run AND has_preview=1;";
        command.Parameters.AddWithValue("$run", runId);
        var relative = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = ArtifactPath(relative);
        return File.Exists(path) ? path : null;
    }

    public string? FindReplayInputPath(string runId)
    {
        using var connection = _db.OpenConnectionAsync().GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT replay_relative_path FROM run_traces WHERE run_id=$run AND has_replay_input=1;";
        command.Parameters.AddWithValue("$run", runId);
        var relative = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = ArtifactPath(relative);
        return File.Exists(path) ? path : null;
    }

    public async Task<TraceStorageStatus> StorageStatusAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(CASE WHEN has_preview=1 THEN 1 ELSE 0 END), SUM(CASE WHEN has_replay_input=1 THEN 1 ELSE 0 END), COALESCE(SUM(preview_bytes + replay_bytes),0) FROM run_traces;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var traceCount = reader.GetInt32(0);
        var previewCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        var replayInputCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        var artifactBytes = reader.GetInt64(3);
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        var databaseBytes = Size(_db.DatabasePath) + Size(_db.DatabasePath + "-wal") + Size(_db.DatabasePath + "-shm");
        var capacity = _capacity is null ? null : await _capacity.RefreshAsync(ct);
        var schema = await _db.GetSchemaStatusAsync(ct);
        return new TraceStorageStatus(Path.GetFileName(_db.DatabasePath), databaseBytes, traceCount, previewCount, replayInputCount, artifactBytes, _retention, capacity, schema);
    }

    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        await _cleanupGate.WaitAsync(ct);
        try
        {
            var changed = 0;
            await using var connection = await _db.OpenConnectionAsync(ct);

        var artifacts = new List<(string RunId, DateTimeOffset StartedAt, string Disposition, string RelativePath)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,started_at,disposition,preview_relative_path FROM run_traces WHERE has_preview=1 AND preview_relative_path IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) artifacts.Add((reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }

        foreach (var item in artifacts)
        {
            if (!ArtifactExpired(item.StartedAt, item.Disposition)) continue;
            var path = ArtifactPath(item.RelativePath);
            try { if (File.Exists(path)) File.Delete(path); } catch { continue; }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_traces SET has_preview=0, preview_relative_path=NULL, preview_bytes=0 WHERE run_id=$run;";
            update.Parameters.AddWithValue("$run", item.RunId);
            changed += await update.ExecuteNonQueryAsync(ct);
            RemoveEmptyParents(Path.GetDirectoryName(path));
        }

        var replayArtifacts = new List<(string RunId, DateTimeOffset StartedAt, string Disposition, string RelativePath)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,started_at,disposition,replay_relative_path FROM run_traces WHERE has_replay_input=1 AND replay_relative_path IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) replayArtifacts.Add((reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }
        foreach (var item in replayArtifacts)
        {
            if (!ArtifactExpired(item.StartedAt, item.Disposition)) continue;
            var path = ArtifactPath(item.RelativePath);
            try { if (File.Exists(path)) File.Delete(path); } catch { continue; }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_traces SET has_replay_input=0, replay_relative_path=NULL, replay_bytes=0, replay_source_node_id=NULL WHERE run_id=$run;";
            update.Parameters.AddWithValue("$run", item.RunId);
            changed += await update.ExecuteNonQueryAsync(ct);
            RemoveEmptyParents(Path.GetDirectoryName(path));
        }

        if (_retention.MetadataRetentionDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_retention.MetadataRetentionDays);
            var expiredPaths = new List<string>();
            await using (var paths = connection.CreateCommand())
            {
                paths.CommandText = "SELECT artifact_path FROM (SELECT preview_relative_path AS artifact_path FROM run_traces WHERE started_at < $cutoff AND preview_relative_path IS NOT NULL UNION ALL SELECT replay_relative_path AS artifact_path FROM run_traces WHERE started_at < $cutoff AND replay_relative_path IS NOT NULL);";
                paths.Parameters.AddWithValue("$cutoff", Iso(cutoff));
                await using var reader = await paths.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) expiredPaths.Add(reader.GetString(0));
            }
            foreach (var relative in expiredPaths)
            {
                var path = ArtifactPath(relative);
                try { if (File.Exists(path)) File.Delete(path); RemoveEmptyParents(Path.GetDirectoryName(path)); } catch { }
            }
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM run_traces WHERE started_at < $cutoff;";
            delete.Parameters.AddWithValue("$cutoff", Iso(cutoff));
            changed += await delete.ExecuteNonQueryAsync(ct);
        }

            if (changed > 0)
            {
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                await checkpoint.ExecuteNonQueryAsync(ct);
            }

            return changed;
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    private static async Task InsertNodeObservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        IReadOnlyList<NodeRunReport> reports,
        CancellationToken ct)
    {
        foreach (var report in reports.Where(x => x.ExecutionSequence > 0).OrderBy(x => x.ExecutionSequence))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO run_node_observations(
    run_id,execution_sequence,node_id,node_type,phase,success,start_offset_ms,end_offset_ms,duration_ms,error)
VALUES($run,$sequence,$node,$type,$phase,$success,$start,$end,$duration,$error);
""";
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$sequence", report.ExecutionSequence);
            command.Parameters.AddWithValue("$node", report.NodeId);
            command.Parameters.AddWithValue("$type", report.NodeType);
            command.Parameters.AddWithValue("$phase", report.Phase.ToString());
            command.Parameters.AddWithValue("$success", report.Success ? 1 : 0);
            command.Parameters.AddWithValue("$start", Math.Max(0, report.StartOffsetMs));
            command.Parameters.AddWithValue("$end", Math.Max(report.StartOffsetMs, report.EndOffsetMs));
            command.Parameters.AddWithValue("$duration", Math.Max(0, report.DurationMs));
            Add(command, "$error", report.Error);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private bool ShouldPersistPreview(string runId, string disposition)
        => RunArtifactSampling.Include(runId, disposition, _retention.OkPreviewSampleEvery);

    private bool ShouldPersistReplayInput(string runId, string disposition, string source)
        // 生产循环（ProductionRuntime）的 OK trace 按采样留存 replay 素材以控制磁盘占用；
        // 交互式运行（ad-hoc / debug / 手动作业）是用户主动留下、供数据集素材与离线重放使用的
        // 证据链，一律持久化——采样曾将其一并剔除，导致参数调优的数据集引用被 409 拒绝。
        => source.Equals("ProductionRuntime", StringComparison.OrdinalIgnoreCase)
            ? RunArtifactSampling.Include(runId, disposition, _retention.OkReplaySampleEvery, replay: true)
            : true;

    public RunArtifactOptions GetArtifactOptions(long maxRawBytes)
        => new(DeferEncoding: true, PreviewSampleEvery: _retention.OkPreviewSampleEvery,
            ReplaySampleEvery: _retention.OkReplaySampleEvery, MaxRawBytes: maxRawBytes);

    private async Task<RunTraceRecord> PersistReplayInputAsync(RunTraceRecord record, ReplayInputArtifact replay, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (_capacity is not null && !_capacity.CanPersistArtifact(replay.Png.LongLength)) return record;
        var relative = ReplayRelativePath(startedAt, record.RunId);
        var absolute = ArtifactPath(relative);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            temp = absolute + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, replay.Png, ct);
            File.Move(temp, absolute, true);
            temp = null;

            await using var connection = await _db.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_traces SET has_replay_input=1,replay_relative_path=$path,replay_bytes=$bytes,replay_source_node_id=$source WHERE run_id=$run;";
            update.Parameters.AddWithValue("$path", relative);
            update.Parameters.AddWithValue("$bytes", replay.Png.LongLength);
            update.Parameters.AddWithValue("$source", replay.SourceNodeId);
            update.Parameters.AddWithValue("$run", record.RunId);
            await update.ExecuteNonQueryAsync(ct);
            return record with { HasReplayInput = true, ReplaySourceNodeId = replay.SourceNodeId };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            try { if (File.Exists(absolute)) File.Delete(absolute); } catch { }
            var note = $"Replay input persistence failed: {ex.Message}";
            try
            {
                await using var connection = await _db.OpenConnectionAsync(CancellationToken.None);
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE run_traces SET note=COALESCE(note || ' | ','') || $note WHERE run_id=$run;";
                update.Parameters.AddWithValue("$note", note);
                update.Parameters.AddWithValue("$run", record.RunId);
                await update.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { }
            return record with { Note = string.IsNullOrWhiteSpace(record.Note) ? note : record.Note + " | " + note };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp))
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            _capacity?.ReleaseArtifactReservation(replay.Png.LongLength);
        }
    }

    private bool ArtifactExpired(DateTimeOffset startedAt, string disposition)
    {
        var days = disposition.ToUpperInvariant() switch
        {
            "NG" => _retention.NgArtifactRetentionDays,
            "ERROR" => _retention.ErrorArtifactRetentionDays,
            "REVIEW" => _retention.ReviewArtifactRetentionDays,
            _ => _retention.OkArtifactRetentionDays
        };
        return days >= 0 && startedAt < DateTimeOffset.UtcNow.AddDays(-days);
    }

    private string PreviewRelativePath(DateTimeOffset at, string runId)
        => Path.Combine("traces", at.UtcDateTime.ToString("yyyy-MM-dd"), Sanitize(runId), "preview.jpg");

    private string ReplayRelativePath(DateTimeOffset at, string runId)
        => Path.Combine("traces", at.UtcDateTime.ToString("yyyy-MM-dd"), Sanitize(runId), "replay-input.png");

    private string ArtifactPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_artifactRoot, relative));
        var root = Path.GetFullPath(_artifactRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact path escaped storage root.");
        return path;
    }

    private void RemoveEmptyParents(string? directory)
    {
        var root = Path.GetFullPath(_artifactRoot).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) break;
            try
            {
                if (Directory.EnumerateFileSystemEntries(full).Any()) break;
                Directory.Delete(full);
            }
            catch { break; }
            directory = Path.GetDirectoryName(full);
        }
    }

    private RunTraceRecord ReadRecord(SqliteDataReader reader)
    {
        var nodeReports = JsonSerializer.Deserialize<List<NodeRunReport>>(reader.GetString(20), _json) ?? [];
        return new RunTraceRecord(
            reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9), reader.GetString(10), reader.GetDouble(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14) != 0,
            reader.GetInt32(15) != 0, reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18), nodeReports,
            reader.IsDBNull(21) ? null : reader.GetString(21));
    }

    private const string TraceColumns = "run_id,started_at,source,job_id,job_version,workflow_id,workflow_name,workflow_hash,dependency_manifest_hash,execution_status,disposition,total_duration_ms,node_count,overlay_count,has_preview,has_replay_input,replay_source_node_id,error,note,preview_relative_path,node_reports_json,error_code";

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
}

public sealed class TraceRetentionHostedService : BackgroundService
{
    /// <summary>Runs older than this without finalization are treated as orphaned by periodic maintenance.</summary>
    private static readonly TimeSpan OrphanedRunAge = TimeSpan.FromHours(1);

    private readonly TraceabilityStore _traces;
    private readonly TraceRetentionOptions _options;
    private readonly ILogger<TraceRetentionHostedService> _logger;
    private readonly StorageMaintenanceCoordinator _maintenance;

    public TraceRetentionHostedService(TraceabilityStore traces, IOptions<TraceRetentionOptions> options, ILogger<TraceRetentionHostedService> logger, StorageMaintenanceCoordinator maintenance)
    {
        _traces = traces;
        _options = options.Value;
        _logger = logger;
        _maintenance = maintenance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup reconciliation: a row still "Running" belongs to a previous process that never finalized it.
        try
        {
            var interrupted = await _traces.SweepStaleRunsAsync(TimeSpan.Zero, stoppingToken);
            if (interrupted > 0) _logger.LogWarning("Marked {Count} interrupted run trace(s) as Unknown after startup.", interrupted);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation of running trace records failed.");
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var maintenanceLease = _maintenance.TryEnterRequest();
                if (maintenanceLease is null) continue;
                var orphaned = await _traces.SweepStaleRunsAsync(OrphanedRunAge, stoppingToken);
                if (orphaned > 0) _logger.LogWarning("Marked {Count} orphaned run trace(s) as Unknown.", orphaned);
                var changed = await _traces.CleanupAsync(stoppingToken);
                if (changed > 0) _logger.LogInformation("Trace retention cleanup changed {Count} records/artifacts.", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trace retention cleanup failed.");
            }
        }
    }
}
