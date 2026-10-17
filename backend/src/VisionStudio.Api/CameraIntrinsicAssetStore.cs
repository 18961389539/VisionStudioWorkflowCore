using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;
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
public sealed class CameraIntrinsicAssetStore(SqliteMetadataDatabase db)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<CameraIntrinsicAssetDescriptor>> ListAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = new List<AssetRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,name,description,created_at,updated_at,latest_version,published_version FROM camera_intrinsic_assets ORDER BY updated_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadAsset(reader));
        }
        var result = new List<CameraIntrinsicAssetDescriptor>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(connection, row, ct));
        return result;
    }

    public async Task<CameraIntrinsicAssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var row = await LoadAssetAsync(connection, id, null, ct);
        return row is null ? null : await BuildDescriptorAsync(connection, row, ct);
    }

    public async Task<CameraIntrinsicAssetDescriptor> CreateAsync(CreateCameraIntrinsicAssetRequest request, CancellationToken ct)
    {
        ValidateId(request.Id);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Camera calibration asset name is required.");
        var snapshot = CreateSnapshot(request.Id, 1, request.Model, request.SourceDocumentJson, request.SummaryJson, request.Note ?? "Initial intrinsic calibration", DateTimeOffset.UtcNow);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO camera_intrinsic_assets(id,name,description,created_at,updated_at,latest_version,published_version) VALUES($id,$name,$description,$created,$updated,1,NULL);",
                ct, ("$id", request.Id), ("$name", request.Name.Trim()), ("$description", request.Description?.Trim() ?? string.Empty),
                ("$created", Iso(snapshot.CreatedAt)), ("$updated", Iso(snapshot.CreatedAt)));
            await InsertVersionAsync(connection, transaction, snapshot, ct);
            await transaction.CommitAsync(ct);
            return (await GetAsync(request.Id, ct))!;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"Camera calibration asset '{request.Id}' already exists.", ex);
        }
    }

    public async Task<CameraIntrinsicVersionSnapshot> AddVersionAsync(string id, SaveCameraIntrinsicVersionRequest request, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var row = await LoadAssetAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"Camera calibration asset '{id}' does not exist.");
        var version = checked(row.LatestVersion + 1);
        var snapshot = CreateSnapshot(id, version, request.Model, request.SourceDocumentJson, request.SummaryJson, request.Note ?? string.Empty, DateTimeOffset.UtcNow);
        await InsertVersionAsync(connection, transaction, snapshot, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE camera_intrinsic_assets SET latest_version=$version,updated_at=$updated WHERE id=$id;",
            ct, ("$version", version), ("$updated", Iso(snapshot.CreatedAt)), ("$id", id));
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<CameraIntrinsicVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await ReadVersionAsync(connection, null, id, version, ct);
    }

    public async Task<CameraIntrinsicVersionSnapshot> GetPublishedAsync(string id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var row = await LoadAssetAsync(connection, id, null, ct)
            ?? throw new ApiNotFoundException($"Camera calibration asset '{id}' does not exist.");
        if (row.PublishedVersion is null) throw new ApiConflictException($"Camera calibration asset '{id}' has no published version.");
        return await ReadVersionAsync(connection, null, id, row.PublishedVersion.Value, ct)
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
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var row = await LoadAssetAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"Camera calibration asset '{id}' does not exist.");
        if (await ReadVersionAsync(connection, transaction, id, version, ct) is null)
            throw new ApiNotFoundException($"Camera calibration version {version} does not exist for '{id}'.");
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, transaction,
            "UPDATE camera_intrinsic_assets SET published_version=$version,updated_at=$updated WHERE id=$id;",
            ct, ("$version", version), ("$updated", Iso(now)), ("$id", id));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO camera_intrinsic_publication_history(asset_id,version,action,at) VALUES($id,$version,$action,$at);",
            ct, ("$id", id), ("$version", version), ("$action", action), ("$at", Iso(now)));
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

    private async Task<CameraIntrinsicVersionSnapshot?> ReadVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, int version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT snapshot_hash,model_hash,created_at,note,model_json,source_document_json,summary_json FROM camera_intrinsic_versions WHERE asset_id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var snapshotHash = reader.GetString(0);
        var modelHash = reader.GetString(1);
        var model = JsonSerializer.Deserialize<CameraCalibrationModel>(reader.GetString(4), _json)
            ?? throw new InvalidDataException($"Stored camera model for '{id}' v{version} is invalid.");
        var sourceDocumentJson = reader.GetString(5);
        var summaryJson = reader.GetString(6);
        var actualModelHash = ComputeModelHash(model);
        var actualSnapshotHash = ComputeSnapshotHash(model, sourceDocumentJson, summaryJson);
        if (!string.Equals(modelHash, actualModelHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshotHash, actualSnapshotHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Camera calibration '{id}' v{version} failed snapshot integrity validation.");
        try { ValidateModel(model); }
        catch (ApiValidationException ex) { throw new InvalidDataException($"Camera calibration '{id}' v{version} contains an invalid camera model.", ex); }
        return new CameraIntrinsicVersionSnapshot(id, version, snapshotHash, modelHash, ParseTime(reader.GetString(2)), reader.GetString(3), model, sourceDocumentJson, summaryJson);
    }

    private string ComputeSnapshotHash(CameraCalibrationModel model, string sourceDocumentJson, string summaryJson)
    {
        var canonical = JsonSerializer.Serialize(new { model, sourceDocumentJson, summaryJson }, _json);
        return Hash(canonical);
    }

    private async Task InsertVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CameraIntrinsicVersionSnapshot snapshot, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT INTO camera_intrinsic_versions(asset_id,version,snapshot_hash,model_hash,created_at,note,model_json,source_document_json,summary_json) VALUES($id,$version,$hash,$modelHash,$created,$note,$model,$source,$summary);",
            ct, ("$id", snapshot.AssetId), ("$version", snapshot.Version), ("$hash", snapshot.SnapshotHash), ("$modelHash", snapshot.ModelHash),
            ("$created", Iso(snapshot.CreatedAt)), ("$note", snapshot.Note), ("$model", JsonSerializer.Serialize(snapshot.Model, _json)),
            ("$source", snapshot.SourceDocumentJson), ("$summary", snapshot.SummaryJson));
    }

    private async Task<CameraIntrinsicAssetDescriptor> BuildDescriptorAsync(SqliteConnection connection, AssetRow row, CancellationToken ct)
    {
        var versions = new List<CameraIntrinsicVersionInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version,snapshot_hash,model_hash,created_at,note FROM camera_intrinsic_versions WHERE asset_id=$id ORDER BY version;";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) versions.Add(new CameraIntrinsicVersionInfo(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), reader.GetString(4), row.PublishedVersion == reader.GetInt32(0)));
        }
        var history = new List<CameraIntrinsicPublicationEvent>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version,action,at FROM camera_intrinsic_publication_history WHERE asset_id=$id ORDER BY seq;";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) history.Add(new CameraIntrinsicPublicationEvent(reader.GetInt32(0), reader.GetString(1), ParseTime(reader.GetString(2))));
        }
        return new CameraIntrinsicAssetDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, versions, history);
    }

    private static async Task<AssetRow?> LoadAssetAsync(SqliteConnection connection, string id, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,description,created_at,updated_at,latest_version,published_version FROM camera_intrinsic_assets WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAsset(reader) : null;
    }

    private static AssetRow ReadAsset(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), ParseTime(reader.GetString(4)), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6));

    private static void ValidateModel(CameraCalibrationModel model)
    {
        if (model is null || model.ImageWidth is < 16 or > 32768 || model.ImageHeight is < 16 or > 32768 ||
            model.CameraMatrix is not { Length: 3 } || model.CameraMatrix.Any(row => row is not { Length: 3 } || row.Any(value => !double.IsFinite(value))) ||
            model.DistCoeffs is null || model.DistCoeffs.Length is not (4 or 5 or 8 or 12 or 14) || model.DistCoeffs.Any(value => !double.IsFinite(value)) ||
            model.CameraMatrix[0][0] <= 0 || model.CameraMatrix[1][1] <= 0 ||
            Math.Abs(model.CameraMatrix[2][0]) > 1e-12 || Math.Abs(model.CameraMatrix[2][1]) > 1e-12 || Math.Abs(model.CameraMatrix[2][2] - 1) > 1e-9 ||
            !double.IsFinite(model.Rms) || model.Rms < 0 ||
            model.NativeCameraParameters?.Any(value => !double.IsFinite(value)) == true)
            throw new ApiValidationException("Camera intrinsic model must contain valid dimensions, a finite normalized camera matrix, positive focal lengths, finite distortion coefficients, and a non-negative finite RMS.");
    }

    private static void ValidateJson(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) throw new ApiValidationException($"{name} must contain valid JSON up to {maxLength} characters.");
        try { using var _ = JsonDocument.Parse(value); }
        catch (JsonException ex) { throw new ApiValidationException($"{name} is invalid JSON: {ex.Message}", ex); }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Sanitize(id) != id) throw new ApiValidationException("Camera calibration asset id may contain only letters, digits, '-' and '_'.");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record AssetRow(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int? PublishedVersion);
}
