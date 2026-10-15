using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

public sealed class GeometryToleranceApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static async Task<JsonNode> Sample(string file) => JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", file)))!;
    private static JsonNode Parameters(JsonNode workflow, string id) => workflow["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!;
    private async Task<JsonElement[]> Run(JsonNode workflow)
    {
        var response = await _client.PostAsJsonAsync("/api/run", workflow); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        return result.GetProperty("nodeReports").EnumerateArray().ToArray();
    }
    private static JsonElement Report(JsonElement[] reports, string id) => Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == id);

    [Fact]
    public async Task HostRegistersAllEightGeometryTools()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog"); var registry = factory.Services.GetRequiredService<VisionNodeRegistry>();
        Assert.Equal(8, GeometryToleranceCatalog.Items.Count);
        foreach (var item in GeometryToleranceCatalog.Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal(item.Category, actual.GetProperty("category").GetString()); Assert.Equal(item.Type, registry.Require(item.Type).Catalog.Type);
            Assert.True(VisionExecutionPolicy.IsPipelineEligible(BuiltInNodeCatalog.Require(item.Type))); Assert.Empty(BuiltInNodeCatalog.Require(item.Type).Actions!);
        }
        Assert.True(BuiltInNodeCatalog.Items.Count >= 129);
    }

    [Theory]
    [InlineData("circular-tolerance-workflow.json", false, "OK")]
    [InlineData("circular-tolerance-workflow.json", true, "NG")]
    [InlineData("edge-tolerance-workflow.json", false, "OK")]
    [InlineData("edge-tolerance-workflow.json", true, "NG")]
    public async Task ShippedExamplesRunBothToleranceOutcomes(string file, bool reject, string disposition)
    {
        var workflow = await Sample(file);
        if (reject) { Parameters(workflow, "tolerance")["min"] = 100; Parameters(workflow, "tolerance")["max"] = 200; }
        var reports = await Run(workflow); Assert.Equal(disposition, Report(reports, "result").GetProperty("summary").GetProperty("disposition").GetString());
        if (file.StartsWith("circular", StringComparison.Ordinal))
        {
            Assert.InRange(Report(reports, "wall").GetProperty("summary").GetProperty("minThickness").GetDouble(), 17.99, 18.01);
            Assert.InRange(Report(reports, "offset").GetProperty("summary").GetProperty("centerDistance").GetDouble(), 1.99, 2.01);
        }
        else
        {
            Assert.InRange(Report(reports, "deviation").GetProperty("summary").GetProperty("deviation").GetDouble(), 11.999, 12.001);
            Assert.InRange(Report(reports, "parallel").GetProperty("summary").GetProperty("deviation").GetDouble(), 1.999, 2.001);
            Assert.InRange(Report(reports, "perpendicular").GetProperty("summary").GetProperty("angleError").GetDouble(), 0, 1e-6);
        }
    }

    [Theory]
    [InlineData("circular-tolerance-workflow.json", "innerPoints")]
    [InlineData("edge-tolerance-workflow.json", "edgePoints")]
    public async Task MissingFitTakesNgWithoutReadingAbsentCircleOrLine(string file, string source)
    {
        var workflow = await Sample(file); Parameters(workflow, source)["pointsJson"] = "[]";
        var reports = await Run(workflow); Assert.Equal("NG", Report(reports, "missingFit").GetProperty("summary").GetProperty("disposition").GetString());
        Assert.DoesNotContain(reports, r => r.GetProperty("nodeId").GetString() == "tolerance");
    }

    [Fact]
    public async Task NoncontainedRingTakesNgWithoutReadingAbsentThickness()
    {
        var workflow = await Sample("circular-tolerance-workflow.json");
        var points = JsonSerializer.Deserialize<double[][]>(Parameters(workflow, "innerPoints")["pointsJson"]!.GetValue<string>())!;
        foreach (var point in points) point[0] += 100;
        Parameters(workflow, "innerPoints")["pointsJson"] = JsonSerializer.Serialize(points);
        var reports = await Run(workflow); Assert.Equal("NG", Report(reports, "missingWall").GetProperty("summary").GetProperty("disposition").GetString());
        Assert.DoesNotContain(reports, r => r.GetProperty("nodeId").GetString() == "offset");
    }

    [Fact]
    public async Task MissingReferenceIndexTakesDeviationNgBranch()
    {
        var workflow = await Sample("edge-tolerance-workflow.json"); Parameters(workflow, "deviation")["referenceIndex"] = 1;
        var reports = await Run(workflow); Assert.Equal("NG", Report(reports, "missingDeviation").GetProperty("summary").GetProperty("disposition").GetString());
        Assert.DoesNotContain(reports, r => r.GetProperty("nodeId").GetString() == "tolerance");
    }
}
