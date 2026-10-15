using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

public sealed class FeatureRegionApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static async Task<JsonNode> Sample(string file) => JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", file)))!;
    private static JsonNode Parameters(JsonNode graph, string id) => graph["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!;
    private async Task<JsonElement[]> Run(JsonNode graph)
    {
        var response = await _client.PostAsJsonAsync("/api/run", graph);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        return result.GetProperty("nodeReports").EnumerateArray().ToArray();
    }
    private static JsonElement Summary(JsonElement[] reports, string id) => Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == id).GetProperty("summary");
    [Fact]
    public async Task AllEightToolsAreRegisteredAndPipelineEligible()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog"); var registry = factory.Services.GetRequiredService<VisionNodeRegistry>();
        Assert.Equal(8, FeatureRegionCatalog.Items.Count);
        foreach (var item in FeatureRegionCatalog.Items)
        {
            Assert.Single(catalog.EnumerateArray(), n => n.GetProperty("type").GetString() == item.Type);
            Assert.Equal(item.Type, registry.Require(item.Type).Catalog.Type);
            Assert.True(VisionExecutionPolicy.IsPipelineEligible(BuiltInNodeCatalog.Require(item.Type)));
            Assert.Empty(BuiltInNodeCatalog.Require(item.Type).Actions!);
        }
        Assert.True(BuiltInNodeCatalog.Items.Count >= 137);
    }
    [Theory]
    [InlineData("small-feature-workflow.json", false, "OK")]
    [InlineData("small-feature-workflow.json", true, "NG")]
    [InlineData("region-shape-workflow.json", false, "OK")]
    [InlineData("region-shape-workflow.json", true, "NG")]
    public async Task ExamplesRunBothToleranceOutcomes(string file, bool reject, string disposition)
    {
        var graph = await Sample(file);
        if (reject) { Parameters(graph, "tolerance")["min"] = 10000; Parameters(graph, "tolerance")["max"] = 20000; }
        var reports = await Run(graph);
        Assert.Equal(disposition, Summary(reports, "result").GetProperty("disposition").GetString());
        if (file.StartsWith("small", StringComparison.Ordinal))
        {
            Assert.Equal(240, Summary(reports, "histogram").GetProperty("pixelCount").GetInt32());
            Assert.Equal(255, Summary(reports, "histogram").GetProperty("mean").GetDouble());
            foreach (var id in new[] { "top", "bottom", "gradient" }) Assert.Equal("JLVision", Summary(reports, id).GetProperty("algorithmBackend").GetString());
        }
        else
        {
            Assert.InRange(Summary(reports, "gap").GetProperty("area").GetDouble(), 1000, 2500);
            Assert.True(Summary(reports, "boundary").GetProperty("found").GetBoolean());
            Assert.True(Summary(reports, "skeleton").GetProperty("found").GetBoolean());
        }
    }
    [Fact]
    public async Task EmptyInspectionDomainTakesNgWithoutReadingMissingMean()
    {
        var graph = await Sample("small-feature-workflow.json"); Parameters(graph, "slit")["roi"]!["width"] = 0;
        var reports = await Run(graph);
        Assert.Equal("NG", Summary(reports, "missing").GetProperty("disposition").GetString());
        Assert.False(Summary(reports, "histogram").GetProperty("found").GetBoolean());
        Assert.DoesNotContain(reports, r => r.GetProperty("nodeId").GetString() == "tolerance");
    }
    [Fact]
    public async Task EmptyRegionShapeSafelyProducesNg()
    {
        var graph = await Sample("region-shape-workflow.json");
        Parameters(graph, "source")["roi"] = JsonSerializer.SerializeToNode(new { type = "rectangle", x = 20, y = 20, width = 0, height = 20 });
        var reports = await Run(graph);
        Assert.Equal("NG", Summary(reports, "result").GetProperty("disposition").GetString());
        Assert.Equal(0, Summary(reports, "gap").GetProperty("area").GetDouble());
    }
}
