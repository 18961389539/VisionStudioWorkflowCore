using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Security;

public sealed class SecurityStore(SqliteMetadataDatabase database, IOptions<SecurityOptions> options)
{
    private readonly SecurityOptions _options = options.Value;

    public async Task<bool> HasUsersAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM security_users LIMIT 1);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0;
    }

    public async Task<SecurityUserDto> BootstrapAdministratorAsync(BootstrapAdminRequest request, CancellationToken ct = default)
    {
        ValidateUsername(request.Username);
        ValidateDisplayName(request.DisplayName);
        ValidatePassword(request.Password);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM security_users;";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != 0)
                throw new ApiConflictException("Security bootstrap is already complete.");
        }
        var user = await InsertUserAsync(connection, transaction, request.Username, request.DisplayName, SecurityRoles.Administrator, request.Password, ct);
        await transaction.CommitAsync(ct);
        return user;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password) || request.Username.Length > 64 || request.Password.Length > 256)
            throw new ApiUnauthorizedException("Invalid username or password.");
        await using var connection = await database.OpenConnectionAsync(ct);
        var row = await FindCredentialAsync(connection, request.Username, ct)
            ?? throw new ApiUnauthorizedException("Invalid username or password.");
        if (!row.Enabled || !VerifyPassword(request.Password, row.Salt, row.Hash, row.Iterations))
            throw new ApiUnauthorizedException("Invalid username or password.");

        return await IssueSessionAsync(connection, row.User, ct);
    }

    /// <summary>
    /// 免密码以内置管理员身份建立会话：账号不存在时自动创建（随机口令，不对外公开）；
    /// 若同名账号已存在但不是启用的管理员则拒绝，避免权限提升。
    /// </summary>
    public async Task<LoginResponse> AutoLoginAdministratorAsync(string username, string displayName, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim();
        ValidateUsername(name);
        await using var connection = await database.OpenConnectionAsync(ct);
        var user = await FindUserByUsernameAsync(connection, name, ct);
        if (user is null)
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                await InsertUserAsync(connection, transaction, name, string.IsNullOrWhiteSpace(displayName) ? name : displayName, SecurityRoles.Administrator, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), ct);
                await transaction.CommitAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                await transaction.RollbackAsync(ct); // 并发创建冲突：回滚后按已存在账号处理
            }
            user = await FindUserByUsernameAsync(connection, name, ct);
        }
        if (user is null || !user.Enabled || !string.Equals(user.Role, SecurityRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException($"Auto-login user '{name}' is missing, disabled or not an enabled Administrator.");
        return await IssueSessionAsync(connection, user, ct);
    }

    private async Task<LoginResponse> IssueSessionAsync(SqliteConnection connection, SecurityUserDto user, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(Math.Clamp(_options.SessionHours, 1, 168));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = HashToken(token);

        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM security_sessions WHERE expires_at <= $now OR (revoked_at IS NOT NULL AND revoked_at <= $old);";
            prune.Parameters.AddWithValue("$now", now.ToString("O"));
            prune.Parameters.AddWithValue("$old", now.AddDays(-7).ToString("O"));
            await prune.ExecuteNonQueryAsync(ct);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
INSERT INTO security_sessions(token_hash, user_id, created_at, expires_at, last_seen_at, revoked_at)
VALUES($token, $user, $created, $expires, $seen, NULL);
""";
            insert.Parameters.AddWithValue("$token", tokenHash);
            insert.Parameters.AddWithValue("$user", user.Id);
            insert.Parameters.AddWithValue("$created", now.ToString("O"));
            insert.Parameters.AddWithValue("$expires", expires.ToString("O"));
            insert.Parameters.AddWithValue("$seen", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE security_users SET last_login_at=$at WHERE id=$id;";
            update.Parameters.AddWithValue("$at", now.ToString("O"));
            update.Parameters.AddWithValue("$id", user.Id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new LoginResponse(token, expires, user with { LastLoginAt = now });
    }

    public async Task<SecurityUserDto?> ResolveSessionAsync(string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT u.id,u.username,u.display_name,u.role,u.enabled,u.created_at,u.updated_at,u.last_login_at,u.password_changed_at
FROM security_sessions s JOIN security_users u ON u.id=s.user_id
WHERE s.token_hash=$token AND s.revoked_at IS NULL AND s.expires_at>$now AND u.enabled=1;
""";
        command.Parameters.AddWithValue("$token", HashToken(token));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadUser(reader);
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE security_sessions SET revoked_at=$at WHERE token_hash=$token AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$token", HashToken(token));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SecurityUserDto>> ListUsersAsync(CancellationToken ct = default)
    {
        var users = new List<SecurityUserDto>();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,username,display_name,role,enabled,created_at,updated_at,last_login_at,password_changed_at FROM security_users ORDER BY username;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) users.Add(ReadUser(reader));
        return users;
    }

    public async Task<SecurityUserDto> CreateUserAsync(CreateSecurityUserRequest request, CancellationToken ct = default)
    {
        ValidateUsername(request.Username); ValidateDisplayName(request.DisplayName); ValidatePassword(request.Password); ValidateRole(request.Role);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var user = await InsertUserAsync(connection, transaction, request.Username, request.DisplayName, NormalizeRole(request.Role), request.Password, ct);
            await transaction.CommitAsync(ct);
            return user;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"User '{request.Username}' already exists.", ex);
        }
    }

    public async Task<SecurityUserDto> UpdateUserAsync(string id, UpdateSecurityUserRequest request, CancellationToken ct = default)
    {
        ValidateRole(request.Role); ValidateDisplayName(request.DisplayName);
        await using var connection = await database.OpenConnectionAsync(ct);
        var current = await GetUserAsync(connection, id, ct) ?? throw new ApiNotFoundException($"Security user '{id}' was not found.");
        var demotesAdministrator = string.Equals(current.Role, SecurityRoles.Administrator, StringComparison.OrdinalIgnoreCase) &&
            (!request.Enabled || !string.Equals(request.Role, SecurityRoles.Administrator, StringComparison.OrdinalIgnoreCase));
        if (demotesAdministrator && !await HasOtherEnabledAdministratorAsync(connection, id, ct))
            throw new ApiConflictException("At least one enabled Administrator must remain.");
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE security_users SET display_name=$display,role=$role,enabled=$enabled,updated_at=$updated WHERE id=$id;";
        command.Parameters.AddWithValue("$display", request.DisplayName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$role", NormalizeRole(request.Role));
        command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiNotFoundException($"Security user '{id}' was not found.");
        if (!request.Enabled) await RevokeUserSessionsAsync(connection, id, ct);
        return (await GetUserAsync(connection, id, ct))!;
    }

    public async Task ResetPasswordAsync(string id, string password, CancellationToken ct = default)
    {
        ValidatePassword(password);
        var (salt, hash, iterations) = HashPassword(password);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE security_users SET password_salt=$salt,password_hash=$hash,password_iterations=$iter,password_changed_at=$at,updated_at=$at WHERE id=$id;";
        command.Parameters.AddWithValue("$salt", salt); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$iter", iterations);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiNotFoundException($"Security user '{id}' was not found.");
        await RevokeUserSessionsAsync(connection, id, ct, transaction);
        await transaction.CommitAsync(ct);
    }

    public async Task ChangePasswordAsync(string id, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        await using var connection = await database.OpenConnectionAsync(ct);
        var credential = await FindCredentialByIdAsync(connection, id, ct) ?? throw new ApiNotFoundException($"Security user '{id}' was not found.");
        if (!VerifyPassword(currentPassword, credential.Salt, credential.Hash, credential.Iterations)) throw new ApiUnauthorizedException("Current password is incorrect.");
        var (salt, hash, iterations) = HashPassword(newPassword);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE security_users SET password_salt=$salt,password_hash=$hash,password_iterations=$iter,password_changed_at=$at,updated_at=$at WHERE id=$id;";
        command.Parameters.AddWithValue("$salt", salt); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$iter", iterations);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
        await RevokeUserSessionsAsync(connection, id, ct, transaction);
        await transaction.CommitAsync(ct);
    }

    private async Task<SecurityUserDto> InsertUserAsync(SqliteConnection connection, SqliteTransaction transaction, string username, string displayName, string role, string password, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var (salt, hash, iterations) = HashPassword(password);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO security_users(id,username,normalized_username,display_name,role,enabled,password_salt,password_hash,password_iterations,created_at,updated_at,last_login_at,password_changed_at)
VALUES($id,$username,$normalized,$display,$role,1,$salt,$hash,$iter,$at,$at,NULL,$at);
""";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$username", username.Trim()); command.Parameters.AddWithValue("$normalized", NormalizeUsername(username));
        command.Parameters.AddWithValue("$display", string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim()); command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$salt", salt); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$iter", iterations); command.Parameters.AddWithValue("$at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        return new SecurityUserDto(id, username.Trim(), string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(), role, true, now, now, null, now);
    }

    private async Task RevokeUserSessionsAsync(SqliteConnection connection, string id, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE security_sessions SET revoked_at=$at WHERE user_id=$id AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> HasOtherEnabledAdministratorAsync(SqliteConnection connection, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM security_users WHERE id<>$id AND enabled=1 AND role=$role LIMIT 1);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$role", SecurityRoles.Administrator);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private async Task<SecurityUserDto?> GetUserAsync(SqliteConnection connection, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,username,display_name,role,enabled,created_at,updated_at,last_login_at,password_changed_at FROM security_users WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    private async Task<SecurityUserDto?> FindUserByUsernameAsync(SqliteConnection connection, string username, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,username,display_name,role,enabled,created_at,updated_at,last_login_at,password_changed_at FROM security_users WHERE normalized_username=$username;";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    private async Task<CredentialRow?> FindCredentialAsync(SqliteConnection connection, string username, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,username,display_name,role,enabled,created_at,updated_at,last_login_at,password_changed_at,password_salt,password_hash,password_iterations FROM security_users WHERE normalized_username=$username;";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadCredential(reader) : null;
    }

    private async Task<CredentialRow?> FindCredentialByIdAsync(SqliteConnection connection, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,username,display_name,role,enabled,created_at,updated_at,last_login_at,password_changed_at,password_salt,password_hash,password_iterations FROM security_users WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadCredential(reader) : null;
    }

    private CredentialRow ReadCredential(SqliteDataReader r) => new(ReadUser(r), (byte[])r[9], (byte[])r[10], r.GetInt32(11), r.GetBoolean(4));
    private static SecurityUserDto ReadUser(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), DateTimeOffset.Parse(r.GetString(5)), DateTimeOffset.Parse(r.GetString(6)), r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)), DateTimeOffset.Parse(r.GetString(8)));

    private (byte[] Salt, byte[] Hash, int Iterations) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16); var iterations = Math.Max(100_000, _options.PasswordPbkdf2Iterations);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32); return (salt, hash, iterations);
    }
    private static bool VerifyPassword(string password, byte[] salt, byte[] expected, int iterations) => CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length), expected);
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string NormalizeUsername(string username) => username.Trim().ToUpperInvariant();
    private static string NormalizeRole(string role) => SecurityRoles.All.First(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase));
    private static void ValidateRole(string role) { if (!SecurityRoles.IsValid(role)) throw new ApiValidationException($"Role must be one of: {string.Join(", ", SecurityRoles.All)}."); }
    private static void ValidateUsername(string username) { var length = username?.Trim().Length ?? 0; if (length < 3 || length > 64) throw new ApiValidationException("Username must be 3 to 64 characters."); }
    private static void ValidatePassword(string password) { if (string.IsNullOrWhiteSpace(password) || password.Length < 10 || password.Length > 256) throw new ApiValidationException("Password must be 10 to 256 characters."); }
    private static void ValidateDisplayName(string? displayName) { if (displayName is { Length: > 128 }) throw new ApiValidationException("Display name must not exceed 128 characters."); }
    private sealed record CredentialRow(SecurityUserDto User, byte[] Salt, byte[] Hash, int Iterations, bool Enabled);
}
