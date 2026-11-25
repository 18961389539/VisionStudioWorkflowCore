using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api;

public sealed record CameraIntrinsicVersionInfo(int Version, string SnapshotHash, string ModelHash, DateTimeOffset CreatedAt, string Note, bool Published);
public sealed record CameraIntrinsicPublicationEvent(int Version, string Action, DateTimeOffset At);
public sealed record CameraIntrinsicAssetDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    int? PublishedVersion,
    IReadOnlyList<CameraIntrinsicVersionInfo> Versions,
    IReadOnlyList<CameraIntrinsicPublicationEvent> PublicationHistory);
public sealed record CameraIntrinsicVersionSnapshot(
    string AssetId,
    int Version,
    string SnapshotHash,
    string ModelHash,
    DateTimeOffset CreatedAt,
    string Note,
    CameraCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson);
public sealed record CreateCameraIntrinsicAssetRequest(
    string Id,
    string Name,
    string? Description,
    CameraCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson,
    string? Note = null);
public sealed record SaveCameraIntrinsicVersionRequest(
    CameraCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson,
    string? Note = null);

/// <summary>SQLite-backed immutable camera-intrinsic model versions with source-view evidence.</summary>
public sealed class CameraIntrinsicAssetStore(IVisionStudioDbFactory dbFactory)
{
    public CameraIntrinsicAssetStore(SqliteMetadataDatabase db) : this(new VisionStudioDbFactory(db)) { }

    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<CameraIntrinsicAssetDescriptor>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.CameraIntrinsicAssets.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        var result = new List<CameraIntrinsicAssetDescriptor>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(db, row, ct));
        return result;
    }

    public async Task<CameraIntrinsicAssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.CameraIntrinsicAssets.FirstOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : await BuildDescriptorAsync(db, row, ct);
    }

    public async Task<CameraIntrinsicAssetDescriptor> CreateAsync(CreateCameraIntrinsicAssetRequest request, CancellationToken ct)
    {
        ValidateId(request.Id);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Camera calibration asset name is required.");
        var snapshot = CreateSnapshot(request.Id, 1, request.Model, request.SourceDocumentJson, request.SummaryJson, request.Note ?? "Initial intrinsic calibration", DateTimeOffset.UtcNow);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            db.CameraIntrinsicAssets.Add(new CameraIntrinsicAssetEntity
            {
                Id = request.Id,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                CreatedAt = snapshot.CreatedAt,
                UpdatedAt = snapshot.CreatedAt,
                LatestVersion = 1,
                PublishedVersion = null
            });
            db.CameraIntrinsicVersions.Add(ToVersionEntity(snapshot));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (await GetAsync(request.Id, ct))!;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"Camera calibration asset '{request.Id}' already exists.", sqliteEx);
        }
    }

    public async Task<CameraIntrinsicVersionSnapshot> AddVersionAsync(string id, SaveCameraIntrinsicVersionRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var row = await db.CameraIntrinsicAssets.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Camera calibration asset '{id}' does not exist.");
        var version = checked(row.LatestVersion + 1);
        var snapshot = CreateSnapshot(id, version, request.Model, request.SourceDocumentJson, request.SummaryJson, request.Note ?? string.Empty, DateTimeOffset.UtcNow);
        db.CameraIntrinsicVersions.Add(ToVersionEntity(snapshot));
        row.LatestVersion = version;
        row.UpdatedAt = snapshot.CreatedAt;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<CameraIntrinsicVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ReadVersionAsync(db, id, version, ct);
    }

    public async Task<CameraIntrinsicVersionSnapshot> GetPublishedAsync(string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.CameraIntrinsicAssets.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Camera calibration asset '{id}' does not exist.");
        if (row.PublishedVersion is null) throw new ApiConflictException($"Camera calibration asset '{id}' has no published version.");
        return await ReadVersionAsync(db, id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published camera calibration version {row.PublishedVersion} is missing.");
    }

    public async Task<CameraIntrinsicVersionSnapshot> RequireModelMatchAsync(string id, int version, string calibrationJson, CancellationToken ct)
    {
        var snapshot = await GetVersionAsync(id, version, ct)
            ?? throw new InvalidOperationException($"Camera undistort node references missing intrinsic calibration '{id}' v{version}.");
        CameraCalibrationModel model;
        try
        {
            model = JsonSerializer.Deserialize<CameraCalibrationModel>(calibrationJson, _json)
                ?? throw new JsonException("empty model");
            ValidateModel(model);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Camera undistort node contains invalid calibrationJson while bound to '{id}' v{version}.", ex);
        }
        catch (ApiValidationException ex)
        {
            throw new InvalidOperationException($"Camera undistort node contains an invalid camera model while bound to '{id}' v{version}: {ex.Message}", ex);
        }
        if (!string.Equals(ComputeModelHash(model), snapshot.ModelHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Camera undistort node calibrationJson does not match immutable intrinsic calibration '{id}' v{version}. Reapply that version from the Calibration Wizard before publishing.");
        return snapshot;
    }

    public async Task<CameraIntrinsicAssetDescriptor> PublishAsync(string id, int version, string action, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var row = await db.CameraIntrinsicAssets.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Camera calibration asset '{id}' does not exist.");
        if (await ReadVersionAsync(db, id, version, ct) is null)
            throw new ApiNotFoundException($"Camera calibration version {version} does not exist for '{id}'.");
        var now = DateTimeOffset.UtcNow;
        row.PublishedVersion = version;
        row.UpdatedAt = now;
        db.CameraIntrinsicPublicationHistory.Add(new CameraIntrinsicPublicationHistoryEntity
        {
            AssetId = id,
            Version = version,
            Action = action,
            At = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public static string ComputeModelHash(CameraCalibrationModel model)
    {
        var normalized = JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Hash(normalized);
    }

    private CameraIntrinsicVersionSnapshot CreateSnapshot(string id, int version, CameraCalibrationModel model, string sourceDocumentJson, string summaryJson, string note, DateTimeOffset createdAt)
    {
        ValidateModel(model);
        ValidateJson(sourceDocumentJson, "sourceDocumentJson", 40_000_000);
        ValidateJson(summaryJson, "summaryJson", 2_000_000);
        CalibrationIntrinsicsWizard.SessionDocument source;
        try
        {
            source = JsonSerializer.Deserialize<CalibrationIntrinsicsWizard.SessionDocument>(sourceDocumentJson, _json)
                ?? throw new JsonException("empty session document");
        }
        catch (JsonException ex)
        {
            throw new ApiValidationException($"sourceDocumentJson must contain a valid intrinsic wizard session document: {ex.Message}", ex);
        }
        if (!string.Equals(source.FormatVersion, CalibrationIntrinsicsWizard.SessionDocument.CurrentVersion, StringComparison.Ordinal))
            throw new ApiValidationException($"sourceDocumentJson must use '{CalibrationIntrinsicsWizard.SessionDocument.CurrentVersion}'.");
        if (source.Model is null || !string.Equals(ComputeModelHash(source.Model), ComputeModelHash(model), StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("The wizard source document model must match the camera calibration model being saved.");
        if (string.IsNullOrWhiteSpace(note)) note = string.Empty;
        if (note.Length > 1000) throw new ApiValidationException("Calibration version note must be at most 1000 characters.");
        var modelHash = ComputeModelHash(model);
        var snapshotHash = ComputeSnapshotHash(model, sourceDocumentJson, summaryJson);
        return new CameraIntrinsicVersionSnapshot(id, version, snapshotHash, modelHash, createdAt, note, model, sourceDocumentJson, summaryJson);
    }

    private async Task<CameraIntrinsicVersionSnapshot?> ReadVersionAsync(VisionStudioDbContext db, string id, int version, CancellationToken ct)
    {
        var entity = await db.CameraIntrinsicVersions.FirstOrDefaultAsync(x => x.AssetId == id && x.Version == version, ct);
        if (entity is null) return null;
        var model = JsonSerializer.Deserialize<CameraCalibrationModel>(entity.ModelJson, _json)
            ?? throw new InvalidDataException($"Stored camera model for '{id}' v{version} is invalid.");
        var actualModelHash = ComputeModelHash(model);
        var actualSnapshotHash = ComputeSnapshotHash(model, entity.SourceDocumentJson, entity.SummaryJson);
        if (!string.Equals(entity.ModelHash, actualModelHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entity.SnapshotHash, actualSnapshotHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Camera calibration '{id}' v{version} failed snapshot integrity validation.");
        try { ValidateModel(model); }
        catch (ApiValidationException ex) { throw new InvalidDataException($"Camera calibration '{id}' v{version} contains an invalid camera model.", ex); }
        return new CameraIntrinsicVersionSnapshot(id, version, entity.SnapshotHash, entity.ModelHash, entity.CreatedAt, entity.Note, model, entity.SourceDocumentJson, entity.SummaryJson);
    }

    private string ComputeSnapshotHash(CameraCalibrationModel model, string sourceDocumentJson, string summaryJson)
    {
        var canonical = JsonSerializer.Serialize(new { model, sourceDocumentJson, summaryJson }, _json);
        return Hash(canonical);
    }

    private CameraIntrinsicVersionEntity ToVersionEntity(CameraIntrinsicVersionSnapshot snapshot) => new()
    {
        AssetId = snapshot.AssetId,
        Version = snapshot.Version,
        SnapshotHash = snapshot.SnapshotHash,
        ModelHash = snapshot.ModelHash,
        CreatedAt = snapshot.CreatedAt,
        Note = snapshot.Note,
        ModelJson = JsonSerializer.Serialize(snapshot.Model, _json),
        SourceDocumentJson = snapshot.SourceDocumentJson,
        SummaryJson = snapshot.SummaryJson
    };

    private async Task<CameraIntrinsicAssetDescriptor> BuildDescriptorAsync(VisionStudioDbContext db, CameraIntrinsicAssetEntity row, CancellationToken ct)
    {
        var versions = await db.CameraIntrinsicVersions
            .Where(x => x.AssetId == row.Id)
            .OrderBy(x => x.Version)
            .Select(x => new CameraIntrinsicVersionInfo(x.Version, x.SnapshotHash, x.ModelHash, x.CreatedAt, x.Note, row.PublishedVersion == x.Version))
            .ToListAsync(ct);

        var history = await db.CameraIntrinsicPublicationHistory
            .Where(x => x.AssetId == row.Id)
            .OrderBy(x => x.Seq)
            .Select(x => new CameraIntrinsicPublicationEvent(x.Version, x.Action, x.At))
            .ToListAsync(ct);

        return new CameraIntrinsicAssetDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, versions, history);
    }

    private static void ValidateModel(CameraCalibrationModel model)
    {
        if (model is null || model.ImageWidth is < 16 or > 32768 || model.ImageHeight is < 16 or > 32768 ||
            model.CameraMatrix is not { Length: 3 } || model.CameraMatrix.Any(row => row is not { Length: 3 } || row.Any(value => !double.IsFinite(value))) ||
            model.DistCoeffs is null || model.DistCoeffs.Length is not (4 or 5 or 8 or 12 or 14) || model.DistCoeffs.Any(value => !double.IsFinite(value)) ||
            model.CameraMatrix[0][0] <= 0 || model.CameraMatrix[1][1] <= 0 ||
            Math.Abs(model.CameraMatrix[2][0]) > 1e-12 || Math.Abs(model.CameraMatrix[2][1]) > 1e-12 || Math.Abs(model.CameraMatrix[2][2] - 1) > 1e-9 ||
            !double.IsFinite(model.Rms) || model.Rms < 0 ||
            model.NativeCameraParameters?.Any(value => !double.IsFinite(value)) == true ||
            !HasValidLensModel(model))
            throw new ApiValidationException("Camera intrinsic model must contain valid dimensions, a finite normalized camera matrix, positive focal lengths, finite distortion coefficients, and a non-negative finite RMS.");
    }

    private static bool HasValidLensModel(CameraCalibrationModel model)
    {
        if (model.NativeCameraType is not null && (model.NativeCameraType is not ("area_scan_polynomial" or "area_scan_telecentric_polynomial") || model.NativeCameraParameters is not { Length: 12 })) return false;
        if (model.NativeCameraType is null && model.NativeCameraParameters is not null) return false;
        return model.LensModel switch
        {
            "opencv_fisheye" => model.DistCoeffs.Length == 4 && model.NativeCameraType is null && model.NativeCameraParameters is null,
            "area_scan_polynomial" => model.NativeCameraType == "area_scan_polynomial" && model.NativeCameraParameters is { Length: 12 },
            "area_scan_telecentric_polynomial" => model.NativeCameraType == "area_scan_telecentric_polynomial" && model.NativeCameraParameters is { Length: 12 },
            "brown_conrady" => model.NativeCameraType is null,
            null => true,
            _ => false
        };
    }

    private static void ValidateJson(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) throw new ApiValidationException($"{name} must contain valid JSON up to {maxLength} characters.");
        try { using var _ = JsonDocument.Parse(value); }
        catch (JsonException ex) { throw new ApiValidationException($"{name} is invalid JSON: {ex.Message}", ex); }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Sanitize(id) != id) throw new ApiValidationException("Camera calibration asset id may contain only letters, digits, '-' and '_'.");
    }
}
