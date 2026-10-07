using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class ProductRecipeLifecycleTests
{
    [Fact]
    public async Task V050ProductRecipe_PublishRequiresAcceptedValidationForExactWorkflowHash()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        var products = new ProductRecipeStore(db, jobs);
        await products.CreateProductAsync(new("phone-a", "Phone A"), default);
        var recipe = await products.CreateRecipeAsync("phone-a", new("phone-a-main", "MAIN", "Main inspection", null, Workflow(120), "initial"), default);

        await Assert.ThrowsAsync<ApiConflictException>(() => jobs.PublishAsync(recipe.Id, 1, "Publish", Manifest("pre-validation"), default));

        var validationRun = await InsertValidationRunAsync(db, recipe.Versions[0].WorkflowHash, accuracy: 1, falseOkRate: 0, falseNgRate: 0, errors: 0);
        var linked = await products.LinkValidationAsync(recipe.Id, 1, new(validationRun), default);
        Assert.True(linked.Accepted);

        var published = await jobs.PublishAsync(recipe.Id, 1, "Publish", Manifest("validated"), default);
        Assert.Equal(1, published.PublishedVersion);
        Assert.True(published.Versions.Single().Validation!.Accepted);
    }

    [Fact]
    public async Task V050ValidationPolicy_RejectsFalseOkEvenWhenAccuracyLooksHigh()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        var products = new ProductRecipeStore(db, jobs);
        await products.CreateProductAsync(new("product-b", "Product B"), default);
        var recipe = await products.CreateRecipeAsync("product-b", new("recipe-b", "B", "Recipe B", null, Workflow(1), null), default);
        var run = await InsertValidationRunAsync(db, recipe.Versions[0].WorkflowHash, accuracy: .995, falseOkRate: .01, falseNgRate: 0, errors: 0);

        var linked = await products.LinkValidationAsync(recipe.Id, 1, new(run), default);
        Assert.False(linked.Accepted);
        Assert.Contains("False OK", linked.Reason);
    }

    [Fact]
    public async Task V050CloneAndDiff_KeepRecipeTopologyTraceable()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        var products = new ProductRecipeStore(db, jobs);
        await products.CreateProductAsync(new("product-c", "Product C"), default);
        var recipe = await products.CreateRecipeAsync("product-c", new("recipe-c", "STD", "Standard", null, Workflow(10), null), default);
        await jobs.AddVersionAsync(recipe.Id, new(Workflow(20), "threshold tune"), default);

        var diff = await products.DiffAsync(recipe.Id, 1, 2, default);
        Assert.True(diff.Changed);
        Assert.Contains(diff.Changes, x => x.Kind == "Parameter" && x.Path.Contains("revision"));

        var clone = await products.CloneRecipeAsync(recipe.Id, new("recipe-c-copy", "product-c", "ALT", "Alternative", SourceVersion: 2), default);
        Assert.Equal("product-c", clone.ProductId);
        Assert.Equal("ALT", clone.RecipeCode);
        Assert.Equal((await jobs.GetVersionAsync(recipe.Id, 2, default))!.WorkflowHash, clone.Versions[0].WorkflowHash);
    }

    private static async Task<string> InsertValidationRunAsync(SqliteMetadataDatabase db, string workflowHash, double accuracy, double falseOkRate, double falseNgRate, int errors)
    {
        var runId = $"validation-{Guid.NewGuid():N}";
        var summary = new ValidationRunSummary(100,100,90,10,0,0,errors,accuracy,falseOkRate,falseNgRate,1,2,3,4);
        await using var connection = await db.OpenConnectionAsync();
        await using var dataset = connection.CreateCommand();
        dataset.CommandText = "INSERT OR IGNORE INTO validation_datasets(id,name,description,topology_hash,created_at,updated_at) VALUES('ds','DS','',NULL,$at,$at);";
        dataset.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await dataset.ExecuteNonQueryAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO validation_runs(run_id,dataset_id,status,source_workflow_run_id,workflow_hash,candidate_workflow_json,requested_count,completed_count,started_at,completed_at,cancel_requested,error,summary_json)
VALUES($run,'ds','Completed','test',$hash,'{}',100,100,$at,$at,0,NULL,$summary);
""";
        command.Parameters.AddWithValue("$run",runId); command.Parameters.AddWithValue("$hash",workflowHash); command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$summary",JsonSerializer.Serialize(summary,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await command.ExecuteNonQueryAsync();
        return runId;
    }

    private static WorkflowDefinition Workflow(int revision) => new("wf","WF",[new NodeDefinition("n1","test.sqlite","Test",null,new(){["revision"]=JsonSerializer.SerializeToElement(revision)})],[]);
    private static RuntimeDependencyManifest Manifest(string seed)
    {
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return new RuntimeDependencyManifest(1,hash,DateTimeOffset.UtcNow,new EngineRuntimeDependency("VisionStudio.Engine","test","test",hash),[],[],[],[],[]);
    }
}
