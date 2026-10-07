using System.Globalization;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Basler;

public sealed partial class BaslerPylonCameraDevice
{
    public async Task<CameraTimeSynchronizationStatus> GetTimeSynchronizationStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireFeatureCamera();
            var supported = HasParameter("GevIEEE1588") || HasParameter("PtpEnable") || HasParameter("PtpStatus") || HasParameter("GevIEEE1588Status");
            if (!supported)
                return new CameraTimeSynchronizationStatus(Id, Driver, false, false, CameraPtpClockState.Unsupported, CapturedAt: DateTimeOffset.UtcNow);

            // Basler recommends latching the PTP dataset before reading related values when the node exists.
            _ = BaslerSdkReflection.TryExecuteCommand(_camera!, "PtpDataSetLatch")
                || BaslerSdkReflection.TryExecuteCommand(_camera!, "GevIEEE1588DataSetLatch");
            _ = BaslerSdkReflection.TryExecuteCommand(_camera!, "TimestampLatch")
                || BaslerSdkReflection.TryExecuteCommand(_camera!, "GevTimestampControlLatch");

            var enabled = ReadBoolAlias("PtpEnable", "GevIEEE1588") ?? false;
            var rawStatus = ReadText("PtpServoStatus") ?? ReadText("PtpStatus") ?? ReadText("GevIEEE1588StatusLatched") ?? ReadText("GevIEEE1588Status");
            var state = MapPtpState(rawStatus, enabled);
            var offset = ReadLong("GevIEEE1588OffsetFromMaster") ?? ReadLong("PtpOffsetFromMaster");
            var rawTimestamp = ReadLong("TimestampLatchValue") ?? ReadLong("GevTimestampValue");
            var tickHz = ReadLong("GevTimestampTickFrequency");
            long? timestampNs = rawTimestamp is > 0
                ? tickHz is > 0 ? (long?)Math.Round(rawTimestamp.Value * (1_000_000_000d / tickHz.Value))
                    : enabled ? rawTimestamp : null
                : null;
            var master = ReadText("PtpGrandmasterClockID") ?? ReadText("GevIEEE1588ParentClockId");
            return new CameraTimeSynchronizationStatus(Id, Driver, true, enabled, state, offset, timestampNs, master, tickHz, CapturedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new CameraTimeSynchronizationStatus(Id, Driver, true, false, CameraPtpClockState.Faulted, Error: ex.Message, CapturedAt: DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    private static CameraPtpClockState MapPtpState(string? raw, bool enabled)
    {
        if (!enabled) return CameraPtpClockState.Disabled;
        var text = raw?.Replace("_", string.Empty).Replace("-", string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "locked" => CameraPtpClockState.Locked,
            "slave" => CameraPtpClockState.Slave,
            "master" => CameraPtpClockState.Master,
            "initializing" or "uncalibrated" or "premaster" => CameraPtpClockState.Initializing,
            "listening" or "passive" => CameraPtpClockState.Listening,
            "faulty" or "faulted" => CameraPtpClockState.Faulted,
            "disabled" => CameraPtpClockState.Disabled,
            _ => CameraPtpClockState.Unknown
        };
    }
}
