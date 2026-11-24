using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Api.Spc;

namespace VisionStudio.Api.Tests;

/// <summary>SPC 只读接口：从 output.database 落库记录一路读到控制图响应。</summary>
public sealed class SpcEndpointsTests
{
    [Fact]
    public async Task DatasetsAndSeriesFlowFromResultRecords()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IResultRecordStore>();
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        for (var index = 0; index < 25; index++)
            await store.AppendAsync(new ResultRecord(
                $"spc-rec-{index}", "spc-e2e", $"SN-{index}",
                $$"""{"type":"Double","value":{{10 + index % 5}}}""",
                "run-spc", null, "db", start.AddSeconds(index)));

        var datasets = await client.GetFromJsonAsync<List<JsonElement>>("/api/spc/datasets?limit=50");
        var entry = Assert.Single(datasets!, item => item.GetProperty("dataset").GetString() == "spc-e2e");
        Assert.Equal(25, entry.GetProperty("recordCount").GetInt32());
        Assert.True(entry.GetProperty("payloadBytes").GetInt64() > 0);

        var analysis = await client.GetFromJsonAsync<JsonElement>(
            "/api/spc/series?dataset=spc-e2e&chartType=individuals&subgroupSize=1&limit=100");
        Assert.Equal(25, analysis.GetProperty("subgroupCount").GetInt32());
        Assert.Equal(25, analysis.GetProperty("recordsUsed").GetInt32());
        Assert.Equal(25, analysis.GetProperty("primaryChart").GetProperty("points").GetArrayLength());
        Assert.Equal("movingRange", analysis.GetProperty("secondaryChart").GetProperty("kind").GetString());
        Assert.True(analysis.GetProperty("primaryChart").GetProperty("upperControlLimit").GetDouble()
            > analysis.GetProperty("mean").GetDouble());
    }

    [Fact]
    public async Task SubgroupChartsReadNumberListsAsRecords()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IResultRecordStore>();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        double[][] parts = [[10, 10.2, 9.8, 10.1, 9.9], [10.1, 10.3, 9.9, 10.2, 10], [9.9, 10.1, 9.7, 10, 9.8]];
        for (var index = 0; index < parts.Length; index++)
            await store.AppendAsync(new ResultRecord(
                $"spc-group-{index}", "spc-groups", $"SN-{index}",
                JsonSerializer.Serialize(new { type = "NumberList", value = parts[index] }),
                "run-spc", null, "db", start.AddSeconds(index)));

        var analysis = await client.GetFromJsonAsync<JsonElement>(
            "/api/spc/series?dataset=spc-groups&chartType=xbarR&subgroupSize=5&grouping=record");
        Assert.Equal(3, analysis.GetProperty("subgroupCount").GetInt32());
        Assert.Equal(15, analysis.GetProperty("samplesUsed").GetInt32());
        Assert.Equal("range", analysis.GetProperty("secondaryChart").GetProperty("kind").GetString());
        Assert.Equal(3, analysis.GetProperty("primaryChart").GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task InvalidChartParametersReturnProblemDetails400()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/spc/series?dataset=spc-e2e&chartType=xbarR&subgroupSize=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_argument", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingDatasetStillReturnsAnEmptyAnalysis()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/spc/series?dataset=does-not-exist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var analysis = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, analysis.GetProperty("samplesUsed").GetInt32());
        Assert.Empty(analysis.GetProperty("primaryChart").GetProperty("points").EnumerateArray());
        Assert.Contains(analysis.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetString()!.Contains("没有可用的数值样本", StringComparison.Ordinal));
    }
}