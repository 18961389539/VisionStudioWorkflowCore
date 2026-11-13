using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using Xunit;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 已合并节点（2026-10-28 批）的类型迁移契约。
///
/// 目录里只保留规范节点，旧类型仅作为别名执行器存在；这两个不变量一旦破坏，
/// 旧流程会在加载时报 "Node type '...' is not registered"，所以在单元层面锁住。
/// </summary>
public sealed class NodeTypeMigrationTests
{
    private static readonly (string Legacy, string Canonical, string Parameter, string Value)[] Rules =
    [
        ("image.blur.gaussian", "image.filter", "method", "gaussian"),
        ("image.blur.median", "image.filter", "method", "median"),
        ("image.topHat", "image.morphology", "operation", "whiteTopHat"),
        ("image.bottomHat", "image.morphology", "operation", "blackBottomHat"),
        ("image.morphGradient", "image.morphology", "operation", "gradient"),
        ("region.fromThreshold", "region.fromRange", "mode", "gray"),
        ("measure.perpendicularity", "measure.parallelism", "mode", "perpendicular")
    ];

    [Fact]
    public void EveryLegacyTypeMapsToACanonicalCatalogEntry()
    {
        var catalogTypes = BuiltInNodeCatalog.Items.Select(item => item.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (legacy, canonical, _, _) in Rules)
        {
            Assert.Equal(canonical, NodeTypeMigration.CanonicalType(legacy));
            Assert.Contains(canonical, catalogTypes);
            // 旧类型必须已经离开目录，否则工具箱会重新出现重复入口。
            Assert.DoesNotContain(legacy, catalogTypes);
        }
    }

    [Fact]
    public void NormalizeRewritesTypeAndFillsTheMergedParameter()
    {
        foreach (var (legacy, canonical, parameter, value) in Rules)
        {
            var workflow = Workflow(legacy, """{"kernelSize":7}""");
            var normalized = NodeTypeMigration.Normalize(workflow);
            var node = Assert.Single(normalized.Nodes);
            Assert.Equal(canonical, node.Type);
            Assert.Equal(value, node.Parameters![parameter].GetString());
            // 旧文档自己的参数必须原样保留。
            Assert.Equal(7, node.Parameters["kernelSize"].GetInt32());
        }
    }

    [Fact]
    public void NormalizeKeepsAnExplicitParameterValue()
    {
        // 旧文档若已显式写了目标参数，迁移不得覆盖用户选择。
        var workflow = Workflow("image.blur.median", """{"method":"gaussian","kernelSize":5}""");
        var node = Assert.Single(NodeTypeMigration.Normalize(workflow).Nodes);
        Assert.Equal("image.filter", node.Type);
        Assert.Equal("gaussian", node.Parameters!["method"].GetString());
    }

    [Fact]
    public void NormalizeLeavesCanonicalAndUnknownTypesUntouched()
    {
        var canonical = Workflow("image.filter", """{"method":"median"}""");
        Assert.Same(canonical, NodeTypeMigration.Normalize(canonical));

        var unknown = Workflow("plugin.custom", null);
        Assert.Same(unknown, NodeTypeMigration.Normalize(unknown));
    }

    [Fact]
    public void LegacyAndCanonicalDocumentsShareThePlanCacheKey()
    {
        // 同一张图用旧类型保存与用规范类型保存，必须命中同一个编译计划缓存条目。
        var registry = new VisionNodeRegistry();
        foreach (var item in BuiltInNodeCatalog.Items)
            registry.Register(item, new NoopExecutor(item.Type), "test-catalog");
        var compiler = new VisionWorkflowCompiler(registry);

        var legacyKey = compiler.GetPlanCacheKey(Workflow("region.fromThreshold", """{"minGray":10,"maxGray":200}"""));
        var canonicalKey = compiler.GetPlanCacheKey(Workflow("region.fromRange", """{"mode":"gray","minGray":10,"maxGray":200}"""));
        Assert.Equal(canonicalKey, legacyKey);
    }

    [Fact]
    public void AliasExecutorsExistForEveryLegacyType()
    {
        var aliasTypes = typeof(NodeTypeMigration).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => typeof(IVisionNodeExecutor).IsAssignableFrom(type))
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => ((IVisionNodeExecutor)Activator.CreateInstance(type)!).Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (legacy, _, _, _) in Rules)
            Assert.Contains(legacy, aliasTypes);
    }

    private static WorkflowDefinition Workflow(string type, string? parametersJson) => new(
        "wf-merge", "merge",
        [
            new NodeDefinition("n1", type, null, null,
                parametersJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson))
        ],
        []);

    private sealed class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type => type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
