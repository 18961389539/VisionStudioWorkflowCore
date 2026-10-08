using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

public sealed class WorkflowModuleTests
{
    [Fact]
    public async Task V052Extract_ProducesTypedPinnedCall_AndExpansionRestoresExecutableGraph()
    {
        using var env = new TempWebHostEnvironment();
        var runtime = CreateRuntime(env);
        var source = Workflow(128);
        var extracted = await runtime.Authoring.ExtractAsync(new(
            source, ["threshold"], "threshold-stage", "Threshold Stage", "Reusable threshold", "initial"), default);

        var call = Assert.Single(extracted.ReplacementWorkflow.Nodes.Where(x => x.Type == "module.call"));
        Assert.Equal("threshold-stage", call.Parameters!["moduleId"].GetString());
        Assert.Equal(1, call.Parameters["moduleVersion"].GetInt32());
        Assert.Equal(extracted.Version.ModuleHash, call.Parameters["moduleHash"].GetString());
        Assert.Single(extracted.Version.Inputs);
        Assert.Single(extracted.Version.Outputs);
        Assert.Equal(VisionDataType.Image, extracted.Version.Inputs[0].DataType);
        Assert.Contains(extracted.Version.Parameters, x => x.Name == "threshold.threshold");

        var expanded = await runtime.Expander.ExpandAsync(extracted.ReplacementWorkflow, default);
        Assert.DoesNotContain(expanded.Workflow.Nodes, x => x.Type == "module.call");
        var inner = Assert.Single(expanded.Workflow.Nodes.Where(x => x.Id.EndsWith("::threshold", StringComparison.Ordinal)));
        Assert.Equal("image.threshold", inner.Type);
        Assert.Contains(expanded.Workflow.Edges, x => x.SourceNodeId == "source" && x.TargetNodeId == inner.Id);
        Assert.Contains(expanded.Workflow.Edges, x => x.SourceNodeId == inner.Id && x.TargetNodeId == "blob");
        runtime.Compiler.Compile(expanded.Workflow);
    }

    [Fact]
    public async Task V052CallParameter_OverridesInternalParameter_ButKeepsPinnedModuleVersion()
    {
        using var env = new TempWebHostEnvironment();
        var runtime = CreateRuntime(env);
        var extracted = await runtime.Authoring.ExtractAsync(new(
            Workflow(128), ["threshold"], "threshold-stage", "Threshold Stage", null, null), default);
        var call = extracted.ReplacementWorkflow.Nodes.Single(x => x.Type == "module.call");
        var parameters = call.Parameters!.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        parameters["threshold.threshold"] = JsonSerializer.SerializeToElement(177d);
        var edited = extracted.ReplacementWorkflow with
        {
            Nodes = extracted.ReplacementWorkflow.Nodes.Select(x => x.Id == call.Id ? x with { Parameters = parameters } : x).ToArray()
        };

        var expanded = await runtime.Expander.ExpandAsync(edited, default);
        var inner = expanded.Workflow.Nodes.Single(x => x.Id.EndsWith("::threshold", StringComparison.Ordinal));
        Assert.Equal(177d, inner.Parameters!["threshold"].GetDouble());
        Assert.Equal(1, call.Parameters["moduleVersion"].GetInt32());
    }

    [Fact]
    public async Task V052NewVersion_IsImmutable_AndBreakingInterfaceParameterRemovalIsRejected()
    {
        using var env = new TempWebHostEnvironment();
        var runtime = CreateRuntime(env);
        var extracted = await runtime.Authoring.ExtractAsync(new(
            Workflow(128), ["threshold"], "threshold-stage", "Threshold Stage", null, null), default);
        var v1 = extracted.Version;
        var updatedWorkflow = v1.Workflow with
        {
            Nodes = v1.Workflow.Nodes.Select(node => node.Id == "threshold"
                ? node with { Parameters = new Dictionary<string, JsonElement> { ["threshold"] = JsonSerializer.SerializeToElement(155d) } }
                : node).ToArray()
        };
        var v2 = await runtime.Authoring.AddVersionAsync("threshold-stage", new(updatedWorkflow, "tune threshold"), default);
        Assert.Equal(2, v2.Version);
        Assert.NotEqual(v1.ModuleHash, v2.ModuleHash);
        Assert.Equal(128d, (await runtime.Store.GetVersionAsync("threshold-stage", 1, default))!.Parameters.Single(x => x.Name == "threshold.threshold").DefaultValue.GetDouble());
        Assert.Equal(155d, v2.Parameters.Single(x => x.Name == "threshold.threshold").DefaultValue.GetDouble());

        var broken = updatedWorkflow with
        {
            Nodes = updatedWorkflow.Nodes.Select(node => node.Id == "threshold" ? node with { Parameters = new Dictionary<string, JsonElement>() } : node).ToArray()
        };
        await Assert.ThrowsAsync<ApiValidationException>(() => runtime.Authoring.AddVersionAsync("threshold-stage", new(broken, "breaking"), default));
    }

    [Fact]
    public async Task V052SchemaV15_CreatesImmutableWorkflowModuleTables()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        // 模块表自 V16 引入：断言当前 schema 不低于该版本（版本持续演进，不再锁定具体快照）
        Assert.True(db.CurrentSchemaVersion >= 16);
        await using var connection = await db.OpenConnectionAsync();
        foreach (var table in new[] { "workflow_modules", "workflow_module_versions" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    private static RuntimeBundle CreateRuntime(TempWebHostEnvironment env)
    {
        var db = new SqliteMetadataDatabase(env);
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new SyntheticImageNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("image.threshold"), new ThresholdNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("measure.blob"), new LargestBlobNode(), "builtin");
        var store = new WorkflowModuleStore(db);
        var expander = new WorkflowModuleExpander(store);
        var compiler = new VisionWorkflowCompiler(registry);
        var authoring = new WorkflowModuleAuthoringService(store, registry, expander, compiler);
        return new RuntimeBundle(store, expander, compiler, authoring);
    }

    private static WorkflowDefinition Workflow(double threshold)
        => new(
            "module-host",
            "Module Host",
            [
                new NodeDefinition("source", "image.synthetic", "Source", new NodePosition(0, 0), new Dictionary<string, JsonElement>()),
                new NodeDefinition("threshold", "image.threshold", "Threshold", new NodePosition(220, 0), new Dictionary<string, JsonElement> { ["threshold"] = JsonSerializer.SerializeToElement(threshold) }),
                new NodeDefinition("blob", "measure.blob", "Blob", new NodePosition(440, 0), new Dictionary<string, JsonElement> { ["minArea"] = JsonSerializer.SerializeToElement(100d) })
            ],
            [
                new EdgeDefinition("e1", "source", "image", "threshold", "image"),
                new EdgeDefinition("e2", "threshold", "image", "blob", "image")
            ]);

    private sealed record RuntimeBundle(WorkflowModuleStore Store, WorkflowModuleExpander Expander, VisionWorkflowCompiler Compiler, WorkflowModuleAuthoringService Authoring);
}
