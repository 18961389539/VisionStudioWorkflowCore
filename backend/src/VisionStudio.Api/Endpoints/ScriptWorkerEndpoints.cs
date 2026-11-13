using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class ScriptWorkerEndpoints
{
    public static IEndpointRouteBuilder MapScriptWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/script-workers", (ScriptWorkerSupervisor workers) => Results.Ok(workers.Status))
            .Produces<PluginWorkerStatus>()
            .RequireEngineer("script.worker.status", "script");

        app.MapPost("/api/script-workers/restart", async (ScriptWorkerSupervisor workers, CancellationToken ct) =>
        {
            await workers.RestartAsync(ct);
            return Results.Ok(workers.Status);
        }).Produces<PluginWorkerStatus>()
          .RequireEngineer("script.worker.restart", "script");

        return app;
    }
}
