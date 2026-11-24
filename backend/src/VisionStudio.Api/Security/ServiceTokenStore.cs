using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;

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
public sealed class ServiceTokenStore(SqliteMetadataDatabase database)
{
    public const string TokenPrefix = "vst_";
    private static readonly TimeSpan UsageWriteInterval = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<ServiceTokenDto>> ListAsync(CancellationToken ct = default)
    {
        var tokens = new List<ServiceTokenDto>();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id,name,prefix,scopes,created_at,created_by,expires_at,last_used_at,revoked_at
FROM security_service_tokens ORDER BY created_at DESC;
""";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) tokens.Add(ReadDto(reader));
        return tokens;
    }

    public async Task<ServiceTokenCreated> CreateAsync(CreateServiceTokenRequest request, string? createdBy, CancellationToken ct = default)
    {
        var name = ValidateName(request.Name);
        var scopes = NormalizeScopes(request.Scopes);
        var expiresAt = ValidateExpiry(request.ExpiresInDays);
        var id = Guid.NewGuid().ToString("N");
        var token = GenerateToken();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO security_service_tokens(id,name,token_hash,prefix,scopes,created_at,created_by,expires_at,last_used_at,revoked_at)
VALUES($id,$name,$hash,$prefix,$scopes,$created,$by,$expires,NULL,NULL);
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$hash", SecurityHashing.HashToken(token));
        command.Parameters.AddWithValue("$prefix", PrefixOf(token));
        command.Parameters.AddWithValue("$scopes", string.Join(',', scopes));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$by", string.IsNullOrWhiteSpace(createdBy) ? DBNull.Value : createdBy);
        command.Parameters.AddWithValue("$expires", expiresAt is null ? DBNull.Value : expiresAt.Value.ToString("O"));
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"Service token name '{name}' already exists.", ex);
        }

        return new ServiceTokenCreated(token, new ServiceTokenDto(id, name, PrefixOf(token), scopes, now, createdBy, expiresAt, null, null));
    }

    /// <summary>轮换只换密钥（新 prefix），id/名称/作用域/过期时间保持不变；已吊销令牌不能轮换。</summary>
    public async Task<ServiceTokenCreated> RotateAsync(string id, CancellationToken ct = default)
    {
        var token = GenerateToken();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindAsync(connection, id, ct, transaction)
            ?? throw new ApiNotFoundException($"Service token '{id}' was not found.");
        if (existing.RevokedAt is not null)
            throw new ApiConflictException("Revoked service tokens cannot be rotated; create a new token instead.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE security_service_tokens SET token_hash=$hash,prefix=$prefix,last_used_at=NULL WHERE id=$id;";
        command.Parameters.AddWithValue("$hash", SecurityHashing.HashToken(token));
        command.Parameters.AddWithValue("$prefix", PrefixOf(token));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);

        return new ServiceTokenCreated(token, existing with { Prefix = PrefixOf(token), LastUsedAt = null });
    }

    /// <summary>吊销幂等：已吊销的令牌再吊销返回现状，不报错。</summary>
    public async Task<ServiceTokenDto> RevokeAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindAsync(connection, id, ct, transaction)
            ?? throw new ApiNotFoundException($"Service token '{id}' was not found.");
        if (existing.RevokedAt is not null)
        {
            await transaction.CommitAsync(ct);
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE security_service_tokens SET revoked_at=$at WHERE id=$id;";
        command.Parameters.AddWithValue("$at", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return existing with { RevokedAt = now };
    }

    /// <summary>按明文令牌解析主体；吊销/过期返回 null（认证层据此 401）。</summary>
    public async Task<ServiceTokenPrincipal?> ResolveAsync(string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,scopes,expires_at,revoked_at,last_used_at FROM security_service_tokens WHERE token_hash=$hash;";
        command.Parameters.AddWithValue("$hash", SecurityHashing.HashToken(token));

        string id;
        string name;
        string[] scopes;
        DateTimeOffset? expiresAt;
        DateTimeOffset? revokedAt;
        DateTimeOffset? lastUsedAt;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            id = reader.GetString(0);
            name = reader.GetString(1);
            scopes = SplitScopes(reader.GetString(2));
            expiresAt = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3));
            revokedAt = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4));
            lastUsedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5));
        }
        if (revokedAt is not null || (expiresAt is { } expiry && expiry <= now)) return null;

        if (lastUsedAt is null || lastUsedAt < now - UsageWriteInterval)
        {
            await using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE security_service_tokens SET last_used_at=$now WHERE id=$id AND (last_used_at IS NULL OR last_used_at<$stale);";
            touch.Parameters.AddWithValue("$now", now.ToString("O"));
            touch.Parameters.AddWithValue("$stale", (now - UsageWriteInterval).ToString("O"));
            touch.Parameters.AddWithValue("$id", id);
            await touch.ExecuteNonQueryAsync(ct);
        }
        return new ServiceTokenPrincipal(id, name, scopes, expiresAt);
    }

    private static async Task<ServiceTokenDto?> FindAsync(SqliteConnection connection, string id, CancellationToken ct, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,prefix,scopes,created_at,created_by,expires_at,last_used_at,revoked_at FROM security_service_tokens WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDto(reader) : null;
    }

    private static ServiceTokenDto ReadDto(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        SplitScopes(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)));

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