using System.Reflection;
using System.Runtime.InteropServices;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Hikrobot;

public sealed partial class HikrobotMvsCameraDevice
{
    private CameraTransportTelemetry? _transportTelemetry;
    private DateTimeOffset _lastTransportTelemetryRefresh = DateTimeOffset.MinValue;

    private void RefreshTransportTelemetryCore(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastTransportTelemetryRefresh < TimeSpan.FromMilliseconds(500)) return;
        _lastTransportTelemetryRefresh = now;
        if (_camera is null || _myCameraType is null)
        {
            _transportTelemetry = new CameraTransportTelemetry(Id, Driver, false, "hikrobot-mvs-match-info", now, Error: "Camera is not open.");
            return;
        }

        try
        {
            var transport = _identity?.Transport ?? "unknown";
            var isGigE = string.Equals(transport, "GigE", StringComparison.OrdinalIgnoreCase);
            var typeName = isGigE ? "MV_MATCH_INFO_NET_DETECT" : "MV_MATCH_INFO_USB_DETECT";
            var matchType = isGigE ? 0x00000001u : 0x00000002u;
            var info = ReadMatchInfo(typeName, matchType);
            if (info is null)
            {
                _transportTelemetry = new CameraTransportTelemetry(Id, Driver, false, "hikrobot-mvs-match-info", now,
                    Error: $"MVS {typeName} telemetry is unavailable for transport '{transport}'.");
                return;
            }

            var receivedBytes = ReadCounter(info, "nReceiveDataSize", "nReviceDataSize");
            double? throughput = null;
            if (receivedBytes is not null && _transportTelemetry?.ReceivedBytes is { } previousBytes &&
                receivedBytes.Value >= previousBytes && now > _transportTelemetry.CapturedAt)
            {
                var seconds = (now - _transportTelemetry.CapturedAt).TotalSeconds;
                if (seconds > 0.001) throughput = (receivedBytes.Value - previousBytes) * 8d / seconds / 1_000_000d;
            }

            if (isGigE)
            {
                var receivedFrames = ReadCounter(info, "nNetRecvFrameCount");
                var lostFrames = ReadCounter(info, "nLostFrameCount", "nThrowFrameCount");
                var lostPackets = ReadCounter(info, "nLostPacketCount");
                var resendRequests = ReadCounter(info, "nRequestResendPacketCount");
                var resentPackets = ReadCounter(info, "nResendPacketCount");
                var native = new long?[] { receivedBytes, receivedFrames, lostFrames, lostPackets, resendRequests, resentPackets }.Any(x => x is not null);
                _transportTelemetry = new CameraTransportTelemetry(
                    Id, Driver, native, "hikrobot-mvs-net-detect", now,
                    ReceivedBytes: receivedBytes,
                    ReceivedFrames: receivedFrames,
                    LostFrames: lostFrames,
                    LostPackets: lostPackets,
                    ResendRequests: resendRequests,
                    ResentPackets: resentPackets,
                    ThroughputMbps: throughput,
                    Error: native ? null : "MVS network match info returned without recognized transport counters.");
            }
            else
            {
                var receivedFrames = ReadCounter(info, "nReceivedFrameCount", "nRevicedFrameCount");
                var failedFrames = ReadCounter(info, "nErrorFrameCount");
                var native = new long?[] { receivedBytes, receivedFrames, failedFrames }.Any(x => x is not null);
                _transportTelemetry = new CameraTransportTelemetry(
                    Id, Driver, native, "hikrobot-mvs-usb-detect", now,
                    ReceivedBytes: receivedBytes,
                    ReceivedFrames: receivedFrames,
                    FailedFrames: failedFrames,
                    ThroughputMbps: throughput,
                    Error: native ? null : "MVS USB match info returned without recognized transport counters.");
            }
        }
        catch (Exception ex)
        {
            _transportTelemetry = new CameraTransportTelemetry(Id, Driver, false, "hikrobot-mvs-match-info", now, Error: ex.Message);
        }
    }

    private object? ReadMatchInfo(string infoTypeName, uint fallbackMatchType)
    {
        if (_camera is null || _myCameraType is null) return null;
        var infoType = FindSdkType(infoTypeName);
        var allType = FindSdkType("MV_ALL_MATCH_INFO");
        if (infoType is null || allType is null) return null;
        var method = _myCameraType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(x => x.Name == "MV_CC_GetAllMatchInfo_NET" && x.GetParameters().Length == 1);
        if (method is null) return null;

        var matchType = fallbackMatchType;
        var constant = _myCameraType.GetField(infoTypeName.Contains("NET", StringComparison.OrdinalIgnoreCase)
                ? "MV_MATCH_TYPE_NET_DETECT" : "MV_MATCH_TYPE_USB_DETECT",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (constant?.GetValue(null) is { } rawConstant) matchType = Convert.ToUInt32(rawConstant);

        var all = Activator.CreateInstance(allType)!;
        var bytes = Marshal.SizeOf(infoType);
        var ptr = Marshal.AllocHGlobal(bytes);
        try
        {
            Marshal.Copy(new byte[bytes], 0, ptr, bytes);
            HikrobotSdkReflection.SetMember(all, "nType", matchType);
            HikrobotSdkReflection.SetMember(all, "pInfo", ptr);
            HikrobotSdkReflection.SetMember(all, "nInfoSize", (uint)bytes);
            object?[] args = [all];
            var ret = Convert.ToInt32(method.Invoke(_camera, args));
            if (ret != 0) return null;
            return Marshal.PtrToStructure(ptr, infoType);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private Type? FindSdkType(string name)
        => _myCameraType?.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
           ?? _assembly?.GetTypes().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

    private static long? ReadCounter(object info, params string[] names)
    {
        foreach (var name in names)
        {
            var value = HikrobotSdkReflection.Member(info, name);
            if (value is null) continue;
            try { return Convert.ToInt64(value); } catch { }
        }
        return null;
    }
}
