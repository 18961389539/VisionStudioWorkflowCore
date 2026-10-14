using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class DefectDetectionApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData(false, "NG")]
    [InlineData(true, "OK")]
    public async Task ShippedDefectGraphExecutesBothInspectionPaths(bool identical, string disposition)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "defect-detection-workflow.json"));
        var workflow = JsonNode.Parse(json)!;
        if (identical) workflow["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "reference")!["parameters"]!["kernelSize"] = 1;
        var response = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var reports = result.GetProperty("nodeReports").EnumerateArray().ToArray();
        Assert.Equal(11, reports.Length);
        var compare = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "compare");
        Assert.Equal("JLVision", compare.GetProperty("summary").GetProperty("algorithmBackend").GetString());
        Assert.Equal(!identical, compare.GetProperty("summary").GetProperty("found").GetBoolean());
        var stats = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "stats");
        if (identical) Assert.False(stats.GetProperty("outputs").TryGetProperty("mean", out _));
        else Assert.True(stats.GetProperty("outputs").GetProperty("mean").GetProperty("value").GetDouble() >= 20);
        Assert.Equal(disposition, Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "result").GetProperty("summary").GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task HostPublishesAllFiveDefectNodes()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        foreach (var item in DefectCatalog.Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal("Defect", actual.GetProperty("category").GetString());
        }
    }
}
