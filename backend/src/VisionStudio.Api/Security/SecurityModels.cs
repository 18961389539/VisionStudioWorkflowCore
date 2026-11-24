using Microsoft.AspNetCore.Authorization;

namespace VisionStudio.Api.Security;

public static class SecurityRoles
{
    public const string Operator = "Operator";
    public const string Engineer = "Engineer";
    public const string Administrator = "Administrator";

    /// <summary>服务令牌主体角色（P3-6）。只经作用域策略授权，刻意不在 <see cref="All"/> 里：不能分配给登录用户。</summary>
    public const string Service = "Service";

    public static readonly string[] All = [Operator, Engineer, Administrator];

    public static bool IsValid(string role) => All.Contains(role, StringComparer.OrdinalIgnoreCase);
}

public static class SecurityPolicies
{
    public const string Operator = "OperatorOrAbove";
    public const string Engineer = "EngineerOrAbove";
    public const string Administrator = "AdministratorOnly";

    /// <summary>追溯结果读取：人类 Operator 及以上，或持有 results:read 的服务令牌。</summary>
    public const string ResultsRead = "ResultsReadOrScope";

    /// <summary>触发流程运行：人类 Engineer 及以上，或持有 runs:run 的服务令牌。</summary>
    public const string RunTrigger = "RunTriggerOrScope";
    public const string StationRead = "StationReadOrScope";
    public const string StationDelivery = "StationRecipeDeliveryScope";
}

public static class SecurityClaims
{
    public const string Scope = "scope";
    public const string ServiceTokenId = "service_token_id";
}

public static class ServiceScopes
{
    public const string ResultsRead = "results:read";
    public const string RunsRun = "runs:run";

    public const string StationRead = "stations:read";

    public const string StationDelivery = "stations:deliver";
    public static readonly string[] All = [ResultsRead, RunsRun, StationRead, StationDelivery];

    public static bool IsValid(string scope) => All.Contains(scope, StringComparer.OrdinalIgnoreCase);
}

public sealed class SecurityOptions
{
    public bool Enabled { get; set; } = true;
    public int SessionHours { get; set; } = 12;
    public int PasswordPbkdf2Iterations { get; set; } = 210_000;
    public string CookieName { get; set; } = "vs_session";

    /// <summary>
    /// 连续登录失败达到该次数后锁定账号（2026-11-12）。0 表示关闭锁定（只保留限流）。
    /// 默认 5 次 / 15 分钟：厂内共享账号常见"走开没退出、别人乱试"，锁定要让误操作有成本，
    /// 又不能让夜班因为按错几次键就叫不到人。
    /// </summary>
    public int MaxFailedLoginAttempts { get; set; } = 5;

    /// <summary>锁定时长（分钟），仅在 <see cref="MaxFailedLoginAttempts"/> &gt; 0 时生效。</summary>
    public int LockoutMinutes { get; set; } = 15;

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
public sealed record SessionUserResponse(DateTimeOffset ExpiresAt, SecurityUserDto User);
public sealed record LogoutResponse(bool LoggedOut);
public sealed record CurrentUserResponse(string? Id, string? Username, string? DisplayName, string? Role);
public sealed record ChangePasswordResult(bool Changed, bool SessionsRevoked);
public sealed record ResetPasswordResult(bool Reset, bool SessionsRevoked);
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

public sealed record CreateServiceTokenRequest(string Name, string[]? Scopes, int? ExpiresInDays);

/// <summary>令牌元数据。明文 token 只在创建/轮换响应里出现一次，之后任何读接口都不返回。</summary>
public sealed record ServiceTokenDto(
    string Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record ServiceTokenCreated(string Token, ServiceTokenDto Details);

/// <summary>
/// 审计链校验结果：<see cref="FirstInvalidSeq"/> 是第一个不可信行，<see cref="FirstInvalidReason"/>
/// 取值 missing-hash / link-mismatch / hash-mismatch。
/// </summary>
public sealed record AuditChainVerification(
    bool Valid,
    long Checked,
    long? FirstInvalidSeq,
    string? FirstInvalidReason,
    long? LastSeq,
    string? LastHash,
    DateTimeOffset VerifiedAt);

public static class SecurityEndpointExtensions
{
    public static TBuilder RequireOperator<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.Operator); builder.WithAudit(action, resource); return builder; }

    public static TBuilder RequireEngineer<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.Engineer); builder.WithAudit(action, resource); return builder; }

    public static TBuilder RequireAdministrator<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.Administrator); builder.WithAudit(action, resource); return builder; }

    /// <summary>人类 Operator 及以上或服务令牌 results:read；服务令牌的每次读取单独留审计。</summary>
    public static TBuilder RequireResultsRead<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.ResultsRead); builder.WithServiceUsageAudit(action, resource); return builder; }

    /// <summary>人类 Engineer 及以上或服务令牌 runs:run；触发运行始终留审计。</summary>
    public static TBuilder RequireRunTrigger<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.RequireAuthorization(SecurityPolicies.RunTrigger); builder.WithAudit(action, resource); return builder; }
}
