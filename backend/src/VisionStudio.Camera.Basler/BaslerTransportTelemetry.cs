using System.Globalization;
using System.Reflection;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Basler;

public sealed partial class BaslerPylonCameraDevice
{
    private CameraTransportTelemetry? _transportTelemetry;
    private DateTimeOffset _lastTransportTelemetryRefresh = DateTimeOffset.MinValue;

    private void RefreshTransportTelemetryCore(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastTransportTelemetryRefresh < TimeSpan.FromMilliseconds(500)) return;
        _lastTransportTelemetryRefresh = now;
        if (_camera is null || _streamGrabber is null)
        {
            _transportTelemetry = new CameraTransportTelemetry(Id, Driver, false, "basler-pylon-stream", now, Error: "Camera/stream grabber is not open.");
            return;
        }

        try
        {
            // Basler Statistic_* parameters live on the StreamGrabber parameter collection (PLStream),
            // not on the camera's GenICam node map. Read by the typed PLStream key when available and
            // fall back to string indexing for SDK variants that expose string keys.
            var totalBuffers = ReadStreamLong("Statistic_Total_Buffer_Count");
            var failedBuffers = ReadStreamLong("Statistic_Failed_Buffer_Count");
            var underruns = ReadStreamLong("Statistic_Buffer_Underrun_Count");
            var missedFrames = ReadStreamLong("Statistic_Missed_Frame_Count");
            var totalPackets = ReadStreamLong("Statistic_Total_Packet_Count");
            var failedPackets = ReadStreamLong("Statistic_Failed_Packet_Count");
            var resendRequests = ReadStreamLong("Statistic_Resend_Request_Count");
            var resendPackets = ReadStreamLong("Statistic_Resend_Packet_Count");
            var resynchronizations = ReadStreamLong("Statistic_Resynchronization_Count");

            // Device-link throughput is a camera/node-map value rather than a StreamGrabber statistic.
            var throughputBytesPerSecond = ReadLong("BslDeviceLinkCurrentThroughput") ?? ReadLong("DeviceLinkCurrentThroughput");
            var anyNative = new long?[]
            {
                totalBuffers, failedBuffers, underruns, missedFrames, totalPackets, failedPackets,
                resendRequests, resendPackets, resynchronizations, throughputBytesPerSecond
            }.Any(x => x is not null);

            _transportTelemetry = new CameraTransportTelemetry(
                Id,
                Driver,
                anyNative,
                "basler-pylon-stream",
                now,
                ReceivedFrames: totalBuffers,
                LostFrames: missedFrames,
                FailedFrames: failedBuffers,
                BufferUnderruns: underruns,
                ReceivedPackets: totalPackets,
                FailedPackets: failedPackets,
                ResendRequests: resendRequests,
                ResentPackets: resendPackets,
                Resynchronizations: resynchronizations,
                ThroughputMbps: throughputBytesPerSecond is null ? null : throughputBytesPerSecond.Value * 8d / 1_000_000d,
                Error: anyNative ? null : "pylon stream statistic parameters are unavailable for this transport/driver combination.");
        }
        catch (Exception ex)
        {
            _transportTelemetry = new CameraTransportTelemetry(Id, Driver, false, "basler-pylon-stream", now, Error: ex.Message);
        }
    }

    private long? ReadStreamLong(string keyName)
    {
        if (_streamGrabber is null) return null;
        try
        {
            var parameters = _streamGrabber.GetType().GetProperty("Parameters", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_streamGrabber);
            if (parameters is null) return null;

            object? key = null;
            var plStream = _assembly?.GetType("Basler.Pylon.PLStream", false);
            if (plStream is not null)
            {
                key = plStream.GetField(keyName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? plStream.GetProperty(keyName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }

            var parameter = key is null ? null : BaslerSdkReflection.Indexed(parameters, key);
            parameter ??= BaslerSdkReflection.Indexed(parameters, keyName);
            if (parameter is null) return null;

            var getter = parameter.GetType().GetMethod("GetValue", Type.EmptyTypes);
            var value = getter?.Invoke(parameter, null);
            if (value is null) return null;
            if (value is IConvertible) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return long.TryParse(BaslerSdkReflection.Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        catch { return null; }
    }
}
