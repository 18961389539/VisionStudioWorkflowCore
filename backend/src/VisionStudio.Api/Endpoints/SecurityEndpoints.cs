using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class SecurityEndpoints
{
    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/status", async (SecurityStore store, IOptions<SecurityOptions> options, CancellationToken ct) =>
            Results.Ok(new SecurityStatus(options.Value.Enabled, options.Value.Enabled && !await store.HasUsersAsync(ct), options.Value.Enabled && options.Value.AutoLoginAdmin)))
            .AllowAnonymous().Produces<SecurityStatus>();

        app.MapPost("/api/auth/bootstrap", async (BootstrapAdminRequest request, HttpContext http, SecurityStore store, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, request.Username);
            return Results.Ok(await store.BootstrapAdministratorAsync(request, ct));
        }).AllowAnonymous().RequireRateLimiting("auth").WithAudit("security.bootstrap", "security")
            .Produces<SecurityUserDto>();

        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, SecurityStore store, IOptions<SecurityOptions> options, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, request.Username);
            var session = await store.LoginAsync(request, ct);
            AppendSessionCookie(http, options.Value, session.Token, session.ExpiresAt);
            return Results.Ok(new SessionUserResponse(session.ExpiresAt, session.User));
        }).AllowAnonymous().RequireRateLimiting("auth").WithAudit("auth.login", "session")
            .Produces<SessionUserResponse>();

        // 未开启 Security:AutoLoginAdmin 时该入口不存在；开启后由前端在启动时调用以免密码取得管理员会话。
        app.MapPost("/api/auth/auto-login", async (HttpContext http, SecurityStore store, IOptions<SecurityOptions> options, CancellationToken ct) =>
        {
            var security = options.Value;
            if (!security.Enabled || !security.AutoLoginAdmin) return Results.NotFound();
            AuditContext.SetTarget(http, security.AutoLoginUsername);
            var session = await store.AutoLoginAdministratorAsync(security.AutoLoginUsername, security.AutoLoginDisplayName, ct);
            AppendSessionCookie(http, security, session.Token, session.ExpiresAt);
            return Results.Ok(new SessionUserResponse(session.ExpiresAt, session.User));
        }).AllowAnonymous().RequireRateLimiting("auth").WithAudit("auth.auto-login", "session")
            .Produces<SessionUserResponse>();

        app.MapPost("/api/auth/logout", async (HttpContext http, SecurityStore store, IOptions<SecurityOptions> options, CancellationToken ct) =>
        {
            var token = http.Request.Cookies[options.Value.CookieName] ?? Bearer(http.Request.Headers.Authorization.ToString());
            if (token is not null) await store.LogoutAsync(token, ct);
            http.Response.Cookies.Delete(options.Value.CookieName, new CookieOptions { Path = "/" });
            return Results.Ok(new LogoutResponse(true));
        }).WithAudit("auth.logout", "session").Produces<LogoutResponse>();

        app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new CurrentUserResponse(
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            user.Identity?.Name,
            user.FindFirstValue("display_name"),
            user.FindFirstValue(ClaimTypes.Role))))
            .Produces<CurrentUserResponse>();

        app.MapPost("/api/auth/change-password", async (ChangePasswordRequest request, ClaimsPrincipal user, SecurityStore store, CancellationToken ct) =>
        {
            await store.ChangePasswordAsync(user.FindFirstValue(ClaimTypes.NameIdentifier)!, request.CurrentPassword, request.NewPassword, ct);
            return Results.Ok(new ChangePasswordResult(true, true));
        }).WithAudit("auth.change-password", "security-user").Produces<ChangePasswordResult>();

        app.MapGet("/api/security/users", async (SecurityStore store, CancellationToken ct) => Results.Ok(await store.ListUsersAsync(ct)))
            .RequireAuthorization(SecurityPolicies.Administrator).Produces<IReadOnlyList<SecurityUserDto>>();

        app.MapPost("/api/security/users", async (CreateSecurityUserRequest request, HttpContext http, SecurityStore store, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, request.Username); return Results.Ok(await store.CreateUserAsync(request, ct));
        }).RequireAuthorization(SecurityPolicies.Administrator).WithAudit("security.user.create", "security-user")
            .Produces<SecurityUserDto>();

        app.MapPut("/api/security/users/{id}", async (string id, UpdateSecurityUserRequest request, HttpContext http, SecurityStore store, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, id); return Results.Ok(await store.UpdateUserAsync(id, request, ct));
        }).RequireAuthorization(SecurityPolicies.Administrator).WithAudit("security.user.update", "security-user")
            .Produces<SecurityUserDto>();

        app.MapPost("/api/security/users/{id}/reset-password", async (string id, ResetPasswordRequest request, HttpContext http, SecurityStore store, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, id); await store.ResetPasswordAsync(id, request.Password, ct); return Results.Ok(new ResetPasswordResult(true, true));
        }).RequireAuthorization(SecurityPolicies.Administrator).WithAudit("security.user.reset-password", "security-user")
            .Produces<ResetPasswordResult>();

        // 解锁（2026-11-12）：连续失败锁定后，管理员不必改密就能恢复账号；解锁本身也进审计。
        app.MapPost("/api/security/users/{id}/unlock", async (string id, HttpContext http, SecurityStore store, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, id); return Results.Ok(await store.UnlockAsync(id, ct));
        }).RequireAuthorization(SecurityPolicies.Administrator).WithAudit("security.user.unlock", "security-user")
            .Produces<SecurityUserDto>();

        app.MapGet("/api/audit", async (int? offset, int? limit, string? username, string? action, AuditEventStore audit, CancellationToken ct) =>
            Results.Ok(await audit.ListAsync(offset ?? 0, limit ?? 100, username, action, ct)))
            .RequireAuthorization(SecurityPolicies.Administrator).Produces<IReadOnlyList<AuditEventDto>>();

        return app;
    }

    private static void AppendSessionCookie(HttpContext http, SecurityOptions options, string token, DateTimeOffset expires)
        => http.Response.Cookies.Append(options.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            Expires = expires
        });

    private static string? Bearer(string header) => header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
}
