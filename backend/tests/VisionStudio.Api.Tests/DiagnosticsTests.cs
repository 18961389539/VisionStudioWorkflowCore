using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api.Diagnostics;
using VisionStudio.Engine.Diagnostics;

namespace VisionStudio.Api.Tests;

public sealed class DiagnosticsTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;
    public DiagnosticsTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Diagnostics_Assets_NormalizeCameraDeviceAndRobotManagers()
    {
        // 服务端将 AssetKind 等枚举序列化为字符串，客户端读取需使用同一枚举约定，
        // 否则枚举字段反序列化会抛 JsonException（此前测试使用裸默认选项）。
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var assets = await _client.GetFromJsonAsync<List<AssetHealthSnapshot>>("/api/diagnostics/assets", jsonOptions);
        Assert.NotNull(assets);
        Assert.Contains(assets!, x => x.Kind == AssetKind.Camera && x.Id == "virtual-1");
        Assert.Contains(assets!, x => x.Kind == AssetKind.Device && x.Id == "virtual-modbus-1");
        Assert.Contains(assets!, x => x.Kind == AssetKind.Robot);
    }

    [Fact]
    public async Task Diagnostics_Summary_AndEvents_AreExposed()
    {
        var summary = await _client.GetAsync("/api/diagnostics/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var events = await _client.GetAsync("/api/diagnostics/events?take=25");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
    }

    [Fact]
    public void HealthLevel_IsAProtocolNeutralContract()
    {
        Assert.Contains(AssetHealthLevel.Healthy, Enum.GetValues<AssetHealthLevel>());
        Assert.Contains(AssetHealthLevel.Degraded, Enum.GetValues<AssetHealthLevel>());
        Assert.Contains(AssetHealthLevel.Faulted, Enum.GetValues<AssetHealthLevel>());
        Assert.Contains(AssetHealthLevel.Offline, Enum.GetValues<AssetHealthLevel>());
    }

    [Fact]
    public async Task SamplingFailure_RetainsLastKnownAssets_AndMarksProviderStale()
    {
        var provider = new FlakyProvider();
        // hub 传 null：SafeBroadcastAsync 会吞掉广播失败，本测试只关心采样与汇总状态机
        var service = new DiagnosticsCenterService(
            new[] { provider },
            hub: null!,
            NullLogger<DiagnosticsCenterService>.Instance,
            new ConfigurationBuilder().Build());

        await service.SampleAsync(default);
        Assert.Single(service.Assets());
        Assert.Empty(service.Summary().StaleProviders);

        // 采样失败 ≠ 资产删除：资产保留、provider 标记为过期、不得发布 AssetRemoved
        provider.Failing = true;
        await service.SampleAsync(default);
        Assert.Single(service.Assets());
        Assert.Equal(new[] { "flaky" }, service.Summary().StaleProviders);
        Assert.DoesNotContain(service.Events(), x => x.Type == "AssetRemoved");

        // 恢复采样：过期标记清除，资产保持
        provider.Failing = false;
        await service.SampleAsync(default);
        Assert.Empty(service.Summary().StaleProviders);
        Assert.Single(service.Assets());
    }

    private sealed class FlakyProvider : IAssetHealthProvider
    {
        public string ProviderId => "flaky";
        public bool Failing { get; set; }

        public IReadOnlyList<AssetHealthSnapshot> Snapshot() => Failing
            ? throw new InvalidOperationException("sampling down")
            : [new AssetHealthSnapshot(AssetKind.Camera, "cam-1", "Cam 1", "virtual", AssetHealthLevel.Healthy, "Open", true, "Idle", null, new Dictionary<string, double>(), DateTimeOffset.UtcNow)];
    }
}
