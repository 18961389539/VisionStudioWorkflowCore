using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Security;

internal static class SecurityHashing
{
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>认证处理器解析出的服务令牌主体：只有令牌 id、名称与已授予的作用域。</summary>
public sealed record ServiceTokenPrincipal(string Id, string Name, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt);

/// <summary>
/// 服务账号令牌（P3-6）。MES/上位机用独立令牌而不是共用操作员账号：
/// - 明文形如 vst_&lt;43 字符 base64url&gt;，库里只存 SHA-256 十六进制哈希，明文仅在创建/轮换时返回一次；
/// - 按作用域授权，认证角色固定为 Service，走不到任何人类角色策略；
/// - 可轮换（同 id/名称/作用域，换新密钥）与吊销；last_used_at 每令牌每分钟最多写一次。
/// </summary>
public sealed class ServiceTokenStore(IVisionStudioDbFactory dbFactory)
{
    public const string TokenPrefix = "vst_";
    private static readonly TimeSpan UsageWriteInterval = TimeSpan.FromMinutes(1);

    public ServiceTokenStore(SqliteMetadataDatabase database) : this(new VisionStudioDbFactory(database)) { }

    public async Task<IReadOnlyList<ServiceTokenDto>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entities = await db.SecurityServiceTokens
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToDto).ToList();
    }

    public async Task<ServiceTokenCreated> CreateAsync(CreateServiceTokenRequest request, string? createdBy, CancellationToken ct = default)
    {
        var name = ValidateName(request.Name);
        var scopes = NormalizeScopes(request.Scopes);
        var expiresAt = ValidateExpiry(request.ExpiresInDays);
        var id = Guid.NewGuid().ToString("N");
        var token = GenerateToken();
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = new SecurityServiceTokenEntity
        {
            Id = id,
            Name = name,
            TokenHash = SecurityHashing.HashToken(token),
            Prefix = PrefixOf(token),
            Scopes = string.Join(',', scopes),
            CreatedAt = now,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy,
            ExpiresAt = expiresAt
        };
        db.SecurityServiceTokens.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"Service token name '{name}' already exists.", sqliteEx);
        }

        return new ServiceTokenCreated(token, ToDto(entity));
    }

    /// <summary>轮换只换密钥（新 prefix），id/名称/作用域/过期时间保持不变；已吊销令牌不能轮换。</summary>
    public async Task<ServiceTokenCreated> RotateAsync(string id, CancellationToken ct = default)
    {
        var token = GenerateToken();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var existing = await db.SecurityServiceTokens.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Service token '{id}' was not found.");
        if (existing.RevokedAt is not null)
            throw new ApiConflictException("Revoked service tokens cannot be rotated; create a new token instead.");

        existing.TokenHash = SecurityHashing.HashToken(token);
        existing.Prefix = PrefixOf(token);
        existing.LastUsedAt = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ServiceTokenCreated(token, ToDto(existing));
    }

    /// <summary>吊销幂等：已吊销的令牌再吊销返回现状，不报错。</summary>
    public async Task<ServiceTokenDto> RevokeAsync(string id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var existing = await db.SecurityServiceTokens.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Service token '{id}' was not found.");
        if (existing.RevokedAt is not null)
        {
            await transaction.CommitAsync(ct);
            return ToDto(existing);
        }

        existing.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToDto(existing);
    }

    /// <summary>按明文令牌解析主体；吊销/过期返回 null（认证层据此 401）。</summary>
    public async Task<ServiceTokenPrincipal?> ResolveAsync(string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = SecurityHashing.HashToken(token);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.SecurityServiceTokens.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (entity is null || entity.RevokedAt is not null || (entity.ExpiresAt is { } expiry && expiry <= now))
            return null;

        var stale = now - UsageWriteInterval;
        if (entity.LastUsedAt is null || entity.LastUsedAt < stale)
        {
            await db.SecurityServiceTokens
                .Where(x => x.Id == entity.Id && (x.LastUsedAt == null || x.LastUsedAt < stale))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, now), ct);
        }
        return new ServiceTokenPrincipal(entity.Id, entity.Name, SplitScopes(entity.Scopes), entity.ExpiresAt);
    }

    private static ServiceTokenDto ToDto(SecurityServiceTokenEntity e) => new(
        e.Id,
        e.Name,
        e.Prefix,
        SplitScopes(e.Scopes),
        e.CreatedAt,
        e.CreatedBy,
        e.ExpiresAt,
        e.LastUsedAt,
        e.RevokedAt);

    private static string GenerateToken()
        => TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string PrefixOf(string token) => token[..12];

    private static string[] SplitScopes(string scopes)
        => scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length < 3 || trimmed.Length > 64)
            throw new ApiValidationException("Service token name must be 3 to 64 characters.");
        if (trimmed.Any(char.IsControl))
            throw new ApiValidationException("Service token name must not contain control characters.");
        return trimmed;
    }

    private static string[] NormalizeScopes(string[]? scopes)
    {
        if (scopes is null || scopes.Length == 0)
            throw new ApiValidationException($"At least one scope is required. Allowed: {string.Join(", ", ServiceScopes.All)}.");
        var normalized = new List<string>();
        foreach (var raw in scopes)
        {
            var match = ServiceScopes.All.FirstOrDefault(x => string.Equals(x, raw?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiValidationException($"Unknown scope '{raw}'. Allowed: {string.Join(", ", ServiceScopes.All)}.");
            if (!normalized.Contains(match, StringComparer.Ordinal)) normalized.Add(match);
        }
        return [.. normalized];
    }

    private static DateTimeOffset? ValidateExpiry(int? expiresInDays)
    {
        if (expiresInDays is null) return null;
        if (expiresInDays is < 1 or > 3650)
            throw new ApiValidationException("expiresInDays must be between 1 and 3650 when provided.");
        return DateTimeOffset.UtcNow.AddDays(expiresInDays.Value);
    }
}