using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Debug session API smoke: run to the first breakpoint, keep the live scene, continue to
/// completion (skipping completed nodes), inspect and release the session.
/// </summary>
public sealed class DebugSessionApiTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public DebugSessionApiTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task DebugSession_RunToBreakpoint_ContinueToCompletion_ThenDelete()
    {
        var create = await _client.PostAsJsonAsync("/api/debug/sessions", new
        {
            workflow = BreakpointWorkflow(),
            options = new { mode = "Breakpoints", breakpoints = new[] { "threshold" } }
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var sessionId = created.RootElement.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal("Breakpoint", created.RootElement.GetProperty("debugState").GetString());
        Assert.Equal("threshold", created.RootElement.GetProperty("haltNodeId").GetString());
        var reports = created.RootElement.GetProperty("nodeReports");
        Assert.Equal(1, reports.GetArrayLength());
        Assert.Equal("source", reports[0].GetProperty("nodeId").GetString());

        // 会话段同样捕获逐节点图像：首段只有已执行的 source（threshold 停而未执行）
        var createdRunId = created.RootElement.GetProperty("runId").GetString();
        var segmentImages = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{createdRunId}/images");
        var segmentNodes = segmentImages.GetProperty("images").EnumerateArray().Select(x => x.GetProperty("nodeId").GetString()).ToArray();
        Assert.Equal(new[] { "source" }, segmentNodes);

        var continueResponse = await _client.PostAsync($"/api/debug/sessions/{sessionId}/continue", null);
        Assert.Equal(HttpStatusCode.OK, continueResponse.StatusCode);
        using var continued = JsonDocument.Parse(await continueResponse.Content.ReadAsStringAsync());
        Assert.Equal("Complete", continued.RootElement.GetProperty("debugState").GetString());
        var reportsAfter = continued.RootElement.GetProperty("nodeReports");
        Assert.Equal(2, reportsAfter.GetArrayLength());
        Assert.Equal("threshold", reportsAfter[1].GetProperty("nodeId").GetString());

        // 续跑段累计两节点的图像（会话数据跨段累积）
        var continuedRunId = continued.RootElement.GetProperty("runId").GetString();
        var continuedImages = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{continuedRunId}/images");
        var continuedNodes = continuedImages.GetProperty("images").EnumerateArray()
            .Select(x => x.GetProperty("nodeId").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "source", "threshold" }, continuedNodes);

        var snapshot = await _client.GetFromJsonAsync<JsonElement>($"/api/debug/sessions/{sessionId}");
        Assert.Equal("Completed", snapshot.GetProperty("state").GetString());
        Assert.Equal(2, snapshot.GetProperty("executedNodeIds").GetArrayLength());

        var delete = await _client.DeleteAsync($"/api/debug/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await _client.GetAsync($"/api/debug/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    private static object BreakpointWorkflow() => new
    {
        id = "debug-session-smoke",
        name = "Debug Session Smoke",
        nodes = new object[]
        {
            new { id = "source", type = "image.synthetic", name = "Synthetic", parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 } },
            new { id = "threshold", type = "image.threshold", name = "Threshold", parameters = new { threshold = 100 } }
        },
        edges = new object[]
        {
            new { id = "c1", sourceNodeId = "source", sourcePort = "next", targetNodeId = "threshold", targetPort = "exec", kind = "control" },
            new { id = "d1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "threshold", targetPort = "image", kind = "data" }
        }
    };
}
