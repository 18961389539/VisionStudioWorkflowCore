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
    public async Task DebugReferenceCaptureReturnsPersistentPixelsWithTheActualPose()
    {
        var response = await _client.PostAsJsonAsync("/api/debug/run", new
        {
            workflow = new
            {
                id = "roi-reference-api", name = "ROI reference capture",
                nodes = new object[]
                {
                    new { id = "source", type = "image.synthetic", parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 } },
                    new { id = "roi", type = "roi.transform", parameters = new { captureReference = true, currentX = 160.125, currentY = 120.25, currentThetaDeg = 15 } }
                },
                edges = new object[]
                {
                    new { id = "control", sourceNodeId = "source", sourcePort = "next", targetNodeId = "roi", targetPort = "exec", kind = "control" },
                    new { id = "image", sourceNodeId = "source", sourcePort = "image", targetNodeId = "roi", targetPort = "debugImage", kind = "data" }
                }
            },
            options = new { mode = "RunNode", targetNodeId = "roi" }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var report = result.GetProperty("nodeReports").EnumerateArray().Single(item => item.GetProperty("nodeId").GetString() == "roi");
        var summary = report.GetProperty("summary");
        Assert.True(summary.GetProperty("referenceCaptured").GetBoolean());
        Assert.Equal(160.125, summary.GetProperty("referenceX").GetDouble());
        Assert.Equal(15, summary.GetProperty("referenceThetaDeg").GetDouble());
        Assert.Equal(320, summary.GetProperty("referenceWidth").GetInt32());
        var png = Convert.FromBase64String(summary.GetProperty("referenceImage").GetString()!);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, png.Take(4).ToArray());
    }

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

    [Fact]
    public async Task Run_ExposesAndServesEachImageOutputPort()
    {
        var run = await _client.PostAsJsonAsync("/api/run", CropWorkflow());
        Assert.True(run.StatusCode == HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        using var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        var runId = runDoc.RootElement.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var catalog = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/images");
        var cropEntries = catalog.GetProperty("images").EnumerateArray()
            .Where(item => item.GetProperty("nodeId").GetString() == "crop").ToArray();
        var imageEntry = Assert.Single(cropEntries, item => item.GetProperty("portName").GetString() == "image");
        var maskEntry = Assert.Single(cropEntries, item => item.GetProperty("portName").GetString() == "roiMask");
        Assert.Equal("image/jpeg", imageEntry.GetProperty("contentType").GetString());
        Assert.Equal("image/png", maskEntry.GetProperty("contentType").GetString());

        // The legacy URL remains the primary cropped image; the mask endpoint is lossless.
        var image = await _client.GetByteArrayAsync($"/api/runs/{runId}/nodes/crop/image");
        using var maskResponse = await _client.GetAsync($"/api/runs/{runId}/nodes/crop/image?portName=roiMask");
        var mask = await maskResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal("image/png", maskResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0xFF, image[0]);
        Assert.Equal(0xD8, image[1]);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, mask.Take(4).ToArray());
        Assert.NotEqual(Convert.ToBase64String(image), Convert.ToBase64String(mask));
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/runs/{runId}/nodes/crop/image?portName=missing")).StatusCode);
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

    private static object CropWorkflow() => new
    {
        id = "node-image-multiple-ports",
        name = "Node Image Multiple Ports",
        nodes = new object[]
        {
            new { id = "source", type = "image.synthetic", parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 } },
            new { id = "crop", type = "image.crop", parameters = new { roi = new { type = "Circle", x = 160, y = 120, radius = 22 } } }
        },
        edges = new object[]
        {
            new { id = "c1", sourceNodeId = "source", sourcePort = "next", targetNodeId = "crop", targetPort = "exec", kind = "control" },
            new { id = "d1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "crop", targetPort = "image", kind = "data" }
        }
    };
}
