using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class RegionProcessingApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ShippedRegionGraphExecutesAndPreservesStructuredDomains()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "region-processing-workflow.json"));
        var workflow = JsonSerializer.Deserialize<JsonElement>(json);
        var response = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var reports = result.GetProperty("nodeReports").EnumerateArray().ToArray();
        Assert.Equal(10, reports.Length);
        var filter = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "filter");
        Assert.Equal("JLVision", filter.GetProperty("summary").GetProperty("algorithmBackend").GetString());
        var region = filter.GetProperty("outputs").GetProperty("region");
        Assert.Equal("Region", region.GetProperty("type").GetString());
        Assert.Equal(1, region.GetProperty("value").GetProperty("components").GetArrayLength());
        Assert.True(region.GetProperty("value").GetProperty("components")[0].GetProperty("runs").GetArrayLength() > 0);
        var domain = Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "domain");
        var scoped = domain.GetProperty("outputs").GetProperty("domainImage");
        Assert.Equal("DomainImage", scoped.GetProperty("type").GetString());
        Assert.Equal(640, scoped.GetProperty("value").GetProperty("domain").GetProperty("width").GetInt32());
        Assert.Equal("OK", Assert.Single(reports, r => r.GetProperty("nodeId").GetString() == "result").GetProperty("summary").GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task HostPublishesAllSixRegionNodesWithTypedPorts()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        foreach (var item in RegionCatalog.Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal("Region", actual.GetProperty("category").GetString());
        }
        var reduce = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "image.reduceDomain");
        Assert.Contains(reduce.GetProperty("outputs").EnumerateArray(), port => port.GetProperty("dataType").GetString() == "DomainImage");
    }
}
