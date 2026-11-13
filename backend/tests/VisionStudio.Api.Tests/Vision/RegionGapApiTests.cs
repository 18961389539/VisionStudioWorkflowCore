using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 分割·轮廓·特征补全批（第十节）的注册契约。这批的核心价值是"数据形态"：
/// Roi 有了生产者、Region 与掩膜图双向、轮廓能回灌区域，所以额外锁端口类型。
/// </summary>
public sealed class RegionGapApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static IEnumerable<NodeCatalogItem> Items => RegionGapCatalog.Items.Concat(FeatureGapCatalog.Items);

    [Fact]
    public async Task HostRegistersAllRegionAndFeatureGapTools()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var registry = factory.Services.GetRequiredService<VisionNodeRegistry>();

        Assert.Equal(5, RegionGapCatalog.Items.Count);
        Assert.Equal(2, FeatureGapCatalog.Items.Count);
        foreach (var item in Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal(item.Category, actual.GetProperty("category").GetString());
            Assert.Equal(item.Type, registry.Require(item.Type).Catalog.Type);
            Assert.True(VisionExecutionPolicy.IsPipelineEligible(BuiltInNodeCatalog.Require(item.Type)));
            Assert.Empty(BuiltInNodeCatalog.Require(item.Type).Actions!);
        }
        Assert.True(BuiltInNodeCatalog.Items.Count >= 239);
    }

    [Fact]
    public async Task RoiProducersAndMaskBridgePublishTheExpectedTypes()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");

        static string OutputType(JsonElement entry, string port)
            => Assert.Single(entry.GetProperty("outputs").EnumerateArray(),
                candidate => candidate.GetProperty("name").GetString() == port).GetProperty("dataType").GetString()!;

        var fromGeometry = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "roi.fromGeometry");
        Assert.Equal("Roi", OutputType(fromGeometry, "roi"));

        var fromDetection = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "roi.fromDetection");
        Assert.Equal("Roi", OutputType(fromDetection, "roi"));

        var fromContour = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "region.fromContour");
        Assert.Equal("Region", OutputType(fromContour, "region"));

        var toMask = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "region.toMask");
        Assert.Equal("Image", OutputType(toMask, "image"));

        var convexity = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "contour.convexity");
        Assert.Equal("Double", OutputType(convexity, "maxDefectDepth"));

        var match = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "contour.matchXld");
        Assert.Equal("Double", OutputType(match, "score"));
    }
}
