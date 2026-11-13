using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 子流程调用（module.call）的**执行**覆盖。
///
/// 既有 WorkflowModuleTests 覆盖到"提取 → 钉住版本 → 展开 → 编译"，但没有验证展开后的图真的能跑。
/// 这里补两条：模块调用在宿主里端到端执行；以及对模块节点做单步调试时的显式拒绝。
/// </summary>
public sealed class ModuleCallExecutionTests
{
    [Fact]
    public async Task ModuleCall_RunsExpandedBodyInsideHost()
    {
        using var env = new TempWebHostEnvironment();
        var registry = Registry();
        var (store, expander, authoring) = Runtime(env, registry);
        var extracted = await authoring.ExtractAsync(new(
            HostWorkflow(128), ["threshold"], "threshold-stage", "Threshold Stage", null, null), default);

        var callId = extracted.ReplacementWorkflow.Nodes.Single(x => x.Type == "module.call").Id;
        await using var provider = Provider(registry, store);
        var runner = new ModuleAwareVisionWorkflowRunner(expander, provider.GetRequiredService<WorkflowCoreVisionRunner>());

        var result = await runner.RunAsync(extracted.ReplacementWorkflow);

        Assert.True(result.Success, result.Error);
        // 内部节点以 "实例ID::内部ID" 出现在报告中，宿主节点仍在。
        Assert.Contains(result.NodeReports, report => report.NodeId.EndsWith("::threshold", StringComparison.Ordinal));
        Assert.Contains(result.NodeReports, report => report.NodeId == "source");
        Assert.Contains(result.NodeReports, report => report.NodeId == "blob");
        // 调用节点本身在展开后被替换，不应出现在运行报告里。
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == callId);
    }

    [Fact]
    public async Task ModuleCall_SingleNodeDebugIsRejectedWithActionableMessage()
    {
        using var env = new TempWebHostEnvironment();
        var registry = Registry();
        var (store, expander, authoring) = Runtime(env, registry);
        var extracted = await authoring.ExtractAsync(new(
            HostWorkflow(128), ["threshold"], "threshold-stage", "Threshold Stage", null, null), default);
        var callId = extracted.ReplacementWorkflow.Nodes.Single(x => x.Type == "module.call").Id;

        await using var provider = Provider(registry, store);
        var runner = new ModuleAwareVisionWorkflowRunner(expander, provider.GetRequiredService<WorkflowCoreVisionRunner>());

        var result = await runner.RunAsync(extracted.ReplacementWorkflow, new VisionRunOptions(DebugRunMode.RunNode, TargetNodeId: callId));

        Assert.False(result.Success);
        Assert.Contains("expanded before execution", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.NodeReports);
    }

    private static (WorkflowModuleStore Store, WorkflowModuleExpander Expander, WorkflowModuleAuthoringService Authoring) Runtime(
        TempWebHostEnvironment env, VisionNodeRegistry registry)
    {
        var store = new WorkflowModuleStore(new SqliteMetadataDatabase(env));
        var expander = new WorkflowModuleExpander(store);
        var authoring = new WorkflowModuleAuthoringService(store, registry, expander, new VisionWorkflowCompiler(registry));
        return (store, expander, authoring);
    }

    private static ServiceProvider Provider(VisionNodeRegistry registry, WorkflowModuleStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        services.AddSingleton(registry);
        services.AddSingleton(store);
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddTransient<VisionLoopStep>();
        services.AddSingleton<WorkflowPlanCache>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        services.AddSingleton<WorkflowDebugSessionService>();
        return services.BuildServiceProvider();
    }

    private static VisionNodeRegistry Registry()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new SyntheticImageNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("image.threshold"), new ThresholdNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("measure.blobs"), new MultiBlobNode(), "builtin");
        return registry;
    }

    /// <summary>宿主流程：合成图 → 阈值（将被提取为子流程）→ 连通域。</summary>
    private static WorkflowDefinition HostWorkflow(double threshold)
        => new("module-host", "Module Host",
            [
                new NodeDefinition("source", "image.synthetic", "Source", new NodePosition(0, 0), new Dictionary<string, JsonElement>()),
                new NodeDefinition("threshold", "image.threshold", "Threshold", new NodePosition(220, 0),
                    new Dictionary<string, JsonElement> { ["threshold"] = JsonSerializer.SerializeToElement(threshold) }),
                new NodeDefinition("blob", "measure.blob", "Blob", new NodePosition(440, 0),
                    new Dictionary<string, JsonElement> { ["minArea"] = JsonSerializer.SerializeToElement(100d) })
            ],
            [
                new EdgeDefinition("e1", "source", "image", "threshold", "image"),
                new EdgeDefinition("e2", "threshold", "image", "blob", "image")
            ]);
}
