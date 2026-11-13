using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;

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
/// <remarks>
/// Every family uses the same three-table layout (<c>{prefix}_assets</c>, <c>{prefix}_versions</c>,
/// <c>{prefix}_publication_history</c>), so the SQL, hashing, integrity checks and publication state
/// machine live here once. A family only supplies its model type, table prefix, model validation and
/// the session-document cross-check. Before this base existed the planar and SE(3) stores were two
/// near-identical 330-line copies that had already started to drift in their error messages.
/// </remarks>
public abstract class CalibrationAssetStoreBase<TModel> where TModel : class
{
    private readonly SqliteMetadataDatabase _db;
    private readonly string _tableAssets;
    private readonly string _tableVersions;
    private readonly string _tableHistory;

    protected CalibrationAssetStoreBase(SqliteMetadataDatabase db, string tablePrefix)
    {
        _db = db;
        _tableAssets = tablePrefix + "_assets";
        _tableVersions = tablePrefix + "_versions";
        _tableHistory = tablePrefix + "_publication_history";
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
        await using var connection = await _db.OpenConnectionAsync(ct);
        var rows = new List<AssetRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT id,name,description,created_at,updated_at,latest_version,published_version FROM {_tableAssets} ORDER BY updated_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadAsset(reader));
        }
        var result = new List<CalibrationAssetData>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(connection, row, ct));
        return result;
    }

    private protected async Task<CalibrationAssetData?> GetCoreAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadAssetAsync(connection, id, null, ct);
        return row is null ? null : await BuildDescriptorAsync(connection, row, ct);
    }

    private protected async Task<CalibrationVersionData> CreateCoreAsync(
        string id, string name, string? description, TModel model, string sourceDocumentJson, string summaryJson, string? note, CancellationToken ct)
    {
        ValidateId(id);
        if (string.IsNullOrWhiteSpace(name)) throw new ApiValidationException($"{Label} asset name is required.");
        var snapshot = CreateSnapshot(id, 1, model, sourceDocumentJson, summaryJson, note ?? DefaultCreateNote, DateTimeOffset.UtcNow);
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await ExecuteAsync(connection, transaction,
                $"INSERT INTO {_tableAssets}(id,name,description,created_at,updated_at,latest_version,published_version) VALUES($id,$name,$description,$created,$updated,1,NULL);",
                ct, ("$id", id), ("$name", name.Trim()), ("$description", description?.Trim() ?? string.Empty),
                ("$created", Iso(snapshot.CreatedAt)), ("$updated", Iso(snapshot.CreatedAt)));
            await InsertVersionAsync(connection, transaction, snapshot, ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"{Label} asset '{id}' already exists.", ex);
        }
        return snapshot;
    }

    private protected async Task<CalibrationVersionData> AddVersionCoreAsync(
        string id, TModel model, string sourceDocumentJson, string summaryJson, string? note, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var row = await LoadAssetAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"{Label} asset '{id}' does not exist.");
        var version = checked(row.LatestVersion + 1);
        var snapshot = CreateSnapshot(id, version, model, sourceDocumentJson, summaryJson, note ?? string.Empty, DateTimeOffset.UtcNow);
        await InsertVersionAsync(connection, transaction, snapshot, ct);
        await ExecuteAsync(connection, transaction,
            $"UPDATE {_tableAssets} SET latest_version=$version,updated_at=$updated WHERE id=$id;",
            ct, ("$version", version), ("$updated", Iso(snapshot.CreatedAt)), ("$id", id));
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    private protected async Task<CalibrationVersionData?> GetVersionCoreAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        return await ReadVersionAsync(connection, null, id, version, ct);
    }

    private protected async Task<CalibrationVersionData> GetPublishedCoreAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadAssetAsync(connection, id, null, ct)
            ?? throw new ApiNotFoundException($"{Label} asset '{id}' does not exist.");
        if (row.PublishedVersion is null) throw new ApiConflictException($"{Label} asset '{id}' has no published version.");
        return await ReadVersionAsync(connection, null, id, row.PublishedVersion.Value, ct)
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
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var row = await LoadAssetAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"{Label} asset '{id}' does not exist.");
        if (await ReadVersionAsync(connection, transaction, id, version, ct) is null)
            throw new ApiNotFoundException($"{Label} version {version} does not exist for '{id}'.");
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, transaction,
            $"UPDATE {_tableAssets} SET published_version=$version,updated_at=$updated WHERE id=$id;",
            ct, ("$version", version), ("$updated", Iso(now)), ("$id", id));
        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {_tableHistory}(asset_id,version,action,at) VALUES($id,$version,$action,$at);",
            ct, ("$id", id), ("$version", version), ("$action", action), ("$at", Iso(now)));
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

    private async Task<CalibrationVersionData?> ReadVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, int version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT snapshot_hash,model_hash,created_at,note,model_json,source_document_json,summary_json FROM {_tableVersions} WHERE asset_id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var snapshotHash = reader.GetString(0);
        var modelHash = reader.GetString(1);
        var model = JsonSerializer.Deserialize<TModel>(reader.GetString(4), Json)
            ?? throw new InvalidDataException($"Stored {Label} model for '{id}' v{version} is invalid.");
        var sourceDocumentJson = reader.GetString(5);
        var summaryJson = reader.GetString(6);
        if (!string.Equals(modelHash, ModelHash(model), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshotHash, ComputeSnapshotHash(model, sourceDocumentJson, summaryJson), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Label} '{id}' v{version} failed snapshot integrity validation.");
        try { ValidateModel(model); }
        catch (ApiValidationException ex) { throw new InvalidDataException($"{Label} '{id}' v{version} contains an invalid hand-eye model.", ex); }
        return new CalibrationVersionData(id, version, snapshotHash, modelHash, ParseTime(reader.GetString(2)), reader.GetString(3), reader.GetString(4), sourceDocumentJson, summaryJson);
    }

    private string ComputeSnapshotHash(TModel model, string sourceDocumentJson, string summaryJson)
    {
        var canonical = JsonSerializer.Serialize(new { model, sourceDocumentJson, summaryJson }, Json);
        return CalibrationAssetHash.Hash(canonical);
    }

    private async Task InsertVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CalibrationVersionData snapshot, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {_tableVersions}(asset_id,version,snapshot_hash,model_hash,created_at,note,model_json,source_document_json,summary_json) VALUES($id,$version,$hash,$modelHash,$created,$note,$model,$source,$summary);",
            ct, ("$id", snapshot.AssetId), ("$version", snapshot.Version), ("$hash", snapshot.SnapshotHash), ("$modelHash", snapshot.ModelHash),
            ("$created", Iso(snapshot.CreatedAt)), ("$note", snapshot.Note), ("$model", snapshot.ModelJson),
            ("$source", snapshot.SourceDocumentJson), ("$summary", snapshot.SummaryJson));
    }

    private async Task<CalibrationAssetData> BuildDescriptorAsync(SqliteConnection connection, AssetRow row, CancellationToken ct)
    {
        var versions = new List<CalibrationVersionInfoData>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT version,snapshot_hash,model_hash,created_at,note FROM {_tableVersions} WHERE asset_id=$id ORDER BY version;";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                versions.Add(new CalibrationVersionInfoData(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), reader.GetString(4), row.PublishedVersion == reader.GetInt32(0)));
        }
        var history = new List<CalibrationPublicationEventData>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT version,action,at FROM {_tableHistory} WHERE asset_id=$id ORDER BY seq;";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) history.Add(new CalibrationPublicationEventData(reader.GetInt32(0), reader.GetString(1), ParseTime(reader.GetString(2))));
        }
        return new CalibrationAssetData(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, versions, history);
    }

    private async Task<AssetRow?> LoadAssetAsync(SqliteConnection connection, string id, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id,name,description,created_at,updated_at,latest_version,published_version FROM {_tableAssets} WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAsset(reader) : null;
    }

    private static AssetRow ReadAsset(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), ParseTime(reader.GetString(4)), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6));

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

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

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

/// <summary>Hashing helpers shared by every calibration family (kept separate from the generic store so
/// the public static <c>ComputeModelHash</c> members of each family stay non-generic).</summary>
public static class CalibrationAssetHash
{
    public static string Model<T>(T model) => Hash(JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
