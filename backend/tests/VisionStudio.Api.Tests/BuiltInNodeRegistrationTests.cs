using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api.Hosting;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

/// <summary>
/// The built-in catalog and the executors resolved from the host container must stay 1:1.
/// Before discovery was introduced the bootstrap list was maintained by hand, so a catalog entry
/// could ship without an executor and only fail when a production workflow first ran that node.
/// </summary>
public sealed class BuiltInNodeRegistrationTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly VisionStudioApiFactory _factory;

    public BuiltInNodeRegistrationTests(VisionStudioApiFactory factory) => _factory = factory;

    [Fact]
    public void HostExecutors_MatchTheBuiltInCatalog()
    {
        var executors = _factory.Services.GetServices<IVisionNodeExecutor>().ToArray();

        Assert.Empty(BuiltInNodeExecutors.FindDrift(executors, BuiltInNodeCatalog.Items));
        // 已合并节点的别名执行器没有目录条目：执行器 = 目录条目 + 声明的别名。
        Assert.Equal(BuiltInNodeCatalog.Items.Count + NodeTypeMigration.ReplacementMap.Count, executors.Length);
    }

    [Fact]
    public void Drift_ListsCatalogEntriesWithoutExecutors()
    {
        var drift = BuiltInNodeExecutors.FindDrift([], BuiltInNodeCatalog.Items);

        Assert.Equal(BuiltInNodeCatalog.Items.Count, drift.Count(item => item.Contains("has no executor")));
    }

    [Fact]
    public void Drift_ListsExecutorsMissingFromTheCatalog()
    {
        var drift = BuiltInNodeExecutors.FindDrift([new StubExecutor("plugin.unknown")], BuiltInNodeCatalog.Items);

        Assert.Contains(drift, item => item.Contains("'plugin.unknown' is not declared in the built-in catalog"));
    }

    [Fact]
    public void Drift_DetectsDuplicateExecutorTypes()
    {
        var declared = BuiltInNodeCatalog.Items[0];
        var drift = BuiltInNodeExecutors.FindDrift(
            [new StubExecutor(declared.Type), new StubExecutor(declared.Type)],
            [declared]);

        Assert.Contains(drift, item => item.Contains("is registered 2 times"));
    }

    private sealed class StubExecutor(string type) : IVisionNodeExecutor
    {
        public string Type => type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(
            NodeExecutionContext context,
            NodeDefinition node,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
