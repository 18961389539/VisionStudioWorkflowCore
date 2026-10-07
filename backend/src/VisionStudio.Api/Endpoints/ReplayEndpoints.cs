using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class ReplayEndpoints
{
    public static IEndpointRouteBuilder MapReplayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/traces/{runId}/replay/context", async (string runId, OfflineReplayService replay, CancellationToken ct) =>
            Results.Ok(await replay.GetContextAsync(runId, ct)))
            .RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/traces/{runId}/replay/input", (string runId, TraceabilityStore traces) =>
        {
            var path = traces.FindReplayInputPath(runId);
            return path is null ? Results.NotFound() : Results.File(path, "image/png");
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/traces/{runId}/replay", async (string runId, OfflineReplayRequest request, OfflineReplayService replay, CancellationToken ct) =>
        {
            var result = await replay.ExecuteAsync(runId, request, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireEngineer("trace.replay.execute", "trace");

        return app;
    }
}
