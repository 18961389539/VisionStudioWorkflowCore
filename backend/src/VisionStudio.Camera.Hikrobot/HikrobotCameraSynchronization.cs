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
            var state = CameraPtpMapping.MapState(rawStatus, enabled, includeGenICamNumericCodes: true);
            var offset = ReadInteger("GevIEEE1588OffsetFromMaster");
            var rawTimestamp = ReadInteger("GevTimestampValue");
            var tickHz = ReadInteger("GevTimestampTickFrequency");
            var timestampNs = CameraPtpMapping.TicksToNanoseconds(rawTimestamp, tickHz);
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
}
