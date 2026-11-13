using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record AssetVersionInfo(
    int Version,
    string Sha256,
    long SizeBytes,
    string MetadataJson,
    string Note,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record AssetDescriptor(
    string Id,
    string Kind,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    IReadOnlyList<AssetVersionInfo> Versions);

public sealed record CreateAssetRequest(
    string? Id,
    string Kind,
    string Name,
    string? Description,
    string MetadataJson,
    string Base64,
    string? Note = null,
    string? CreatedBy = null,
    int MaxBytes = AssetStore.DefaultMaxBytes);

public sealed record AppendAssetVersionRequest(
    string MetadataJson,
    string Base64,
    string? Note = null,
    string? CreatedBy = null,
    int MaxBytes = AssetStore.DefaultMaxBytes);

/// <summary>
/// 通用可寻址资产存储（P2）：字节按 SHA256 去重，版本不可变，类型相关字段放在 metadata_json。
///
/// 引入它是为了避免每种资产（形状模型 / 模板图 / ONNX 包）各养一套表与一套 GC；
/// 具体类型的字段语义由适配器负责（见 <see cref="ShapeModelAssetStore"/>）。
///
/// 表：assets(kind) → asset_versions(metadata_json) → asset_blobs(sha256)。
/// v25 迁移会把旧的 shape_model_* 数据原样搬进来；旧表暂留一个版本用于回退。
/// </summary>
public sealed class AssetStore(SqliteMetadataDatabase db)
{
    public const int DefaultMaxBytes = 16 * 1024 * 1024;
    private static readonly JsonDocumentOptions MetadataOptions = new() { AllowTrailingCommas = true };

    public async Task<IReadOnlyList<AssetDescriptor>> ListAsync(string? kind, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = string.IsNullOrWhiteSpace(kind)
                ? "SELECT id FROM assets WHERE deleted_at IS NULL ORDER BY updated_at DESC;"
                : "SELECT id FROM assets WHERE deleted_at IS NULL AND kind=$kind ORDER BY updated_at DESC;";
            if (!string.IsNullOrWhiteSpace(kind)) command.Parameters.AddWithValue("$kind", kind.Trim().ToLowerInvariant());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }
        var result = new List<AssetDescriptor>(ids.Count);
        foreach (var id in ids)
        {
            var descriptor = await BuildDescriptorAsync(connection, id, ct);
            if (descriptor is not null) result.Add(descriptor);
        }
        return result;
    }

    public async Task<AssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await BuildDescriptorAsync(connection, id, ct);
    }

    public async Task<AssetDescriptor> CreateAsync(CreateAssetRequest request, CancellationToken ct)
    {
        var kind = NormalizeKind(request.Kind);
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ApiValidationException("Asset name is required.");
        if (name.Length > 200) throw new ApiValidationException("Asset name must be at most 200 characters.");
        var metadata = NormalizeMetadata(request.MetadataJson);
        var bytes = DecodePayload(request.Base64, request.MaxBytes, out var sha256);
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"{kind}-{Guid.NewGuid():N}"[..14] : request.Id.Trim();
        ValidateId(id);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO assets(id,kind,name,description,created_at,updated_at,latest_version,deleted_at) VALUES($id,$kind,$name,$description,$created,$updated,1,NULL);",
                ct, ("$id", id), ("$kind", kind), ("$name", name), ("$description", (request.Description ?? string.Empty).Trim()),
                ("$created", Iso(now)), ("$updated", Iso(now)));
            await InsertBlobAsync(connection, transaction, sha256, bytes, now, ct);
            await InsertVersionAsync(connection, transaction, id, 1, sha256, bytes.Length, metadata,
                request.Note, request.CreatedBy, now, ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"Asset '{id}' already exists.", ex);
        }
        return (await GetAsync(id, ct))!;
    }

    public async Task<AssetVersionInfo> AppendVersionAsync(string id, AppendAssetVersionRequest request, CancellationToken ct)
    {
        var metadata = NormalizeMetadata(request.MetadataJson);
        var bytes = DecodePayload(request.Base64, request.MaxBytes, out var sha256);
        var now = DateTimeOffset.UtcNow;

        // (asset_id, version) 唯一约束 + MAX+1：并发追加时重读一次最新版再试，仍冲突则 409。
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var row = await LoadAssetAsync(connection, id, transaction, ct)
                ?? throw new ApiNotFoundException($"Asset '{id}' does not exist.");
            var version = checked(row.LatestVersion + 1);
            try
            {
                await InsertBlobAsync(connection, transaction, sha256, bytes, now, ct);
                await InsertVersionAsync(connection, transaction, id, version, sha256, bytes.Length, metadata,
                    request.Note, request.CreatedBy, now, ct);
                await ExecuteAsync(connection, transaction,
                    "UPDATE assets SET latest_version=$version,updated_at=$updated WHERE id=$id;",
                    ct, ("$version", version), ("$updated", Iso(now)), ("$id", id));
                await transaction.CommitAsync(ct);
                return new AssetVersionInfo(version, sha256, bytes.Length, metadata,
                    (request.Note ?? string.Empty).Trim(), now, (request.CreatedBy ?? string.Empty).Trim());
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && attempt == 0)
            {
                await transaction.RollbackAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new ApiConflictException($"Asset '{id}' version was appended concurrently; reload and retry.", ex);
            }
        }
    }

    public async Task<AssetVersionInfo?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await ReadVersionInfoAsync(connection, id, version, ct);
    }

    public async Task<byte[]?> ReadBytesAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT b.bytes
