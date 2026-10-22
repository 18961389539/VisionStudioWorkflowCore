using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

/// <summary>
/// Stateful debug sessions: run to the first breakpoint, keep the live scene, then continue
/// (skipping completed nodes) or run single nodes from cached inputs.
/// </summary>
public static class DebugSessionEndpoints
{
    public static IEndpointRouteBuilder MapDebugSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/debug/sessions", async (
            DebugSessionCreateRequest request,
            DebugSessionService sessions,
            ProductionRuntimeService production,
            RunStore store,
            CancellationToken ct) =>
        {
            await production.EnsureWorkflowRunAllowedAsync(request.Workflow, ct);
            var (sessionId, result) = await sessions.CreateAsync(request.Workflow, request.Options, ct);
            return MapSessionRun(sessionId, "create", result, store);
        }).RequireEngineer("workflow.debug.session.create", "workflow");

        app.MapPost("/api/debug/sessions/{sessionId}/continue", async (
            string sessionId,
            DebugSessionService sessions,
            RunStore store,
            CancellationToken ct) =>
        {
            var result = await sessions.ContinueAsync(sessionId, ct);
            return MapSessionRun(sessionId, "continue", result, store);
        }).RequireEngineer("workflow.debug.session.continue", "workflow");

        app.MapPost("/api/debug/sessions/{sessionId}/run-node", async (
            string sessionId,
            DebugSessionRunNodeRequest request,
            DebugSessionService sessions,
            RunStore store,
            CancellationToken ct) =>
        {
            var result = await sessions.RunNodeAsync(sessionId, request.NodeId, ct);
            return MapSessionRun(sessionId, "run-node", result, store);
        }).RequireEngineer("workflow.debug.session.run-node", "workflow");

        app.MapGet("/api/debug/sessions/{sessionId}", async (
            string sessionId,
            DebugSessionService sessions,
            CancellationToken ct) =>
        {
            var snapshot = await sessions.SnapshotAsync(sessionId, ct);
            return Results.Ok(new
            {
                snapshot.SessionId,
                snapshot.State,
                snapshot.HaltNodeId,
                snapshot.HaltReason,
                snapshot.QualityDisposition,
                snapshot.Error,
                reportCount = snapshot.Reports.Count,
                snapshot.Reports,
                snapshot.Overlays,
                snapshot.PreviewWidth,
                snapshot.PreviewHeight,
                snapshot.ExecutedNodeIds,
                snapshot.CreatedAt,
                snapshot.LastActivityAt
            });
        }).RequireEngineer("workflow.debug.session.read", "workflow");

        app.MapDelete("/api/debug/sessions/{sessionId}", async (
            string sessionId,
            DebugSessionService sessions) =>
            await sessions.DeleteAsync(sessionId) ? Results.NoContent() : Results.NotFound())
            .RequireEngineer("workflow.debug.session.delete", "workflow");

        return app;
    }

    private static IResult MapSessionRun(string sessionId, string operation, WorkflowRunResult result, RunStore store)
    {
        store.Put(result);
        var response = new
        {
            sessionId,
            operation,
            result.RunId,
            result.Success,
            result.TotalDurationMs,
            result.PreviewAvailable,
            result.PreviewWidth,
            result.PreviewHeight,
            result.Overlays,
            result.NodeReports,
            result.Error,
            result.DebugState,
            result.HaltNodeId,
            result.HaltReason,
            result.QualityDisposition,
            result.ControlFlowDecisions,
            engine = "Workflow Core + Recursive Structured IR + VisionPipelineExecutor"
        };
        return result.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
}
