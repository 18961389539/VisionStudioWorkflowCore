using System.Net;
using System.Net.Http.Json;
using VisionStudio.Engine.Diagnostics;

namespace VisionStudio.Api.Tests;

public sealed class DiagnosticsTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;
    public DiagnosticsTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Diagnostics_Assets_NormalizeCameraDeviceAndRobotManagers()
    {
        var assets = await _client.GetFromJsonAsync<List<AssetHealthSnapshot>>("/api/diagnostics/assets");
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
}
