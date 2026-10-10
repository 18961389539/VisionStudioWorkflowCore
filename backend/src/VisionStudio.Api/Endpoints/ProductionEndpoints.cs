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

        app.MapPost("/api/production/device-actions/resolve", async (DeviceActionResolutionRequest request, HttpContext http, ProductionRuntimeService production, CancellationToken ct) =>
        {
            var operatorName = http.User.Identity?.Name;
            await production.ResolveUnknownDeviceActionsAsync(request, operatorName ?? string.Empty, ct);
            return Results.Ok(production.Status);
        }).RequireAdministrator("production.device-actions.resolve", "production");

        app.MapGet("/api/production/device-actions/pending", (ProductionRuntimeService production) =>
        {
            var state = production.DeviceActionResolutionState;
            return state is null ? Results.NoContent() : Results.Ok(state);
        }).RequireAdministrator("production.device-actions.read", "production");

        app.MapGet("/api/alarms", async (bool? activeOnly, AlarmStore alarms, CancellationToken ct) =>
            Results.Ok(await alarms.ListAsync(activeOnly ?? false, ct)));

        app.MapPost("/api/alarms/{id}/ack", async (string id, AlarmStore alarms, CancellationToken ct) =>
            Results.Ok(await alarms.AcknowledgeAsync(id, ct))).RequireOperator("alarm.ack", "alarm");

        return app;
    }
}
