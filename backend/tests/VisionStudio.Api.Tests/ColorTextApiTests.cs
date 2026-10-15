using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

public sealed class ColorTextApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HostPublishesAllEightColorEnhancementAndTextTools()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var registry = factory.Services.GetRequiredService<VisionNodeRegistry>();
        Assert.Equal(8, ColorTextCatalog.Items.Count);
        foreach (var item in ColorTextCatalog.Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal(item.Category, actual.GetProperty("category").GetString());
            Assert.Equal(item.Type, registry.Require(item.Type).Catalog.Type);
            Assert.True(VisionExecutionPolicy.IsPipelineEligible(BuiltInNodeCatalog.Require(item.Type)));
            Assert.Empty(BuiltInNodeCatalog.Require(item.Type).Actions!);
        }
        Assert.True(BuiltInNodeCatalog.Items.Count >= 121);
    }

    [Theory]
    [InlineData("color-enhancement-workflow.json", false, "OK")]
    [InlineData("color-enhancement-workflow.json", true, "NG")]
    [InlineData("text-validation-workflow.json", false, "OK")]
    [InlineData("text-validation-workflow.json", true, "NG")]
    public async Task ShippedSamplesRunBothInspectionOutcomes(string filename, bool reject, string disposition)
    {
        var workflow = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", filename)))!;
        if (reject)
        {
            if (filename.StartsWith("text", StringComparison.Ordinal))
                workflow["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "match")!["parameters"]!["text"] = "LOT-BAD";
            else
            {
                var tolerance = workflow["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "tolerance")!["parameters"]!;
                tolerance["min"] = 1000; tolerance["max"] = 2000;
            }
        }
        var response = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var reports = result.GetProperty("nodeReports").EnumerateArray().ToArray();
        Assert.Equal(disposition, Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "result").GetProperty("summary").GetProperty("disposition").GetString());
        if (filename.StartsWith("color", StringComparison.Ordinal))
        {
            Assert.Equal(12, reports.Length);
            var difference = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "difference");
            Assert.Equal("JLVision", difference.GetProperty("summary").GetProperty("algorithmBackend").GetString());
            Assert.True(difference.GetProperty("summary").GetProperty("found").GetBoolean());
            Assert.Equal("srgb-constant", difference.GetProperty("summary").GetProperty("referenceSource").GetString());
            Assert.InRange(difference.GetProperty("summary").GetProperty("deltaE").GetDouble(), 1, 30);
        }
        else
        {
            var regex = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "regex");
            Assert.Equal(!reject, regex.GetProperty("summary").GetProperty("pass").GetBoolean());
            if (!reject)
            {
                var groups = JsonDocument.Parse(regex.GetProperty("outputs").GetProperty("groupsJson").GetProperty("value").GetString()!);
                Assert.Equal("20261015", groups.RootElement.GetProperty("date").GetString());
            }
        }
    }

    [Fact]
    public async Task EmptyColorSelectionTakesNgBranchWithoutReadingAbsentDeltaE()
    {
        var workflow = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "color-enhancement-workflow.json")))!;
        workflow["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "range")!["parameters"] = JsonSerializer.SerializeToNode(new { space = "rgb", min1 = 0, max1 = 0, min2 = 1, max2 = 1, min3 = 0, max3 = 0 });
        var response = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var reports = result.GetProperty("nodeReports").EnumerateArray().ToArray();
        Assert.DoesNotContain(reports, r => r.GetProperty("nodeId").GetString() == "tolerance");
        Assert.Equal("NG", Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "missing").GetProperty("summary").GetProperty("disposition").GetString());
    }
}
