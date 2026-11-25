using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

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
public sealed class AssetStore(IVisionStudioDbFactory dbFactory)
{
    public AssetStore(SqliteMetadataDatabase db) : this(new VisionStudioDbFactory(db)) { }

    public const int DefaultMaxBytes = 16 * 1024 * 1024;
    private static readonly JsonDocumentOptions MetadataOptions = new() { AllowTrailingCommas = true };

    public async Task<IReadOnlyList<AssetDescriptor>> ListAsync(string? kind, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<AssetEntity> query = db.Assets.Where(x => x.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var normalizedKind = kind.Trim().ToLowerInvariant();
            query = query.Where(x => x.Kind == normalizedKind);
        }
        var rows = await query.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var ids = rows.Select(x => x.Id).ToList();
        var versions = await db.AssetVersions
            .Where(x => ids.Contains(x.AssetId))
            .OrderByDescending(x => x.Version)
            .ToListAsync(ct);
        var byAsset = versions.GroupBy(x => x.AssetId).ToDictionary(g => g.Key, g => g.Select(ToVersionInfo).ToList());
        return rows.Select(r => new AssetDescriptor(
            r.Id, r.Kind, r.Name, r.Description, r.CreatedAt, r.UpdatedAt, r.LatestVersion,
            byAsset.TryGetValue(r.Id, out var v) ? v : [])).ToList();
    }

    public async Task<AssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Assets.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (row is null) return null;
        var versions = await db.AssetVersions
            .Where(x => x.AssetId == id)
            .OrderByDescending(x => x.Version)
            .Select(x => new AssetVersionInfo(x.Version, x.Sha256, x.SizeBytes, x.MetadataJson, x.Note, x.CreatedAt, x.CreatedBy))
            .ToListAsync(ct);
        return new AssetDescriptor(row.Id, row.Kind, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, versions);
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

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            db.Assets.Add(new AssetEntity
            {
                Id = id,
                Kind = kind,
                Name = name,
                Description = (request.Description ?? string.Empty).Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                LatestVersion = 1,
                DeletedAt = null
            });
            await EnsureBlobAsync(db, sha256, bytes, now, ct);
            db.AssetVersions.Add(new AssetVersionEntity
            {
                AssetId = id,
                Version = 1,
                Sha256 = sha256,
                SizeBytes = bytes.Length,
                MetadataJson = metadata,
                Note = (request.Note ?? string.Empty).Trim(),
                CreatedAt = now,
                CreatedBy = (request.CreatedBy ?? string.Empty).Trim()
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"Asset '{id}' already exists.", sqliteEx);
        }
        return (await GetAsync(id, ct))!;
    }

    public async Task<AssetVersionInfo> AppendVersionAsync(string id, AppendAssetVersionRequest request, CancellationToken ct)
    {
        var metadata = NormalizeMetadata(request.MetadataJson);
        var bytes = DecodePayload(request.Base64, request.MaxBytes, out var sha256);
        var now = DateTimeOffset.UtcNow;

        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
            var row = await db.Assets.AsTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct)
                ?? throw new ApiNotFoundException($"Asset '{id}' does not exist.");
            var version = checked(row.LatestVersion + 1);
            try
            {
                await EnsureBlobAsync(db, sha256, bytes, now, ct);
                db.AssetVersions.Add(new AssetVersionEntity
                {
                    AssetId = id,
                    Version = version,
                    Sha256 = sha256,
                    SizeBytes = bytes.Length,
                    MetadataJson = metadata,
                    Note = (request.Note ?? string.Empty).Trim(),
                    CreatedAt = now,
                    CreatedBy = (request.CreatedBy ?? string.Empty).Trim()
                });
                row.LatestVersion = version;
                row.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new AssetVersionInfo(version, sha256, bytes.Length, metadata,
                    (request.Note ?? string.Empty).Trim(), now, (request.CreatedBy ?? string.Empty).Trim());
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } && attempt == 0)
            {
                await transaction.RollbackAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
            {
                throw new ApiConflictException($"Asset '{id}' version was appended concurrently; reload and retry.", sqliteEx);
            }
        }
    }

    public async Task<AssetVersionInfo?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await (
            from v in db.AssetVersions
            join a in db.Assets on v.AssetId equals a.Id
            where a.DeletedAt == null && v.AssetId == id && v.Version == version
            select v)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : ToVersionInfo(entity);
    }

    public async Task<byte[]?> ReadBytesAsync(string id, int version, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await (
            from v in db.AssetVersions
            join a in db.Assets on v.AssetId equals a.Id
            join b in db.AssetBlobs on v.Sha256 equals b.Sha256
            where a.DeletedAt == null && v.AssetId == id && v.Version == version
            select b.Bytes)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>同步读取（供引擎的 <c>IModelAssetSource</c> 使用）；version&lt;=0 表示 latest。</summary>
    public byte[]? ReadBytesSync(string id, int version, string? kind = null)
    {
        using var db = dbFactory.CreateDbContext();
        var effective = version;
        if (effective <= 0)
        {
            var latest = db.Assets
                .Where(x => x.Id == id && x.DeletedAt == null)
                .Select(x => (int?)x.LatestVersion)
                .FirstOrDefault();
            if (latest is null) return null;
            effective = latest.Value;
        }

        return (
            from v in db.AssetVersions
            join a in db.Assets on v.AssetId equals a.Id
            join b in db.AssetBlobs on v.Sha256 equals b.Sha256
            where a.DeletedAt == null && v.AssetId == id && v.Version == effective && (kind == null || a.Kind == kind)
            select b.Bytes)
            .FirstOrDefault();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Assets
            .Where(x => x.Id == id && x.DeletedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);
        return affected > 0;
    }

    public async Task<AssetDescriptor?> UpdateAsync(string id, string? name, string? description, CancellationToken ct)
    {
        var trimmedName = name?.Trim();
        if (name is not null && string.IsNullOrEmpty(trimmedName))
            throw new ApiValidationException("Asset name cannot be blank.");
        if (trimmedName is { Length: > 200 })
            throw new ApiValidationException("Asset name must be at most 200 characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var entity = await db.Assets.AsTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (entity is null) return null;
        if (trimmedName is not null) entity.Name = trimmedName;
        if (description is not null) entity.Description = description.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    private static async Task EnsureBlobAsync(VisionStudioDbContext db, string sha256, byte[] bytes, DateTimeOffset createdAt, CancellationToken ct)
    {
        if (!await db.AssetBlobs.AnyAsync(x => x.Sha256 == sha256, ct))
        {
            db.AssetBlobs.Add(new AssetBlobEntity
            {
                Sha256 = sha256,
                SizeBytes = bytes.Length,
                Bytes = bytes,
                CreatedAt = createdAt
            });
        }
    }

    private static AssetVersionInfo ToVersionInfo(AssetVersionEntity v)
        => new(v.Version, v.Sha256, v.SizeBytes, v.MetadataJson, v.Note, v.CreatedAt, v.CreatedBy);

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
}
