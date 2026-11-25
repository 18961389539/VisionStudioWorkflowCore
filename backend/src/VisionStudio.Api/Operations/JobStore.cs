using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

public sealed record JobVersionInfo(
    int Version,
    string WorkflowHash,
    DateTimeOffset CreatedAt,
    string Note,
    bool Published,
    JobVersionValidationInfo? Validation = null,
    int BindingCount = 0);

public sealed record PublicationEvent(
    int Version,
    string Action,
    DateTimeOffset At,
    string? DependencyManifestHash = null);

public sealed record JobDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    int? PublishedVersion,
    string? PublishedDependencyManifestHash,
    IReadOnlyList<JobVersionInfo> Versions,
    IReadOnlyList<PublicationEvent> PublicationHistory,
    string? ProductId = null,
    string? RecipeCode = null);

public sealed record JobVersionSnapshot(
    string JobId,
    int Version,
    string WorkflowHash,
    DateTimeOffset CreatedAt,
    string Note,
    WorkflowDefinition Workflow,
    string? BaseWorkflowHash = null,
    WorkflowDefinition? BaseWorkflow = null,
    IReadOnlyDictionary<string, string>? ParameterBindings = null,
    IReadOnlyDictionary<string, JsonElement>? ParameterSnapshot = null);


public sealed record PublishedJobSnapshot(
    JobVersionSnapshot VersionSnapshot,
    RuntimeDependencyManifest DependencyManifest)
{
    public string JobId => VersionSnapshot.JobId;
    public int Version => VersionSnapshot.Version;
    public string WorkflowHash => VersionSnapshot.WorkflowHash;
    /// <summary>Immutable expanded execution graph captured when production starts (module versions are pinned by the manifest).</summary>
    public WorkflowDefinition? ExpandedWorkflow { get; init; }
    public WorkflowDefinition Workflow => ExpandedWorkflow ?? VersionSnapshot.Workflow;
}

public sealed record CreateJobRequest(
    string Id,
    string Name,
    string? Description,
    WorkflowDefinition Workflow,
    string? Note = null,
    string? ProductId = null,
    string? RecipeCode = null,
    Dictionary<string, string>? ParameterBindings = null,
    bool RequiresValidation = false);

public sealed record SaveJobVersionRequest(
    WorkflowDefinition Workflow,
    string? Note = null,
    Dictionary<string, string>? ParameterBindings = null);

/// <summary>
/// SQLite-backed immutable Job/Recipe version store.
/// Version snapshots are transactional rows and are never overwritten.
/// Publish/Rollback only changes the published pointer and appends history.
/// </summary>
public sealed partial class JobStore
{
    private readonly IVisionStudioDbFactory _dbFactory;
    private readonly RecipeParameterStore _parameters;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public JobStore(IVisionStudioDbFactory dbFactory, RecipeParameterStore parameters)
    {
        _dbFactory = dbFactory;
        _parameters = parameters;
    }
    public JobStore(SqliteMetadataDatabase db, RecipeParameterStore parameters) : this(new VisionStudioDbFactory(db), parameters) { }
    public JobStore(SqliteMetadataDatabase db) : this(new VisionStudioDbFactory(db), new RecipeParameterStore(db)) { }
    public JobStore(IWebHostEnvironment env) : this(new SqliteMetadataDatabase(env)) { }

