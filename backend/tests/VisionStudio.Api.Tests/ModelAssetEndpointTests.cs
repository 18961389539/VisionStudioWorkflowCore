using System.Net;
using System.Net.Http.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 路由注册守卫：模型资产端点曾出现过"实现写好了但 MapModelAssetEndpoints 没挂上"的情况，
/// 这里只做只读探测（不写共享数据根），确保 /api/model-assets 已注册且按契约返回。
/// 存储行为与 409 引用检查由 <see cref="ShapeModelAssetStoreTests"/> 与端点实现覆盖。
/// </summary>
public sealed class ModelAssetEndpointTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public ModelAssetEndpointTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task ModelAssets_ListRouteIsMapped()
    {
        var response = await _client.GetAsync("/api/model-assets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var assets = await response.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        Assert.NotNull(assets);
    }

    [Fact]
    public async Task ModelAssets_UnknownIdAndVersionReturnNotFound()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/model-assets/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/model-assets/does-not-exist/versions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/model-assets/does-not-exist/versions/1/blob")).StatusCode);
    }
}
