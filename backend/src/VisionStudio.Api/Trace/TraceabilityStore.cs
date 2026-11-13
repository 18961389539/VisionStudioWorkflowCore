using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class TraceabilityStore
{
    private readonly SqliteMetadataDatabase _db;
    private readonly string _artifactRoot;
    private readonly TraceRetentionOptions _retention;
    private readonly StorageCapacityService? _capacity;
    /// <summary>
    /// R07：附件一致性门持有者（可选）。生产部署经 DI 注入；单测构造时不传即退化为无门（原行为）。
    /// 落盘 preview/replay 与保留清理时持**共享**租约，与备份的**排他**冻结窗口互斥。
    /// </summary>
    private readonly StorageMaintenanceCoordinator? _maintenance;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    // F05：在线备份冻结窗口期间跳过的附件落盘计数（经 /api/storage/status 暴露）。
    private long _snapshotSkippedPreviews;
    private long _snapshotSkippedReplays;

    /// <summary>F05：因备份冻结窗口跳过的预览落盘次数。</summary>
    public long SnapshotSkippedPreviews => Interlocked.Read(ref _snapshotSkippedPreviews);
    /// <summary>F05：因备份冻结窗口跳过的回放落盘次数。</summary>
    public long SnapshotSkippedReplays => Interlocked.Read(ref _snapshotSkippedReplays);

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention, StorageCapacityService capacity)
        : this(db, env, retention)
    {
        _capacity = capacity;
    }

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention, StorageCapacityService capacity, StorageMaintenanceCoordinator maintenance)
        : this(db, env, retention)
    {
        _capacity = capacity;
        _maintenance = maintenance;
    }

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention)
    {
        _db = db;
        _artifactRoot = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "artifacts");
        Directory.CreateDirectory(_artifactRoot);
        _retention = retention.Value;
    }

    public TraceabilityStore(IWebHostEnvironment env)
        : this(new SqliteMetadataDatabase(env), env, Options.Create(new TraceRetentionOptions())) { }

    /// <summary>
    /// Records a run start before execution so an interrupted host still leaves trace evidence.
    /// <see cref="RecordAsync"/> later updates this same row; production cycles also write this start
    /// record and finalize it with an upsert, so the row never disappears between begin and end.
    /// </summary>
    public async Task BeginAsync(string runId, DateTimeOffset startedAt, WorkflowDefinition workflow, RunTraceContext context, CancellationToken ct, bool strongDurability = false)
    {
        // Ad-hoc/debug runs embed the workflow immediately so even an interrupted row stays resolvable.
        var embeddedWorkflow = string.IsNullOrWhiteSpace(context.JobId) || context.JobVersion is null
            ? JsonSerializer.Serialize(workflow, _json)
            : null;

        // R03：生产周期的起始登记是关键恢复锚点（"已接受未完成"可见性），由调用方要求强持久。
        await using var connection = await _db.OpenConnectionAsync(ct, strongDurability);
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
        CancellationToken ct,
        bool strongDurability = false)
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
        // R03：生产提交要求强持久——设备动作意图只在该事务确认耐久后才允许闭合。
        await using (var connection = await _db.OpenConnectionAsync(ct, strongDurability))
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
            catch { /* 容量告警备注写入失败：返回值仍携带跳过原因 */ }
            return record with { Note = capacityNote };
        }

        var capacityReservation = _capacity is not null ? jpeg.LongLength : 0L;
        var previewRelativePath = PreviewRelativePath(startedAt, result.RunId);
        var absolute = ArtifactPath(previewRelativePath);
        string? temp = null;

        // R07：持共享附件写租约。备份冻结窗口进行中时获得 null → 跳过本次落盘并记 note
        // （附件是可选证据，绝不阻塞生产循环，也不反压）。
        using var artifactWrite = _maintenance?.TryEnterArtifactWrite();
        if (_maintenance is not null && artifactWrite is null)
        {
            const string snapshotNote = "Preview artifact skipped because a storage backup snapshot is in progress.";
            Interlocked.Increment(ref _snapshotSkippedPreviews);
            try
            {
                await using var noteConnection = await _db.OpenConnectionAsync(CancellationToken.None);
                await using var noteUpdate = noteConnection.CreateCommand();
                noteUpdate.CommandText = "UPDATE run_traces SET note=COALESCE(note || ' | ','') || $note WHERE run_id=$run;";
                noteUpdate.Parameters.AddWithValue("$note", snapshotNote);
                noteUpdate.Parameters.AddWithValue("$run", result.RunId);
                await noteUpdate.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { /* 备份快照备注写入失败：返回值仍携带跳过原因 */ }
            if (capacityReservation > 0) _capacity?.ReleaseArtifactReservation(capacityReservation);
            return record with { Note = snapshotNote };
        }

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
            try { if (File.Exists(absolute)) File.Delete(absolute); } catch { /* 预览残留文件清理失败：已记录持久化失败备注 */ }
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
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 临时预览文件清理失败：不影响主流程 */ }
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

    public async Task<TracePage> PageAsync(TraceQuery query, CancellationToken ct)
    {
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 500);
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var count = connection.CreateCommand();
        await using var list = connection.CreateCommand();
        var whereSql = BuildTraceWhere(query, (name, value) =>
        {
            count.Parameters.AddWithValue(name, value ?? DBNull.Value);
            list.Parameters.AddWithValue(name, value ?? DBNull.Value);
        });
        count.CommandText = $"SELECT COUNT(*) FROM run_traces {whereSql};";
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

        // 稳定排序：started_at 并列（同一批并发落库的记录）时以 run_id 定序，
        // OFFSET 翻页才不会因次序抖动而重复或漏行。
        list.CommandText =
            $"SELECT {TraceColumns} FROM run_traces {whereSql} ORDER BY started_at DESC, run_id DESC LIMIT $limit OFFSET $offset;";
        list.Parameters.AddWithValue("$limit", limit);
        list.Parameters.AddWithValue("$offset", offset);
        var items = new List<RunTraceRecord>();
        await using var reader = await list.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ReadRecord(reader));
        if (!query.IncludeReports)
            items = items.Select(item => item with { NodeReports = Array.Empty<NodeRunReport>() }).ToList();
        return new TracePage(offset, limit, total, items);
    }

    /// <summary>分页与统计共用的 WHERE 构造：两边口径永远一致。</summary>
    private static string BuildTraceWhere(TraceQuery query, Action<string, object?> bind)
    {
        var where = new List<string>();
        void Add(string clause, string name, object? value)
        {
            where.Add(clause);
            bind(name, value);
        }

        if (!string.IsNullOrWhiteSpace(query.JobId)) Add("job_id=$job", "$job", query.JobId.Trim());
        if (query.JobVersion is { } version) Add("job_version=$jobver", "$jobver", version);
        if (!string.IsNullOrWhiteSpace(query.Disposition))
            Add("UPPER(disposition)=UPPER($disposition)", "$disposition", query.Disposition.Trim());
        if (!string.IsNullOrWhiteSpace(query.ExecutionStatus))
            Add("UPPER(execution_status)=UPPER($exec)", "$exec", query.ExecutionStatus.Trim());
        if (!string.IsNullOrWhiteSpace(query.Source)) Add("source=$source", "$source", query.Source.Trim());
        if (!string.IsNullOrWhiteSpace(query.RunIdPrefix))
            Add("run_id LIKE $runp", "$runp", query.RunIdPrefix.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(query.WorkflowKeyword))
            Add("(workflow_name LIKE $wf OR workflow_id LIKE $wf)", "$wf", "%" + query.WorkflowKeyword.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(query.ErrorCode))
            Add("UPPER(error_code)=UPPER($ec)", "$ec", query.ErrorCode.Trim());
        if (query.From is { } from) Add("started_at >= $from", "$from", Iso(from));
        if (query.To is { } to) Add("started_at <= $to", "$to", Iso(to));
        return where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
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

    /// <summary>与分页同一组过滤谓词的统计（列表、数量、统计共用筛选条件的后端保证）。</summary>
    public async Task<TraceStats> StatsAsync(TraceQuery query, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        var whereSql = BuildTraceWhere(query, (name, value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value));
        command.CommandText = $"""
SELECT COUNT(*),
       SUM(CASE WHEN UPPER(disposition)='OK' THEN 1 ELSE 0 END),
       SUM(CASE WHEN UPPER(disposition)='NG' THEN 1 ELSE 0 END),
       SUM(CASE WHEN UPPER(disposition)='REVIEW' THEN 1 ELSE 0 END),
       SUM(CASE WHEN UPPER(disposition)='ERROR' THEN 1 ELSE 0 END),
       COALESCE(AVG(total_duration_ms),0)
FROM run_traces {whereSql};
""";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new TraceStats(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3), reader.IsDBNull(4) ? 0 : reader.GetInt32(4), reader.GetDouble(5));
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
        // 同步读路径：SqliteMetadataDatabase.OpenConnection 就是为这类"微秒级打开"准备的，
        // 不再用 GetAwaiter().GetResult() 在线程池上阻塞异步打开。
        using var connection = _db.OpenConnection();
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
        using var connection = _db.OpenConnection();
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
        return new TraceStorageStatus(Path.GetFileName(_db.DatabasePath), databaseBytes, traceCount, previewCount, replayInputCount, artifactBytes, _retention, capacity, schema,
            SnapshotSkippedPreviews, SnapshotSkippedReplays);
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
