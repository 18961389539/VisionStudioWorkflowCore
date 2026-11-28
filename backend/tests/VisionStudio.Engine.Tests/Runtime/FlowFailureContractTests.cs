using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// flow 结构化失败码：flow.fail 的 intentional/failureCode、flow.break 的 reasonCode 与循环绑定、
/// flow.result 的作用域审计与粘滞/重置语义。
/// </summary>
public sealed class FlowFailureContractTests
{
    // ── flow.fail 单元契约 ──────────────────────────────────────────
    [Fact]
    public async Task Fail_RejectsUnstableCodesAndMalformedDetails()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fail(("code", " ")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fail(("code", "1_BAD")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fail(("code", "E STOP")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fail(("code", new string('A', 65))).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fail(("detailsJson", "[1,2]")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fail(("detailsJson", "{\"a\":\"" + new string('x', 9000) + "\"}")).AsTask());
    }

    [Fact]
    public async Task Fail_CarriesCodeAndSanitizesControlCharacters()
    {
        var error = await Assert.ThrowsAsync<IntentionalWorkflowFailureException>(() =>
            Fail(("code", "E_ROBOT"), ("message", "\u001b[31mrobot stuck\r\nretry later")).AsTask());

        Assert.Equal("E_ROBOT", error.Code);
        Assert.Contains("robot stuck", error.Message);
        // 换行保留，其余控制字符（ESC/CR/DEL）必须被替换掉，避免终端转义序列写进日志。
        Assert.False(error.Message.Any(ch => ch != '\n' && char.IsControl(ch)), "message 不应包含控制字符");
        Assert.Contains("retry later", error.Message);

        var blank = await Assert.ThrowsAsync<IntentionalWorkflowFailureException>(() => Fail(("message", "   ")).AsTask());
        Assert.Contains("Workflow stopped by flow.fail", blank.Message);
    }

    [Fact]
    public async Task Fail_RunReportMarksIntentionalFailure()
    {
        using var provider = Provider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = new WorkflowDefinition("fail-run", "Fail Run",
            [Node("start", "test.mark"), Node("fail", "flow.fail", """{"code":"E_STOP","detailsJson":"{\"station\":\"A3\"}"}"""), Node("after", "test.mark")],
            [C("s-f", "start", "next", "fail", "exec"), C("f-a", "fail", "next", "after", "exec")]);

        var result = await runner.RunAsync(workflow, new VisionRunOptions(), runId: "fail-run-1");

        Assert.False(result.Success);
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "after");
        var report = Assert.Single(result.NodeReports, item => item.NodeId == "fail");
        Assert.False(report.Success);
        Assert.Equal(true, report.Summary["intentional"]);
        Assert.Equal("E_STOP", Convert.ToString(report.Summary["failureCode"]));
        Assert.Contains("A3", Convert.ToString(report.Summary["details"]));
    }

    // ── flow.break ──────────────────────────────────────────────────
    [Fact]
    public async Task Break_RejectsUnstableReasonCodeAndControlCharacters()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Break(("reasonCode", "has space")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Break(("reasonCode", "")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Break(("reason", "stop\u0007here")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Break(("reason", new string('x', 513))).AsTask());
    }

    [Fact]
    public async Task Break_BindsReasonCodeAndInnermostLoopIdentity()
    {
        using var provider = Provider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(BreakWorkflow("loop", "operator-stop"), new VisionRunOptions(), runId: "break-run");

        Assert.True(result.Success, result.Error);
        var breakReport = Assert.Single(result.NodeReports, item => item.NodeId == "break");
        Assert.Equal("operator-stop", Convert.ToString(breakReport.Summary["reasonCode"]));
        Assert.Equal("loop", Convert.ToString(breakReport.Summary["enclosingLoopId"]));
        Assert.Equal(0L, Convert.ToInt64(breakReport.Summary["loopIteration"]));
        Assert.Equal(true, breakReport.Summary["loopBound"]);

    }

    [Fact]
    public async Task Break_SecondLoopDoesNotInheritFirstLoopIdentity()
    {
        using var provider = Provider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = new WorkflowDefinition("two-loops", "Two Loops",
            [
                Node("start", "test.mark"),
                Node("loop1", "flow.while", """{"maxIterations":5}"""), Node("cond1", "test.flag", """{"value":true}"""),
                Node("break1", "flow.break", """{"reasonCode":"first-stop"}"""), Node("end1", "flow.loopEnd"),
                Node("mid", "test.mark"),
                Node("loop2", "flow.while", """{"maxIterations":5}"""), Node("cond2", "test.flag", """{"value":true}"""),
                Node("break2", "flow.break", """{"reasonCode":"second-stop"}"""), Node("end2", "flow.loopEnd"),
                Node("after", "test.mark")
            ],
            [
                C("s-l1", "start", "next", "loop1", "exec"), C("l1-c1", "loop1", "body", "cond1", "exec"),
                C("c1-b1", "cond1", "next", "break1", "exec"), D("c1-cond1", "cond1", "pass", "loop1", "condition"),
                C("b1-e1", "break1", "next", "end1", "exec"), C("e1-l1", "end1", "repeat", "loop1", "repeat"),
                C("l1-m", "loop1", "done", "mid", "exec"),
                C("m-l2", "mid", "next", "loop2", "exec"), C("l2-c2", "loop2", "body", "cond2", "exec"),
                C("c2-b2", "cond2", "next", "break2", "exec"), D("c2-cond2", "cond2", "pass", "loop2", "condition"),
                C("b2-e2", "break2", "next", "end2", "exec"), C("e2-l2", "end2", "repeat", "loop2", "repeat"),
                C("l2-a", "loop2", "done", "after", "exec")
            ]);

        var result = await runner.RunAsync(workflow, new VisionRunOptions(), runId: "two-loops-run");

        Assert.True(result.Success, result.Error);
        var breaks = result.NodeReports.Where(item => item.NodeType == "flow.break").ToArray();
        Assert.Equal(2, breaks.Length);
        Assert.Equal("loop1", Convert.ToString(breaks[0].Summary["enclosingLoopId"]));
        Assert.Equal("first-stop", Convert.ToString(breaks[0].Summary["reasonCode"]));
        Assert.Equal("loop2", Convert.ToString(breaks[1].Summary["enclosingLoopId"]));
        Assert.Equal("second-stop", Convert.ToString(breaks[1].Summary["reasonCode"]));
    }

    // ── flow.result 处置语义 ────────────────────────────────────────
    [Fact]
    public async Task Result_StickyNgSurvivesLaterOk_AndResetRecovers()
    {
        using var provider = Provider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var sticky = await runner.RunAsync(ResultWorkflow(reset: false), new VisionRunOptions(), runId: "sticky-run");
        Assert.True(sticky.Success, sticky.Error);
        Assert.Equal("NG", sticky.QualityDisposition);
        var second = Assert.Single(sticky.NodeReports, item => item.NodeId == "r2");
        Assert.Equal("NG", Convert.ToString(second.Summary[NodeSummaryKeys.DispositionPrevious]));
        Assert.Equal("run", Convert.ToString(second.Summary[NodeSummaryKeys.DispositionScope]));
        Assert.Equal("r2", Convert.ToString(second.Summary[NodeSummaryKeys.DispositionSetBy]));

        var reset = await runner.RunAsync(ResultWorkflow(reset: true), new VisionRunOptions(), runId: "reset-run");
        Assert.True(reset.Success, reset.Error);
        Assert.Equal("OK", reset.QualityDisposition);
        Assert.Equal(true, Assert.Single(reset.NodeReports, item => item.NodeId == "r2").Summary[NodeSummaryKeys.DispositionReset]);
    }

    // ── 夹具 ────────────────────────────────────────────────────────
    private static ValueTask<NodeExecutorResult> Fail(params (string Key, string Value)[] parameters)
    {
        var payload = JsonSerializer.Serialize(parameters.ToDictionary(item => item.Key, item => item.Value));
        return new FlowFailNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            new NodeDefinition("fail", "flow.fail", "fail", null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload)),
            CancellationToken.None);
    }

