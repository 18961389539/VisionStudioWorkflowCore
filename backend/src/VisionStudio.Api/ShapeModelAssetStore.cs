using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record ShapeModelVersionInfo(
    int Version,
    string Sha256,
    long SizeBytes,
    string TrainingConfig,
    string SourceImageSha256,
    string Note,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record ShapeModelAssetDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    IReadOnlyList<ShapeModelVersionInfo> Versions);

public sealed record ShapeModelAssetVersion(
    string AssetId,
    int Version,
    string Sha256,
    long SizeBytes,
    string TrainingConfig,
    string SourceImageSha256,
    string Note,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    byte[] Bytes);

public sealed record CreateShapeModelAssetRequest(
    string? Id,
    string Name,
    string? Description,
    string ModelBase64,
    string TrainingConfig,
    string? SourceImageSha256 = null,
    string? Note = null,
    string? CreatedBy = null);

public sealed record AppendShapeModelVersionRequest(
    string ModelBase64,
    string TrainingConfig,
    string? SourceImageSha256 = null,
    string? Note = null,
    string? CreatedBy = null);

/// <summary>
/// v24 形状模型资产：版本不可变、字节内容寻址（同一份模型被多个资产或版本引用时只存一份）、软删除。
///
/// P0 只到"最新版本"，没有发布态与配方锁定（P1）。因此运行期解析固定取
/// <c>modelAssetVersion=0 → latest_version</c>，不会出现"最新版被追加后悄悄换模型"的问题——
/// 换版本必须显式写版本号，最新版只影响显式要 0 的节点。
/// </summary>
public sealed class ShapeModelAssetStore(SqliteMetadataDatabase db)
{
    /// <summary>与引擎侧 match.shape 的 16 MB 模型上限保持一致；超限明确拒绝，不截断。</summary>
    public const int MaxModelBytes = 16 * 1024 * 1024;

    public async Task<IReadOnlyList<ShapeModelAssetDescriptor>> ListAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM shape_model_assets WHERE deleted_at IS NULL ORDER BY updated_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }
        var result = new List<ShapeModelAssetDescriptor>(ids.Count);
        foreach (var id in ids)
        {
            var descriptor = await BuildDescriptorAsync(connection, id, ct);
            if (descriptor is not null) result.Add(descriptor);
        }
        return result;
    }

    public async Task<ShapeModelAssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await BuildDescriptorAsync(connection, id, ct);
    }

    public async Task<ShapeModelAssetDescriptor> CreateAsync(CreateShapeModelAssetRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ApiValidationException("Shape model asset name is required.");
        if (name.Length > 200) throw new ApiValidationException("Shape model asset name must be at most 200 characters.");
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"shape-{Guid.NewGuid():N}"[..14] : request.Id.Trim();
        ValidateId(id);
        var bytes = DecodeModel(request.ModelBase64, out var sha256);
        var trainingConfig = (request.TrainingConfig ?? string.Empty).Trim();
        if (trainingConfig.Length == 0)
            throw new ApiValidationException("trainingConfig is required: it pins which training parameters produced this model.");
        var now = DateTimeOffset.UtcNow;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO shape_model_assets(id,name,description,created_at,updated_at,latest_version,deleted_at) VALUES($id,$name,$description,$created,$updated,1,NULL);",
                ct, ("$id", id), ("$name", name), ("$description", (request.Description ?? string.Empty).Trim()),
                ("$created", Iso(now)), ("$updated", Iso(now)));
            await InsertBlobAsync(connection, transaction, sha256, bytes, now, ct);
            await InsertVersionAsync(connection, transaction, id, 1, sha256, bytes.Length, trainingConfig,
                request.SourceImageSha256, request.Note, request.CreatedBy, now, ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"Shape model asset '{id}' already exists.", ex);
        }
        return (await GetAsync(id, ct))!;
    }

    public async Task<ShapeModelVersionInfo> AppendVersionAsync(string id, AppendShapeModelVersionRequest request, CancellationToken ct)
    {
        var bytes = DecodeModel(request.ModelBase64, out var sha256);
        var trainingConfig = (request.TrainingConfig ?? string.Empty).Trim();
        if (trainingConfig.Length == 0)
            throw new ApiValidationException("trainingConfig is required: it pins which training parameters produced this model.");
        var now = DateTimeOffset.UtcNow;

        // (asset_id, version) 唯一约束 + MAX+1：并发追加时重读一次最新版再试，仍冲突则 409。
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var row = await LoadAssetAsync(connection, id, transaction, ct)
                ?? throw new ApiNotFoundException($"Shape model asset '{id}' does not exist.");
            var version = checked(row.LatestVersion + 1);
            try
            {
                await InsertBlobAsync(connection, transaction, sha256, bytes, now, ct);
                await InsertVersionAsync(connection, transaction, id, version, sha256, bytes.Length, trainingConfig,
                    request.SourceImageSha256, request.Note, request.CreatedBy, now, ct);
                await ExecuteAsync(connection, transaction,
                    "UPDATE shape_model_assets SET latest_version=$version,updated_at=$updated WHERE id=$id;",
                    ct, ("$version", version), ("$updated", Iso(now)), ("$id", id));
                await transaction.CommitAsync(ct);
                return new ShapeModelVersionInfo(version, sha256, bytes.Length, trainingConfig,
                    request.SourceImageSha256 ?? string.Empty, (request.Note ?? string.Empty).Trim(), now,
                    (request.CreatedBy ?? string.Empty).Trim());
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && attempt == 0)
            {
                // 版本号被并发追加抢占：整个事务回滚后重读 latest_version 再试一次。
                await transaction.RollbackAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new ApiConflictException($"Shape model asset '{id}' version was appended concurrently; reload and retry.", ex);
            }
        }
    }

    public async Task<ShapeModelVersionInfo?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await ReadVersionInfoAsync(connection, id, version, ct);
    }

    /// <summary>读取指定版本的模型字节；资产/版本不存在或已删除时返回 null（调用方决定报 404 还是运行期失败）。</summary>
    public async Task<byte[]?> ReadBytesAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT b.bytes
