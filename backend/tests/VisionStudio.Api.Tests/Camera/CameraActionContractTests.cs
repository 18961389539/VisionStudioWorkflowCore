using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

/// <summary>
/// R02 回归（2026-10-11 复审）：工作流相机触发遗漏安全分类、意图和派发校验。
/// 反例：未闭合 PLC 意图存在时，带 triggerBeforeGrab=true 的取图工作流仍返回 200 成功
/// （探针：status=200 success=true unresolved=1）。现在与手动/同步触发共用同一动作协议。
/// </summary>
public sealed class CameraActionContractTests
{
    private sealed class StubExecutor(string type) : IVisionNodeExecutor
    {
        public string Type => type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }

    private static VisionNodeRegistry BuiltInRegistry()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.acquire"), new StubExecutor("image.acquire"), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("camera.syncCapture"), new StubExecutor("camera.syncCapture"), "builtin");
        return registry;
    }

    private static NodeDefinition Node(string type, Dictionary<string, JsonElement>? parameters = null)
        => new("n1", type, "Node", null, parameters);

    [Fact]
    public void ImageAcquire_IsActionOnly_WhenTriggerBeforeGrabIsEnabled()
    {
        var registry = BuiltInRegistry();
        var triggering = Node("image.acquire", new() { ["triggerBeforeGrab"] = JsonSerializer.SerializeToElement(true) });
        var passive = Node("image.acquire", new() { ["triggerBeforeGrab"] = JsonSerializer.SerializeToElement(false) });
        var absent = Node("image.acquire"); // 目录默认 triggerBeforeGrab=false

        // 主动触发是设备动作；纯取图保持只读语义（参数条件按节点有效值求值）。
        Assert.True(DeviceSideEffectClassifier.IsDeviceSideEffectNode(triggering, registry));
        Assert.False(DeviceSideEffectClassifier.IsDeviceSideEffectNode(passive, registry));
        Assert.False(DeviceSideEffectClassifier.IsDeviceSideEffectNode(absent, registry));

        Assert.True(DeviceSideEffectClassifier.HasDeviceSideEffects(
            new WorkflowDefinition("w-trigger", "W", [triggering], []), registry));
        Assert.False(DeviceSideEffectClassifier.HasDeviceSideEffects(
            new WorkflowDefinition("w-passive", "W", [passive], []), registry));

        // 同步采集总会下发设备端 Action Command：恒为设备动作。
        Assert.True(DeviceSideEffectClassifier.IsDeviceSideEffectNode(Node("camera.syncCapture"), registry));
    }

    [Fact]
    public void DebugApproval_IsNodeAware_ForConditionedCameraTrigger()
    {
        var registry = BuiltInRegistry();
        var cache = new WorkflowPlanCache(null!, null!, null!, registry);

        Assert.True(cache.RequiresSideEffectApproval(Node("image.acquire",
            new() { ["triggerBeforeGrab"] = JsonSerializer.SerializeToElement(true) })));
        Assert.False(cache.RequiresSideEffectApproval(Node("image.acquire",
            new() { ["triggerBeforeGrab"] = JsonSerializer.SerializeToElement(false) })));
        Assert.False(cache.RequiresSideEffectApproval(Node("image.acquire")));
        Assert.True(cache.RequiresSideEffectApproval(Node("camera.syncCapture")));
    }

    [Fact]
    public async Task WorkflowCameraTrigger_IsBlocked_WhileAnIntentIsUnresolved()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var auth = factory.Services.GetRequiredService<DeviceActionAuthorizationService>();
        using (var intent = auth.BeginManualIntent("previous-device-write", ["virtual-modbus-1"], []))
            intent.MarkUnknown("pending physical reconciliation");

        // triggerBeforeGrab=true → 主动触发 → 与手动/同步入口一致拒绝（探针反例：200）。
        var triggering = await client.PostAsJsonAsync("/api/run", Workflow(trigger: true));
        Assert.Equal(HttpStatusCode.Conflict, triggering.StatusCode);
        var blocked = await triggering.Content.ReadAsStringAsync();
        Assert.Contains("device-action", blocked, StringComparison.OrdinalIgnoreCase);

        // 纯取图（triggerBeforeGrab=false）：只读语义保持可用，不被动作闸门阻断。
        var passive = await client.PostAsJsonAsync("/api/run", Workflow(trigger: false));
        Assert.NotEqual(HttpStatusCode.Conflict, passive.StatusCode);
        Assert.Contains("\"success\":true", await passive.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SuccessfulCameraTrigger_RegistersAndClosesItsIntent()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();

        var response = await client.PostAsJsonAsync("/api/run", Workflow(trigger: true));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        // 主动触发成功（Synchronous 完成：帧到达即闭合）⇒ 意图不残留，后续动作放行。
        Assert.Empty(store.Load());
        var write = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "after-camera" } });
        Assert.NotEqual(HttpStatusCode.Conflict, write.StatusCode);
    }

    private static JsonNode Workflow(bool trigger)
        => JsonNode.Parse("""
            {"id":"camera-action-probe","name":"Camera Action Probe","nodes":[
              {"id":"acquire","type":"image.acquire","name":"Acquire","parameters":{
                "cameraId":"virtual-1","frameMode":"Latest","timeoutMs":1500,"autoStart":true,"triggerBeforeGrab":__TRIGGER__}}],
             "edges":[]}
            """.Replace("__TRIGGER__", trigger ? "true" : "false"))!;
}