using VisionStudio.Api.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>
/// One-shot upgrade path from V0.19 file metadata to V0.20 SQLite metadata.
/// Legacy files are intentionally left untouched so rollback to an older binary remains possible.
/// </summary>
public sealed class LegacyStorageMigrationService
{
    private const string MigrationKey = "v20-file-metadata-to-sqlite";
    private readonly IVisionStudioDbFactory _dbFactory;
    private readonly ILogger<LegacyStorageMigrationService> _logger;
    private int _migrationErrors;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public LegacyStorageMigrationService(IVisionStudioDbFactory dbFactory, IWebHostEnvironment env, ILogger<LegacyStorageMigrationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public LegacyStorageMigrationService(SqliteMetadataDatabase db, IWebHostEnvironment env, ILogger<LegacyStorageMigrationService> logger)
        : this(new VisionStudioDbFactory(db), env, logger) { }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.MigrationStates.AnyAsync(x => x.Key == MigrationKey, ct)) return;

        _migrationErrors = 0;
        var jobs = await MigrateJobsAsync(ct);
        var calibrations = await MigrateCalibrationsAsync(ct);
        var traces = await MigrateTracesAsync(ct);

        if (_migrationErrors > 0)
        {
            _logger.LogWarning("V0.20 legacy migration imported partial data but had {Errors} errors. Completion marker was not written; startup will retry safely.", _migrationErrors);
            return;
        }