FROM shape_model_versions v
JOIN shape_model_assets a ON a.id = v.asset_id AND a.deleted_at IS NULL
JOIN shape_model_blobs b ON b.sha256 = v.sha256
WHERE v.asset_id = $id AND v.version = $version;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (byte[])reader["bytes"] : null;
    }

    /// <summary>软删除：保留历史版本供已发布配方回指；返回 false 表示资产不存在或已删除。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE shape_model_assets SET deleted_at=$at,updated_at=$at WHERE id=$id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$at", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    internal static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] DecodeModel(string? modelBase64, out string sha256)
    {
        if (string.IsNullOrWhiteSpace(modelBase64))
            throw new ApiValidationException("modelBase64 is required.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(modelBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new ApiValidationException("modelBase64 must be a valid base64 string.", ex);
        }
        if (bytes.Length == 0) throw new ApiValidationException("modelBase64 decodes to an empty model.");
        if (bytes.Length > MaxModelBytes)
            throw new ApiValidationException($"Shape model is {bytes.Length} bytes, above the {MaxModelBytes} byte (16 MB) limit.");
        sha256 = ComputeSha256(bytes);
        return bytes;
    }

    private static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 64 || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            throw new ApiValidationException("Shape model asset id must be 1-64 characters of letters, digits, '-', '_' or '.'.");
    }

    private static async Task<ShapeModelAssetDescriptor?> BuildDescriptorAsync(SqliteConnection connection, string id, CancellationToken ct)
    {
        var row = await LoadAssetAsync(connection, id, null, ct);
        if (row is null) return null;
        var versions = new List<ShapeModelVersionInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT version,sha256,size_bytes,training_config,source_image_sha256,note,created_at,created_by
FROM shape_model_versions WHERE asset_id=$id ORDER BY version DESC;
""";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(new ShapeModelVersionInfo(
                reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(7)));
        }
        return new ShapeModelAssetDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, versions);
    }

    private static async Task<ShapeModelVersionInfo?> ReadVersionInfoAsync(SqliteConnection connection, string id, int version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT v.version,v.sha256,v.size_bytes,v.training_config,v.source_image_sha256,v.note,v.created_at,v.created_by
FROM shape_model_versions v
JOIN shape_model_assets a ON a.id = v.asset_id AND a.deleted_at IS NULL
WHERE v.asset_id=$id AND v.version=$version;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ShapeModelVersionInfo(
            reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(7));
    }

    private static async Task<AssetRow?> LoadAssetAsync(SqliteConnection connection, string id, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT id,name,description,created_at,updated_at,latest_version
FROM shape_model_assets WHERE id=$id AND deleted_at IS NULL;
""";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AssetRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt32(5))
            : null;
    }

    private static async Task InsertBlobAsync(SqliteConnection connection, SqliteTransaction transaction, string sha256, byte[] bytes, DateTimeOffset createdAt, CancellationToken ct)
    {
        // 内容寻址：同一份字节只存一次；INSERT OR IGNORE 让重复写入变成空操作。
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT OR IGNORE INTO shape_model_blobs(sha256,size_bytes,bytes,created_at) VALUES($sha,$size,$bytes,$created);
""";
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$size", bytes.Length);
        command.Parameters.Add("$bytes", SqliteType.Blob).Value = bytes;
        command.Parameters.AddWithValue("$created", Iso(createdAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, SqliteTransaction transaction, string id, int version,
        string sha256, long sizeBytes, string trainingConfig, string? sourceImageSha256, string? note, string? createdBy,
        DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO shape_model_versions(asset_id,version,sha256,size_bytes,training_config,source_image_sha256,note,created_at,created_by)
VALUES($id,$version,$sha,$size,$config,$source,$note,$created,$by);
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$config", trainingConfig);
        command.Parameters.AddWithValue("$source", (sourceImageSha256 ?? string.Empty).Trim());
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

    private sealed record AssetRow(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion);
}
