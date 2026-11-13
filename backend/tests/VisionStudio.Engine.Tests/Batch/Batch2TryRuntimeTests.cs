using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class Batch2TryRuntimeTests
{
    [Fact]
    public async Task Try_RetriesOnlyDeterministicBody_RecordsHandledFailureAndRunsSuccessRoute()
    {
        var body = new FailOnceNode(alwaysFail: false);
        await using var provider = Provider(body);
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(Workflow(maxRetries: 1));

        Assert.True(result.Success, result.Error);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, body.Attempts);
        Assert.Contains(result.NodeReports, report => report.NodeId == "body" && !report.Success &&
            Equals(report.Summary["handledByTry"], true) && Equals(report.Summary["tryAttempt"], 1));
        Assert.Contains(result.NodeReports, report => report.NodeId == "body" && report.Success);
        Assert.Contains(result.NodeReports, report => report.NodeId == "success-route");
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "error-route");
        var tryReport = Assert.Single(result.NodeReports, report => report.NodeId == "try");
        Assert.Equal(2, tryReport.Summary["attempts"]);
        Assert.Equal(true, tryReport.Summary["succeeded"]);
        Assert.Equal(true, tryReport.Summary["handled"]);
    }

    [Fact]
    public async Task Try_ExhaustionRunsErrorRouteAndKeepsFailedNodeReportWithoutFailingWholeRun()
    {
        var body = new FailOnceNode(alwaysFail: true);
        await using var provider = Provider(body);
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(Workflow(maxRetries: 2));

        Assert.True(result.Success, result.Error);
        Assert.Null(result.ErrorCode);
        Assert.Equal(3, body.Attempts);
        Assert.Contains(result.NodeReports, report => report.NodeId == "error-route");
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "success-route");
        var failures = result.NodeReports.Where(report => report.NodeId == "body" && !report.Success).ToArray();
        Assert.Equal(3, failures.Length);
        Assert.All(failures, report => Assert.Equal(true, report.Summary["handledByTry"]));
        Assert.Equal(new object?[] { 1, 2, 3 }, failures.Select(report => report.Summary["tryAttempt"]).ToArray());
        var marker = Assert.Single(result.NodeReports, report => report.NodeId == "try");
        Assert.Equal(false, marker.Summary["succeeded"]);
        Assert.Equal("expected body failure", marker.Summary["error"]);
        Assert.Equal("error", Assert.Single(result.ControlFlowDecisions!, x => x.NodeId == "try").SelectedBranch);
    }

    [Fact]
    public async Task Try_CallerCancellationPropagatesAndIsNeverRetried()
    {
        var body = new BlockingNode();
        await using var provider = Provider(body);
        using var cts = new CancellationTokenSource();
        var run = provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(Workflow(maxRetries: 3, BlockingNode.NodeType), cancellationToken: cts.Token);
        var first = await Task.WhenAny(body.Started.Task, run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Same(body.Started.Task, first);
        cts.Cancel();
        var result = await run;

        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.Cancelled, result.ErrorCode);
        Assert.Equal(1, body.Attempts);
        Assert.Contains(result.NodeReports, report => report.NodeId == "body" && !report.Success &&
            Equals(report.Summary["handledByTry"], false));
    }

    [Fact]
    public void Try_RejectsNondeterministicOrWorkflowCoreBodyBeforeExecution()
    {
        var registry = Registry(new FailOnceNode(alwaysFail: false), executionMode: VisionExecutionMode.WorkflowCore);
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(registry).Compile(Workflow(1)));
        Assert.Contains("deterministic built-in Pipeline", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Try_ExecutesBuiltInArithmeticAndAllowsBodyOutputInSuccessRoute()
    {
        var registry = BuiltInRegistry();
        var observer = new ExceptionSpy();
        await using var provider = Provider(registry, observer);
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(BuiltInArithmeticWorkflow(divideByZero: false));

        Assert.True(result.Success, result.Error);
        Assert.Empty(observer.Calls);
        Assert.Contains(result.NodeReports, report => report.NodeId == "success-range" && report.Success);
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "error-route");
        Assert.Equal(5d, (double)result.NodeReports.Single(report => report.NodeId == "success-range").Inputs!["value"].Value!);
    }

    [Fact]
    public async Task Try_CatchesActualBuiltInArithmeticFailureAndRejectsBodyValueInErrorRoute()
    {
        var registry = BuiltInRegistry();
        var observer = new ExceptionSpy();
        await using var provider = Provider(registry, observer);
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(BuiltInArithmeticWorkflow(divideByZero: true));

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.NodeReports, report => report.NodeId == "body-arithmetic" && !report.Success &&
            Equals(report.Summary["handledByTry"], true));
        Assert.Contains(result.NodeReports, report => report.NodeId == "error-route");
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "success-range");
        Assert.Null(result.ErrorCode);
        Assert.Empty(observer.Calls);
        Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(registry).Compile(BuiltInArithmeticWorkflow(divideByZero: true, useBodyValueInError: true)));
    }

    [Fact]
    public async Task Try_HandledBodyFailureDoesNotMaskLaterUnhandledFailureFromObserver()
    {
        var observer = new ExceptionSpy();
        await using var provider = Provider(BuiltInRegistry(), observer);
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(
            BuiltInArithmeticWorkflow(divideByZero: true, failAfterTry: true));

        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.NodeFailure, result.ErrorCode);
        var observed = Assert.Single(observer.Calls);
        Assert.Equal("after", observed.Context.NodeId);
        Assert.Equal("unhandled after try", observed.Exception.Message);
        Assert.Contains(result.NodeReports, report => report.NodeId == "body-arithmetic" && !report.Success &&
            Equals(report.Summary["handledByTry"], true));
    }

    private static VisionNodeRegistry BuiltInRegistry()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.try"), new FlowTryNode(), "batch2-test");
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.merge"), new FlowMergeNode(), "batch2-test");
        registry.Register(BuiltInNodeCatalog.Require("value.number"), new ValueNumberNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("value.arithmetic"), new ValueArithmeticNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("value.range"), new ValueRangeNode(), "builtin");
        registry.Register(MarkNode.Catalog, new MarkNode(), "test");
        registry.Register(TestThrowNode.Catalog, new TestThrowNode(), "test");
        return registry;
    }

    private static ServiceProvider Provider(VisionNodeRegistry registry, IVisionRunExceptionObserver? observer = null)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddWorkflow(); services.AddWorkflowDSL();
        services.AddSingleton(registry); services.AddSingleton<VisionNodeDispatcher>(); services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>(); services.AddSingleton<VisionWorkflowCompiler>(); services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>(); services.AddSingleton<WorkflowPlanCache>();
        if (observer is not null) services.AddSingleton<IVisionRunExceptionObserver>(observer);
        services.AddTransient<WorkflowCoreVisionRunner>();
        return services.BuildServiceProvider();
    }

    private static WorkflowDefinition BuiltInArithmeticWorkflow(bool divideByZero, bool useBodyValueInError = false, bool failAfterTry = false)
    {
        var nodes = new List<NodeDefinition>
        {
            Node("try", "flow.try", new Dictionary<string, object?> { ["maxRetries"] = 1, ["retryDelayMs"] = 0 }),
            Node("numerator", "value.number", new Dictionary<string, object?> { ["value"] = 10 }),
            Node("denominator", "value.number", new Dictionary<string, object?> { ["value"] = divideByZero ? 0 : 2 }),
            Node("body-arithmetic", "value.arithmetic", new Dictionary<string, object?> { ["op"] = "divide" }),
            Node("success-range", "value.range", new Dictionary<string, object?> { ["min"] = 5, ["max"] = 5 }),
            Node("error-route", useBodyValueInError ? "value.range" : "test.mark"),
            Node("merge", "flow.merge"), Node("after", failAfterTry ? TestThrowNode.NodeType : "test.mark")
        };
        var edges = new List<EdgeDefinition>
        {
            C("try-body", "try", "body", "numerator", "exec"),
            C("numerator-denominator", "numerator", "next", "denominator", "exec"),
            C("denominator-math", "denominator", "next", "body-arithmetic", "exec"),
            C("try-success", "try", "success", "success-range", "exec"),
            C("try-error", "try", "error", "error-route", "exec"),
            C("body-merge", "body-arithmetic", "next", "merge", "case1"),
            C("success-merge", "success-range", "next", "merge", "case2"),
            C("error-merge", "error-route", "next", "merge", "default"),
            C("merge-after", "merge", "next", "after", "exec"),
            new("numerator-data", "numerator", "value", "body-arithmetic", "a"),
            new("denominator-data", "denominator", "value", "body-arithmetic", "b"),
            new("result-success", "body-arithmetic", "result", "success-range", "value")
        };
        if (useBodyValueInError) edges.Add(new("result-error", "body-arithmetic", "result", "error-route", "value"));
        return new WorkflowDefinition("try-real-builtins", "Try Real Builtins", nodes, edges);
    }

    private static WorkflowDefinition Workflow(int maxRetries, string bodyNodeType = FailOnceNode.NodeType) => new("try-runtime", "Try Runtime",
        [
            Node("try", "flow.try", new Dictionary<string, object?> { ["maxRetries"] = maxRetries, ["retryDelayMs"] = 0 }),
            Node("body", bodyNodeType), Node("success-route", "test.mark"), Node("error-route", "test.mark"),
            Node("merge", "flow.merge"), Node("after", "test.mark")
        ],
        [
            C("try-body", "try", "body", "body", "exec"), C("try-success", "try", "success", "success-route", "exec"),
            C("try-error", "try", "error", "error-route", "exec"), C("body-merge", "body", "next", "merge", "case1"),
            C("success-merge", "success-route", "next", "merge", "case2"), C("error-merge", "error-route", "next", "merge", "default"),
            C("after-merge", "merge", "next", "after", "exec")
        ]);

    private static ServiceProvider Provider(IVisionNodeExecutor body, VisionExecutionMode mode = VisionExecutionMode.Pipeline)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddWorkflow(); services.AddWorkflowDSL();
        services.AddSingleton(Registry(body, mode)); services.AddSingleton<VisionNodeDispatcher>(); services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>(); services.AddSingleton<VisionWorkflowCompiler>(); services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>(); services.AddSingleton<WorkflowPlanCache>(); services.AddTransient<WorkflowCoreVisionRunner>();
        return services.BuildServiceProvider();
    }

    private static VisionNodeRegistry Registry(IVisionNodeExecutor body, VisionExecutionMode executionMode = VisionExecutionMode.Pipeline)
    {
        var registry = new VisionNodeRegistry();
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.try"), new FlowTryNode(), "batch2-test");
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.merge"), new FlowMergeNode(), "batch2-test");
        registry.Register(body.Type == FailOnceNode.NodeType ? FailOnceNode.Catalog(executionMode) : BlockingNode.Catalog, body, "builtin");
        registry.Register(MarkNode.Catalog, new MarkNode(), "test");
        return registry;
    }

    private sealed class FailOnceNode(bool alwaysFail) : IVisionNodeExecutor
    {
        public const string NodeType = "test.retryable";
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public string Type => NodeType;
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (alwaysFail || attempt == 1) throw new InvalidOperationException("expected body failure");
            return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?> { ["attempt"] = attempt }));
        }
        public static NodeCatalogItem Catalog(VisionExecutionMode mode) => new(NodeType, "Retryable Compute", "Test", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [], "", "builtin",
            new VisionToolCapabilities(Deterministic: true, ExecutionMode: mode), Actions: []);
    }

    private sealed class BlockingNode : IVisionNodeExecutor
    {
        public const string NodeType = "test.blocking";
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(NodeType, "Blocking", "Test", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [], "", "builtin",
            new VisionToolCapabilities(Deterministic: true, ExecutionMode: VisionExecutionMode.Pipeline), Actions: []);
        public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts); Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>());
        }
    }

    private sealed class MarkNode : IVisionNodeExecutor
    {
        public string Type => "test.mark";
        public static NodeCatalogItem Catalog { get; } = new("test.mark", "Mark", "Test", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [], Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?> { ["node"] = node.Id }));
    }

    private sealed class ExceptionSpy : IVisionRunExceptionObserver
    {
        public List<(VisionRunExceptionContext Context, Exception Exception)> Calls { get; } = [];
        public void OnRunException(VisionRunExceptionContext context, Exception exception) => Calls.Add((context, exception));
    }

    private sealed class TestThrowNode : IVisionNodeExecutor
    {
        public const string NodeType = "test.throwAfterTry";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(NodeType, "Throw", "Test", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [],
            Capabilities: new VisionToolCapabilities(Deterministic: true, ExecutionMode: VisionExecutionMode.Pipeline), Actions: []);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new InvalidOperationException("unhandled after try");
    }

    private static NodeDefinition Node(string id, string type, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var raw = parameters?.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
        return new NodeDefinition(id, type, id, null, raw);
    }
    private static EdgeDefinition C(string id, string source, string sourcePort, string target, string targetPort) => new(id, source, sourcePort, target, targetPort, "control");
}