        await using var writeDb = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await _dbFactory.BeginImmediateTransactionAsync(writeDb, ct);
        var existing = await writeDb.MigrationStates.AsTracking().FirstOrDefaultAsync(x => x.Key == MigrationKey, ct);
        var now = DateTimeOffset.UtcNow;
        var detail = $"jobs={jobs}; calibrations={calibrations}; traces={traces}";
        if (existing is null)
        {
            writeDb.MigrationStates.Add(new MigrationStateEntity { Key = MigrationKey, CompletedAt = now, Detail = detail });
        }
        else
        {
            existing.CompletedAt = now;
            existing.Detail = detail;
        }
        await writeDb.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("V0.20 legacy storage migration finished: {Jobs} jobs, {Calibrations} calibrations, {Traces} traces.", jobs, calibrations, traces);
    }

    private async Task<int> MigrateJobsAsync(CancellationToken ct)
    {
        var root = Path.Combine(_dbFactory.Database.DataRoot, "jobs");
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
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);

                if (!await db.Jobs.AnyAsync(x => x.Id == meta.Id, ct))
                {
                    db.Jobs.Add(new JobEntity
                    {
                        Id = meta.Id,
                        Name = meta.Name,
                        Description = meta.Description,
                        CreatedAt = meta.CreatedAt,
                        UpdatedAt = meta.UpdatedAt,
                        LatestVersion = meta.LatestVersion,
                        PublishedVersion = meta.PublishedVersion
                    });
                }

                var versionsDir = Path.Combine(dir, "versions");
                if (Directory.Exists(versionsDir))
                {
                    var existingVersions = (await db.JobVersions.Where(x => x.JobId == meta.Id).Select(x => x.Version).ToListAsync(ct)).ToHashSet();
                    foreach (var file in Directory.EnumerateFiles(versionsDir, "v*.json").OrderBy(x => x))
                    {
                        var snapshot = await ReadAsync<JobVersionSnapshot>(file, ct);
                        if (snapshot is null || !existingVersions.Add(snapshot.Version)) continue;
                        db.JobVersions.Add(new JobVersionEntity
                        {
                            JobId = snapshot.JobId,
                            Version = snapshot.Version,
                            WorkflowHash = snapshot.WorkflowHash,
                            CreatedAt = snapshot.CreatedAt,
                            Note = snapshot.Note,
                            WorkflowJson = JsonSerializer.Serialize(snapshot.Workflow, _json)
                        });
                    }
                }

                var existingHistory = await db.JobPublicationHistory
                    .Where(x => x.JobId == meta.Id)
                    .Select(x => new { x.Version, x.Action, x.At })
                    .ToListAsync(ct);
                var historySet = existingHistory.Select(x => (x.Version, x.Action, x.At)).ToHashSet();
                foreach (var evt in meta.PublicationHistory)
                {
                    if (historySet.Add((evt.Version, evt.Action, evt.At)))
                    {
                        db.JobPublicationHistory.Add(new JobPublicationHistoryEntity
                        {
                            JobId = meta.Id,
                            Version = evt.Version,
                            Action = evt.Action,
                            At = evt.At
                        });
                    }
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                count++;
            }
            catch (Exception ex) { Interlocked.Increment(ref _migrationErrors); _logger.LogWarning(ex, "Could not migrate legacy job directory {Directory}.", dir); }
        }
        return count;
    }

    private async Task<int> MigrateCalibrationsAsync(CancellationToken ct)
    {
        var root = Path.Combine(_dbFactory.Database.DataRoot, "calibrations");
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
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);

                if (!await db.CalibrationAssets.AnyAsync(x => x.Id == meta.Id, ct))
                {
                    db.CalibrationAssets.Add(new CalibrationAssetEntity
                    {
                        Id = meta.Id,
                        Name = meta.Name,
                        Description = meta.Description,
                        CreatedAt = meta.CreatedAt,
                        UpdatedAt = meta.UpdatedAt,
                        LatestVersion = meta.LatestVersion,
                        PublishedVersion = meta.PublishedVersion
                    });
                }

                var versionsDir = Path.Combine(dir, "versions");
                if (Directory.Exists(versionsDir))
                {
                    var existingVersions = (await db.CalibrationVersions.Where(x => x.AssetId == meta.Id).Select(x => x.Version).ToListAsync(ct)).ToHashSet();
                    foreach (var file in Directory.EnumerateFiles(versionsDir, "v*.json").OrderBy(x => x))
                    {
                        var snapshot = await ReadAsync<CalibrationVersionSnapshot>(file, ct);
                        if (snapshot is null || !existingVersions.Add(snapshot.Version)) continue;
                        db.CalibrationVersions.Add(new CalibrationVersionEntity
                        {
                            AssetId = snapshot.AssetId,
                            Version = snapshot.Version,
                            SnapshotHash = snapshot.SnapshotHash,
                            CreatedAt = snapshot.CreatedAt,
                            Note = snapshot.Note,
                            WorkspaceJson = JsonSerializer.Serialize(snapshot.Workspace, _json),
                            ResultJson = JsonSerializer.Serialize(snapshot.Result, _json)
                        });
                    }
                }

                var existingHistory = await db.CalibrationPublicationHistory
                    .Where(x => x.AssetId == meta.Id)
                    .Select(x => new { x.Version, x.Action, x.At })
                    .ToListAsync(ct);
                var historySet = existingHistory.Select(x => (x.Version, x.Action, x.At)).ToHashSet();
                foreach (var evt in meta.PublicationHistory)
                {
                    if (historySet.Add((evt.Version, evt.Action, evt.At)))
                    {
                        db.CalibrationPublicationHistory.Add(new CalibrationPublicationHistoryEntity
                        {
                            AssetId = meta.Id,
                            Version = evt.Version,
                            Action = evt.Action,
                            At = evt.At
                        });
                    }
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                count++;
            }
            catch (Exception ex) { Interlocked.Increment(ref _migrationErrors); _logger.LogWarning(ex, "Could not migrate legacy calibration directory {Directory}.", dir); }
        }
        return count;
    }

    private async Task<int> MigrateTracesAsync(CancellationToken ct)
    {
        var root = Path.Combine(_dbFactory.Database.DataRoot, "traces");
        if (!Directory.Exists(root)) return 0;
        var artifactRoot = Path.Combine(_dbFactory.Database.DataRoot, "artifacts");
        var count = 0;
        foreach (var metadataPath in Directory.EnumerateFiles(root, "metadata.json", SearchOption.AllDirectories))
        {
            try
            {
                var record = await ReadAsync<RunTraceRecord>(metadataPath, ct);
                if (record is null) continue;
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                if (await db.RunTraces.AnyAsync(x => x.RunId == record.RunId, ct)) continue;

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

                db.RunTraces.Add(new RunTraceEntity
                {
                    RunId = record.RunId,
                    StartedAt = record.StartedAt,
                    Source = record.Source,
                    JobId = record.JobId,
                    JobVersion = record.JobVersion,
                    WorkflowId = record.WorkflowId,
                    WorkflowName = record.WorkflowName,
                    WorkflowHash = record.WorkflowHash,
                    ExecutionStatus = record.ExecutionStatus,
                    Disposition = record.Disposition,
                    TotalDurationMs = record.TotalDurationMs,
                    NodeCount = record.NodeCount,
                    OverlayCount = record.OverlayCount,
                    HasPreview = previewRelative is not null,
                    PreviewRelativePath = previewRelative,
                    PreviewBytes = File.Exists(previewPath) ? new FileInfo(previewPath).Length : 0,
                    Error = record.Error,
                    Note = record.Note,
                    NodeReportsJson = JsonSerializer.Serialize(record.NodeReports, _json),
                    WorkflowJson = workflowJson,
                    OverlaysJson = overlaysJson
                });
                await db.SaveChangesAsync(ct);
                count++;
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

    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private sealed record LegacyJobMetadata(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int? PublishedVersion, IReadOnlyList<PublicationEvent> PublicationHistory);
    private sealed record LegacyCalibrationMetadata(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int? PublishedVersion, IReadOnlyList<CalibrationPublicationEvent> PublicationHistory);
}
