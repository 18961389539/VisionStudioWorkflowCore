using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class ProductionWorkstationEndpoints
{
    public static IEndpointRouteBuilder MapProductionWorkstationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/production/operator-layout/{jobId}", async (string jobId, OperatorLayoutStore layouts,
            JobStore jobs, HttpContext http, CancellationToken ct) =>
        {
            await RequireJobAsync(jobs, jobId, ct);
            var layout = await layouts.GetAsync(jobId, ct);
            http.Response.Headers.ETag = OperatorLayoutStore.RevisionOf(layout);
            return Results.Ok(layout);
        }).Produces<OperatorLayout>().RequireAuthorization(SecurityPolicies.Operator);

        app.MapPut("/api/production/operator-layout/{jobId}", async (string jobId, OperatorLayout request,
            [FromHeader(Name = "If-Match")] string? expectedRevision, OperatorLayoutStore layouts, JobStore jobs,
            HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(expectedRevision))
                return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, detail: "Read the operator layout and send its ETag in If-Match.");
            AuditContext.SetTarget(http, jobId);
            await RequireJobAsync(jobs, jobId, ct);
            var layout = await layouts.SaveAsync(jobId, request, expectedRevision, ct);
            http.Response.Headers.ETag = OperatorLayoutStore.RevisionOf(layout);
            return Results.Ok(layout);
        }).Produces<OperatorLayout>().ProducesProblem(StatusCodes.Status428PreconditionRequired).ProducesProblem(StatusCodes.Status409Conflict)
            .RequireEngineer("production.operator-layout.update", "production");

        app.MapGet("/api/production/stations", (ProductionFleet fleet) => Results.Ok(fleet.Snapshot()))
            .Produces<IReadOnlyList<ProductionStationOverview>>().RequireAuthorization(SecurityPolicies.Operator);

        app.MapPost("/api/production/stations/control", async (StationProductionControlRequest request,
            ProductionFleet fleet, HttpContext http, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, $"{request.Action}:{string.Join(',', request.StationIds)}");
            return Results.Ok(await fleet.ControlAsync(request, ct));
        }).Produces<StationProductionControlBatch>().RequireOperator("production.station-batch-control", "production");

        app.MapGet("/api/production/stations/local", async (IOptions<ProductionFleetOptions> options, ProductionRuntimeService production,
            JobStore jobs, AlarmStore alarms, CancellationToken ct) =>
        {
            if (options.Value.StationId is not { } stationId) return Results.NotFound();
            var runtime = production.Status;
            var job = runtime.JobId is { Length: > 0 } jobId ? await jobs.GetStationJobAsync(jobId, ct) : null;
            var alarm = await alarms.GetSummaryAsync(ct);
            return Results.Ok(new ProductionStationStatus(stationId, runtime.State, runtime.JobId, job, runtime.LockedJobVersion,
                runtime.LockedWorkflowHash, runtime.CycleCount, runtime.OkCount, runtime.NgCount, runtime.LastDisposition,
                runtime.LastCycleAt, alarm.ActiveCount, alarm.CriticalCount, alarm.PendingCount));
        }).Produces<ProductionStationStatus>().Produces(StatusCodes.Status404NotFound).RequireAuthorization(SecurityPolicies.StationRead);

        app.MapGet("/api/production/operator-preview", async (string? stationId, bool? ngOnly, ProductionRuntimeService production,
            TraceabilityStore traces, CancellationToken ct) =>
        {
            var state = string.IsNullOrWhiteSpace(stationId) ? production.Status : ToRuntimeStatus(production.GetStatus(stationId));
            if (!state.ProductionLocked || state.JobId is not { } jobId || state.LockedJobVersion is not { } version ||
                state.StartedAt is not { } startedAt)
                return Results.NoContent();

            var source = string.IsNullOrWhiteSpace(stationId)
                ? "ProductionRuntime"
                : $"ProductionRuntime:{ProductionRuntimeStationIds.Normalize(stationId)}";
            var preview = await traces.GetOperatorPreviewAsync(jobId, version, startedAt, ct, ngOnly == true, source);
            var current = string.IsNullOrWhiteSpace(stationId) ? production.Status : ToRuntimeStatus(production.GetStatus(stationId));
            if (preview is null || !current.ProductionLocked || current.StartedAt != startedAt || current.JobId != jobId || current.LockedJobVersion != version)
                return Results.NoContent();
            return Results.Ok(preview);
        }).Produces<ProductionOperatorPreview>().Produces(StatusCodes.Status204NoContent).RequireAuthorization(SecurityPolicies.Operator);

        return app;
    }

    private static ProductionRuntimeStatus ToRuntimeStatus(ProductionStationRuntimeStatus status) => new(
        status.State, status.JobId, status.LockedJobVersion, status.LockedWorkflowHash, status.LockedDependencyManifestHash,
        status.StartedAt, status.LastCycleAt, status.CurrentRunId, status.CycleCount, status.OkCount, status.NgCount,
        status.ErrorCount, status.ConsecutiveFailures, status.WatchdogTrips, status.LastDurationMs, status.LastDisposition,
        status.LastError, status.ProductionLocked, status.PtpGuard, status.SynchronizationGuard, status.PendingTraceCount,
        status.TraceQueueCapacity, status.RecoveryAttempts, status.HasDeviceSideEffects, status.StatusEpoch, status.StatusVersion);

    private static async Task RequireJobAsync(JobStore jobs, string jobId, CancellationToken ct)
    {
        if (await jobs.GetStationJobAsync(jobId, ct) is null)
            throw new ApiNotFoundException($"Job '{jobId}' does not exist.");
    }
}
