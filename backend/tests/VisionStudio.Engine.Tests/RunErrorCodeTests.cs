using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Structured run lifecycle codes: caller cancellation, internal timeout and node faults must stay
/// distinguishable, and a caller-supplied run id must survive into the result.
/// </summary>
public sealed class RunErrorCodeTests
{
    [Fact]
    public async Task CallerCancelledRun_ReportsCancelledCode_AndSuppliedRunId()
    {
        var blocking = new BlockingExecutor();
        await using var provider = BuildProvider(blocking);
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        using var cts = new CancellationTokenSource();

        var pending = runner.RunAsync(
            CreateWorkflow(),
            new VisionRunOptions(DebugRunMode.Full, TimeoutMs: 60000),
            "run-cancel-1",
            cts.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("run-cancel-1", result.RunId);
        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task InternalTimeout_ReportsTimeoutCode()
    {
        var blocking = new BlockingExecutor();
        await using var provider = BuildProvider(blocking);
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var pending = runner.RunAsync(CreateWorkflow(), new VisionRunOptions(DebugRunMode.Full, TimeoutMs: 300));
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.Timeout, result.ErrorCode);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task NodeFault_ReportsNodeFailureCode()
    {
        await using var provider = BuildProvider(new ThrowingExecutor());
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(CreateWorkflow());

        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.NodeFailure, result.ErrorCode);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task SuccessfulRun_KeepsErrorCodeNull()
    {
        await using var provider = BuildProvider(new SuccessExecutor());
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(CreateWorkflow(), runId: "run-ok-1");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.ErrorCode);
        Assert.Equal("run-ok-1", result.RunId);
    }

    private static WorkflowDefinition CreateWorkflow() => new(
        "lifecycle",
        "Lifecycle",
        [new NodeDefinition("n1", SuccessExecutor.NodeType, null, null, null)],
        []);

    private static ServiceProvider BuildProvider(IVisionNodeExecutor executor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        var registry = new VisionNodeRegistry();
        registry.Register(Catalog(executor.Type), executor, "test");
        services.AddSingleton(registry);
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddSingleton<WorkflowPlanCache>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        return services.BuildServiceProvider();
    }

    private static NodeCatalogItem Catalog(string type) => new(
        type,
        type,
        "Test",
        [],
        [],
        [],
        PluginId: "test",
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private sealed class BlockingExecutor : IVisionNodeExecutor
    {
        public string Type => SuccessExecutor.NodeType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ThrowingExecutor : IVisionNodeExecutor
    {
        public string Type => SuccessExecutor.NodeType;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class SuccessExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.lifecycle.node";
        public string Type => NodeType;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}