FROM asset_versions v
JOIN assets a ON a.id = v.asset_id AND a.deleted_at IS NULL
JOIN asset_blobs b ON b.sha256 = v.sha256
WHERE v.asset_id = $id AND v.version = $version;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (byte[])reader["bytes"] : null;
    }

    /// <summary>同步读取（供引擎的 <c>IModelAssetSource</c> 使用）；version&lt;=0 表示 latest。</summary>
    public byte[]? ReadBytesSync(string id, int version)
    {
        using var connection = db.OpenConnection();
        var effective = version;
        if (effective <= 0)
        {
            using var latest = connection.CreateCommand();
            latest.CommandText = "SELECT latest_version FROM assets WHERE id=$id AND deleted_at IS NULL;";
            latest.Parameters.AddWithValue("$id", id);
            var value = latest.ExecuteScalar();
            if (value is null or System.DBNull) return null;
            effective = Convert.ToInt32(value);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT b.bytes
FROM asset_versions v
JOIN assets a ON a.id = v.asset_id AND a.deleted_at IS NULL
JOIN asset_blobs b ON b.sha256 = v.sha256
WHERE v.asset_id = $id AND v.version = $version;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", effective);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (byte[])reader["bytes"] : null;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE assets SET deleted_at=$at,updated_at=$at WHERE id=$id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$at", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<AssetDescriptor?> UpdateAsync(string id, string? name, string? description, CancellationToken ct)
    {
        var trimmedName = name?.Trim();
        if (name is not null && string.IsNullOrEmpty(trimmedName))
            throw new ApiValidationException("Asset name cannot be blank.");
        if (trimmedName is { Length: > 200 })
            throw new ApiValidationException("Asset name must be at most 200 characters.");

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE assets
SET name = COALESCE($name, name),
    description = COALESCE($description, description),
    updated_at = $updated
WHERE id = $id AND deleted_at IS NULL;
""";
        command.Parameters.AddWithValue("$name", (object?)trimmedName ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)(description?.Trim()) ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(ct) == 0) return null;
        return await GetAsync(id, ct);
    }

    internal static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string NormalizeKind(string kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length is < 1 or > 32 || !normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ApiValidationException("Asset kind must be 1-32 characters of letters, digits, '-' or '_'.");
        return normalized;
    }

    private static string NormalizeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return "{}";
        try
        {
            using var document = JsonDocument.Parse(metadataJson, MetadataOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ApiValidationException("Asset metadata must be a JSON object.");
            return metadataJson.Trim();
        }
        catch (JsonException ex)
        {
            throw new ApiValidationException("Asset metadata must be a valid JSON object.", ex);
        }
    }

    private static byte[] DecodePayload(string? base64, int maxBytes, out string sha256)
    {
        if (string.IsNullOrWhiteSpace(base64)) throw new ApiValidationException("Asset payload is required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException ex) { throw new ApiValidationException("Asset payload must be a valid base64 string.", ex); }
        if (bytes.Length == 0) throw new ApiValidationException("Asset payload is empty.");
        if (bytes.Length > maxBytes)
            throw new ApiValidationException($"Asset payload is {bytes.Length} bytes, above the {maxBytes} byte limit.");
        sha256 = ComputeSha256(bytes);
        return bytes;
    }

    private static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 64 || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            throw new ApiValidationException("Asset id must be 1-64 characters of letters, digits, '-', '_' or '.'.");
    }

    private static async Task<AssetDescriptor?> BuildDescriptorAsync(SqliteConnection connection, string id, CancellationToken ct)
    {
        var row = await LoadAssetAsync(connection, id, null, ct);
        if (row is null) return null;
        var versions = new List<AssetVersionInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT version,sha256,size_bytes,metadata_json,note,created_at,created_by
FROM asset_versions WHERE asset_id=$id ORDER BY version DESC;
""";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(new AssetVersionInfo(
                reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6)));
        }
        return new AssetDescriptor(row.Id, row.Kind, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, versions);
    }

    private static async Task<AssetVersionInfo?> ReadVersionInfoAsync(SqliteConnection connection, string id, int version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT v.version,v.sha256,v.size_bytes,v.metadata_json,v.note,v.created_at,v.created_by
FROM asset_versions v
JOIN assets a ON a.id = v.asset_id AND a.deleted_at IS NULL
WHERE v.asset_id=$id AND v.version=$version;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new AssetVersionInfo(
            reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
            reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6));
    }

    private static async Task<AssetRow?> LoadAssetAsync(SqliteConnection connection, string id, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT id,kind,name,description,created_at,updated_at,latest_version
FROM assets WHERE id=$id AND deleted_at IS NULL;
""";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AssetRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)), reader.GetInt32(6))
            : null;
    }

    private static async Task InsertBlobAsync(SqliteConnection connection, SqliteTransaction transaction, string sha256, byte[] bytes, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO asset_blobs(sha256,size_bytes,bytes,created_at) VALUES($sha,$size,$bytes,$created);";
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$size", bytes.Length);
        command.Parameters.Add("$bytes", SqliteType.Blob).Value = bytes;
        command.Parameters.AddWithValue("$created", Iso(createdAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, SqliteTransaction transaction, string id, int version,
        string sha256, long sizeBytes, string metadataJson, string? note, string? createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO asset_versions(asset_id,version,sha256,size_bytes,metadata_json,note,created_at,created_by)
VALUES($id,$version,$sha,$size,$metadata,$note,$created,$by);
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$metadata", metadataJson);
        command.Parameters.AddWithValue("$note", (note ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$created", Iso(createdAt));
        command.Parameters.AddWithValue("$by", (createdBy ?? string.Empty).Trim());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private sealed record AssetRow(string Id, string Kind, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion);
}
