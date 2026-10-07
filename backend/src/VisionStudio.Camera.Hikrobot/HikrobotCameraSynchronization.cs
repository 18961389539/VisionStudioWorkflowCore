using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Hikrobot;

public sealed partial class HikrobotMvsCameraDevice
{
    public async Task<CameraTimeSynchronizationStatus> GetTimeSynchronizationStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireFeatureCamera();
            var enabled = ReadBool("GevIEEE1588") ?? false;
            var rawStatus = TryReadStringNode(_camera!, "GevIEEE1588Status")
                ?? ReadEnumText("GevIEEE1588Status")
                ?? ReadEnumText("PtpStatus");
            var state = MapHikPtpState(rawStatus, enabled);
            var offset = ReadInteger("GevIEEE1588OffsetFromMaster");
            var rawTimestamp = ReadInteger("GevTimestampValue");
            var tickHz = ReadInteger("GevTimestampTickFrequency");
            long? timestampNs = rawTimestamp is > 0 && tickHz is > 0
                ? (long?)Math.Round(rawTimestamp.Value * (1_000_000_000d / tickHz.Value))
                : null;
            var master = TryReadStringNode(_camera!, "GevIEEE1588ParentClockId");
            return new CameraTimeSynchronizationStatus(Id, Driver, true, enabled, state, offset, timestampNs, master, tickHz, CapturedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new CameraTimeSynchronizationStatus(Id, Driver, true, false, CameraPtpClockState.Faulted, Error: ex.Message, CapturedAt: DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    private string? ReadEnumText(string key)
    {
        var numeric = ReadEnumNumeric(key);
        return numeric?.ToString();
    }

    private static CameraPtpClockState MapHikPtpState(string? raw, bool enabled)
    {
        if (!enabled) return CameraPtpClockState.Disabled;
        var text = raw?.Replace("_", string.Empty).Replace("-", string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "locked" => CameraPtpClockState.Locked,
            "slave" or "8" => CameraPtpClockState.Slave,
            "master" or "5" => CameraPtpClockState.Master,
            "initializing" or "uncalibrated" or "premaster" or "0" or "4" or "7" => CameraPtpClockState.Initializing,
            "listening" or "passive" or "3" or "6" => CameraPtpClockState.Listening,
            "faulty" or "faulted" or "1" => CameraPtpClockState.Faulted,
            "disabled" or "2" => CameraPtpClockState.Disabled,
            _ => CameraPtpClockState.Unknown
        };
    }
}
