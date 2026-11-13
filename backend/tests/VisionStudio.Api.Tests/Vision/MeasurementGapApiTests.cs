using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 测量与几何专项补全批的注册契约：目录条目、执行器、pipeline 资格、无物理副作用四条必须同时成立。
/// 逐个节点断言而不是只数个数——漏注册一个只会让画布上多一个"放不下去"的工具。
/// </summary>
public sealed class MeasurementGapApiTests(VisionStudioApiFactory factory) : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HostRegistersAllMeasurementGapTools()
    {
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var registry = factory.Services.GetRequiredService<VisionNodeRegistry>();

        Assert.Equal(13, MeasurementGapCatalog.Items.Count);
        foreach (var item in MeasurementGapCatalog.Items)
        {
            var actual = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == item.Type);
            Assert.Equal(item.Category, actual.GetProperty("category").GetString());
            Assert.Equal(item.Type, registry.Require(item.Type).Catalog.Type);
            Assert.True(VisionExecutionPolicy.IsPipelineEligible(BuiltInNodeCatalog.Require(item.Type)));
            Assert.Empty(BuiltInNodeCatalog.Require(item.Type).Actions!);
        }
        Assert.True(BuiltInNodeCatalog.Items.Count >= 225);
    }

    [Fact]
    public async Task PointListPortBridgesContourAndProfileTolerance()
    {
        // 断链修复的端到端证据：轮廓 → 点集 → 轮廓度，全部在目录端口层面连通（不靠脚本桥接）
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var toPoints = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "contour.toPoints");
        Assert.Equal("PointList", Assert.Single(toPoints.GetProperty("outputs").EnumerateArray(),
            port => port.GetProperty("name").GetString() == "pointList").GetProperty("dataType").GetString());

        var fromPoints = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "contour.fromPoints");
        Assert.Equal("PointList", Assert.Single(fromPoints.GetProperty("inputs").EnumerateArray(),
            port => port.GetProperty("name").GetString() == "pointList").GetProperty("dataType").GetString());

        var profile = Assert.Single(catalog.EnumerateArray(), entry => entry.GetProperty("type").GetString() == "tolerance.profile");
        Assert.Equal("PointList", Assert.Single(profile.GetProperty("inputs").EnumerateArray(),
            port => port.GetProperty("name").GetString() == "samples").GetProperty("dataType").GetString());
    }
}