    private static ValueTask<NodeExecutorResult> Break(params (string Key, string Value)[] parameters)
    {
        var payload = JsonSerializer.Serialize(parameters.ToDictionary(item => item.Key, item => item.Value));
        return new BreakLoopNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            new NodeDefinition("break", "flow.break", "break", null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload)),
            CancellationToken.None);
    }

    private static WorkflowDefinition BreakWorkflow(string loopId, string reasonCode) => new("break-run", "Break Run",
        [
            Node("start", "test.mark"), Node("loop", "flow.while", """{"maxIterations":5}"""),
            Node("cond", "test.flag", """{"value":true}"""), Node("break", "flow.break", $$"""{"reasonCode":"{{reasonCode}}"}"""),
            Node("end", "flow.loopEnd"), Node("after", "test.mark")
        ],
        [
            C("s-l", "start", "next", "loop", "exec"), C("l-c", "loop", "body", "cond", "exec"),
            C("c-b", "cond", "next", "break", "exec"), D("c-cond", "cond", "pass", "loop", "condition"),
            C("b-e", "break", "next", "end", "exec"), C("e-l", "end", "repeat", "loop", "repeat"),
            C("l-a", "loop", "done", "after", "exec")
        ]);

    private static WorkflowDefinition ResultWorkflow(bool reset) => new("result-run", "Result Run",
        [
            Node("start", "test.mark"),
            Node("f1", "test.flag", """{"value":false}"""), Node("r1", "flow.result", """{"sticky":true}"""),
            Node("f2", "test.flag", """{"value":true}"""),
            Node("r2", "flow.result", reset ? """{"reset":true}""" : "{}"),
            Node("after", "test.mark")
        ],
        [
            C("s-f1", "start", "next", "f1", "exec"), C("f1-r1", "f1", "next", "r1", "exec"),
            D("f1-pass", "f1", "pass", "r1", "pass"),
            C("r1-f2", "r1", "next", "f2", "exec"), C("f2-r2", "f2", "next", "r2", "exec"),
            D("f2-pass", "f2", "pass", "r2", "pass"), C("r2-a", "r2", "next", "after", "exec")
        ]);

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
        registry.Register(BuiltInNodeCatalog.Require("flow.fail"), new FlowFailNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.result"), new ResultDispositionNode(), "builtin");
        registry.Register(Batch4IterationCatalog.Items.Single(item => item.Type == "flow.while"), new WhileLoopNode(), "builtin");
        registry.Register(Batch4IterationCatalog.Items.Single(item => item.Type == "flow.break"), new BreakLoopNode(), "builtin");
        registry.Register(Batch4IterationCatalog.Items.Single(item => item.Type == "flow.loopEnd"), new LoopEndNode(), "builtin");
        registry.Register(FlagCatalog, new FlagNode(), "test");
        registry.Register(MarkCatalog, new MarkNode(), "test");
        return registry;
    }

    private static NodeDefinition Node(string id, string type, string? json = null)
        => new(id, type, id, null, json is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static EdgeDefinition C(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort, "control");

    private static EdgeDefinition D(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort, "data");

    private static NodeCatalogItem MarkCatalog { get; } = new("test.mark", "Mark", "Test",
        [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private static NodeCatalogItem FlagCatalog { get; } = new("test.flag", "Flag", "Test",
        [new("exec", VisionDataType.Control)],
        [new("next", VisionDataType.Control), new("pass", VisionDataType.Boolean)],
        [new("value", "Value", "boolean", true)],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private sealed class MarkNode : IVisionNodeExecutor
    {
        public string Type => "test.mark";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }

    private sealed class FlagNode : IVisionNodeExecutor
    {
        public string Type => "test.flag";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var value = node.GetBool("value", true);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(value) },
                new Dictionary<string, object?> { ["pass"] = value }));
        }
    }
}
