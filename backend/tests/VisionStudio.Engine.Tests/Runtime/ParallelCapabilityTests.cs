using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class ParallelCapabilityTests
{
    [Fact]
    public void Compiler_RejectsNodeThatDeclaresParallelUnsafe()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(ParallelExecutor.Catalog, new ParallelExecutor(), "test");
        registry.Register(JoinExecutor.Catalog, new JoinExecutor(), "test");
        registry.Register(UnsafeExecutor.Catalog, new UnsafeExecutor(), "test");
        registry.Register(SafeExecutor.Catalog, new SafeExecutor(), "test");
        var compiler = new VisionWorkflowCompiler(registry);

        var workflow = new WorkflowDefinition(
            "parallel-capability",
            "Parallel capability",
            [
                new NodeDefinition("p", "flow.parallel", "Parallel", null, null),
                new NodeDefinition("unsafe", UnsafeExecutor.NodeType, "Unsafe", null, null),
                new NodeDefinition("safe", SafeExecutor.NodeType, "Safe", null, null),
                new NodeDefinition("join", "flow.join", "Join", null, null)
            ],
            [
                C("c1", "p", "branch1", "unsafe", "exec"),
                C("c2", "p", "branch2", "safe", "exec"),
                C("c3", "unsafe", "next", "join", "exec"),
                C("c4", "safe", "next", "join", "exec")
            ]);

        var ex = Assert.Throws<InvalidOperationException>(() => compiler.Compile(workflow));
        Assert.Contains("SupportsParallel=false", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EdgeDefinition C(string id, string s, string sp, string t, string tp)
        => new(id, s, sp, t, tp, "control");

    private abstract class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }

    private sealed class ParallelExecutor() : NoopExecutor("flow.parallel")
    {
        public static NodeCatalogItem Catalog { get; } = new(
            "flow.parallel", "Parallel", "Flow", [],
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control)], []);
    }

    private sealed class JoinExecutor() : NoopExecutor("flow.join")
    {
        public static NodeCatalogItem Catalog { get; } = new(
            "flow.join", "Join", "Flow", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], []);
    }

    private sealed class UnsafeExecutor() : NoopExecutor(NodeType)
    {
        public const string NodeType = "test.parallelUnsafe";
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Unsafe", "Test", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [],
            Capabilities: new VisionToolCapabilities(SupportsParallel: false));
    }

    private sealed class SafeExecutor() : NoopExecutor(NodeType)
    {
        public const string NodeType = "test.parallelSafe";
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Safe", "Test", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], []);
    }
}
