using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// /api/validate 与 /api/compile 的响应契约（2026-11-10 具名化 + warnings）。
/// 编译器单测覆盖提示生成，这里守住真实 Minimal API 的 JSON 形状（camelCase、400 失败体、dslJson）。
/// </summary>
public sealed class WorkflowContractApiTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public WorkflowContractApiTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    private static object ParallelWorkflow() => new
    {
        id = "api-contract-parallel",
        name = "API Contract Parallel",
        nodes = new[]
        {
            new { id = "p", type = "flow.parallel" },
            new { id = "n1", type = "value.number" },
            new { id = "rng1", type = "value.range" },
            new { id = "r1", type = "flow.result" },
            new { id = "n2", type = "value.number" },
            new { id = "rng2", type = "value.range" },
            new { id = "r2", type = "flow.result" },
            new { id = "j", type = "flow.join" }
        },
        edges = new[]
        {
            new { id = "e1", sourceNodeId = "p", sourcePort = "branch1", targetNodeId = "n1", targetPort = "exec", kind = "control" },
            new { id = "e2", sourceNodeId = "n1", sourcePort = "next", targetNodeId = "rng1", targetPort = "exec", kind = "control" },
            new { id = "e3", sourceNodeId = "rng1", sourcePort = "next", targetNodeId = "r1", targetPort = "exec", kind = "control" },
            new { id = "e4", sourceNodeId = "r1", sourcePort = "next", targetNodeId = "j", targetPort = "branch1", kind = "control" },
            new { id = "e5", sourceNodeId = "p", sourcePort = "branch2", targetNodeId = "n2", targetPort = "exec", kind = "control" },
            new { id = "e6", sourceNodeId = "n2", sourcePort = "next", targetNodeId = "rng2", targetPort = "exec", kind = "control" },
            new { id = "e7", sourceNodeId = "rng2", sourcePort = "next", targetNodeId = "r2", targetPort = "exec", kind = "control" },
            new { id = "e8", sourceNodeId = "r2", sourcePort = "next", targetNodeId = "j", targetPort = "branch2", kind = "control" },
            new { id = "d1", sourceNodeId = "n1", sourcePort = "value", targetNodeId = "rng1", targetPort = "value", kind = "data" },
            new { id = "d2", sourceNodeId = "rng1", sourcePort = "pass", targetNodeId = "r1", targetPort = "pass", kind = "data" },
            new { id = "d3", sourceNodeId = "n2", sourcePort = "value", targetNodeId = "rng2", targetPort = "value", kind = "data" },
            new { id = "d4", sourceNodeId = "rng2", sourcePort = "pass", targetNodeId = "r2", targetPort = "pass", kind = "data" }
        }
    };

    [Fact]
    public async Task Validate_ReturnsTypedSuccessShapeAndParallelSequentialWarning()
    {
        var response = await _client.PostAsJsonAsync("/api/validate", ParallelWorkflow());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("workflowCoreId").GetString()));
        Assert.Equal(JsonValueKind.Array, body.GetProperty("registeredNodeTypes").ValueKind);
        Assert.True(body.GetProperty("pipelineSegmentCount").GetInt32() >= 1);

        var warning = Assert.Single(body.GetProperty("warnings").EnumerateArray());
        Assert.Equal("flow.parallel.sequential", warning.GetProperty("code").GetString());
        Assert.Equal("p", warning.GetProperty("nodeId").GetString());
        Assert.Contains("runs both branches sequentially", warning.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Validate_InvalidWorkflow_Returns400WithValidFalseAndError()
    {
        var response = await _client.PostAsJsonAsync("/api/validate", new
        {
            id = "api-contract-invalid",
            name = "Invalid",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.Contains("no nodes", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Compile_ReturnsTypedFieldsAndParsableDsl()
    {
        var response = await _client.PostAsJsonAsync("/api/compile", ParallelWorkflow());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("dslJson").GetString()));
        using var dsl = JsonDocument.Parse(body.GetProperty("dslJson").GetString()!);
        Assert.True(dsl.RootElement.TryGetProperty("Steps", out _));
        Assert.Equal("p", Assert.Single(body.GetProperty("warnings").EnumerateArray()).GetProperty("nodeId").GetString());
    }
}
