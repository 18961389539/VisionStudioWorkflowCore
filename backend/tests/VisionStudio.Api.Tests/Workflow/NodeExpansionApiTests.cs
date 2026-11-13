using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class NodeExpansionApiTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;
    public NodeExpansionApiTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task HostRegistersEveryBuiltInCatalogItem()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var registered = catalog.EnumerateArray().Select(item => item.GetProperty("type").GetString()).ToHashSet();
        foreach (var item in BuiltInNodeCatalog.Items) Assert.Contains(item.Type, registered);
    }

    [Fact]
    public async Task NewNodesExecuteThroughApiAndPreserveStructuredDetections()
    {
        var nodes = new object[]
        {
            new { id = "source", type = "image.synthetic", parameters = new { } },
            new { id = "gray", type = "image.grayscale", parameters = new { } },
            new { id = "threshold", type = "image.threshold.otsu", parameters = new { } },
            new { id = "blobs", type = "measure.blobs", parameters = new { minArea = 1000 } },
            new { id = "range", type = "value.range", parameters = new { min = 1, max = 5 } },
            new { id = "result", type = "flow.result", parameters = new { } }
        };
        var ids = new[] { "source", "gray", "threshold", "blobs", "range", "result" };
        var edges = new List<object>();
        for (var i = 0; i < ids.Length - 1; i++)
        {
            edges.Add(new { id = $"c{i}", sourceNodeId = ids[i], sourcePort = "next", targetNodeId = ids[i + 1], targetPort = "exec", kind = "control" });
            edges.Add(new { id = $"d{i}", sourceNodeId = ids[i], sourcePort = i < 3 ? "image" : i == 3 ? "count" : "pass", targetNodeId = ids[i + 1], targetPort = i < 3 ? "image" : i == 3 ? "value" : "pass", kind = "data" });
        }
        var response = await _client.PostAsJsonAsync("/api/run", new { id = "expanded-node-api", name = "Expanded Inspection", nodes, edges });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var reports = result.GetProperty("nodeReports").EnumerateArray().ToArray();
        Assert.Equal(6, reports.Length);
        var blob = Assert.Single(reports, report => report.GetProperty("nodeId").GetString() == "blobs");
        Assert.Equal(JsonValueKind.Array, blob.GetProperty("outputs").GetProperty("detections").GetProperty("value").ValueKind);
        Assert.Equal("OK", Assert.Single(reports, report => report.GetProperty("nodeId").GetString() == "result").GetProperty("summary").GetProperty("disposition").GetString());
    }
}
