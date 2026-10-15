using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

public sealed class AlignmentRegionApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HostRegistersAllEightNodesWithMatchingRuntimeContracts()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var registry = factory.Services.GetRequiredService<VisionNodeRegistry>();
        Assert.Equal(8, AlignmentRegionCatalog.Items.Count);
        foreach (var item in AlignmentRegionCatalog.Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal(item.Category, actual.GetProperty("category").GetString());
            Assert.Equal(item.Type, registry.Require(item.Type).Catalog.Type);
            Assert.True(VisionExecutionPolicy.IsPipelineEligible(BuiltInNodeCatalog.Require(item.Type)));
            Assert.Empty(BuiltInNodeCatalog.Require(item.Type).Actions!);
        }
        Assert.True(BuiltInNodeCatalog.Items.Count >= 113);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShippedAlignmentGraphExecutesMatchedAndMissingBranches(bool missing)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "alignment-region-workflow.json"));
        var workflow = JsonNode.Parse(json)!;
        if (missing)
        {
            var patch = workflow["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "register")!["parameters"]!;
            patch["x"] = 460; patch["y"] = 50; patch["width"] = 100; patch["height"] = 100;
        }
        var response = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var reports = result.GetProperty("nodeReports").EnumerateArray().ToArray();
        Assert.Equal(!missing, Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "register").GetProperty("summary").GetProperty("found").GetBoolean());
        var disposition = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == (missing ? "missing" : "result")).GetProperty("summary").GetProperty("disposition").GetString();
        Assert.Equal(missing ? "NG" : "OK", disposition);
        if (missing) Assert.DoesNotContain(reports, r => r.GetProperty("nodeId").GetString() == "warp");
        else
        {
            var properties = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "properties");
            Assert.True(properties.GetProperty("outputs").GetProperty("area").GetProperty("value").GetDouble() > 19000);
            var detections = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "detections");
            Assert.Equal(1, detections.GetProperty("summary").GetProperty("count").GetInt32());
        }
    }
}
