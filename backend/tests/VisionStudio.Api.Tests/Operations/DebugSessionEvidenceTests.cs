using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace VisionStudio.Api.Tests;

/// <summary>
/// A03 回归（2026-10-11 复审）：调试动作的追溯证据必须与意图闭合绑定。
/// 反例一：Create 到断点前已执行 PLC 写，但尚未提交运行追溯就 Complete()（durableTraceCount=0）。
/// 反例二：Continue 的追溯提交失败被吞掉后照常 Complete() 并返回成功——实际写了设备却
/// 无追溯、无待核对意图，调用方仍看到成功（探针：continue=200 intents=0 durableTraceCount=0）。
/// </summary>
public sealed class DebugSessionEvidenceTests
{
    private static JsonNode WriteThenBreakWorkflow(string value)
        => JsonNode.Parse("""
            {"id":"debug-evidence","name":"Debug evidence","nodes":[
              {"id":"write","type":"device.writeTag","name":"Write","parameters":{"deviceId":"virtual-modbus-1","tagId":"statusText","fallbackValue":"__VALUE__"}},
              {"id":"tail","type":"coordinate.pose","name":"Tail"}],
             "edges":[{"id":"c1","sourceNodeId":"write","sourcePort":"next","targetNodeId":"tail","targetPort":"exec","kind":"control"}]}
            """.Replace("__VALUE__", value))!;

    [Fact]
    public async Task DebugCreate_PersistsFragmentEvidence_BeforeClosingIntent()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/debug/sessions", new
        {
            workflow = WriteThenBreakWorkflow("DEBUG-WRITE"),
            options = new { mode = "Breakpoints", breakpoints = new[] { "tail" }, allowSideEffects = true }
        });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await create.Content.ReadAsStringAsync())!;
        Assert.True(body["success"]!.GetValue<bool>());

        // 断点前已执行的设备写必须留下耐久追溯（旧实现：Create 从不提交，durableTraceCount=0）。
        var sessionId = body["sessionId"]!.GetValue<string>();
        var traces = await client.GetFromJsonAsync<JsonArray>("/api/traces");
        Assert.NotNull(traces);
        Assert.NotEmpty(traces);
        Assert.Contains(sessionId, traces!.ToJsonString()); // 片段记录保留会话关联

        // 动作结果确认 + 证据提交成功 ⇒ 意图闭合。
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        Assert.Empty(store.Load());

        await client.DeleteAsync($"/api/debug/sessions/{sessionId}");
    }

    [Fact]
    public async Task DebugEvidenceSubmissionFailure_KeepsIntentUnknown_AndFailsTheCall()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/debug/sessions", new
        {
            workflow = WriteThenBreakWorkflow("DEBUG-TRACE-FAIL"),
            options = new { mode = "Breakpoints", breakpoints = new[] { "write" }, allowSideEffects = true }
        });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await create.Content.ReadAsStringAsync())!;
        var sessionId = body["sessionId"]!.GetValue<string>();

        // 注入 run_traces 提交失败（与探针一致：BEFORE INSERT 触发器 ABORT）。
        await using (var connection = await factory.Services.GetRequiredService<SqliteMetadataDatabase>().OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER probe_block_trace BEFORE INSERT ON run_traces BEGIN SELECT RAISE(ABORT,'probe trace commit denied'); END;";
            await command.ExecuteNonQueryAsync();
        }

        // Continue 实际执行了 PLC 写，但证据提交失败 ⇒ 必须传播失败状态（旧实现：200 + 假成功）。
        var continued = await client.PostAsync($"/api/debug/sessions/{sessionId}/continue", null);
        Assert.True((int)continued.StatusCode >= 500, $"expected failure, got {(int)continued.StatusCode}");

        // 意图保留为 Unknown ⇒ 下一动作阻断（旧实现：intents=0、下一动作放行）。
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        Assert.NotEmpty(store.Load());
        var blocked = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        await client.DeleteAsync($"/api/debug/sessions/{sessionId}");
    }
}
