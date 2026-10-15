using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VisionStudio.Api.Tests;

public sealed class IndependentRecipeApiTests
{
    [Fact]
    public async Task CreateParametersVersionCloneAndPublishGate_WorkWithoutProducts()
    {
        using var factory = new RecipeApiFactory();
        using var client = factory.CreateClient();
        var workflow = new
        {
            id = "inspection", name = "Inspection",
            nodes = new[] { new { id = "source", type = "image.synthetic", parameters = new { width = 320, height = 240 } } },
            edges = Array.Empty<object>()
        };
        var create = await client.PostAsJsonAsync("/api/recipes", new { id = "main", name = "Main", workflow });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var recipe = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(!recipe.TryGetProperty("productId", out var product) || product.ValueKind == JsonValueKind.Null);

        var parameters = await client.PutAsJsonAsync("/api/jobs/main/parameters", new { values = new Dictionary<string, int> { ["Width"] = 400 } });
        Assert.Equal(HttpStatusCode.OK, parameters.StatusCode);
        var version = await client.PostAsJsonAsync("/api/jobs/main/versions", new
        {
            workflow, parameterBindings = new Dictionary<string, string> { ["source.width"] = "recipe.Width" }
        });
        Assert.True(version.IsSuccessStatusCode, await version.Content.ReadAsStringAsync());
        var snapshot = await version.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, snapshot.GetProperty("workflow").GetProperty("nodes")[0].GetProperty("parameters").GetProperty("width").GetInt32());

        var publish = await client.PostAsync("/api/jobs/main/versions/2/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);
        Assert.Contains("Validation", await publish.Content.ReadAsStringAsync());
        var copy = await client.PostAsJsonAsync("/api/jobs/main/clone", new { newId = "copy", name = "Copy" });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/jobs")).GetArrayLength());
        // These tests use a temporary content root; the developer database stays untouched.
    }

    [Fact]
    public async Task CreateRecipe_RejectsEmptyName_AndProductRoutesAreRetired()
    {
        using var factory = new RecipeApiFactory();
        using var client = factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/recipes", new
        {
            id = "main", name = " ",
            workflow = new { id = "inspection", name = "Inspection", nodes = new[] { new { id = "source", type = "image.synthetic", parameters = new { width = 320, height = 240 } } }, edges = Array.Empty<object>() }
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task NameOnlyCreationAndCopy_GenerateUniqueIds_AndRetainValidationGateWithoutCode()
    {
        using var factory = new RecipeApiFactory();
        using var client = factory.CreateClient();
        var workflow = new { id = "inspection", name = "Inspection", nodes = new[] { new { id = "source", type = "image.synthetic", parameters = new { width = 320, height = 240 } } }, edges = Array.Empty<object>() };
        var ids = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync("/api/recipes", new { name = "Same display name", workflow });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var recipe = await response.Content.ReadFromJsonAsync<JsonElement>();
            var id = recipe.GetProperty("id").GetString()!;
            Assert.True(ids.Add(id));
            Assert.True(!recipe.TryGetProperty("recipeCode", out var code) || code.ValueKind == JsonValueKind.Null);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/jobs/{id}/versions/1/publish", null)).StatusCode);
        }
        var copyResponse = await client.PostAsJsonAsync($"/api/jobs/{ids.First()}/clone", new { name = "Copy" });
        Assert.True(copyResponse.IsSuccessStatusCode, await copyResponse.Content.ReadAsStringAsync());
        var copy = await copyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var copyId = copy.GetProperty("id").GetString()!;
        Assert.True(ids.Add(copyId));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/jobs/{copyId}/versions/1/publish", null)).StatusCode);
    }

    private sealed class RecipeApiFactory : WebApplicationFactory<Program>
    {
        private readonly TempWebHostEnvironment _environment = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(_environment.ContentRootPath);
            builder.UseSetting("Security:Enabled", "false");
            builder.UseSetting("RuntimeOnly", "false");
            builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _environment.Dispose();
        }
    }
}
