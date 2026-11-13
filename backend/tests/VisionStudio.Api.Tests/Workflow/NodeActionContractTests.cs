using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 扩展安全契约回归（2026-10-11 复审「插件动作契约未统一」整改）：
/// 节点动作（SideEffectKind）、资源请求与完成证据必须由**目录声明**驱动，
/// 插件与内置节点走同一判定——不再由宿主硬编码节点字符串或 SupportsRunNode 能力位替代。
/// </summary>
public sealed class NodeActionContractTests
{
    private sealed class StubExecutor(string type) : IVisionNodeExecutor
    {
        public string Type => type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }

    private static NodeCatalogItem Register(
        VisionNodeRegistry registry,
        string type,
        IReadOnlyList<NodeActionDeclaration>? actions,
        IReadOnlyList<NodeResourceDeclaration>? resources = null)
    {
        var catalog = new NodeCatalogItem(
            type, type, "Plugin", [], [], [], PluginId: "test-plugin", Actions: actions, Resources: resources);
        registry.Register(catalog, new StubExecutor(type), "test-plugin");
        return catalog;
    }

    [Fact]
    public void DeclaredActions_ClassifyPluginNodes_LikeBuiltInNodes()
    {
        var registry = new VisionNodeRegistry();
        Register(registry, "plugin.plcWrite", [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)]);
        Register(registry, "plugin.pure", []);
        Register(registry, "plugin.undeclared", actions: null);

        // 显式声明：非空 = 动作，空列表 = 明确无副作用（不再看 SupportsRunNode / 节点字符串）。
        Assert.True(DeviceSideEffectClassifier.IsDeviceSideEffectNode("plugin.plcWrite", registry));
        Assert.False(DeviceSideEffectClassifier.IsDeviceSideEffectNode("plugin.pure", registry));
        // 未声明目录走兼容回退（未知插件类型不在内置分类里）。
        Assert.False(DeviceSideEffectClassifier.IsDeviceSideEffectNode("plugin.undeclared", registry));

        // 内置动作节点走同一声明判定；无注册表时保持既有兼容分类（旧调用点行为不变）。
        Assert.True(DeviceSideEffectClassifier.IsDeviceSideEffectNode("device.writeTag", registry));
        Assert.True(DeviceSideEffectClassifier.IsDeviceSideEffectNode("device.writeTag"));
        Assert.True(DeviceSideEffectClassifier.IsDeviceSideEffectNode("robot.executeTarget"));
        Assert.False(DeviceSideEffectClassifier.IsDeviceSideEffectNode("device.readTag", registry));
    }

    [Fact]
    public void DeclaredRobotCommand_ConsumesCompletionEvidenceContract()
    {
        var protocol = new VisionNodeRegistry();
        Register(protocol, "plugin.robotMove", [new(NodeActionKind.RobotCommand, NodeActionCompletion.ProtocolConfirmed)]);
        var synchronous = new VisionNodeRegistry();
        Register(synchronous, "plugin.robotMove", [new(NodeActionKind.RobotCommand, NodeActionCompletion.Synchronous)]);

        var workflow = new WorkflowDefinition(
            "contract-probe",
            "Contract Probe",
            [new NodeDefinition("p1", "plugin.robotMove", "Move", null, null)],
            []);
        // 节点返回成功但没有任何协议证据（无 inPosition/handshake 快照）。
        var result = new WorkflowRunResult("run-contract", true, 1, false, 0, 0,
            [new NodeRunReport("p1", "plugin.robotMove", true, 1, new Dictionary<string, object?>(), Phase: NodeExecutionPhase.Run)],
            [], null, QualityDisposition: "OK");

        // 完成证据契约：ProtocolConfirmed 缺证据 ⇒ 保守要求核对（插件的机器人指令不再漏判）。
        Assert.True(DeviceSideEffectClassifier.RequiresManualReconciliation(workflow, result, protocol));
        // Synchronous 声明：调用返回即完成 ⇒ 不要求核对。
        Assert.False(DeviceSideEffectClassifier.RequiresManualReconciliation(workflow, result, synchronous));
    }

    [Fact]
    public void DebugApproval_PrefersDeclaredActions_OverCapabilityBit()
    {
        var registry = new VisionNodeRegistry();
        Register(registry, "plugin.plcWrite", [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)]);
        Register(registry, "plugin.pure", []);
        Register(registry, "plugin.undeclared", actions: null);
        // 未声明目录 + SupportsRunNode=false ⇒ 回退旧能力位（兼容旧插件目录）。
        var legacySideEffect = new NodeCatalogItem(
            "plugin.legacy", "Legacy", "Plugin", [], [], [],
            Capabilities: new VisionToolCapabilities(SupportsRunNode: false));
        registry.Register(legacySideEffect, new StubExecutor("plugin.legacy"), "test-plugin");
        registry.Register(BuiltInNodeCatalog.Require("device.writeTag"), new StubExecutor("device.writeTag"), "builtin");

        var cache = new WorkflowPlanCache(null!, null!, null!, registry);

        Assert.True(cache.RequiresSideEffectApproval("plugin.plcWrite"));   // 声明动作
        Assert.False(cache.RequiresSideEffectApproval("plugin.pure"));      // 声明纯净
        Assert.False(cache.RequiresSideEffectApproval("plugin.undeclared"));// 未声明且无旧能力位
        Assert.True(cache.RequiresSideEffectApproval("plugin.legacy"));     // 未声明 ⇒ 回退 SupportsRunNode=false
        Assert.True(cache.RequiresSideEffectApproval("device.writeTag"));   // 内置声明
    }

    [Fact]
    public async Task DeclaredResources_ReplaceParameterNameConvention()
    {
        var registry = new VisionNodeRegistry();
        // 插件用非约定参数名 plcId 承载设备资源——只有声明能让仲裁/依赖捕获看到它。
        Register(registry,
            "plugin.plcWrite",
            [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            [new("device", "plcId")]);

        var workflow = new WorkflowDefinition(
            "resource-probe",
            "Resource Probe",
            [new NodeDefinition("p1", "plugin.plcWrite", "Write", null,
                new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["plcId"] = System.Text.Json.JsonSerializer.SerializeToElement("virtual-modbus-1")
                })],
            []);

        var manifest = new RuntimeDependencyManifestService(
            registry,
            plugins: null!,
            cameras: null!,
            devices: null!,
            deviceProfiles: null!,
            robots: null!,
            calibrations: null!,
            hardwareProvenance: null!);

        var references = await manifest.ExtractReferencesAsync(workflow, default);
        Assert.Contains("virtual-modbus-1", references.Devices);
        Assert.Empty(references.Cameras);
        Assert.Empty(references.Robots);

        // 未知资源类别 ⇒ 失败关闭（目录契约写错必须暴露，而不是静默漏掉资源）。
        Register(registry, "plugin.bad", null, [new("plc", "plcId")]);
        var broken = new WorkflowDefinition(
            "resource-probe-broken",
            "Resource Probe Broken",
            [new NodeDefinition("p1", "plugin.bad", "Write", null, null)],
            []);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manifest.ExtractReferencesAsync(broken, default));
    }
}