    public async Task<IReadOnlyList<JobDescriptor>> ListAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Jobs.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        var result = new List<JobDescriptor>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(db, row, ct));
        return result;
    }

    public async Task<JobDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : await BuildDescriptorAsync(db, row, ct);
    }

    internal async Task<StationJob?> GetStationJobAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var data = await (
            from j in db.Jobs
            where j.Id == id
            join v in db.JobVersions on new { JobId = j.Id, Version = j.PublishedVersion } equals new { JobId = v.JobId, Version = (int?)v.Version } into vj
            from v in vj.DefaultIfEmpty()
            select new { j.Name, j.PublishedVersion, WorkflowHash = v != null ? v.WorkflowHash : null })
            .FirstOrDefaultAsync(ct);
        return data is null ? null : new StationJob(data.Name, data.PublishedVersion, data.WorkflowHash);
    }

    public async Task<JobDescriptor> CreateAsync(CreateJobRequest request, CancellationToken ct)
    {
        ValidateId(request.Id);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Job name is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = await CreateSnapshotAsync(request.Id, 1, request.Workflow, request.Note ?? "Initial version", request.ProductId, request.ParameterBindings, now, ct);

            db.Jobs.Add(new JobEntity
            {
                Id = request.Id,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
                LatestVersion = 1,
                PublishedVersion = null,
                ProductId = NullIfWhiteSpace(request.ProductId),
                RecipeCode = NullIfWhiteSpace(request.RecipeCode),
                RequiresValidation = request.RequiresValidation
            });

            AddVersionEntity(db, snapshot);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (await GetAsync(request.Id, ct))!;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"Job '{request.Id}' already exists.", sqliteEx);
        }
    }

    public async Task<JobVersionSnapshot> AddVersionAsync(string id, SaveJobVersionRequest request, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);

        var row = await db.Jobs.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");
        var next = row.LatestVersion + 1;
        var now = DateTimeOffset.UtcNow;
        var snapshot = await CreateSnapshotAsync(id, next, request.Workflow, request.Note ?? string.Empty, row.ProductId, request.ParameterBindings, now, ct);

        AddVersionEntity(db, snapshot);
        row.LatestVersion = next;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<JobVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.JobVersions.FirstOrDefaultAsync(x => x.JobId == id && x.Version == version, ct);
        if (entity is null) return null;
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(entity.WorkflowJson, _json)
            ?? throw new InvalidOperationException($"Stored workflow for job '{id}' v{version} is invalid.");
        var baseWorkflow = string.IsNullOrWhiteSpace(entity.BaseWorkflowJson)
            ? workflow
            : JsonSerializer.Deserialize<WorkflowDefinition>(entity.BaseWorkflowJson, _json) ?? workflow;
        var bindings = string.IsNullOrWhiteSpace(entity.ParameterBindingsJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(entity.ParameterBindingsJson, _json) ?? new();
        var parameterSnapshot = string.IsNullOrWhiteSpace(entity.ParameterSnapshotJson)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entity.ParameterSnapshotJson, _json) ?? new();
        return new JobVersionSnapshot(id, version, entity.WorkflowHash, entity.CreatedAt, entity.Note, workflow,
            string.IsNullOrWhiteSpace(entity.BaseWorkflowHash) ? WorkflowFingerprint.Compute(baseWorkflow) : entity.BaseWorkflowHash,
            baseWorkflow, bindings, parameterSnapshot);
    }

    public async Task<JobVersionSnapshot> GetPublishedVersionAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");
        if (row.PublishedVersion is null)
            throw new ApiConflictException($"Job '{id}' has no published production version.");
        return await GetVersionAsync(id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published version {row.PublishedVersion} is missing for job '{id}'.");
    }

    public async Task<PublishedJobSnapshot> GetPublishedSnapshotAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");
        if (row.PublishedVersion is null)
            throw new ApiConflictException($"Job '{id}' has no published production version.");
        if (string.IsNullOrWhiteSpace(row.PublishedDependencyManifestHash))
            throw new ApiConflictException($"Job '{id}' was published before RuntimeDependencyManifest was introduced. Re-publish version {row.PublishedVersion} before production use.");

        var version = await GetVersionAsync(id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published version {row.PublishedVersion} is missing for job '{id}'.");
        var manifest = await LoadManifestAsync(db, row.PublishedDependencyManifestHash, ct)
            ?? throw new InvalidDataException($"Dependency manifest '{row.PublishedDependencyManifestHash}' is missing for published job '{id}'.");
        return new PublishedJobSnapshot(version, manifest);
    }

    public async Task<JobDescriptor> PublishAsync(string id, int version, string action, RuntimeDependencyManifest dependencyManifest, CancellationToken ct, DateTimeOffset? expectedUpdatedAt = null)
    {
        // R03：发布是不可重放的现场动作基准——发布事务必须能在断电后存活。
        await using var db = await _dbFactory.CreateDbContextAsync(ct, strongDurability: true);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        var row = await db.Jobs.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");

        if (expectedUpdatedAt.HasValue && row.UpdatedAt != expectedUpdatedAt.Value)
            throw new ApiConflictException($"Recipe '{id}' changed after the publication review. Reload the recipe and review the active version before publishing again.");

        if (!await db.JobVersions.AnyAsync(x => x.JobId == id && x.Version == version, ct))
            throw new ApiNotFoundException($"Version {version} does not exist for job '{id}'.");

        if (RequiresDatasetValidation(row) || await DeliveredVersionRequiresValidationAsync(db, id, version, ct))
        {
            var validation = await db.JobVersionValidations
                .Where(x => x.JobId == id && x.Version == version)
                .Select(x => new { x.Accepted, x.Reason })
                .FirstOrDefaultAsync(ct)
                ?? throw new ApiConflictException($"Recipe '{id}' V{version} must be linked to an accepted Dataset Validation run before publish.");
            if (!validation.Accepted)
                throw new ApiConflictException($"Recipe '{id}' V{version} failed its linked validation policy: {validation.Reason}");
        }

        // 发布修订严格递增，时钟分辨率或回拨也不能让旧确认在 A→B→A 后重新生效。
        var now = DateTimeOffset.UtcNow;
        if (now <= row.UpdatedAt) now = row.UpdatedAt.AddTicks(1);
        await StoreManifestAsync(db, dependencyManifest, ct);
        row.PublishedVersion = version;
        row.PublishedDependencyManifestHash = dependencyManifest.ManifestHash;
        row.UpdatedAt = now;
        db.JobPublicationHistory.Add(new JobPublicationHistoryEntity
        {
            JobId = id,
            Version = version,
            Action = action,
            At = now,
            DependencyManifestHash = dependencyManifest.ManifestHash
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    private async Task<JobDescriptor> BuildDescriptorAsync(VisionStudioDbContext db, JobEntity row, CancellationToken ct)
    {
        var versionRows = await (
            from v in db.JobVersions
            where v.JobId == row.Id
            join jv in db.JobVersionValidations on new { v.JobId, v.Version } equals new { jv.JobId, jv.Version } into jvg
            from jv in jvg.DefaultIfEmpty()
            orderby v.Version
            select new
            {
                v.Version,
                v.WorkflowHash,
                v.CreatedAt,
                v.Note,
                v.ParameterBindingsJson,
                Validation = jv
            })
            .ToListAsync(ct);

        var versions = new List<JobVersionInfo>(versionRows.Count);
        foreach (var item in versionRows)
        {
            JobVersionValidationInfo? validation = null;
            if (item.Validation is { } jv)
            {
                var summary = JsonSerializer.Deserialize<ValidationRunSummary>(jv.SummaryJson, _json);
                var policy = JsonSerializer.Deserialize<ValidationAcceptancePolicy>(jv.PolicyJson, _json);
                validation = new JobVersionValidationInfo(jv.ValidationRunId, jv.DatasetId, jv.Accepted, jv.LinkedAt, jv.Reason, summary, policy);
            }
            var bindingsJson = string.IsNullOrWhiteSpace(item.ParameterBindingsJson) ? "{}" : item.ParameterBindingsJson;
            var bindingCount = (JsonSerializer.Deserialize<Dictionary<string, string>>(bindingsJson, _json) ?? new()).Count;
            versions.Add(new JobVersionInfo(item.Version, item.WorkflowHash, item.CreatedAt, item.Note, row.PublishedVersion == item.Version, validation, bindingCount));
        }

        var history = await db.JobPublicationHistory
            .Where(x => x.JobId == row.Id)
            .OrderBy(x => x.Seq)
            .Select(x => new PublicationEvent(x.Version, x.Action, x.At, x.DependencyManifestHash))
            .ToListAsync(ct);

        return new JobDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, row.PublishedDependencyManifestHash, versions, history, row.ProductId, row.RecipeCode);
    }

    private async Task<JobVersionSnapshot> CreateSnapshotAsync(
        string jobId, int version, WorkflowDefinition baseWorkflow, string note, string? productId,
        IReadOnlyDictionary<string,string>? bindings, DateTimeOffset now, CancellationToken ct)
    {
        var resolved = await _parameters.ResolveForCreateAsync(jobId, productId, baseWorkflow, bindings, ct);
        return new JobVersionSnapshot(jobId, version, resolved.EffectiveWorkflowHash, now, note, resolved.EffectiveWorkflow,
            resolved.BaseWorkflowHash, resolved.BaseWorkflow, resolved.Bindings, resolved.Snapshot);
    }

    private void AddVersionEntity(VisionStudioDbContext db, JobVersionSnapshot snapshot)
    {
        db.JobVersions.Add(new JobVersionEntity
        {
            JobId = snapshot.JobId,
            Version = snapshot.Version,
            WorkflowHash = snapshot.WorkflowHash,
            CreatedAt = snapshot.CreatedAt,
            Note = snapshot.Note,
            WorkflowJson = JsonSerializer.Serialize(snapshot.Workflow, _json),
            BaseWorkflowHash = snapshot.BaseWorkflowHash ?? snapshot.WorkflowHash,
            BaseWorkflowJson = JsonSerializer.Serialize(snapshot.BaseWorkflow ?? snapshot.Workflow, _json),
            ParameterBindingsJson = JsonSerializer.Serialize(snapshot.ParameterBindings ?? new Dictionary<string, string>(), _json),
            ParameterSnapshotJson = JsonSerializer.Serialize(snapshot.ParameterSnapshot ?? new Dictionary<string, JsonElement>(), _json)
        });
    }

    private async Task StoreManifestAsync(VisionStudioDbContext db, RuntimeDependencyManifest manifest, CancellationToken ct)
    {
        if (!await db.RuntimeDependencyManifests.AnyAsync(x => x.ManifestHash == manifest.ManifestHash, ct))
        {
            db.RuntimeDependencyManifests.Add(new RuntimeDependencyManifestEntity
            {
                ManifestHash = manifest.ManifestHash,
                SchemaVersion = manifest.SchemaVersion,
                CapturedAt = manifest.CapturedAt,
                ManifestJson = JsonSerializer.Serialize(manifest, _json)
            });
        }
    }

    private async Task<RuntimeDependencyManifest?> LoadManifestAsync(VisionStudioDbContext db, string hash, CancellationToken ct)
    {
        var json = await db.RuntimeDependencyManifests
            .Where(x => x.ManifestHash == hash)
            .Select(x => x.ManifestJson)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<RuntimeDependencyManifest>(json, _json);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Sanitize(id) != id)
            throw new ApiValidationException("Job id may contain only letters, digits, '-' and '_'.");
    }

    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
