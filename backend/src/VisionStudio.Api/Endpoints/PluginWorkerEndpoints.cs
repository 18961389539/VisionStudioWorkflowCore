using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class PluginWorkerEndpoints
{
    public static IEndpointRouteBuilder MapPluginWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plugin-workers", (PluginWorkerSupervisor workers) => Results.Ok(workers.Status))
            .Produces<IReadOnlyList<PluginWorkerStatus>>();
        app.MapGet("/api/plugin-workers/performance", (PluginWorkerSupervisor workers) => Results.Ok(workers.Performance))
            .Produces<IReadOnlyList<PluginWorkerPerformanceProfile>>();

        app.MapPost("/api/plugin-workers/{pluginId}/restart", async (
            string pluginId,
            PluginWorkerSupervisor workers,
            ProductionRuntimeService production,
            CancellationToken cancellationToken) =>
        {
            if (production.Status.ProductionLocked)
                throw new ApiConflictException("Stop Production Runtime before restarting a plugin worker.");
            try { return Results.Ok(await workers.RestartAsync(pluginId, cancellationToken)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
        }).Produces<PluginWorkerStatus>().RequireEngineer("plugin-worker.restart", "plugin-worker");

        app.MapPost("/api/plugin-workers/{pluginId}/performance/reset", (
            string pluginId,
            PluginWorkerSupervisor workers) =>
        {
            try { return Results.Ok(workers.ResetPerformance(pluginId)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
        }).Produces<PluginWorkerPerformanceProfile>().RequireEngineer("plugin-worker.performance.reset", "plugin-worker");

        return app;
    }
}
