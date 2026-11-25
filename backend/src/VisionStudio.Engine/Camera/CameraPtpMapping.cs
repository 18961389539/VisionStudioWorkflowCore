namespace VisionStudio.Engine.Camera;

/// <summary>
/// Shared IEEE 1588 PTP state normalization and device-tick-to-nanoseconds conversion
/// for vendor camera adapters (Basler pylon / Hikrobot MVS).
/// </summary>
public static class CameraPtpMapping
{
    public static CameraPtpClockState MapState(string? raw, bool enabled, bool includeGenICamNumericCodes = false)
    {
        if (!enabled) return CameraPtpClockState.Disabled;
        var text = raw?.Replace("_", string.Empty).Replace("-", string.Empty).Trim().ToLowerInvariant();
        if (includeGenICamNumericCodes)
        {
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

    public static long? TicksToNanoseconds(long? rawTimestamp, long? tickHz, bool fallbackRawWhenEnabled = false, bool enabled = false)
    {
        if (rawTimestamp is not > 0) return null;
        if (tickHz is > 0)
            return (long?)Math.Round(rawTimestamp.Value * (1_000_000_000d / tickHz.Value));
        return fallbackRawWhenEnabled && enabled ? rawTimestamp : null;
    }
}
