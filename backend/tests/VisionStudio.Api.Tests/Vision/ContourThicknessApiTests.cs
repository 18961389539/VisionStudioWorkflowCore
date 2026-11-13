using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisionStudio.Api.Tests;

public sealed class ContourThicknessApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("shortest", 10, "OK")]
    [InlineData("shortest", 20, "NG")]
    [InlineData("innernormal", 10, "OK")]
    public async Task ExtractedContourTemplateRunsToToleranceResult(string mode, double minimum, string disposition)
    {
        var workflow = await Workflow();
        Parameters(workflow, "thickness")["measurementMode"] = mode;
        Parameters(workflow, "thickness")["minThickness"] = minimum;
        var result = await Run(workflow);
        var wall = Report(result, "thickness");
        var summary = wall.GetProperty("summary");
        Assert.True(summary.GetProperty("found").GetBoolean(), string.Join("\n", result.GetProperty("nodeReports").EnumerateArray()
            .Select(report => report.GetProperty("nodeId").GetString() + ": " + report.GetProperty("summary").ToString())));
        Assert.InRange(summary.GetProperty("minThickness").GetDouble(), 13, 16);
        Assert.Equal(disposition, Report(result, "result").GetProperty("summary").GetProperty("disposition").GetString());
        Assert.True(wall.GetProperty("outputs").TryGetProperty("minLine", out _));
        Assert.DoesNotContain(result.GetProperty("nodeReports").EnumerateArray(), report => report.GetProperty("nodeId").GetString() == "missing");
    }

    [Fact]
    public async Task MissingHoleTakesNgBranchWithoutReadingOptionalVerdict()
    {
        var workflow = await Workflow();
        Parameters(workflow, "innerContour")["minArea"] = 999999;
        var result = await Run(workflow);
        var wall = Report(result, "thickness");
        Assert.False(wall.GetProperty("summary").GetProperty("found").GetBoolean());
        Assert.False(wall.GetProperty("outputs").TryGetProperty("minThickness", out _));
        Assert.Equal("NG", Report(result, "missing").GetProperty("summary").GetProperty("disposition").GetString());
        Assert.DoesNotContain(result.GetProperty("nodeReports").EnumerateArray(), report => report.GetProperty("nodeId").GetString() == "result");
    }

    [Fact]
    public async Task InvalidThicknessLimitsAreRejectedBeforeExecution()
    {
        var workflow = await Workflow();
        Parameters(workflow, "thickness")["minThickness"] = 20;
        Parameters(workflow, "thickness")["maxThickness"] = 10;
        var response = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("maxThickness", await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> Run(JsonNode workflow)
    {
        var response = await _client.PostAsJsonAsync("/api/run", workflow);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var result = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(result.GetProperty("success").GetBoolean(), text);
        return result;
    }
    private static async Task<JsonNode> Workflow() => JsonNode.Parse(await File.ReadAllTextAsync(
        Path.Combine(AppContext.BaseDirectory, "samples", "contour-thickness-workflow.json")))!;
    private static JsonNode Parameters(JsonNode workflow, string id) => workflow["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == id)!["parameters"]!;
    private static JsonElement Report(JsonElement result, string id) => Assert.Single(result.GetProperty("nodeReports").EnumerateArray(),
        report => report.GetProperty("nodeId").GetString() == id);
}
