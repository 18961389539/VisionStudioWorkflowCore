using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class RecipeParameterBindingTests
{
    [Fact]
    public async Task V051Resolve_BindsProductAndRecipeValues_AndFreezesSnapshotIntoVersion()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var parameters = new RecipeParameterStore(db);
        var jobs = new JobStore(db, parameters);
        var products = new ProductRecipeStore(db, jobs);
        await products.CreateProductAsync(new("phone-x", "Phone X"), default);
        await parameters.ReplaceProductAsync("phone-x", new(new(){["NominalWidth"] = JsonSerializer.SerializeToElement(12.5)}), default);
        var recipe = await products.CreateRecipeAsync("phone-x", new("phone-x-main","MAIN","Main",null,Workflow(),null), default);
        await parameters.ReplaceRecipeAsync(recipe.Id, new(new(){["MatchThreshold"] = JsonSerializer.SerializeToElement(0.82)}), default);

        var bindings = new Dictionary<string,string>
        {
            ["inspect.nominalWidth"] = "product.NominalWidth",
            ["inspect.threshold"] = "recipe.MatchThreshold"
        };
        var v2 = await jobs.AddVersionAsync(recipe.Id, new(Workflow(), "bind recipe parameters", bindings), default);

        Assert.Equal(2, v2.ParameterBindings!.Count);
        Assert.Equal(2, v2.ParameterSnapshot!.Count);
        var node = v2.Workflow.Nodes.Single();
        Assert.Equal(12.5, node.Parameters!["nominalWidth"].GetDouble(), 3);
        Assert.Equal(0.82, node.Parameters["threshold"].GetDouble(), 3);
        Assert.NotEqual(v2.BaseWorkflowHash, v2.WorkflowHash);
    }

    [Fact]
    public async Task V051ImmutableVersion_DoesNotHotChangeWhenRecipeParameterDraftChanges()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var parameters = new RecipeParameterStore(db);
        var jobs = new JobStore(db, parameters);
        var products = new ProductRecipeStore(db, jobs);
        await products.CreateProductAsync(new("p", "P"), default);
        var recipe = await products.CreateRecipeAsync("p", new("p-main","MAIN","Main",null,Workflow(),null), default);
        await parameters.ReplaceRecipeAsync(recipe.Id, new(new(){["MatchThreshold"] = JsonSerializer.SerializeToElement(0.70)}), default);
        var bindings = new Dictionary<string,string>{{"inspect.threshold","recipe.MatchThreshold"}};
        var v2 = await jobs.AddVersionAsync(recipe.Id, new(Workflow(), "0.70", bindings), default);

        await parameters.ReplaceRecipeAsync(recipe.Id, new(new(){["MatchThreshold"] = JsonSerializer.SerializeToElement(0.90)}), default);
        var rereadV2 = await jobs.GetVersionAsync(recipe.Id, 2, default);
        var v3 = await jobs.AddVersionAsync(recipe.Id, new(Workflow(), "0.90", bindings), default);

        Assert.Equal(0.70, rereadV2!.Workflow.Nodes.Single().Parameters!["threshold"].GetDouble(), 3);
        Assert.Equal(0.90, v3.Workflow.Nodes.Single().Parameters!["threshold"].GetDouble(), 3);
        Assert.NotEqual(v2.WorkflowHash, v3.WorkflowHash);
    }

    [Fact]
    public async Task V051Resolver_RejectsMissingSourceAndTypeMismatch()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var parameters = new RecipeParameterStore(db);
        var jobs = new JobStore(db, parameters);
        var products = new ProductRecipeStore(db, jobs);
        await products.CreateProductAsync(new("p2", "P2"), default);
        var recipe = await products.CreateRecipeAsync("p2", new("p2-main","MAIN","Main",null,Workflow(),null), default);

        await Assert.ThrowsAsync<ApiValidationException>(() => jobs.AddVersionAsync(recipe.Id,
            new(Workflow(), "missing", new(){{"inspect.threshold","recipe.NotDefined"}}), default));

        await parameters.ReplaceRecipeAsync(recipe.Id, new(new(){["BadThreshold"] = JsonSerializer.SerializeToElement("abc")}), default);
        var ex = await Assert.ThrowsAsync<ApiValidationException>(() => jobs.AddVersionAsync(recipe.Id,
            new(Workflow(), "bad type", new(){{"inspect.threshold","recipe.BadThreshold"}}), default));
        Assert.Contains("expects Number", ex.Message);
    }

    [Fact]
    public async Task V051Schema14_CreatesParameterTablesAndVersionSnapshotColumns()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var status = await db.GetSchemaStatusAsync();
        // schema 版本随迁移演进（当前 20）：断言 status 与实现常量一致而非硬编码快照
        Assert.Equal(db.CurrentSchemaVersion, status.CurrentVersion);
        await using var connection = await db.OpenConnectionAsync();
        foreach (var table in new[] { "product_parameter_values", "recipe_parameter_values" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        var columns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(job_versions);";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }
        Assert.Contains("base_workflow_hash", columns);
        Assert.Contains("base_workflow_json", columns);
        Assert.Contains("parameter_bindings_json", columns);
        Assert.Contains("parameter_snapshot_json", columns);
    }

    private static WorkflowDefinition Workflow() => new(
        "wf", "WF",
        [new NodeDefinition("inspect","test.sqlite","Inspect",null,new()
        {
            ["threshold"] = JsonSerializer.SerializeToElement(0.5),
            ["nominalWidth"] = JsonSerializer.SerializeToElement(10.0)
        })], []);
}
