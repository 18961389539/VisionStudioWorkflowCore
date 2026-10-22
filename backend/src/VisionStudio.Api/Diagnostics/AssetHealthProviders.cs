using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Diagnostics;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Diagnostics;

public sealed class CameraAssetHealthProvider(CameraManager cameras) : IAssetHealthProvider
{
    public string ProviderId => "camera";

    public IReadOnlyList<AssetHealthSnapshot> Snapshot() => cameras.List().Select(x =>
    {
        var level = x.State == CameraState.Faulted || x.Acquisition.AcquisitionState == CameraAcquisitionState.Faulted
            ? AssetHealthLevel.Faulted
            : x.State == CameraState.Streaming && x.Acquisition.AcquisitionState is CameraAcquisitionState.Running or CameraAcquisitionState.WaitingTrigger
                ? AssetHealthLevel.Healthy
                : x.Acquisition.AcquisitionState == CameraAcquisitionState.Reconnecting
                    ? AssetHealthLevel.Degraded
                    : x.State == CameraState.Closed ? AssetHealthLevel.Offline : AssetHealthLevel.Degraded;

        var metrics = new Dictionary<string, double>
        {
            ["actualFps"] = x.Acquisition.ActualFps,
            ["frames"] = x.Acquisition.FramesPublished,
            ["acquisitionErrors"] = x.Acquisition.AcquisitionErrors,
            ["reconnects"] = x.Acquisition.ReconnectCount,
            ["ringOverwrites"] = x.Acquisition.RingOverwrites,
            ["frameTimeouts"] = x.Acquisition.FrameTimeouts,
            ["driverDroppedFrames"] = x.Acquisition.DriverDroppedFrames
        };
        var transport = x.Acquisition.Transport;
        AddTransportMetric(metrics, "transportReceivedFrames", transport?.ReceivedFrames);
        AddTransportMetric(metrics, "transportLostFrames", transport?.LostFrames);
        AddTransportMetric(metrics, "transportFailedFrames", transport?.FailedFrames);
        AddTransportMetric(metrics, "transportBufferUnderruns", transport?.BufferUnderruns);
        AddTransportMetric(metrics, "transportLostPackets", transport?.LostPackets);
        AddTransportMetric(metrics, "transportFailedPackets", transport?.FailedPackets);
        AddTransportMetric(metrics, "transportResendRequests", transport?.ResendRequests);
        AddTransportMetric(metrics, "transportResentPackets", transport?.ResentPackets);
        AddTransportMetric(metrics, "transportResynchronizations", transport?.Resynchronizations);
        if (transport?.ThroughputMbps is { } throughput && double.IsFinite(throughput)) metrics["transportThroughputMbps"] = throughput;

        return new AssetHealthSnapshot(
            AssetKind.Camera, x.Id, x.Name, x.Driver, level,
            $"{x.State}/{x.Acquisition.AcquisitionState}",
            x.State is CameraState.Open or CameraState.Streaming,
            x.Settings.TriggerMode == CameraTriggerMode.Continuous ? $"{x.Acquisition.ActualFps:0.0} fps" : x.Settings.TriggerMode.ToString(),
            x.Error ?? x.Acquisition.RuntimeError,
            metrics,
            x.Acquisition.LastFrameAt ?? DateTimeOffset.UtcNow);
    }).ToArray();

    private static void AddTransportMetric(Dictionary<string, double> metrics, string key, long? value)
    {
        if (value is not null) metrics[key] = value.Value;
    }
}

public sealed class DeviceAssetHealthProvider(DeviceManager devices) : IAssetHealthProvider
{
    public string ProviderId => "device";

    public IReadOnlyList<AssetHealthSnapshot> Snapshot() => devices.List().Select(x =>
    {
        var level = x.ConnectionState switch
        {
            DeviceConnectionState.Connected when string.IsNullOrWhiteSpace(x.Error) && string.IsNullOrWhiteSpace(x.Stats.RuntimeError) => AssetHealthLevel.Healthy,
            DeviceConnectionState.Connected => AssetHealthLevel.Degraded,
            DeviceConnectionState.Connecting or DeviceConnectionState.Reconnecting => AssetHealthLevel.Degraded,
            DeviceConnectionState.Faulted => AssetHealthLevel.Faulted,
            _ => AssetHealthLevel.Offline
        };

        return new AssetHealthSnapshot(
            AssetKind.Device, x.Id, x.Name, x.Driver, level,
            x.ConnectionState.ToString(), x.ConnectionState == DeviceConnectionState.Connected,
            $"{x.Stats.ActualPollHz:0.0} Hz · {x.Values.Count}/{x.Tags.Count} tags",
            x.Error ?? x.Stats.RuntimeError ?? x.Diagnostics.LastProtocolError,
            new Dictionary<string, double>
            {
                ["pollHz"] = x.Stats.ActualPollHz,
                ["pollCycles"] = x.Stats.PollCycles,
                ["readErrors"] = x.Stats.ReadErrors,
                ["writeErrors"] = x.Stats.WriteErrors,
                ["reconnects"] = x.Stats.ReconnectCount,
                ["roundTripMs"] = x.Diagnostics.LastRoundTripMs
            },
            x.UpdatedAt);
    }).ToArray();
}

public sealed class RobotAssetHealthProvider(RobotManager robots) : IAssetHealthProvider
{
    public string ProviderId => "robot";

    public IReadOnlyList<AssetHealthSnapshot> Snapshot() => robots.List().Select(x =>
    {
        var level = x.ConnectionState == RobotConnectionState.Faulted || x.HandshakeState == RobotHandshakeState.Faulted || x.Handshake.Error
            ? AssetHealthLevel.Faulted
            : x.ConnectionState == RobotConnectionState.Connected
                ? (x.HandshakeState == RobotHandshakeState.Disconnected ? AssetHealthLevel.Degraded : AssetHealthLevel.Healthy)
                : x.ConnectionState == RobotConnectionState.Connecting ? AssetHealthLevel.Degraded : AssetHealthLevel.Offline;

        return new AssetHealthSnapshot(
            AssetKind.Robot, x.Id, x.Name, x.Driver, level,
            $"{x.ConnectionState}/{x.HandshakeState}", x.ConnectionState == RobotConnectionState.Connected,
            x.Busy ? $"Busy · command #{x.LastCommandId}" : x.InPosition ? "InPosition" : x.HandshakeState.ToString(),
            x.Error ?? x.Handshake.ErrorCode,
            new Dictionary<string, double>
            {
                ["lastCommandId"] = x.LastCommandId,
                ["busy"] = x.Busy ? 1 : 0,
                ["inPosition"] = x.InPosition ? 1 : 0,
                ["x"] = x.CurrentPose.X,
                ["y"] = x.CurrentPose.Y,
                ["thetaDeg"] = x.CurrentPose.ThetaDeg
            },
            x.UpdatedAt);
    }).ToArray();
}
