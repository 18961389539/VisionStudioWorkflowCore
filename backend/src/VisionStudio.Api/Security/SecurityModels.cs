using Microsoft.AspNetCore.Authorization;

namespace VisionStudio.Api.Security;

public static class SecurityRoles
{
    public const string Operator = "Operator";
    public const string Engineer = "Engineer";
    public const string Administrator = "Administrator";

    public static readonly string[] All = [Operator, Engineer, Administrator];

    public static bool IsValid(string role) => All.Contains(role, StringComparer.OrdinalIgnoreCase);
}

public static class SecurityPolicies
{
    public const string Operator = "OperatorOrAbove";
    public const string Engineer = "EngineerOrAbove";
    public const string Administrator = "AdministratorOnly";
}

public sealed class SecurityOptions
{
    public bool Enabled { get; set; } = true;
    public int SessionHours { get; set; } = 12;
    public int PasswordPbkdf2Iterations { get; set; } = 210_000;
    public string CookieName { get; set; } = "vs_session";

    /// <summary>启动时免密码以内置管理员身份建立会话（默认关闭）。</summary>
    public bool AutoLoginAdmin { get; set; }

    /// <summary>
    /// R08：允许把明文 HTTP 绑定到非 loopback 地址。默认 false——把控制端口暴露到网络必须
    /// 使用 HTTPS（或可信反向代理终止 TLS）；仅为隔离测试网络保留逃生舱。
    /// </summary>
    public bool AllowInsecureRemoteTransport { get; set; }

    /// <summary>自动登录使用的内置管理员账号名，账号不存在时自动创建（随机口令，不对外公开）。</summary>
    public string AutoLoginUsername { get; set; } = "admin";

    public string AutoLoginDisplayName { get; set; } = "默认管理员";
}

public sealed record LoginRequest(string Username, string Password);
public sealed record BootstrapAdminRequest(string Username, string DisplayName, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CreateSecurityUserRequest(string Username, string DisplayName, string Role, string Password);
public sealed record UpdateSecurityUserRequest(string DisplayName, string Role, bool Enabled);
public sealed record ResetPasswordRequest(string Password);

public sealed record SecurityUserDto(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset PasswordChangedAt);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, SecurityUserDto User);
public sealed record SecurityStatus(bool Enabled, bool BootstrapRequired, bool AutoLogin);
public sealed record AuditEventDto(
    long Sequence,
    DateTimeOffset At,
    string? UserId,
    string? Username,
    string? Role,
    string Action,
    string Resource,
    string Method,
    string Path,
    int StatusCode,
    bool Success,
    string? Target,
    string? CorrelationId,
    string? ClientIp);

public static class SecurityEndpointExtensions
{
    public static TBuilder RequireOperator<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.Operator); builder.WithAudit(action, resource); return builder; }

    public static TBuilder RequireEngineer<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.Engineer); builder.WithAudit(action, resource); return builder; }

    public static TBuilder RequireAdministrator<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.Administrator); builder.WithAudit(action, resource); return builder; }
}
