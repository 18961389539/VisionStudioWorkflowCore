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
            // Q05（2026-10-13 审查）：交互保存必须携带并发基线。旧实现把 expectedRevision 设为
            // 可选查询参数——普通工程师路由上省略它即可无基线覆盖（探针复现：省略参数 PUT 得到
            // 204 并实际覆盖了文档）。缺基线 ⇒ 明确 400；强制覆盖走管理员专用端点（授权 + 原因
            // + 审计），普通保存不能通过"省参数"切换语义。
            if (expectedRevision is null)
            {
                return Results.BadRequest(new
                {
                    error = "expectedRevision is required for interactive saves. " +
                            "Load the latest version to obtain the baseline, or use the administrator force-overwrite endpoint with a reason."
                });
            }
            try
            {
                // R07：expectedRevision 显式提供时做乐观并发校验（不一致 ⇒ 409）；
                // 响应头返回写入后的新修订，供客户端作为下一次保存的并发基线。
                var outcome = await store.SaveAsync(id, workflow, ct, expectedRevision);
                http.Response.Headers["X-Workflow-Revision"] = outcome.Revision;
                // Q06：修订记录延迟落定时如实通告（内容已提交、读取路径自愈对齐），
                // 前端据此给出"已保存、版本基线将自动对齐"的一致反馈。
                if (outcome.RevisionRecordDeferred)
                    http.Response.Headers["X-Workflow-Revision-Deferred"] = "true";
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                // ID 含非法字符等校验失败：明确 400，而不是让参数异常冒泡成 500
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireEngineer("workflow.save", "workflow");

        // Q05：强制覆盖专用入口——仅管理员、必须给出原因、全程审计。语义上等价于
        // "无基线覆盖"，但从普通保存路径剥离：工程师角色不再能通过省略参数获得该能力。
        app.MapPost("/api/workflows/{id}/overwrite", async (
            string id,
            WorkflowForceOverwriteRequest request,
            WorkflowStore store,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
                return Results.BadRequest(new { error = "A non-empty reason is required for a forced overwrite." });
            AuditContext.SetTarget(http, $"force-overwrite: {request.Reason.Trim()}");
            try
            {
                var outcome = await store.SaveAsync(id, request.Workflow, ct, expectedRevision: null);
                http.Response.Headers["X-Workflow-Revision"] = outcome.Revision;
                if (outcome.RevisionRecordDeferred)
                    http.Response.Headers["X-Workflow-Revision-Deferred"] = "true";
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAdministrator("workflow.force-overwrite", "workflow");

        app.MapGet("/api/workflows/{id}", async (string id, WorkflowStore store, HttpContext http, CancellationToken ct) =>
        {
            // R07/F04：内容与修订在同一临界区读取（同代）——响应头与 body 永远匹配；
            // 修订漂移（保存中断/外部改动）以内容为权威自愈后再返回。
            var loaded = await store.LoadWithRevisionAsync(id, ct);
            if (loaded is not { } found) return Results.NotFound();
            if (found.Revision is { } revision)
                http.Response.Headers["X-Workflow-Revision"] = revision;
            return Results.Ok(found.Workflow);
        });

        return app;
    }

    /// <summary>Q05：管理员强制覆盖请求体——文档 + 必填原因（原因进入审计目标）。</summary>
    public sealed record WorkflowForceOverwriteRequest(WorkflowDefinition Workflow, string Reason);
}
