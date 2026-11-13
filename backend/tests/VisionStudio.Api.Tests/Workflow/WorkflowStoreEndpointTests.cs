using System.Net;
using System.Net.Http.Json;
using VisionStudio.Api.Infrastructure;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 2026-10-13 全面审查 Q05/Q06：工作流保存端点的基线与提交事实契约。
/// 宿主以 Security 禁用运行（与审查探针同构），验证路由层行为：
///   · Q05：交互 PUT 缺基线 ⇒ 400；强制覆盖走管理员端点且必须带原因；
///   · Q06：修订落定失败 ⇒ 204 + X-Workflow-Revision-Deferred（内容已提交、反馈一致），
///     而不是"HTTP 500 + 前端提示未写入"的旧不一致。
/// </summary>
public sealed class WorkflowStoreEndpointTests
{
    [Fact]
    public async Task Put_WithoutBaseline_IsRejectedWithClearError()
    {
        // Q05：省略 expectedRevision 不得获得无基线覆盖能力（探针曾复现 204 覆盖）。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/workflows/q05-doc", new
        {
            id = "q05-doc",
            name = "V1",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("expectedRevision", body, StringComparison.OrdinalIgnoreCase);
        // 文档不应被创建。
        var loaded = await client.GetAsync("/api/workflows/q05-doc");
        Assert.Equal(HttpStatusCode.NotFound, loaded.StatusCode);
    }

    [Fact]
    public async Task Put_WithBaseline_Saves_And_ForceOverwrite_RequiresReason()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        // 显式空基线 = "期望新建"：成功并返回新修订。
        var created = await client.PutAsJsonAsync("/api/workflows/q05-doc?expectedRevision=", new
        {
            id = "q05-doc",
            name = "V1",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var baseline = created.Headers.GetValues("X-Workflow-Revision").Single();

        // 携带最新基线的保存成功；旧基线再保存 ⇒ 409。
        var updated = await client.PutAsJsonAsync($"/api/workflows/q05-doc?expectedRevision={baseline}", new
        {
            id = "q05-doc",
            name = "V2",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var stale = await client.PutAsJsonAsync($"/api/workflows/q05-doc?expectedRevision={baseline}", new
        {
            id = "q05-doc",
            name = "V3",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Q05：强制覆盖端点缺原因 ⇒ 400。
        var noReason = await client.PostAsJsonAsync("/api/workflows/q05-doc/overwrite", new
        {
            workflow = new { id = "q05-doc", name = "V4", nodes = Array.Empty<object>(), edges = Array.Empty<object>() },
            reason = "  "
        });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // 带原因的强制覆盖成功（本宿主 Security 禁用，管理员策略放行；生产需管理员角色）。
        var forced = await client.PostAsJsonAsync("/api/workflows/q05-doc/overwrite", new
        {
            workflow = new { id = "q05-doc", name = "V4-forced", nodes = Array.Empty<object>(), edges = Array.Empty<object>() },
            reason = "accept the reviewed external revision"
        });
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);
        var loaded = await client.GetFromJsonAsync<WorkflowPayloadShape>("/api/workflows/q05-doc");
        Assert.Equal("V4-forced", loaded!.Name);
    }

    [Fact]
    public async Task Put_WhenRevisionFinalizeFails_ReportsDeferredCommitInsteadOfClaimingNotWritten()
    {
        // Q06：修订文件被占用时，旧实现内容已提交却返回 500、前端提示"未写入后端"。
        // 现在：204 + X-Workflow-Revision-Deferred，内容确实已提交，反馈与事实一致。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var created = await client.PutAsJsonAsync("/api/workflows/q06-doc?expectedRevision=", new
        {
            id = "q06-doc",
            name = "V1",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var baseline = created.Headers.GetValues("X-Workflow-Revision").Single();

        var revisionPath = Path.Combine(factory.ContentRoot, "data", "workflows", "q06-doc.revision.json");
        // 允许读取、阻止写入/替换——精确命中"修订落定失败"（与审查探针的锁模式一致）。
        using var lockHandle = new FileStream(revisionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var response = await client.PutAsJsonAsync($"/api/workflows/q06-doc?expectedRevision={baseline}", new
        {
            id = "q06-doc",
            name = "CONTENT-COMMITTED-REVISION-FAILED",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Workflow-Revision-Deferred", out var deferred));
        Assert.Equal("true", deferred.Single());

        // 内容已提交（探针场景的旧实现在这里内容已更新但 UI 说未写入）。
        var loaded = await client.GetFromJsonAsync<WorkflowPayloadShape>("/api/workflows/q06-doc");
        Assert.Equal("CONTENT-COMMITTED-REVISION-FAILED", loaded!.Name);

        lockHandle.Dispose();
        // 自愈后 GET 的修订与保存响应头一致（内容为权威）。
        var healed = await client.GetAsync("/api/workflows/q06-doc");
        Assert.Equal(response.Headers.GetValues("X-Workflow-Revision").Single(),
            healed.Headers.GetValues("X-Workflow-Revision").Single());
    }

    private sealed record WorkflowPayloadShape(string Id, string Name);
}
