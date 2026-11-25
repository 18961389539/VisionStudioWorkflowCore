using VisionStudio.Api.Security;
using VisionStudio.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace VisionStudio.Api.Endpoints;

public static class ProductionEndpoints
{
    public static IEndpointRouteBuilder MapProductionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/production/status", (ProductionRuntimeService production) => Results.Ok(production.Status))
            .Produces<ProductionRuntimeStatus>();

        app.MapGet("/api/production/config", async (ProductionRuntimeService production, HttpContext http, CancellationToken ct) =>
        {
            var config = await production.GetConfigAsync(ct);
            http.Response.Headers.ETag = ProductionRuntimeConfigStore.RevisionOf(config);
            return Results.Ok(config);
        })
            .Produces<ProductionRuntimeConfig>();

        app.MapGet("/api/production/ptp-guard", (ProductionRuntimeService production) =>
            Results.Ok(production.Status.PtpGuard))
            .Produces<ProductionPtpGuardSnapshot>();

        app.MapGet("/api/production/synchronization-guard", (ProductionRuntimeService production) =>
            Results.Ok(production.Status.SynchronizationGuard))
            .Produces<ProductionSynchronizationGuardSnapshot>();

        app.MapPut("/api/production/config", async (ProductionRuntimeConfig request, [FromHeader(Name = "If-Match")] string? expectedRevision,
            ProductionRuntimeService production, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(expectedRevision))
                return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, detail: "Read the production configuration and send its ETag in If-Match.");
            var saved = await production.UpdateConfigAsync(request, ct, expectedRevision);
            http.Response.Headers.ETag = ProductionRuntimeConfigStore.RevisionOf(saved);
            return Results.Ok(saved);
        }).Produces<ProductionRuntimeConfig>().ProducesProblem(StatusCodes.Status428PreconditionRequired).ProducesProblem(StatusCodes.Status409Conflict)
            .RequireEngineer("production.config.update", "production");

        app.MapPost("/api/production/start", async (ProductionStartRequest request, ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.StartAsync(request.JobId, ct)))
            .Produces<ProductionRuntimeStatus>().RequireAuthorization(SecurityPolicies.StationControl).WithAudit("production.start", "production");

        app.MapPost("/api/production/stop", async (ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.StopAsync(ct))).Produces<ProductionRuntimeStatus>()
            .RequireAuthorization(SecurityPolicies.StationControl).WithAudit("production.stop", "production");

        app.MapPost("/api/production/recover", async (ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.RecoverAsync(ct))).Produces<ProductionRuntimeStatus>().RequireOperator("production.recover", "production");

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
        }).Produces<ProductionRuntimeStatus>().RequireAdministrator("production.device-actions.resolve", "production").WithAudit("production.device-actions.resolve", "production");

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

        // 分页 + 总数（2026-10-26）：此前返回全量数组，报警历史只增不减时会一次拉进手机端/看板。
        // 保持响应体仍是数组（前端不必改），总数放 X-Total-Count；take 上限由 Store 夹紧。
        app.MapGet("/api/alarms", async (string? stationId, bool? activeOnly, bool? pendingOnly, int? skip, int? take, HttpContext http, AlarmStore alarms, CancellationToken ct) =>
        {
            var source = AlarmSourceForStation(stationId);
            var page = await alarms.ListAsync(activeOnly ?? false, skip ?? 0, take ?? AlarmStore.DefaultPageSize, ct, pendingOnly ?? false, source);
            http.Response.Headers["X-Total-Count"] = page.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Ok(page.Items);
        }).Produces<IReadOnlyList<AlarmRecord>>();

        app.MapGet("/api/alarms/summary", async (string? stationId, AlarmStore alarms, CancellationToken ct) =>
            Results.Ok(await alarms.GetSummaryAsync(ct, AlarmSourceForStation(stationId)))).Produces<AlarmSummary>();

        app.MapPost("/api/alarms/{id}/ack", async (string id, AlarmStore alarms, CancellationToken ct) =>
            Results.Ok(await alarms.AcknowledgeAsync(id, ct))).Produces<AlarmRecord>().RequireOperator("alarm.ack", "alarm");

        // --- 本机多工位并行生产端点 ---

        app.MapGet("/api/production/runtimes", (ProductionRuntimeService production) =>
            Results.Ok(production.ListStatuses()))
            .Produces<IReadOnlyList<ProductionStationRuntimeStatus>>();

        app.MapGet("/api/production/runtimes/{stationId}/status", (string stationId, ProductionRuntimeService production) =>
            Results.Ok(production.GetStatus(stationId)))
            .Produces<ProductionStationRuntimeStatus>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet("/api/production/runtimes/{stationId}/config", async (string stationId, ProductionRuntimeService production, HttpContext http, CancellationToken ct) =>
        {
            var config = await production.GetConfigAsync(stationId, ct);
            http.Response.Headers.ETag = ProductionRuntimeConfigStore.RevisionOf(config);
            return Results.Ok(config);
        }).Produces<ProductionRuntimeConfig>().ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPut("/api/production/runtimes/{stationId}/config", async (string stationId, ProductionRuntimeConfig request,
            [FromHeader(Name = "If-Match")] string? expectedRevision, ProductionRuntimeService production, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(expectedRevision))
                return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, detail: "Read the production configuration and send its ETag in If-Match.");
            AuditContext.SetTarget(http, stationId);
            var saved = await production.UpdateConfigAsync(stationId, request, ct, expectedRevision);
            http.Response.Headers.ETag = ProductionRuntimeConfigStore.RevisionOf(saved);
            return Results.Ok(saved);
        }).Produces<ProductionRuntimeConfig>().ProducesProblem(StatusCodes.Status428PreconditionRequired).ProducesProblem(StatusCodes.Status409Conflict)
            .RequireEngineer("production.config.update", "production");

        app.MapPost("/api/production/runtimes/{stationId}/start", async (string stationId, ProductionStartRequest request, ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.StartAsync(stationId, request.JobId, ct)))
            .Produces<ProductionStationRuntimeStatus>().RequireOperator("production.start", "production");

        app.MapPost("/api/production/runtimes/{stationId}/stop", async (string stationId, ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.StopAsync(stationId, ct)))
            .Produces<ProductionStationRuntimeStatus>().RequireOperator("production.stop", "production");

        app.MapPost("/api/production/runtimes/{stationId}/recover", async (string stationId, ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.RecoverAsync(stationId, ct)))
            .Produces<ProductionStationRuntimeStatus>().RequireOperator("production.recover", "production");

        app.MapPost("/api/production/runtimes/{stationId}/trigger", (string stationId,
            [FromHeader(Name = "Idempotency-Key")] string? eventId, HttpContext http, ProductionRuntimeService production) =>
        {
            var accepted = production.Trigger(stationId, eventId);
            if (accepted is not null) http.Response.Headers["X-Production-Trigger-Id"] = accepted.Event.EventId;
            return Results.Ok(production.GetStatus(stationId));
        })
            .Produces<ProductionStationRuntimeStatus>().ProducesProblem(StatusCodes.Status409Conflict)
            .RequireOperator("production.start", "production");

        app.MapGet("/api/production/runtimes/{stationId}/triggers", (string stationId, ProductionRuntimeService production) =>
            Results.Ok(production.ListTriggers(stationId)))
            .Produces<IReadOnlyList<ProductionTriggerEvent>>()
            .RequireAuthorization(SecurityPolicies.Operator);

        app.MapGet("/api/production/runtimes/{stationId}/latest-result", (string stationId, ProductionRuntimeService production) =>
        {
            var result = production.GetLatestResult(stationId);
            return result is null ? Results.NoContent() : Results.Ok(result);
        }).Produces<ProductionStationResultSnapshot>().Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(SecurityPolicies.Operator);

        app.MapDelete("/api/production/runtimes/{stationId}", async (string stationId, ProductionRuntimeService production, CancellationToken ct) =>
        {
            await production.RemoveStationAsync(stationId, ct);
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict)
            .RequireEngineer("production.config.update", "production");

        app.MapPost("/api/production/runtimes/start-all", async (ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.StartAllAsync(ct)))
            .Produces<IReadOnlyList<ProductionStationRuntimeStatus>>().RequireOperator("production.start", "production");

        app.MapPost("/api/production/runtimes/stop-all", async (ProductionRuntimeService production, CancellationToken ct) =>
            Results.Ok(await production.StopAllAsync(ct)))
            .Produces<IReadOnlyList<ProductionStationRuntimeStatus>>().RequireOperator("production.stop", "production");

        app.MapGet("/api/production/runtimes/{stationId}/device-actions/pending", (string stationId, ProductionRuntimeService production) =>
        {
            var state = production.GetDeviceActionResolutionState(stationId);
            if (state is null) return Results.NoContent();
            return Results.Ok(state);
        }).Produces<ProductionDeviceActionPendingState>().Produces(StatusCodes.Status204NoContent)
            .RequireAdministrator("production.device-actions.read", "production");

        app.MapPost("/api/production/runtimes/{stationId}/device-actions/resolve", async (string stationId, DeviceActionResolutionRequest request,
            HttpContext http, ProductionRuntimeService production, CancellationToken ct) =>
        {
            var operatorName = http.User.Identity?.Name;
            AuditContext.SetTarget(http, $"{stationId}:{request.RunId}");
            await production.ResolveUnknownDeviceActionsAsync(stationId, request, operatorName ?? string.Empty, ct);
            return Results.Ok(production.GetStatus(stationId));
        }).Produces<ProductionStationRuntimeStatus>()
            .RequireAdministrator("production.device-actions.resolve", "production")
            .WithAudit("production.device-actions.resolve", "production");

        return app;
    }

    private static string? AlarmSourceForStation(string? stationId)
        => string.IsNullOrWhiteSpace(stationId) || ProductionRuntimeStationIds.IsDefault(stationId)
            ? null
            : $"ProductionRuntime:{ProductionRuntimeStationIds.Normalize(stationId)}";
}
