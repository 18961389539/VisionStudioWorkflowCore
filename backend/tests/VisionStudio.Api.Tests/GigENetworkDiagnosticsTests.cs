using System.Net;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Tests;

public sealed class GigENetworkDiagnosticsTests
{
    [Fact]
    public void SameSubnet_UsesHostMaskAndConservativeSlash24Fallback()
    {
        Assert.True(GigENetworkDiagnosticsService.SameSubnet(
            IPAddress.Parse("192.168.10.55"), IPAddress.Parse("192.168.10.2"), IPAddress.Parse("255.255.255.0")));
        Assert.False(GigENetworkDiagnosticsService.SameSubnet(
            IPAddress.Parse("192.168.11.55"), IPAddress.Parse("192.168.10.2"), IPAddress.Parse("255.255.255.0")));
        Assert.True(GigENetworkDiagnosticsService.SameSubnet(
            IPAddress.Parse("10.20.30.40"), IPAddress.Parse("10.20.30.1"), null));
    }

    [Fact]
    public void BuildTransportTrend_ComputesVendorCounterDeltasAndResendRate()
    {
        var t0 = DateTimeOffset.UtcNow;
        CameraTransportTelemetry T(long frames, long lost, long packets, long lostPackets, long resends, double mbps, DateTimeOffset at)
            => new("cam-1", "basler-pylon", true, "vendor", at,
                ReceivedFrames: frames, LostFrames: lost, ReceivedPackets: packets, LostPackets: lostPackets,
                ResendRequests: resends, ThroughputMbps: mbps);
        CameraSynchronizationRunRecord R(string id, DateTimeOffset at, CameraTransportTelemetry telemetry)
            => new(id, "group-1", "hash", "test", false, at.AddMilliseconds(-5), at, 5, null, "DevicePtp", 10, 100, true,
                "Completed", null, null, [], [], [telemetry]);

        var trend = GigENetworkDiagnosticsService.BuildTransportTrend([
            R("r1", t0, T(100, 1, 1000, 2, 4, 300, t0)),
            R("r2", t0.AddSeconds(1), T(200, 2, 2000, 4, 14, 320, t0.AddSeconds(1)))
        ]);

        Assert.Equal(2, trend.Count);
        Assert.Equal(100, trend[1].ReceivedFramesDelta);
        Assert.Equal(1, trend[1].LostFramesDelta);
        Assert.Equal(1000, trend[1].ReceivedPacketsDelta);
        Assert.Equal(2, trend[1].LostPacketsDelta);
        Assert.Equal(10, trend[1].ResendRequestsDelta);
        Assert.InRange(trend[1].ResendRequestRate!.Value, 0.0099, 0.0101);
        Assert.Equal(320, trend[1].TotalThroughputMbps, 3);
    }

    [Fact]
    public void BuildTransportTrend_TreatsCounterResetAsNewCounterEpoch()
    {
        var t0 = DateTimeOffset.UtcNow;
        CameraSynchronizationRunRecord R(string id, DateTimeOffset at, long frames)
            => new(id, "group-1", "hash", "test", false, at.AddMilliseconds(-5), at, 5, null, "DevicePtp", 10, 100, true,
                "Completed", null, null, [], [], [new CameraTransportTelemetry("cam-1", "hikrobot-mvs", true, "vendor", at, ReceivedFrames: frames, ThroughputMbps: 100)]);

        var trend = GigENetworkDiagnosticsService.BuildTransportTrend([
            R("before-restart", t0, 1000),
            R("after-restart", t0.AddSeconds(1), 25)
        ]);

        Assert.Equal(25, trend[1].ReceivedFramesDelta);
    }
}
