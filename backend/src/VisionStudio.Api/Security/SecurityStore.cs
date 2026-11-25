using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Security;

public sealed class SecurityStore(IVisionStudioDbFactory dbFactory, IOptions<SecurityOptions> options)
{
    private readonly SecurityOptions _options = options.Value;

    public SecurityStore(SqliteMetadataDatabase database, IOptions<SecurityOptions> options)
        : this(new VisionStudioDbFactory(database), options)
    {
    }

    public async Task<bool> HasUsersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SecurityUsers.AnyAsync(ct);
    }

    public async Task<SecurityUserDto> BootstrapAdministratorAsync(BootstrapAdminRequest request, CancellationToken ct = default)
    {
        ValidateUsername(request.Username);
        ValidateDisplayName(request.DisplayName);
        ValidatePassword(request.Password);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        if (await db.SecurityUsers.AnyAsync(ct))
            throw new ApiConflictException("Security bootstrap is already complete.");
        var user = await InsertUserAsync(db, request.Username, request.DisplayName, SecurityRoles.Administrator, request.Password, ct);
        await transaction.CommitAsync(ct);
        return user;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password) || request.Username.Length > 64 || request.Password.Length > 256)
            throw new ApiUnauthorizedException("Invalid username or password.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalized = NormalizeUsername(request.Username);
        var row = await db.SecurityUsers.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, ct)
            ?? throw new ApiUnauthorizedException("Invalid username or password.");

        var now = DateTimeOffset.UtcNow;
        if (row.LockedUntil is { } lockedUntil && lockedUntil > now)
            throw new ApiTooManyRequestsException(
                $"Account is locked until {lockedUntil:yyyy-MM-dd HH:mm:ss} UTC after repeated failed sign-ins. " +
                "Ask an administrator to unlock it, or wait for the window to pass.");
        // 锁定窗口已过：先清零再验证，否则过期后的第一次输错会立刻重新锁死。
        if (row.LockedUntil is not null)
        {
            await ClearFailedLoginsAsync(db, row.Id, ct);
            row.FailedAttempts = 0;
            row.LockedUntil = null;
        }
        if (!row.Enabled || !VerifyPassword(request.Password, row.PasswordSalt, row.PasswordHash, row.PasswordIterations))
        {
            if (row.Enabled) await RecordFailedLoginAsync(db, row.Id, row.FailedAttempts + 1, now, ct);
            throw new ApiUnauthorizedException("Invalid username or password.");
        }
        if (row.FailedAttempts > 0) await ClearFailedLoginsAsync(db, row.Id, ct);

        return await IssueSessionAsync(db, ToUserDto(row), ct);
    }

    /// <summary>管理员解锁：清空失败计数与锁定窗口（2026-11-12）。账号不存在时报 404。</summary>
    public async Task<SecurityUserDto> UnlockAsync(string id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var entity = await db.SecurityUsers.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Security user '{id}' was not found.");
        entity.FailedAttempts = 0;
        entity.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToUserDto(entity);
    }

    /// <summary>
    /// 免密码以内置管理员身份建立会话：账号不存在时自动创建（随机口令，不对外公开）；
    /// 若同名账号已存在但不是启用的管理员则拒绝，避免权限提升。
    /// </summary>
    public async Task<LoginResponse> AutoLoginAdministratorAsync(string username, string displayName, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim();
        ValidateUsername(name);
        var normalized = NormalizeUsername(name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.SecurityUsers.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, ct);
        if (user is null)
        {
            await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
            try
            {
                user = await db.SecurityUsers.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, ct);
                if (user is null)
                {
                    await InsertUserAsync(db, name, string.IsNullOrWhiteSpace(displayName) ? name : displayName, SecurityRoles.Administrator, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), ct);
                    await transaction.CommitAsync(ct);
                }
                else
                {
                    await transaction.RollbackAsync(ct);
                }
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                await transaction.RollbackAsync(ct);
            }
            user = await db.SecurityUsers.FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, ct);
        }
        if (user is null || !user.Enabled || !string.Equals(user.Role, SecurityRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException($"Auto-login user '{name}' is missing, disabled or not an enabled Administrator.");
        return await IssueSessionAsync(db, ToUserDto(user), ct);
    }

    private async Task<LoginResponse> IssueSessionAsync(VisionStudioDbContext db, SecurityUserDto user, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(Math.Clamp(_options.SessionHours, 1, 168));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = HashToken(token);
        var oldCutoff = now.AddDays(-7);

        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        await db.SecuritySessions
            .Where(x => x.ExpiresAt <= now || (x.RevokedAt != null && x.RevokedAt <= oldCutoff))
            .ExecuteDeleteAsync(ct);
        db.SecuritySessions.Add(new SecuritySessionEntity
        {
            TokenHash = tokenHash,
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = expires,
            LastSeenAt = now,
            RevokedAt = null
        });
        await db.SaveChangesAsync(ct);
        await db.SecurityUsers
            .Where(x => x.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastLoginAt, now), ct);
        await transaction.CommitAsync(ct);
        return new LoginResponse(token, expires, user with { LastLoginAt = now });
    }

    public async Task<SecurityUserDto?> ResolveSessionAsync(string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashToken(token);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await (
            from s in db.SecuritySessions
            join u in db.SecurityUsers on s.UserId equals u.Id
            where s.TokenHash == tokenHash && s.RevokedAt == null && s.ExpiresAt > now && u.Enabled
            select u)
            .FirstOrDefaultAsync(ct);
        return user is null ? null : ToUserDto(user);
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashToken(token);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SecuritySessions
            .Where(x => x.TokenHash == tokenHash && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    public async Task<IReadOnlyList<SecurityUserDto>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.SecurityUsers.OrderBy(x => x.Username).ToListAsync(ct);
        return users.Select(ToUserDto).ToList();
    }

    public async Task<SecurityUserDto> CreateUserAsync(CreateSecurityUserRequest request, CancellationToken ct = default)
    {
        ValidateUsername(request.Username); ValidateDisplayName(request.DisplayName); ValidatePassword(request.Password); ValidateRole(request.Role);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            var user = await InsertUserAsync(db, request.Username, request.DisplayName, NormalizeRole(request.Role), request.Password, ct);
            await transaction.CommitAsync(ct);
            return user;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"User '{request.Username}' already exists.", sqliteEx);
        }
    }

    public async Task<SecurityUserDto> UpdateUserAsync(string id, UpdateSecurityUserRequest request, CancellationToken ct = default)
    {
        ValidateRole(request.Role); ValidateDisplayName(request.DisplayName);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // F09：降级检查与 UPDATE 必须在同一写事务（BEGIN IMMEDIATE，开始即取写锁）内完成。
        // 否则两个并发请求都能读到"还有另一名启用管理员"，然后依次降级，把最后两名管理员同时移除。
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var current = await db.SecurityUsers.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Security user '{id}' was not found.");
        var demotesAdministrator = string.Equals(current.Role, SecurityRoles.Administrator, StringComparison.OrdinalIgnoreCase) &&
            (!request.Enabled || !string.Equals(request.Role, SecurityRoles.Administrator, StringComparison.OrdinalIgnoreCase));
        if (demotesAdministrator && !await db.SecurityUsers.AnyAsync(x => x.Id != id && x.Enabled && x.Role == SecurityRoles.Administrator, ct))
            throw new ApiConflictException("At least one enabled Administrator must remain.");

        current.DisplayName = request.DisplayName?.Trim() ?? string.Empty;
        current.Role = NormalizeRole(request.Role);
        current.Enabled = request.Enabled;
        current.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (!request.Enabled) await RevokeUserSessionsAsync(db, id, ct);
        await transaction.CommitAsync(ct);
        return ToUserDto(current);
    }

    public async Task ResetPasswordAsync(string id, string password, CancellationToken ct = default)
    {
        ValidatePassword(password);
        var (salt, hash, iterations) = HashPassword(password);
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var affected = await db.SecurityUsers
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PasswordSalt, salt)
                .SetProperty(x => x.PasswordHash, hash)
                .SetProperty(x => x.PasswordIterations, iterations)
                .SetProperty(x => x.PasswordChangedAt, now)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.FailedAttempts, 0)
                .SetProperty(x => x.LockedUntil, (DateTimeOffset?)null), ct);
        if (affected == 0) throw new ApiNotFoundException($"Security user '{id}' was not found.");
        await RevokeUserSessionsAsync(db, id, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ChangePasswordAsync(string id, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var credential = await db.SecurityUsers.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Security user '{id}' was not found.");
        if (!VerifyPassword(currentPassword, credential.PasswordSalt, credential.PasswordHash, credential.PasswordIterations))
            throw new ApiUnauthorizedException("Current password is incorrect.");
        var (salt, hash, iterations) = HashPassword(newPassword);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        await db.SecurityUsers
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PasswordSalt, salt)
                .SetProperty(x => x.PasswordHash, hash)
                .SetProperty(x => x.PasswordIterations, iterations)
                .SetProperty(x => x.PasswordChangedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);
        await RevokeUserSessionsAsync(db, id, ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<SecurityUserDto> InsertUserAsync(VisionStudioDbContext db, string username, string displayName, string role, string password, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var (salt, hash, iterations) = HashPassword(password);
        var entity = new SecurityUserEntity
        {
            Id = id,
            Username = username.Trim(),
            NormalizedUsername = NormalizeUsername(username),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            Role = role,
            Enabled = true,
            PasswordSalt = salt,
            PasswordHash = hash,
            PasswordIterations = iterations,
            CreatedAt = now,
            UpdatedAt = now,
            LastLoginAt = null,
            PasswordChangedAt = now
        };
        db.SecurityUsers.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToUserDto(entity);
    }

    private static async Task RevokeUserSessionsAsync(VisionStudioDbContext db, string id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.SecuritySessions
            .Where(x => x.UserId == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    private static SecurityUserDto ToUserDto(SecurityUserEntity e)
        => new(e.Id, e.Username, e.DisplayName, e.Role, e.Enabled, e.CreatedAt, e.UpdatedAt, e.LastLoginAt, e.PasswordChangedAt);

    /// <summary>
    /// 记录一次失败登录。达到上限时写入锁定截止时间；计数与锁定写在同一行，
    /// 避免"计数加了、锁定没写"的半状态（并发失败请求最多多记一次，不会绕过上限）。
    /// </summary>
    private async Task RecordFailedLoginAsync(VisionStudioDbContext db, string id, int attempts, DateTimeOffset now, CancellationToken ct)
    {
        var max = Math.Max(0, _options.MaxFailedLoginAttempts);
        if (max == 0) return;
        var lockedUntil = attempts >= max ? now.AddMinutes(Math.Clamp(_options.LockoutMinutes, 1, 1440)) : (DateTimeOffset?)null;
        await db.SecurityUsers
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.FailedAttempts, attempts)
                .SetProperty(x => x.LockedUntil, lockedUntil), ct);
    }

    private static async Task ClearFailedLoginsAsync(VisionStudioDbContext db, string id, CancellationToken ct)
    {
        await db.SecurityUsers
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.FailedAttempts, 0)
                .SetProperty(x => x.LockedUntil, (DateTimeOffset?)null), ct);
    }

    private (byte[] Salt, byte[] Hash, int Iterations) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16); var iterations = Math.Max(100_000, _options.PasswordPbkdf2Iterations);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32); return (salt, hash, iterations);
    }
    private static bool VerifyPassword(string password, byte[] salt, byte[] expected, int iterations) => CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length), expected);
    private static string HashToken(string token) => SecurityHashing.HashToken(token);
    private static string NormalizeUsername(string username) => username.Trim().ToUpperInvariant();
    private static string NormalizeRole(string role) => SecurityRoles.All.First(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase));
    private static void ValidateRole(string role) { if (!SecurityRoles.IsValid(role)) throw new ApiValidationException($"Role must be one of: {string.Join(", ", SecurityRoles.All)}."); }
    private static void ValidateUsername(string username) { var length = username?.Trim().Length ?? 0; if (length < 3 || length > 64) throw new ApiValidationException("Username must be 3 to 64 characters."); }
    private static void ValidatePassword(string password) { if (string.IsNullOrWhiteSpace(password) || password.Length < 10 || password.Length > 256) throw new ApiValidationException("Password must be 10 to 256 characters."); }
    private static void ValidateDisplayName(string? displayName) { if (displayName is { Length: > 128 }) throw new ApiValidationException("Display name must not exceed 128 characters."); }
}
