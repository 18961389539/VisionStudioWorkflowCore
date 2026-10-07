using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class CameraSynchronizationEndpoints
{
    public static IEndpointRouteBuilder MapCameraSynchronizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/camera-sync/groups", async (CameraSynchronizationService sync, CancellationToken ct) =>
            Results.Ok(await sync.ListAsync(ct)))
            .RequireEngineer("camera.sync.list", "camera-sync");

        app.MapGet("/api/camera-sync/groups/{id}", async (string id, CameraSynchronizationService sync, CancellationToken ct) =>
            Results.Ok(await sync.GetAsync(id, ct)))
            .RequireEngineer("camera.sync.get", "camera-sync");

        app.MapGet("/api/camera-sync/groups/{id}/status", async (string id, CameraSynchronizationService sync, CancellationToken ct) =>
            Results.Ok(await sync.GetStatusAsync(id, ct)))
            .RequireOperator("camera.sync.status", "camera-sync");

        app.MapGet("/api/camera-sync/runs", async (string? groupId, int? limit, CameraSynchronizationService sync, CancellationToken ct) =>
            Results.Ok(await sync.ListRunsAsync(groupId, limit ?? 50, ct)))
            .RequireOperator("camera.sync.runs.list", "camera-sync");

        app.MapGet("/api/camera-sync/runs/{runId}", async (string runId, CameraSynchronizationService sync, CancellationToken ct) =>
            Results.Ok(await sync.GetRunAsync(runId, ct)))
            .RequireOperator("camera.sync.runs.get", "camera-sync");

        app.MapGet("/api/camera-sync/groups/{id}/statistics", async (string id, int? limit, int? minimumSamples, CameraSynchronizationService sync, CancellationToken ct) =>
            Results.Ok(await sync.GetStatisticsAsync(id, limit ?? 100, minimumSamples ?? 20, ct)))
            .RequireOperator("camera.sync.statistics", "camera-sync");

        app.MapGet("/api/camera-sync/groups/{id}/ptp-diagnostics", async (string id, int? limit, CameraSynchronizationService sync, CancellationToken ct) =>
            Results.Ok(await sync.GetPtpDiagnosticsAsync(id, limit ?? 100, ct)))
            .RequireOperator("camera.sync.ptp-diagnostics", "camera-sync");

        app.MapGet("/api/camera-sync/groups/{id}/gige-network-diagnostics", async (string id, int? limit, GigENetworkDiagnosticsService network, CancellationToken ct) =>
            Results.Ok(await network.GetGroupAsync(id, limit ?? 100, ct)))
            .RequireOperator("camera.sync.gige-network-diagnostics", "camera-sync");

        app.MapPost("/api/camera-sync/groups/{id}/gige-network-tests", async (string id, GigENetworkCommissioningTestRequest request, GigENetworkDiagnosticsService network, CancellationToken ct) =>
            Results.Accepted(value: await network.StartNetworkTestAsync(id, request, ct)))
            .RequireEngineer("camera.sync.gige-network-test.start", "camera-sync");

        app.MapGet("/api/camera-sync/gige-network-tests/{testId}/report", async (string testId, GigENetworkDiagnosticsService network, CancellationToken ct) =>
            Results.Ok(await network.GetNetworkTestReportAsync(testId, ct)))
            .RequireOperator("camera.sync.gige-network-test.report", "camera-sync");

        app.MapGet("/api/camera-sync/groups/{id}/commissioning-tests", async (string id, int? limit, CameraSynchronizationCommissioningService commissioning, CancellationToken ct) =>
            Results.Ok(await commissioning.ListAsync(id, limit ?? 20, ct)))
            .RequireOperator("camera.sync.commissioning.list", "camera-sync");

        app.MapPost("/api/camera-sync/groups/{id}/commissioning-tests", async (string id, CameraSynchronizationCommissioningTestRequest request, CameraSynchronizationCommissioningService commissioning, CancellationToken ct) =>
            Results.Accepted(value: await commissioning.StartAsync(id, request, ct)))
            .RequireEngineer("camera.sync.commissioning.start", "camera-sync");

        app.MapGet("/api/camera-sync/commissioning-tests/{testId}", async (string testId, CameraSynchronizationCommissioningService commissioning, CancellationToken ct) =>
            Results.Ok(await commissioning.GetAsync(testId, ct)))
            .RequireOperator("camera.sync.commissioning.get", "camera-sync");

        app.MapPost("/api/camera-sync/commissioning-tests/{testId}/cancel", async (string testId, CameraSynchronizationCommissioningService commissioning, CancellationToken ct) =>
            Results.Ok(await commissioning.CancelAsync(testId, ct)))
            .RequireEngineer("camera.sync.commissioning.cancel", "camera-sync");

        app.MapGet("/api/camera-sync/commissioning-tests/{testId}/report", async (string testId, CameraSynchronizationCommissioningService commissioning, CancellationToken ct) =>
            Results.Ok(await commissioning.GetReportAsync(testId, ct)))
            .RequireOperator("camera.sync.commissioning.report", "camera-sync");

        app.MapGet("/api/camera-sync/commissioning-tests/{testId}/report.html", async (string testId, CameraSynchronizationCommissioningService commissioning, CancellationToken ct) =>
        {
            var html = await commissioning.GetReportHtmlAsync(testId, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", $"camera-sync-commissioning-{testId}.html");
        }).RequireOperator("camera.sync.commissioning.report.html", "camera-sync");

        app.MapPost("/api/camera-sync/groups/{id}/validate", async (string id, CameraSynchronizationService sync, CancellationToken ct) =>
        {
            var group = await sync.GetAsync(id, ct);
            await sync.ValidateOrThrowAsync(group, scheduled: false, ct);
            return Results.Ok(new { valid = true, groupId = id });
        }).RequireEngineer("camera.sync.validate", "camera-sync");

        app.MapPut("/api/camera-sync/groups/{id}", async (
            string id, CameraSynchronizationGroupRequest request, CameraSynchronizationService sync, ProductionRuntimeService production, CancellationToken ct) =>
        {
            request = request with { Id = id };
            foreach (var cameraId in request.CameraIds) production.EnsureDependencyMutationAllowed("camera", cameraId);
            return Results.Ok(await sync.SaveAsync(request, ct));
        }).RequireEngineer("camera.sync.save", "camera-sync");

        app.MapDelete("/api/camera-sync/groups/{id}", async (
            string id, CameraSynchronizationService sync, ProductionRuntimeService production, CancellationToken ct) =>
        {
            var group = await sync.GetAsync(id, ct);
            foreach (var cameraId in group.CameraIds) production.EnsureDependencyMutationAllowed("camera", cameraId);
            await sync.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequireEngineer("camera.sync.delete", "camera-sync");

        app.MapPost("/api/camera-sync/groups/{id}/trigger", async (
            string id, CameraSynchronizationTriggerRequest request, CameraSynchronizationService sync,
            DeviceLeaseRegistry leases, CancellationToken ct) =>
        {
            // 请求期硬件租约：立即触发路径在请求内等待成帧，租约覆盖组内全部相机
            using var lease = await AcquireGroupLeaseAsync(id, sync, leases, "trigger", ct);
            return Results.Ok(await sync.TriggerAsync(id, request with { Scheduled = false }, ct));
        })
            .RequireOperator("camera.sync.trigger", "camera-sync");

        app.MapPost("/api/camera-sync/groups/{id}/schedule", async (
            string id, CameraSynchronizationTriggerRequest request, CameraSynchronizationService sync,
            DeviceLeaseRegistry leases, CancellationToken ct) =>
        {
            // 调度触发同样在请求内等待目标时刻成帧（lead time 最长 60s），故用同一请求期租约
            using var lease = await AcquireGroupLeaseAsync(id, sync, leases, "schedule", ct);
            return Results.Ok(await sync.TriggerAsync(id, request with { Scheduled = true }, ct));
        })
            .RequireOperator("camera.sync.schedule", "camera-sync");

        return app;
    }

    /// <summary>Request-scoped manual lease covering every camera of one synchronization group.</summary>
    private static async Task<DeviceLease> AcquireGroupLeaseAsync(
        string groupId,
        CameraSynchronizationService sync,
        DeviceLeaseRegistry leases,
        string operation,
        CancellationToken ct)
    {
        var group = await sync.GetAsync(groupId, ct);
        return leases.AcquireManualOrThrow(
            group.CameraIds.Select(cameraId => new DeviceLeaseResource(DeviceLeaseResourceKinds.Camera, cameraId)).ToArray(),
            $"Cannot {operation} camera synchronization group '{groupId}'");
    }
}
