using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 节点级超时（<c>__timeoutMs</c>）。
///
/// 核心约束：节点预算与整轮预算必须保持可区分 —— 节点超时要归因到节点（<c>node_timeout</c>），
/// 整轮超时仍归因到运行（<c>timeout</c>），用户取消仍归因到调用方（<c>cancelled</c>）。
/// 三者混在一起正是此前"超时只说整个流程超时、定位不到节点"的根因。
/// </summary>
public sealed class NodeTimeoutTests
{
    [Fact]
    public async Task NodeBudget_Expires_FailsFastWithNodeTimeoutCode()
    {
        var blocking = new BlockingExecutor();
        await using var provider = BuildProvider(blocking);
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(CreateWorkflow(timeoutMs: 200), new VisionRunOptions(DebugRunMode.Full, TimeoutMs: 60000));

        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.NodeTimeout, result.ErrorCode);
        Assert.Contains(BlockingExecutor.NodeType, result.Error);
        Assert.Contains("200 ms", result.Error);
        var report = Assert.Single(result.NodeReports);
        Assert.False(report.Success);
        Assert.Contains("200 ms", report.Error);
    }

    [Fact]
    public async Task NodeBudget_IsReportedAsNodeFault_NotRunCancellation()
    {
        // 关键设计：NodeTimeoutException 不继承 OperationCanceledException，
        // 否则会被 Try/Catch 与运行器的 catch(OCE) 误判为"调用方取消"而整轮中断。
        Assert.False(typeof(OperationCanceledException).IsAssignableFrom(typeof(NodeTimeoutException)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NodeWithoutBudget_StillHonoursWholeRunBudget()
    {
        // 回归护栏：未配置 __timeoutMs 时不得改变既有语义，超时仍报整轮 timeout。
        var blocking = new BlockingExecutor();
        await using var provider = BuildProvider(blocking);
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var pending = runner.RunAsync(CreateWorkflow(timeoutMs: null), new VisionRunOptions(DebugRunMode.Full, TimeoutMs: 1500));
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task NodeBudget_LargerThanRunBudget_DoesNotMaskRunTimeout()
    {
        // 节点预算比整轮还长时，责任仍属整轮看门狗，不能被误报成节点超时。
        var blocking = new BlockingExecutor();
        await using var provider = BuildProvider(blocking);
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var pending = runner.RunAsync(CreateWorkflow(timeoutMs: 60000), new VisionRunOptions(DebugRunMode.Full, TimeoutMs: 1500));
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Equal(VisionRunErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task NodeBudget_DoesNotFireForFastNode()
    {
        await using var provider = BuildProvider(new SuccessExecutor());
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(CreateWorkflow(timeoutMs: 5000), runId: "run-node-budget-ok");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(-500, 0)]
    [InlineData(1, 1)]
    [InlineData(600_000, 600_000)]
    [InlineData(900_000, NodeTimeoutPolicy.MaxTimeoutMs)]
    public void Normalize_TreatsNonPositiveAsUnset_AndClampsToCeiling(int input, int expected)
        => Assert.Equal(expected, NodeTimeoutPolicy.Normalize(input));

    private static WorkflowDefinition CreateWorkflow(int? timeoutMs)
    {
        Dictionary<string, JsonElement>? parameters = timeoutMs is null
            ? null
            : new Dictionary<string, JsonElement>
            {
                [NodeTimeoutPolicy.ParameterName] = JsonSerializer.SerializeToElement(timeoutMs.Value)
            };
        return new WorkflowDefinition(
            "node-timeout",
            "Node timeout",
            [new NodeDefinition("n1", BlockingExecutor.NodeType, null, null, parameters)],
            []);
    }

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
        public const string NodeType = "test.nodetimeout.blocking";
        public string Type => NodeType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class SuccessExecutor : IVisionNodeExecutor
    {
        public string Type => BlockingExecutor.NodeType;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
