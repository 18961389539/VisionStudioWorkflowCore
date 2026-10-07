using VisionStudio.Api.Security;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Endpoints;

public static class PluginBenchmarkEndpoints
{
    public static IEndpointRouteBuilder MapPluginBenchmarkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plugin-benchmarks", async (string? pluginId, PluginBenchmarkStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(pluginId, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/plugin-benchmarks", async (
            StartPluginBenchmarkRequest request,
            PluginPerformanceBenchmarkService service,
            ProductionRuntimeService production,
            CancellationToken ct) =>
        {
            if (production.Status.ProductionLocked)
                throw new ApiConflictException("Stop Production Runtime before starting a plugin performance benchmark.");
            return Results.Ok(await service.StartAsync(request, ct));
        }).RequireEngineer("plugin-benchmark.start", "plugin-benchmark");

        app.MapGet("/api/plugin-benchmarks/{runId}", async (string runId, PluginBenchmarkStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAsync(runId, true, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/plugin-benchmarks/{runId}/ci-spec", async (string runId, PluginBenchmarkStore store, CancellationToken ct) =>
        {
            var run = await store.GetAsync(runId, true, ct);
            return Results.Ok(PluginBenchmarkCiSpec.FromBaseline(run));
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/plugin-benchmarks/{runId}/cancel", async (
            string runId,
            PluginPerformanceBenchmarkService service,
            CancellationToken ct) =>
        {
            await service.CancelAsync(runId, ct);
            return Results.Accepted();
        }).RequireEngineer("plugin-benchmark.cancel", "plugin-benchmark");

        return app;
    }
}
