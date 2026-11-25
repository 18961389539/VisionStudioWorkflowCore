using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

public sealed partial class TraceabilityStore
{
    private readonly IVisionStudioDbFactory _dbFactory;
    private readonly SqliteMetadataDatabase _db;
    private readonly string _artifactRoot;
    private readonly TraceRetentionOptions _retention;
    private readonly StorageCapacityService? _capacity;
    private readonly StorageMaintenanceCoordinator? _maintenance;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private long _snapshotSkippedPreviews;
    private long _snapshotSkippedReplays;

    public long SnapshotSkippedPreviews => Interlocked.Read(ref _snapshotSkippedPreviews);
    public long SnapshotSkippedReplays => Interlocked.Read(ref _snapshotSkippedReplays);

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention, StorageCapacityService capacity)
        : this(new VisionStudioDbFactory(db), env, retention, capacity) { }

    public TraceabilityStore(IVisionStudioDbFactory dbFactory, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention, StorageCapacityService capacity)
        : this(dbFactory, env, retention)
    {
        _capacity = capacity;
    }

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention, StorageCapacityService capacity, StorageMaintenanceCoordinator maintenance)
        : this(new VisionStudioDbFactory(db), env, retention, capacity, maintenance) { }

    public TraceabilityStore(IVisionStudioDbFactory dbFactory, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention, StorageCapacityService capacity, StorageMaintenanceCoordinator maintenance)
        : this(dbFactory, env, retention)
    {
        _capacity = capacity;
        _maintenance = maintenance;
    }

    public TraceabilityStore(SqliteMetadataDatabase db, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention)
        : this(new VisionStudioDbFactory(db), env, retention) { }

    public TraceabilityStore(IVisionStudioDbFactory dbFactory, IWebHostEnvironment env, IOptions<TraceRetentionOptions> retention)
    {
        _dbFactory = dbFactory;
        _db = dbFactory.Database;
        _artifactRoot = Path.Combine(_db.DataRoot, "artifacts");
        Directory.CreateDirectory(_artifactRoot);
        _retention = retention.Value;
    }

    public TraceabilityStore(IWebHostEnvironment env)
        : this(new SqliteMetadataDatabase(env), env, Options.Create(new TraceRetentionOptions())) { }

    public async Task BeginAsync(string runId, DateTimeOffset startedAt, WorkflowDefinition workflow, RunTraceContext context, CancellationToken ct, bool strongDurability = false)
    {
        var embeddedWorkflow = string.IsNullOrWhiteSpace(context.JobId) || context.JobVersion is null
            ? JsonSerializer.Serialize(workflow, _json)
            : null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct, strongDurability);
        await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        if (await db.RunTraces.AnyAsync(x => x.RunId == runId, ct))
        {
            await tx.CommitAsync(ct);
            return;
        }
        db.RunTraces.Add(new RunTraceEntity
        {
            RunId = runId,
            StartedAt = startedAt,
            Source = context.Source,
            JobId = context.JobId,
            JobVersion = context.JobVersion,
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            WorkflowHash = context.WorkflowHash,
            DependencyManifestHash = context.DependencyManifestHash,
            ExecutionStatus = "Running",
            Disposition = "PENDING",
            TotalDurationMs = 0,
            NodeCount = 0,
            OverlayCount = 0,
            HasPreview = false,
            PreviewRelativePath = null,
            PreviewBytes = 0,
            Error = null,
            Note = context.Note,
            NodeReportsJson = "[]",
            WorkflowJson = embeddedWorkflow,
            OverlaysJson = "[]",
            ErrorCode = null
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

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
        var executionStatus = result.Success
            ? "Complete"
            : result.ErrorCode == VisionRunErrorCodes.Cancelled ? "Cancelled" : "Failed";
        var wantsPreview = result.PreviewJpeg is { Length: > 0 } && ShouldPersistPreview(result.RunId, disposition);

        var record = new RunTraceRecord(
            result.RunId, startedAt, context.Source, context.JobId, context.JobVersion,
            workflow.Id, workflow.Name, context.WorkflowHash, context.DependencyManifestHash,
            executionStatus, disposition, result.TotalDurationMs, result.NodeReports.Count,
            result.Overlays.Count, false, false, null, result.Error, context.Note, result.NodeReports, result.ErrorCode);

        var nodesJson = JsonSerializer.Serialize(record.NodeReports, _json);
        var embeddedWorkflow = string.IsNullOrWhiteSpace(record.JobId) || record.JobVersion is null
            ? JsonSerializer.Serialize(workflow, _json)
            : null;
        var overlaysJson = JsonSerializer.Serialize(result.Overlays, _json);

        await using (var db = await _dbFactory.CreateDbContextAsync(ct, strongDurability))
        await using (var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct))
        {
            var entity = await db.RunTraces.AsTracking().FirstOrDefaultAsync(x => x.RunId == record.RunId, ct);
            if (entity is null)
            {
                entity = new RunTraceEntity { RunId = record.RunId };
                db.RunTraces.Add(entity);
            }
            entity.StartedAt = record.StartedAt;
            entity.Source = record.Source;
            entity.JobId = record.JobId;
            entity.JobVersion = record.JobVersion;
            entity.WorkflowId = record.WorkflowId;
            entity.WorkflowName = record.WorkflowName;
            entity.WorkflowHash = record.WorkflowHash;
            entity.DependencyManifestHash = record.DependencyManifestHash;
            entity.ExecutionStatus = record.ExecutionStatus;
            entity.Disposition = record.Disposition;
            entity.TotalDurationMs = record.TotalDurationMs;
            entity.NodeCount = record.NodeCount;
            entity.OverlayCount = record.OverlayCount;
            entity.Error = record.Error;
            entity.Note = record.Note;
            entity.NodeReportsJson = nodesJson;
            entity.WorkflowJson = embeddedWorkflow;
            entity.OverlaysJson = overlaysJson;
            entity.ErrorCode = record.ErrorCode;

            foreach (var report in result.NodeReports.Where(x => x.ExecutionSequence > 0).OrderBy(x => x.ExecutionSequence))
            {
                db.RunNodeObservations.Add(new RunNodeObservationEntity
                {
                    RunId = result.RunId,
                    ExecutionSequence = report.ExecutionSequence,
                    NodeId = report.NodeId,
                    NodeType = report.NodeType,
                    Phase = report.Phase.ToString(),
                    Success = report.Success,
                    StartOffsetMs = Math.Max(0, report.StartOffsetMs),
                    EndOffsetMs = Math.Max(report.StartOffsetMs, report.EndOffsetMs),
                    DurationMs = Math.Max(0, report.DurationMs),
                    Error = report.Error
                });
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        if (result.ReplayInput is { Png.Length: > 0 } replay && ShouldPersistReplayInput(result.RunId, disposition, record.Source))
            record = await PersistReplayInputAsync(record, replay, startedAt, ct);

        if (!wantsPreview || result.PreviewJpeg is not { Length: > 0 } jpeg) return record;

        if (_capacity is not null && !_capacity.CanPersistArtifact(jpeg.LongLength))
        {
            const string capacityNote = "Preview artifact skipped because storage capacity guard is active.";
            await AppendTraceNoteAsync(result.RunId, capacityNote);
            return record with { Note = capacityNote };
        }

        var capacityReservation = _capacity is not null ? jpeg.LongLength : 0L;
        var previewRelativePath = PreviewRelativePath(startedAt, result.RunId);
        var absolute = ArtifactPath(previewRelativePath);
        string? temp = null;

        using var artifactWrite = _maintenance?.TryEnterArtifactWrite();
        if (_maintenance is not null && artifactWrite is null)
        {
            const string snapshotNote = "Preview artifact skipped because a storage backup snapshot is in progress.";
            Interlocked.Increment(ref _snapshotSkippedPreviews);
            await AppendTraceNoteAsync(result.RunId, snapshotNote);
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

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.RunTraces
                .Where(x => x.RunId == result.RunId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.HasPreview, true)
                    .SetProperty(x => x.PreviewRelativePath, previewRelativePath)
                    .SetProperty(x => x.PreviewBytes, jpeg.LongLength), ct);
            return record with { HasPreview = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or DbUpdateException)
        {
            try { if (File.Exists(absolute)) File.Delete(absolute); } catch { /* 预览残留文件清理失败：已记录持久化失败备注 */ }
            var note = $"Preview artifact persistence failed: {ex.Message}";
            await AppendTraceNoteAsync(result.RunId, note);
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

    private async Task AppendTraceNoteAsync(string runId, string note)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.RunTraces
                .Where(x => x.RunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.Note,
                    x => x.Note == null ? note : x.Note + " | " + note), CancellationToken.None);
        }
        catch { /* 追溯备注写入失败：主追溯行或返回值仍保留状态 */ }
    }

    public async Task<int> SweepStaleRunsAsync(TimeSpan maximumAge, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - maximumAge;
        const string fallbackError = "No final record was written; the run outcome is unknown.";
        const string suffix = "Marked unknown: no finalization was recorded.";
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.RunTraces
            .Where(x => x.ExecutionStatus == "Running" && x.StartedAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ExecutionStatus, "Unknown")
                .SetProperty(x => x.Error, x => x.Error ?? fallbackError)
                .SetProperty(x => x.Note, x => string.IsNullOrEmpty(x.Note) ? suffix : x.Note + " | " + suffix), ct);
    }

    public async Task<IReadOnlyList<RunTraceRecord>> ListAsync(int take, string? jobId, string? disposition, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<RunTraceEntity> query = db.RunTraces;
        if (!string.IsNullOrWhiteSpace(jobId)) query = query.Where(x => x.JobId == jobId);
        if (!string.IsNullOrWhiteSpace(disposition))
        {
            var upper = disposition.ToUpperInvariant();
            query = query.Where(x => x.Disposition == upper);
        }
        var entities = await query
            .OrderByDescending(x => x.StartedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
        return entities.Select(e => ToRecord(e)).ToList();
    }

    public async Task<TracePage> PageAsync(TraceQuery query, CancellationToken ct)
    {
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 500);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var filtered = ApplyTraceFilter(db.RunTraces, query);
        var total = await filtered.CountAsync(ct);
        var entities = await filtered
            .OrderByDescending(x => x.StartedAt)
            .ThenByDescending(x => x.RunId)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
        var items = entities.Select(e => ToRecord(e, query.IncludeReports)).ToList();
        return new TracePage(offset, limit, total, items);
    }

    private static IQueryable<RunTraceEntity> ApplyTraceFilter(IQueryable<RunTraceEntity> source, TraceQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.JobId))
        {
            var jid = query.JobId.Trim();
            source = source.Where(x => x.JobId == jid);
        }
        if (query.JobVersion is { } version)
            source = source.Where(x => x.JobVersion == version);
        if (!string.IsNullOrWhiteSpace(query.Disposition))
        {
            var d = query.Disposition.Trim().ToUpperInvariant();
            source = source.Where(x => x.Disposition.ToUpper() == d);
        }
        if (!string.IsNullOrWhiteSpace(query.ExecutionStatus))
        {
            var es = query.ExecutionStatus.Trim().ToUpperInvariant();
            source = source.Where(x => x.ExecutionStatus.ToUpper() == es);
        }
        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            var src = query.Source.Trim();
            source = source.Where(x => x.Source == src);
        }
        if (!string.IsNullOrWhiteSpace(query.RunIdPrefix))
        {
            var prefix = query.RunIdPrefix.Trim() + "%";
            source = source.Where(x => EF.Functions.Like(x.RunId, prefix));
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowKeyword))
        {
            var kw = "%" + query.WorkflowKeyword.Trim() + "%";
            source = source.Where(x => EF.Functions.Like(x.WorkflowName, kw) || EF.Functions.Like(x.WorkflowId, kw));
        }
        if (!string.IsNullOrWhiteSpace(query.ErrorCode))
        {
            var ec = query.ErrorCode.Trim().ToUpperInvariant();
            source = source.Where(x => x.ErrorCode != null && x.ErrorCode.ToUpper() == ec);
        }
        if (query.From is { } from) source = source.Where(x => x.StartedAt >= from);
        if (query.To is { } to) source = source.Where(x => x.StartedAt <= to);
        return source;
    }

    public async Task<RunTraceRecord?> GetAsync(string runId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.RunTraces.FirstOrDefaultAsync(x => x.RunId == runId, ct);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<RunTraceRecord> UpdateDispositionAsync(string runId, UpdateDispositionRequest request, CancellationToken ct)
    {
        var allowed = new HashSet<string>(["OK", "NG", "REVIEW", "ERROR"], StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains(request.Disposition)) throw new ApiValidationException("Disposition must be OK, NG, REVIEW or ERROR.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        var affected = await db.RunTraces
            .Where(x => x.RunId == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Disposition, request.Disposition.ToUpperInvariant())
                .SetProperty(x => x.Note, request.Note), ct);
        if (affected == 0) throw new ApiNotFoundException($"Trace '{runId}' was not found.");
        await transaction.CommitAsync(ct);
        return await GetAsync(runId, ct) ?? throw new InvalidDataException($"Trace '{runId}' could not be read.");
    }

    public async Task<TraceStats> StatsAsync(TraceQuery query, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var filtered = ApplyTraceFilter(db.RunTraces, query);
        return await ComputeStatsAsync(filtered, ct);
    }

    public async Task<TraceStats> StatsAsync(int days, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ComputeStatsAsync(db.RunTraces.Where(x => x.StartedAt >= cutoff), ct);
    }

    private static async Task<TraceStats> ComputeStatsAsync(IQueryable<RunTraceEntity> query, CancellationToken ct)
    {
        var agg = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Ok = g.Sum(x => x.Disposition.ToUpper() == "OK" ? 1 : 0),
                Ng = g.Sum(x => x.Disposition.ToUpper() == "NG" ? 1 : 0),
                Review = g.Sum(x => x.Disposition.ToUpper() == "REVIEW" ? 1 : 0),
                Error = g.Sum(x => x.Disposition.ToUpper() == "ERROR" ? 1 : 0),
                Avg = g.Average(x => x.TotalDurationMs)
            })
            .FirstOrDefaultAsync(ct);
        return agg is null ? new TraceStats(0, 0, 0, 0, 0, 0) : new TraceStats(agg.Total, agg.Ok, agg.Ng, agg.Review, agg.Error, agg.Avg);
    }

    public async Task<WorkflowDefinition?> GetWorkflowSnapshotAsync(string runId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Equivalent to COALESCE(t.workflow_json, j.workflow_json) across run_traces and job_versions.
        var json = await (
            from t in db.RunTraces
            where t.RunId == runId
            join j in db.JobVersions on new { JobId = t.JobId!, Version = t.JobVersion ?? 0 } equals new { j.JobId, j.Version } into jg
            from j in jg.DefaultIfEmpty()
            select t.WorkflowJson ?? (j != null ? j.WorkflowJson : null))
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WorkflowDefinition>(json, _json);
    }

    public async Task<RuntimeDependencyManifest?> GetDependencyManifestAsync(string runId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var json = await (
            from t in db.RunTraces
            where t.RunId == runId
            join m in db.RuntimeDependencyManifests on t.DependencyManifestHash equals m.ManifestHash into mg
            from m in mg.DefaultIfEmpty()
            select m != null ? m.ManifestJson : null)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<RuntimeDependencyManifest>(json, _json);
    }

    public async Task<IReadOnlyList<VisionOverlay>?> GetOverlaysAsync(string runId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var json = await db.RunTraces
            .Where(x => x.RunId == runId)
            .Select(x => x.OverlaysJson)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<VisionOverlay>>(json, _json);
    }

    public string? FindPreviewPath(string runId)
    {
        using var db = _dbFactory.CreateDbContext();
        var relative = db.RunTraces
            .Where(x => x.RunId == runId && x.HasPreview)
            .Select(x => x.PreviewRelativePath)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = ArtifactPath(relative);
        return File.Exists(path) ? path : null;
    }

    public string? FindReplayInputPath(string runId)
    {
        using var db = _dbFactory.CreateDbContext();
        var relative = db.RunTraces
            .Where(x => x.RunId == runId && x.HasReplayInput)
            .Select(x => x.ReplayRelativePath)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = ArtifactPath(relative);
        return File.Exists(path) ? path : null;
    }

    public async Task<TraceStorageStatus> StorageStatusAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stats = await db.RunTraces
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                PreviewCount = g.Sum(x => x.HasPreview ? 1 : 0),
                ReplayCount = g.Sum(x => x.HasReplayInput ? 1 : 0),
                ArtifactBytes = g.Sum(x => x.PreviewBytes + x.ReplayBytes)
            })
            .FirstOrDefaultAsync(ct);
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        var databaseBytes = Size(_db.DatabasePath) + Size(_db.DatabasePath + "-wal") + Size(_db.DatabasePath + "-shm");
        var capacity = _capacity is null ? null : await _capacity.RefreshAsync(ct);
        var schema = await _db.GetSchemaStatusAsync(ct);
        return new TraceStorageStatus(Path.GetFileName(_db.DatabasePath), databaseBytes, stats?.Total ?? 0,
            stats?.PreviewCount ?? 0, stats?.ReplayCount ?? 0, stats?.ArtifactBytes ?? 0,
            _retention, capacity, schema, SnapshotSkippedPreviews, SnapshotSkippedReplays);
    }

    private RunTraceRecord ToRecord(RunTraceEntity e, bool includeReports = true)
    {
        var nodeReports = includeReports
            ? JsonSerializer.Deserialize<List<NodeRunReport>>(e.NodeReportsJson, _json) ?? []
            : (IReadOnlyList<NodeRunReport>)Array.Empty<NodeRunReport>();
        return new RunTraceRecord(
            e.RunId, e.StartedAt, e.Source, e.JobId, e.JobVersion, e.WorkflowId, e.WorkflowName,
            e.WorkflowHash, e.DependencyManifestHash, e.ExecutionStatus, e.Disposition,
            e.TotalDurationMs, e.NodeCount, e.OverlayCount, e.HasPreview, e.HasReplayInput,
            e.ReplaySourceNodeId, e.Error, e.Note, nodeReports, e.ErrorCode);
    }

    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
}
