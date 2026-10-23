using VisionStudio.Api.Security;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Endpoints;

public static class ProductionEndpoints
{
    public static IEndpointRouteBuilder MapProductionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/production/status", (ProductionRuntimeService production) => Results.Ok(production.Status));

        app.MapGet("/api/production/config", async (ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.GetConfigAsync(ct)));

        app.MapGet("/api/production/ptp-guard", (ProductionRuntimeService production) =>
            Results.Ok(production.Status.PtpGuard));

        app.MapGet("/api/production/synchronization-guard", (ProductionRuntimeService production) =>
            Results.Ok(production.Status.SynchronizationGuard));

        app.MapPut("/api/production/config", async (ProductionRuntimeConfig request, ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.UpdateConfigAsync(request, ct))).RequireEngineer("production.config.update", "production");

        app.MapPost("/api/production/start", async (ProductionStartRequest request, ProductionRuntimeService production, PluginPerformanceBenchmarkService benchmarks, CancellationToken ct) =>
        {
            if (benchmarks.HasActiveRuns) throw new ApiConflictException("A plugin performance benchmark is running. Cancel or finish it before starting Production Runtime.");
            return Results.Ok(await production.StartAsync(request.JobId, ct));
        }).RequireOperator("production.start", "production");

        app.MapPost("/api/production/stop", async (ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.StopAsync(ct))).RequireOperator("production.stop", "production");

        app.MapPost("/api/production/recover", async (ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.RecoverAsync(ct))).RequireOperator("production.recover", "production");

        app.MapPost("/api/production/device-actions/resolve", async (DeviceActionResolutionRequest request, HttpContext http,
            ProductionRuntimeService production, DeviceActionAuthorizationService authorization, CancellationToken ct) =>
        {
            var operatorName = http.User.Identity?.Name;
            // F03：手动/临时运行动作意图的核对路径——与生产意图共用同一端点，但按意图 ID 精确清除。
            // 证据必填（否则核对不可追溯），清除动作写入审计（AuditContext 目标 = 意图 ID）。
            if (!string.IsNullOrWhiteSpace(request.ManualIntentId))
            {
                // Fix3：证据校验与"先存证、后解除"的顺序由授权服务统一负责（单一来源）。
                AuditContext.SetTarget(http, request.ManualIntentId);
                authorization.ResolveManualIntent(
                    request.ManualIntentId,
                    operatorName ?? string.Empty,
                    string.IsNullOrWhiteSpace(request.Reason) ? "manual reconciliation" : request.Reason,
                    request.Evidence,
                    request.DeviceIdentityEvidence);
                return Results.Ok(production.Status);
            }

            AuditContext.SetTarget(http, request.RunId);
            await production.ResolveUnknownDeviceActionsAsync(request, operatorName ?? string.Empty, ct);
            return Results.Ok(production.Status);
        }).RequireAdministrator("production.device-actions.resolve", "production").WithAudit("production.device-actions.resolve", "production");

        app.MapGet("/api/production/device-actions/pending", (ProductionRuntimeService production, DeviceActionAuthorizationService authorization) =>
        {
            // F03：未闭合意图的**单一事实源**——生产状态 + 手动/临时/调试/作业运行登记的意图
            // （此前 pending 只认识生产意图，导致遗留手动意图能锁死系统却没有核对入口）。
            var state = production.DeviceActionResolutionState;
            var manual = authorization.UnresolvedManualIntents()
                .Select(x => new
                {
                    x.RunId,
                    x.Action,
                    x.DeviceIds,
                    x.RobotIds,
                    x.CameraIds,
                    x.SetAt,
                    State = x.State.ToString(),
                    x.ClosedReason
                })
                .ToArray();
            if (state is null && manual.Length == 0) return Results.NoContent();
            return Results.Ok(new
            {
                state?.RunId,
                state?.ManifestHash,
                state?.DeviceIds,
                state?.RobotIds,
                state?.DeviceFingerprints,
                state?.Phase,
                state?.SetAt,
                state?.Reason,
                state?.Unreadable,
                ManualIntents = manual
            });
        }).RequireAdministrator("production.device-actions.read", "production");

        app.MapGet("/api/alarms", async (bool? activeOnly, AlarmStore alarms, CancellationToken ct) =>
            Results.Ok(await alarms.ListAsync(activeOnly ?? false, ct)))
            .Produces<IReadOnlyList<AlarmRecord>>();

        app.MapPost("/api/alarms/{id}/ack", async (string id, AlarmStore alarms, CancellationToken ct) =>
            Results.Ok(await alarms.AcknowledgeAsync(id, ct))).RequireOperator("alarm.ack", "alarm");

        return app;
    }
}
