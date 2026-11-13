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
        var failureBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, failureBody);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var reports = result.GetProperty("nodeReports").EnumerateArray().ToArray();
        Assert.Equal(11, reports.Length);
        var compare = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "compare");
        Assert.Equal("hybrid", compare.GetProperty("summary").GetProperty("algorithmBackend").GetString());
        Assert.Equal(!identical, compare.GetProperty("summary").GetProperty("found").GetBoolean());
        var stats = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "stats");
        if (identical)
        {
            var outputs = stats.GetProperty("outputs");
            Assert.False(outputs.GetProperty("found").GetProperty("value").GetBoolean());
            Assert.Equal(0, outputs.GetProperty("pixelCount").GetProperty("value").GetInt64());
            Assert.Equal(0, outputs.GetProperty("mean").GetProperty("value").GetDouble());
        }
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
