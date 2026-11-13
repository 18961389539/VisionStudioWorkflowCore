using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed class StorageMaintenanceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, StorageMaintenanceCoordinator coordinator, IProblemDetailsService problemDetails)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        // Restart-pending takes precedence over every storage maintenance exception. Authentication
        // and health remain reachable; restore/backup/recover cannot mutate disk while runtime state
        // still reflects the pre-restore generation.
        if (coordinator.IsRestartPending)
        {
            if (IsRestartPendingReachabilityPath(context.Request))
            {
                await next(context);
                return;
            }
            await WriteUnavailableAsync(context, problemDetails, restartPending: true, coordinator.Reason);
            return;
        }
        // F03：受控恢复入口在失败锁激活（或维护进行中）时也必须可达——否则锁定后没有任何受认证
        // 路径可以完成核对并解锁。它自行做状态检查与审计，不持有请求 lease。
        if (IsRecoveryEntrypoint(context.Request))
        {
            await next(context);
            return;
        }
        // Q06：失败锁是**持久**状态（重启后仍在）。只豁免恢复端点还不够——认证与最小健康诊断
        // 也必须可达，否则新浏览器/会话过期/重启后无法登录（login 返回 503），恢复入口形同虚设。
        // 仅放行认证与健康读取；普通业务写入、设备操作继续被维护状态阻断。
        if (coordinator.IsFailureLocked && IsFailureLockReachabilityPath(context.Request))
        {
            await next(context);
            return;
        }
        // The first restore request must not count itself or it would deadlock while acquiring
        // exclusive mode. Once maintenance is active, later restore requests are rejected too.
        // 排他操作（恢复与创建备份）自行进入维护窗口：若它们持有请求 lease，BeginExclusiveAsync
        // 排空时会等待当前请求自身而死锁——与 restore 相同地豁免（维护已激活时仍走拒绝路径）。
        var isExclusiveOperation = context.Request.Path.StartsWithSegments("/api/storage/restore") ||
            (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals("/api/storage/backups", StringComparison.OrdinalIgnoreCase));
        if (isExclusiveOperation && !coordinator.IsMaintenanceActive) { await next(context); return; }

        using var lease = coordinator.TryEnterRequest();
        if (lease is not null) { await next(context); return; }

        await WriteUnavailableAsync(context, problemDetails, restartPending: false, coordinator.Reason);
    }

    private static async Task WriteUnavailableAsync(HttpContext context, IProblemDetailsService problemDetails, bool restartPending, string? reason)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = restartPending ? "Restart required" : "Storage maintenance in progress",
            Detail = restartPending
                ? "Storage was restored successfully; the host must be restarted before it can serve requests " +
                  "(on-disk assets and the in-memory runtime would otherwise disagree). Restart the service to complete the recovery."
                : $"The host is temporarily quiesced for storage maintenance ({reason ?? "maintenance"}).",
            Instance = context.Request.Path
        };
        details.Extensions["code"] = restartPending ? "restart_required" : "storage_maintenance";
        details.Extensions["correlationId"] = RequestCorrelation.Get(context);
        await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details });
    }

    private static bool IsRecoveryEntrypoint(HttpRequest request)
        => HttpMethods.IsPost(request.Method) &&
           request.Path.Equals("/api/storage/maintenance/recover", StringComparison.OrdinalIgnoreCase);

    /// <summary>失败锁（无维护 lease 语义）下必须保留的最小可达路径：登录/登出、安全状态、健康读取，
    /// 以及 **受管理员认证的备份清单**（R05：重启后的新浏览器必须能列出并选择可恢复的备份，
    /// 否则恢复入口要求"事先知道 backupId"，现场无法在产品内完成恢复）。</summary>
    private static bool IsFailureLockReachabilityPath(HttpRequest request)
    {
        var path = request.Path;
        if (HttpMethods.IsGet(request.Method))
            return path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/storage/backups", StringComparison.OrdinalIgnoreCase);
        if (HttpMethods.IsPost(request.Method))
            return path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/storage/restore", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>待重启期间仍需可达的路径：认证（登录以查看状态）、健康读取、以及维护端点自查。</summary>
    private static bool IsRestartPendingReachabilityPath(HttpRequest request)
    {
        var path = request.Path;
        if (HttpMethods.IsGet(request.Method))
            return path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase);
        if (HttpMethods.IsPost(request.Method))
            return path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
