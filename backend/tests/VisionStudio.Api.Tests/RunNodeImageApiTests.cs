using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Interactive run images: /api/run captures per-node JPEGs, exposes a catalog per run and serves
/// each node's image so the viewer can inspect intermediate results (input/output selection).
/// </summary>
public sealed class RunNodeImageApiTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public RunNodeImageApiTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Run_ExposesPerNodeImageCatalog_AndServesJpeg()
    {
        var run = await _client.PostAsJsonAsync("/api/run", Workflow());
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        var runId = runDoc.RootElement.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var catalog = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/images");
        var images = catalog.GetProperty("images");
        Assert.Equal(2, images.GetArrayLength());
        var byNode = images.EnumerateArray().ToDictionary(x => x.GetProperty("nodeId").GetString()!, x => x);
        Assert.Contains("source", byNode.Keys);
        Assert.Contains("threshold", byNode.Keys);
        Assert.Equal(320, byNode["source"].GetProperty("width").GetInt32());
        Assert.Equal(240, byNode["source"].GetProperty("height").GetInt32());
        Assert.Equal("image", byNode["source"].GetProperty("portName").GetString());
        Assert.True(byNode["threshold"].GetProperty("bytes").GetInt32() > 0);

        var image = await _client.GetAsync($"/api/runs/{runId}/nodes/source/image");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        var bytes = await image.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
        Assert.Equal(0xFF, bytes[0]); // JPEG SOI
        Assert.Equal(0xD8, bytes[1]);

        var missingNode = await _client.GetAsync($"/api/runs/{runId}/nodes/not-a-node/image");
        Assert.Equal(HttpStatusCode.NotFound, missingNode.StatusCode);

        var missingRun = await _client.GetAsync("/api/runs/no-such-run/images");
        Assert.Equal(HttpStatusCode.NotFound, missingRun.StatusCode);
    }

    private static object Workflow() => new
    {
        id = "node-image-smoke",
        name = "Node Image Smoke",
        nodes = new object[]
        {
            new { id = "source", type = "image.synthetic", name = "Synthetic", parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 } },
            new { id = "threshold", type = "image.threshold", name = "Threshold", parameters = new { threshold = 100 } }
        },
        edges = new object[]
        {
            new { id = "c1", sourceNodeId = "source", sourcePort = "next", targetNodeId = "threshold", targetPort = "exec", kind = "control" },
            new { id = "d1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "threshold", targetPort = "image", kind = "data" }
        }
    };
}
