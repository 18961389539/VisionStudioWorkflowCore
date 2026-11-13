using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 图像预处理补全批的注册契约：目录条目、执行器、pipeline 资格、无物理副作用。
/// 这 7 个节点都用 OpenCV / 托管实现，不进 JLVision 聚合入口，所以要单独锁一遍。
/// </summary>
public sealed class PreprocessGapApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HostRegistersAllPreprocessGapTools()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var registry = factory.Services.GetRequiredService<VisionNodeRegistry>();

        Assert.Equal(7, PreprocessGapCatalog.Items.Count);
        foreach (var item in PreprocessGapCatalog.Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal(item.Category, actual.GetProperty("category").GetString());
            Assert.Equal("Preprocess", actual.GetProperty("category").GetString());
            Assert.Equal(item.Type, registry.Require(item.Type).Catalog.Type);
            Assert.True(VisionExecutionPolicy.IsPipelineEligible(BuiltInNodeCatalog.Require(item.Type)));
            Assert.Empty(BuiltInNodeCatalog.Require(item.Type).Actions!);
        }
        Assert.True(BuiltInNodeCatalog.Items.Count >= 232);
    }

    [Fact]
    public async Task PreprocessGapNodesPublishImageAndDiagnosticsPorts()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        foreach (var item in PreprocessGapCatalog.Items)
        {
            var entry = Assert.Single(catalog.EnumerateArray(), candidate => candidate.GetProperty("type").GetString() == item.Type);
            var outputs = entry.GetProperty("outputs").EnumerateArray().Select(port => port.GetProperty("name").GetString()).ToArray();
            Assert.Contains("diagnostics", outputs);
            if (item.Type == "image.qualityMetrics")
            {
                // 指标节点不产出图像，只出标量——这正是它能进自动曝光闭环的原因
                Assert.DoesNotContain("image", outputs);
                Assert.Contains("brightness", outputs);
                Assert.Contains("saturationRatio", outputs);
            }
            else Assert.Contains("image", outputs);
        }
    }
}
