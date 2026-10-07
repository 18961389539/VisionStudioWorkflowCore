using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class WorkflowStoreEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowStoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/workflows/{id}", async (
            string id,
            WorkflowDefinition workflow,
            WorkflowStore store,
            CancellationToken ct) =>
        {
            try
            {
                await store.SaveAsync(id, workflow, ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                // ID 含非法字符等校验失败：明确 400，而不是让参数异常冒泡成 500
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireEngineer("workflow.save", "workflow");

        app.MapGet("/api/workflows/{id}", async (string id, WorkflowStore store, CancellationToken ct) =>
        {
            var workflow = await store.LoadAsync(id, ct);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        return app;
    }
}
