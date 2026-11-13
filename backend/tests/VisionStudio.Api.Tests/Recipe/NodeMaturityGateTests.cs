using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VisionStudio.Abstractions;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 2026-11-12 整改回归：发布门禁消费节点成熟度。
///
/// 背景：成熟度此前只是工具箱里的一个徽标。工程师把 <c>inference.obb</c>（无端到端验证）
/// 加进流程后能直接发布，发布之后这条信息就消失了——配方管理只显示"已验证 / 已发布"。
/// 数据集验证记录衡量的是准确率，不能替代"这个节点的算法路径从未被断言"。
///
/// 门禁口径：含 Preview / Experimental 节点的版本，发布时必须带 acknowledgeMaturityNodes=true，
/// 确认结果写进审计 target。回滚不设门禁（目标版本当初已确认过）。
/// </summary>
public sealed class NodeMaturityGateTests
{
    private static WorkflowDefinition Workflow(params (string Id, string Type)[] nodes) => new(
        "inspection",
        "Inspection",
        nodes.Select(n => new NodeDefinition(n.Id, n.Type, null, new NodePosition(0, 0), null)).ToList(),
        []);

    [Fact]
    public void Scan_ReturnsNonStableNodesWithReason()
    {
        var findings = NodeMaturityGate.Scan(Workflow(
            ("source", "image.synthetic"),
            ("detect", "inference.obb"),
            ("count", "value.counter")));

        Assert.Equal(2, findings.Count);
        // Experimental 排在被前面：现场先要看到风险最高的那条。
        Assert.Equal("inference.obb", findings[0].NodeType);
        Assert.Equal("Experimental", findings[0].Maturity);
        Assert.False(string.IsNullOrWhiteSpace(findings[0].Reason));
        Assert.Equal("value.counter", findings[1].NodeType);
        Assert.Equal("Preview", findings[1].Maturity);
    }

    [Fact]
    public void Scan_IgnoresStableNodes_AndEmptyWorkflows()
    {
        Assert.Empty(NodeMaturityGate.Scan(Workflow(("source", "image.synthetic"), ("crop", "image.crop"))));
        Assert.Empty(NodeMaturityGate.Scan(null));
        Assert.Empty(NodeMaturityGate.Scan(new WorkflowDefinition("w", "W", [], [])));
    }

    [Fact]
    public void Scan_TreatsScriptAsTrustedCodeBoundary()
    {
        // script.csharp 是进程内 trusted-code 运行时（denylist 不是 OS 沙箱）：
        // 发布时必须和其它非 Stable 节点一样被显式确认，不能静默带入产线。
        var finding = Assert.Single(NodeMaturityGate.Scan(Workflow(("script", "script.csharp"))));
        Assert.Equal("script.csharp", finding.NodeType);
        Assert.Equal("Preview", finding.Maturity);
        Assert.Contains("沙箱", finding.Reason);
    }

    [Fact]
    public void Scan_ReportsNodeNameForTheOperator()
    {
        var workflow = new WorkflowDefinition("inspection", "Inspection",
            [new NodeDefinition("detect", "inference.obb", "OBB 检测", new NodePosition(0, 0), null)], []);
        var finding = Assert.Single(NodeMaturityGate.Scan(workflow));
        // 现场按名字找节点，不按 id：两者都要给。
        Assert.Equal("OBB 检测", finding.NodeName);
        Assert.Equal("detect", finding.NodeId);
    }

    [Fact]
    public void Describe_SummarizesForAuditTarget()
    {
        var findings = NodeMaturityGate.Scan(Workflow(
            ("a", "inference.obb"),
            ("b", "inference.pose"),
            ("c", "value.counter")));
        var text = NodeMaturityGate.Describe(findings);
        Assert.Contains("experimental=2", text);
        Assert.Contains("preview=1", text);
        Assert.Contains("inference.obb", text);
    }

    [Fact]
    public async Task Publish_WithoutAcknowledgement_IsRejectedWithFindings()
    {
        using var factory = new RecipeApiFactory();
        using var client = factory.CreateClient();
        var jobId = await CreateJobWithVersionAsync(client,
            new[] { new { id = "source", type = "image.synthetic" }, new { id = "detect", type = "inference.obb" } });

        var publish = await client.PostAsync($"/api/jobs/{jobId}/versions/1/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);

        var body = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NODE_MATURITY_ACK_REQUIRED", body.GetProperty("code").GetString());
        var findings = body.GetProperty("findings");
        Assert.Equal(1, findings.GetArrayLength());
        Assert.Equal("inference.obb", findings[0].GetProperty("nodeType").GetString());
        Assert.Equal("Experimental", findings[0].GetProperty("maturity").GetString());
    }

    [Fact]
    public async Task Publish_WithAcknowledgement_PassesTheMaturityGate()
    {
        using var factory = new RecipeApiFactory();
        using var client = factory.CreateClient();
        var jobId = await CreateJobWithVersionAsync(client,
            new[] { new { id = "source", type = "image.synthetic" }, new { id = "detect", type = "inference.obb" } });

        var publish = await client.PostAsync($"/api/jobs/{jobId}/versions/1/publish?acknowledgeMaturityNodes=true", null);
        // 仍然可能是 Conflict（本版本没有通过数据集验证），但不能是成熟度门禁：
        // 这正是本用例要锁住的"确认之后门禁必须放行"。
        var text = await publish.Content.ReadAsStringAsync();
        Assert.DoesNotContain("NODE_MATURITY_ACK_REQUIRED", text);
        Assert.DoesNotContain("inference.obb", text);
    }

    [Fact]
    public async Task Publish_StableWorkflow_IsNotGatedByMaturity()
    {
        using var factory = new RecipeApiFactory();
        using var client = factory.CreateClient();
        var jobId = await CreateJobWithVersionAsync(client, new[] { new { id = "source", type = "image.synthetic" } });

        var publish = await client.PostAsync($"/api/jobs/{jobId}/versions/1/publish", null);
        var text = await publish.Content.ReadAsStringAsync();
        Assert.DoesNotContain("NODE_MATURITY_ACK_REQUIRED", text);
    }

    private static async Task<string> CreateJobWithVersionAsync(HttpClient client, object[] nodes)
    {
        // 校验要求必填图像输入必须接线：source → 第一个带 image 输入的节点。
        var edges = nodes.Length > 1
            ? new object[] { new { id = "e1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "detect", targetPort = "image" } }
            : Array.Empty<object>();
        var workflow = new { id = "inspection", name = "Inspection", nodes, edges };
        var create = await client.PostAsJsonAsync("/api/recipes", new { name = "Maturity gate", workflow });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var recipe = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = recipe.GetProperty("id").GetString()!;
        // 首次创建即 V1：直接用它做发布对象。
        return id;
    }

    private sealed class RecipeApiFactory : WebApplicationFactory<Program>
    {
        private readonly TempWebHostEnvironment _environment = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(_environment.ContentRootPath);
            builder.UseSetting("Security:Enabled", "false");
            builder.UseSetting("RuntimeOnly", "false");
            builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _environment.Dispose();
        }
    }
}
