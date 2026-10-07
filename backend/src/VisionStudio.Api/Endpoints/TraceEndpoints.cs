using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public sealed record StorageBackupCreateRequest(bool? IncludeArtifacts = null);

public static class TraceEndpoints
{
    public static IEndpointRouteBuilder MapTraceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/traces", async (int? take, string? jobId, string? disposition, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.ListAsync(take ?? 100, jobId, disposition, ct)));

        app.MapGet("/api/traces/page", async (int? offset, int? limit, string? jobId, string? disposition, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.PageAsync(offset ?? 0, limit ?? 100, jobId, disposition, ct)));

        app.MapGet("/api/traces/stats", async (int? days, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.StatsAsync(days ?? 7, ct)));

        app.MapGet("/api/traces/{runId}", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var trace = await traces.GetAsync(runId, ct);
            return trace is null ? Results.NotFound() : Results.Ok(trace);
        });

        app.MapGet("/api/traces/{runId}/observability", async (string runId, int? days, int? history, RunObservabilityService observability, CancellationToken ct) =>
            Results.Ok(await observability.GetAsync(runId, days ?? 7, history ?? 100, ct)));

        app.MapGet("/api/traces/{runId}/compare", async (string runId, string? baselineRunId, TraceAnalysisService analysis, CancellationToken ct) =>
            Results.Ok(await analysis.CompareAsync(runId, baselineRunId, ct)));

        app.MapGet("/api/traces/{runId}/failure-signature", async (string runId, int? days, int? take, TraceAnalysisService analysis, CancellationToken ct) =>
            Results.Ok(await analysis.AnalyzeFailureAsync(runId, days ?? 30, take ?? 12, ct)));

        app.MapPut("/api/traces/{runId}/disposition", async (string runId, UpdateDispositionRequest request, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.UpdateDispositionAsync(runId, request, ct))).RequireOperator("trace.disposition.update", "trace");

        app.MapGet("/api/traces/{runId}/preview", (string runId, TraceabilityStore traces) =>
        {
            var path = traces.FindPreviewPath(runId);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        app.MapGet("/api/traces/{runId}/workflow", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var workflow = await traces.GetWorkflowSnapshotAsync(runId, ct);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        app.MapGet("/api/traces/{runId}/dependencies", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var manifest = await traces.GetDependencyManifestAsync(runId, ct);
            return manifest is null ? Results.NotFound() : Results.Ok(manifest);
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/traces/{runId}/overlays", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var overlays = await traces.GetOverlaysAsync(runId, ct);
            return overlays is null ? Results.NotFound() : Results.Ok(overlays);
        });

        app.MapGet("/api/storage/status", async (TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.StorageStatusAsync(ct)));

        app.MapGet("/api/storage/schema", async (SqliteMetadataDatabase database, CancellationToken ct) =>
            Results.Ok(await database.GetSchemaStatusAsync(ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/storage/capacity", async (StorageCapacityService capacity, CancellationToken ct) =>
            Results.Ok(await capacity.RefreshAsync(ct)));

        app.MapPost("/api/storage/cleanup", async (TraceabilityStore traces, StorageCapacityService capacity, CancellationToken ct) =>
        {
            var changed = await traces.CleanupAsync(ct);
            return Results.Ok(new { changed, capacity = await capacity.RefreshAsync(ct) });
        }).RequireAdministrator("storage.cleanup", "storage");

        app.MapGet("/api/storage/backups", async (StorageBackupService backups, CancellationToken ct) =>
            Results.Ok(await backups.ListAsync(ct))).RequireAuthorization(SecurityPolicies.Administrator);

        app.MapPost("/api/storage/backups", async (StorageBackupCreateRequest? request, StorageBackupService backups, CancellationToken ct) =>
            Results.Ok(await backups.CreateAsync(request?.IncludeArtifacts, ct)))
            .RequireAdministrator("storage.backup.create", "storage");

        app.MapDelete("/api/storage/backups/{backupId}", async (string backupId, HttpContext http, StorageBackupService backups, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, backupId);
            await backups.DeleteAsync(backupId, ct);
            return Results.NoContent();
        }).RequireAdministrator("storage.backup.delete", "storage");

        app.MapPost("/api/storage/restore", async (StorageRestoreRequest request, HttpContext http, StorageBackupService backups, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, request.BackupId);
            return Results.Ok(await backups.RestoreAsync(request, ct));
        }).RequireAdministrator("storage.restore", "storage");

        return app;
    }
}
