using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine.Runtime;
using VisionStudio.Plugin.Sample;

namespace VisionStudio.Engine.Tests;

public sealed class SampleWorkflowCompilationTests
{
    [Fact]
    public void EveryBundledWorkflowSample_CompilesAgainstCurrentCatalog()
    {
        var registry = new VisionNodeRegistry();
        foreach (var catalog in BuiltInNodeCatalog.Items)
            registry.Register(catalog, new NoopExecutor(catalog.Type), "test-catalog");
        foreach (var pluginNode in new SampleMathPlugin().CreateNodes())
            registry.Register(pluginNode.Catalog, pluginNode, "sample.math");
        var compiler = new VisionWorkflowCompiler(registry);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var sampleDir = Path.Combine(AppContext.BaseDirectory, "samples");
        var workflowFiles = Directory.GetFiles(sampleDir, "*-workflow.json", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(workflowFiles);

        foreach (var path in workflowFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(File.ReadAllText(path), options);
            Assert.NotNull(workflow);
            var compiled = compiler.Compile(workflow!);
            Assert.NotEmpty(compiled.DslJson);
            Assert.NotEmpty(compiled.OrderedNodeIds);
        }
    }

    private sealed class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new NotSupportedException("Compilation-only executor.");
    }
}
