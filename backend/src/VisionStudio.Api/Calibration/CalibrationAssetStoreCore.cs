using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

/// <summary>Family-independent view of one immutable calibration version row.</summary>
internal sealed record CalibrationVersionData(
    string AssetId,
    int Version,
    string SnapshotHash,
    string ModelHash,
    DateTimeOffset CreatedAt,
    string Note,
    string ModelJson,
    string SourceDocumentJson,
    string SummaryJson);

internal sealed record CalibrationVersionInfoData(int Version, string SnapshotHash, string ModelHash, DateTimeOffset CreatedAt, string Note, bool Published);

internal sealed record CalibrationPublicationEventData(int Version, string Action, DateTimeOffset At);

internal sealed record CalibrationAssetData(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    int? PublishedVersion,
    IReadOnlyList<CalibrationVersionInfoData> Versions,
    IReadOnlyList<CalibrationPublicationEventData> PublicationHistory);

/// <summary>
/// Immutable, hash-chained calibration asset storage shared by every calibration family.
/// </summary>
public abstract class CalibrationAssetStoreBase<TModel, TAsset, TVersion, THistory>
    where TModel : class
    where TAsset : SpecializedCalibrationAssetEntity, new()
    where TVersion : SpecializedCalibrationVersionEntity, new()
    where THistory : SpecializedCalibrationHistoryEntity, new()
{
    private readonly IVisionStudioDbFactory _dbFactory;

    protected CalibrationAssetStoreBase(IVisionStudioDbFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    protected CalibrationAssetStoreBase(SqliteMetadataDatabase db)
        : this(new VisionStudioDbFactory(db))
    {
    }

    /// <summary>Human-readable family name used in error messages, e.g. "Hand-eye calibration".</summary>
    protected abstract string Label { get; }

    /// <summary>Name used when talking about the wizard, e.g. "hand-eye wizard".</summary>
    protected abstract string WizardName { get; }

    /// <summary>Note stored on the first version of a newly created asset.</summary>
    protected abstract string DefaultCreateNote { get; }

    protected abstract void ValidateModel(TModel model);

    /// <summary>Deserialise the wizard session document and prove that the settings it carries match the
    /// model being saved, so an asset version can always be reproduced from its own evidence.</summary>
    protected abstract void ValidateSourceDocument(string sourceDocumentJson, TModel model);

    protected JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private protected async Task<IReadOnlyList<CalibrationAssetData>> ListCoreAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Set<TAsset>().OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        var result = new List<CalibrationAssetData>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(db, row, ct));
        return result;
    }

    private protected async Task<CalibrationAssetData?> GetCoreAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Set<TAsset>().FirstOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : await BuildDescriptorAsync(db, row, ct);
    }

    private protected async Task<CalibrationVersionData> CreateCoreAsync(
        string id, string name, string? description, TModel model, string sourceDocumentJson, string summaryJson, string? note, CancellationToken ct)
    {
        ValidateId(id);
        if (string.IsNullOrWhiteSpace(name)) throw new ApiValidationException($"{Label} asset name is required.");
        var snapshot = CreateSnapshot(id, 1, model, sourceDocumentJson, summaryJson, note ?? DefaultCreateNote, DateTimeOffset.UtcNow);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            db.Set<TAsset>().Add(new TAsset
            {
                Id = id,
                Name = name.Trim(),
                Description = description?.Trim() ?? string.Empty,
                CreatedAt = snapshot.CreatedAt,
                UpdatedAt = snapshot.CreatedAt,
                LatestVersion = 1,
                PublishedVersion = null
            });
            db.Set<TVersion>().Add(ToVersionEntity(snapshot));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"{Label} asset '{id}' already exists.", sqliteEx);
        }
        return snapshot;
    }

    private protected async Task<CalibrationVersionData> AddVersionCoreAsync(
        string id, TModel model, string sourceDocumentJson, string summaryJson, string? note, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        var row = await db.Set<TAsset>().AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"{Label} asset '{id}' does not exist.");
        var version = checked(row.LatestVersion + 1);
        var snapshot = CreateSnapshot(id, version, model, sourceDocumentJson, summaryJson, note ?? string.Empty, DateTimeOffset.UtcNow);
        db.Set<TVersion>().Add(ToVersionEntity(snapshot));
        row.LatestVersion = version;
        row.UpdatedAt = snapshot.CreatedAt;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    private protected async Task<CalibrationVersionData?> GetVersionCoreAsync(string id, int version, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ReadVersionAsync(db, id, version, ct);
    }

    private protected async Task<CalibrationVersionData> GetPublishedCoreAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Set<TAsset>().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"{Label} asset '{id}' does not exist.");
        if (row.PublishedVersion is null) throw new ApiConflictException($"{Label} asset '{id}' has no published version.");
        return await ReadVersionAsync(db, id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published {Label} version {row.PublishedVersion} is missing.");
    }

    private protected async Task<CalibrationVersionData> RequireModelMatchCoreAsync(string id, int version, string calibrationJson, CancellationToken ct)
    {
        var snapshot = await GetVersionCoreAsync(id, version, ct)
            ?? throw new InvalidOperationException($"{Label} node references missing hand-eye calibration '{id}' v{version}.");
        TModel model;
        try
        {
            model = JsonSerializer.Deserialize<TModel>(calibrationJson, Json) ?? throw new JsonException("empty model");
            ValidateModel(model);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{Label} node produced invalid calibrationJson while bound to '{id}' v{version}.", ex);
        }
        catch (ApiValidationException ex)
        {
            throw new InvalidOperationException($"{Label} node produced an invalid hand-eye model while bound to '{id}' v{version}: {ex.Message}", ex);
        }
        if (!string.Equals(ModelHash(model), snapshot.ModelHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Label} node result does not match immutable hand-eye calibration '{id}' v{version}. Re-solve with the pinned samples/settings or reapply that version from the {WizardName} before publishing.");
        return snapshot;
    }

    private protected async Task<CalibrationAssetData> PublishCoreAsync(string id, int version, string action, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        var row = await db.Set<TAsset>().AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"{Label} asset '{id}' does not exist.");
        if (await ReadVersionAsync(db, id, version, ct) is null)
            throw new ApiNotFoundException($"{Label} version {version} does not exist for '{id}'.");
        var now = DateTimeOffset.UtcNow;
        row.PublishedVersion = version;
        row.UpdatedAt = now;
        db.Set<THistory>().Add(new THistory
        {
            AssetId = id,
            Version = version,
            Action = action,
            At = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await GetCoreAsync(id, ct))!;
    }

    /// <summary>Model hash used for pin verification. Kept byte-for-byte compatible with the previous
    /// per-family implementations (web defaults, no converter) so existing hashes stay valid.</summary>
    protected static string ModelHash(TModel model) => CalibrationAssetHash.Model(model);

    private CalibrationVersionData CreateSnapshot(string id, int version, TModel model, string sourceDocumentJson, string summaryJson, string note, DateTimeOffset createdAt)
    {
        ValidateModel(model);
        ValidateJson(sourceDocumentJson, "sourceDocumentJson", 40_000_000);
        ValidateJson(summaryJson, "summaryJson", 2_000_000);
        ValidateSourceDocument(sourceDocumentJson, model);
        if (string.IsNullOrWhiteSpace(note)) note = string.Empty;
        if (note.Length > 1000) throw new ApiValidationException("Calibration version note must be at most 1000 characters.");
        var modelHash = ModelHash(model);
        var snapshotHash = ComputeSnapshotHash(model, sourceDocumentJson, summaryJson);
        return new CalibrationVersionData(id, version, snapshotHash, modelHash, createdAt, note,
            JsonSerializer.Serialize(model, Json), sourceDocumentJson, summaryJson);
    }

    private async Task<CalibrationVersionData?> ReadVersionAsync(VisionStudioDbContext db, string id, int version, CancellationToken ct)
    {
        var entity = await db.Set<TVersion>().FirstOrDefaultAsync(x => x.AssetId == id && x.Version == version, ct);
        if (entity is null) return null;
        var model = JsonSerializer.Deserialize<TModel>(entity.ModelJson, Json)
            ?? throw new InvalidDataException($"Stored {Label} model for '{id}' v{version} is invalid.");
        if (!string.Equals(entity.ModelHash, ModelHash(model), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entity.SnapshotHash, ComputeSnapshotHash(model, entity.SourceDocumentJson, entity.SummaryJson), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Label} '{id}' v{version} failed snapshot integrity validation.");
        try { ValidateModel(model); }
        catch (ApiValidationException ex) { throw new InvalidDataException($"{Label} '{id}' v{version} contains an invalid hand-eye model.", ex); }
        return new CalibrationVersionData(id, version, entity.SnapshotHash, entity.ModelHash, entity.CreatedAt, entity.Note, entity.ModelJson, entity.SourceDocumentJson, entity.SummaryJson);
    }

    private string ComputeSnapshotHash(TModel model, string sourceDocumentJson, string summaryJson)
    {
        var canonical = JsonSerializer.Serialize(new { model, sourceDocumentJson, summaryJson }, Json);
        return CalibrationAssetHash.Hash(canonical);
    }

    private static TVersion ToVersionEntity(CalibrationVersionData snapshot) => new()
    {
        AssetId = snapshot.AssetId,
        Version = snapshot.Version,
        SnapshotHash = snapshot.SnapshotHash,
        ModelHash = snapshot.ModelHash,
        CreatedAt = snapshot.CreatedAt,
        Note = snapshot.Note,
        ModelJson = snapshot.ModelJson,
        SourceDocumentJson = snapshot.SourceDocumentJson,
        SummaryJson = snapshot.SummaryJson
    };

    private async Task<CalibrationAssetData> BuildDescriptorAsync(VisionStudioDbContext db, TAsset row, CancellationToken ct)
    {
        var versions = await db.Set<TVersion>()
            .Where(x => x.AssetId == row.Id)
            .OrderBy(x => x.Version)
            .Select(x => new CalibrationVersionInfoData(x.Version, x.SnapshotHash, x.ModelHash, x.CreatedAt, x.Note, row.PublishedVersion == x.Version))
            .ToListAsync(ct);

        var history = await db.Set<THistory>()
            .Where(x => x.AssetId == row.Id)
            .OrderBy(x => x.Seq)
            .Select(x => new CalibrationPublicationEventData(x.Version, x.Action, x.At))
            .ToListAsync(ct);

        return new CalibrationAssetData(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, versions, history);
    }

    private static void ValidateJson(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) throw new ApiValidationException($"{name} must contain valid JSON up to {maxLength} characters.");
        try { using var _ = JsonDocument.Parse(value); }
        catch (JsonException ex) { throw new ApiValidationException($"{name} is invalid JSON: {ex.Message}", ex); }
    }

    private void ValidateId(string id)
    {
        var sanitized = string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrWhiteSpace(id) || sanitized != id) throw new ApiValidationException($"{Label} asset id may contain only letters, digits, '-' and '_'.");
    }
}

/// <summary>Hashing helpers shared by every calibration family (kept separate from the generic store so
/// the public static <c>ComputeModelHash</c> members of each family stay non-generic).</summary>
public static class CalibrationAssetHash
{
    public static string Model<T>(T model) => Hash(JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
