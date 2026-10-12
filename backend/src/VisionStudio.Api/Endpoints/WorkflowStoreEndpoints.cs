using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class WorkflowStoreEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowStoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/workflows/{id}", async (
            string id,
            WorkflowDefinition workflow,
            string? expectedRevision,
            WorkflowStore store,
            HttpContext http,
            CancellationToken ct) =>
        {
            try
            {
                // R07：expectedRevision 可选——显式提供时做乐观并发校验（不一致 ⇒ 409）；
                // 响应头返回写入后的新修订，供客户端作为下一次保存的并发基线。
                var revision = await store.SaveAsync(id, workflow, ct, expectedRevision);
                http.Response.Headers["X-Workflow-Revision"] = revision;
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                // ID 含非法字符等校验失败：明确 400，而不是让参数异常冒泡成 500
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireEngineer("workflow.save", "workflow");

        app.MapGet("/api/workflows/{id}", async (string id, WorkflowStore store, HttpContext http, CancellationToken ct) =>
        {
            var workflow = await store.LoadAsync(id, ct);
            if (workflow is null) return Results.NotFound();
            // R07：当前修订随 GET 输出——客户端保存时原样带回（expectedRevision），并发冲突可见。
            if (await store.GetRevisionAsync(id, ct) is { } revision)
                http.Response.Headers["X-Workflow-Revision"] = revision;
            return Results.Ok(workflow);
        });

        return app;
    }
}
