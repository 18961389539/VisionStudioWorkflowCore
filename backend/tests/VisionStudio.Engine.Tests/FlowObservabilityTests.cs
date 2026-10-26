using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 流程内日志（flow.log）与主动失败（flow.fail）的单元与端到端测试。
/// 重点不是"能返回什么"，而是：日志落点是否可追溯、失败是否真的终止运行并带出可读原因。
/// </summary>
public sealed class FlowObservabilityTests
{
    [Fact]
    public async Task Log_EmitsMessageLevelAndSeverity()
    {
        var result = await new FlowLogNode().ExecuteAsync(Ctx(), Def("warning", ("message", "step=2 配方=B")), CancellationToken.None);

        Assert.Equal("step=2 配方=B", result.Outputs["message"].Require<string>("message"));
        Assert.Equal("warning", result.Outputs["level"].Require<string>("level"));
        Assert.Equal(2L, result.Outputs["severity"].Require<long>("severity"));
        Assert.True(result.Outputs["logged"].Require<bool>("logged"));
        Assert.Contains("flow.log", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task Log_InputOverridesParameterAndEmptyTextIsAllowed()
    {
        var overridden = await new FlowLogNode().ExecuteAsync(
            Ctx(("message", VisionValue.String("来自端口"))), Def("info", ("message", "参数文本")), CancellationToken.None);
        Assert.Equal("来自端口", overridden.Outputs["message"].Require<string>("message"));

        // 日志是观测手段，不应把产线跑停：空文本照记。
        var empty = await new FlowLogNode().ExecuteAsync(Ctx(), Def("info"), CancellationToken.None);
        Assert.Equal(string.Empty, empty.Outputs["message"].Require<string>("message"));
        Assert.True(empty.Outputs["logged"].Require<bool>("logged"));
    }

    [Fact]
    public async Task Log_RejectsUnknownLevelAndOversizedText()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FlowLogNode().ExecuteAsync(Ctx(), Def("verbose"), CancellationToken.None).AsTask());

        var oversized = new string('x', FlowLogNode.MaxMessageLength + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FlowLogNode().ExecuteAsync(Ctx(), Def("info", ("message", oversized)), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Fail_ThrowsWithCodeAndMessage()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FlowFailNode().ExecuteAsync(Ctx(), Def("E_ROBOT_TIMEOUT", ("message", "机器人 3 秒内未回位")), CancellationToken.None).AsTask());
        Assert.Equal("[E_ROBOT_TIMEOUT] 机器人 3 秒内未回位", error.Message);
    }

    [Fact]
    public async Task Fail_InputOverridesParameterAndBlankFallsBack()
    {
        var overridden = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FlowFailNode().ExecuteAsync(Ctx(("message", VisionValue.String("端口原因"))), Def("NG", ("message", "参数原因")), CancellationToken.None).AsTask());
        Assert.Contains("端口原因", overridden.Message);

        // 空白文本回退到默认句，而不是抛出 "[CODE] " 这种无信息量的消息。
        var blank = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FlowFailNode().ExecuteAsync(Ctx(), Def("NG", ("message", "   ")), CancellationToken.None).AsTask());
        Assert.Contains("stopped by flow.fail", blank.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FlowFailNode().ExecuteAsync(Ctx(), Def("  "), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Fail_StopsRunAndSurfacesReason()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(Workflow(
            ("fail", "flow.fail", """{"code":"E_STOP","message":"主动终止"}""")));

        Assert.False(result.Success);
        Assert.Contains("E_STOP", result.Error);
        Assert.Contains("主动终止", result.Error);
        // fail 之后的节点不得执行。
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "after");
        // 失败节点本身要被标记为失败，并保留原因，供追溯面板展开。
        var failed = Assert.Single(result.NodeReports, report => report.NodeId == "fail");
        Assert.False(failed.Success);
        Assert.Contains("E_STOP", failed.Error);
    }

    [Fact]
    public async Task Log_RunsInlineAndExposesLevelInReport()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(Workflow(
            ("log", "flow.log", """{"level":"error","message":"这一轮走了兜底分支"}""")));

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.NodeReports, report => report.NodeId == "after");
        var log = Assert.Single(result.NodeReports, report => report.NodeId == "log");
        Assert.True(log.Success);
        Assert.Equal("error", Convert.ToString(log.Summary["level"]));
        Assert.Equal(3L, Convert.ToInt64(log.Summary["severity"]));
        Assert.Contains("兜底分支", Convert.ToString(log.Summary["message"]));
    }

    // ===== 夹具 =====

    private static NodeDefinition Def(string codeOrLevel, params (string Key, string Value)[] extra)
    {
        var payload = new Dictionary<string, object> { ["code"] = codeOrLevel, ["level"] = codeOrLevel };
        foreach (var (key, value) in extra) payload[key] = value;
        return new NodeDefinition("n1", "test", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)));
    }

    private static NodeExecutionContext Ctx(params (string Port, VisionValue Value)[] inputs)
        => new(inputs.ToDictionary(item => item.Port, item => item.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>start → 被测节点 → after 的线性链。</summary>
    private static WorkflowDefinition Workflow((string Id, string Type, string Json) subject)
        => new("observability", "Observability",
            [
                Node("start", "test.mark"),
                new NodeDefinition(subject.Id, subject.Type, subject.Id, null,
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(subject.Json)),
                Node("after", "test.mark")
            ],
            [
                C("s-x", "start", "next", subject.Id, "exec"),
                C("x-a", subject.Id, "next", "after", "exec")
            ]);

    private static NodeDefinition Node(string id, string type)
        => new(id, type, id, null, null);

    private static EdgeDefinition C(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort, "control");

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        services.AddSingleton(Registry());
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
        registry.Register(BuiltInNodeCatalog.Require("flow.log"), new FlowLogNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.fail"), new FlowFailNode(), "builtin");
        registry.Register(MarkCatalog, new MarkNode(), "test");
        return registry;
    }

    private static NodeCatalogItem MarkCatalog { get; } = new("test.mark", "Mark", "Test",
        [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private sealed class MarkNode : IVisionNodeExecutor
    {
        public string Type => "test.mark";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
