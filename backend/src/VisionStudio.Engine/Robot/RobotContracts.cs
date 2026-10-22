namespace VisionStudio.Engine.Robot;

public enum RobotConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}

/// <summary>
/// Protocol-neutral runtime state. Vendor-specific controller states remain inside adapters.
/// </summary>
public enum RobotHandshakeState
{
    Disconnected,
    Ready,
    TargetAccepted,
    Executing,
    InPosition,
    Stopped,
    Faulted
}

/// <summary>
/// Standard industrial handshake bits used by PLC bridges, TCP adapters and workflow diagnostics.
/// TargetReady/Execute are command-side bits; Busy/Complete/Error are robot-side status bits; Ack closes the cycle.
/// </summary>
public sealed record RobotHandshakeSignals(
    bool TargetReady = false,
    bool Execute = false,
    bool Busy = false,
    bool Complete = false,
    bool Error = false,
    bool Ack = false,
    long CommandId = 0,
    string? ErrorCode = null,
    DateTimeOffset UpdatedAt = default)
{
    public static RobotHandshakeSignals Empty => new(UpdatedAt: DateTimeOffset.UtcNow);
}

public sealed record RobotRuntimeSettings(
    double LinearSpeedMmPerSec = 250,
    double AngularSpeedDegPerSec = 90,
    double PositionToleranceMm = 0.05,
    double AngleToleranceDeg = 0.05)
{
    public RobotRuntimeSettings Normalize(RobotCapabilities capabilities) => this with
    {
        LinearSpeedMmPerSec = Math.Clamp(LinearSpeedMmPerSec, 1, capabilities.MaxLinearSpeedMmPerSec),
        AngularSpeedDegPerSec = Math.Clamp(AngularSpeedDegPerSec, 1, capabilities.MaxAngularSpeedDegPerSec),
        PositionToleranceMm = Math.Clamp(PositionToleranceMm, 0.001, 10),
        AngleToleranceDeg = Math.Clamp(AngleToleranceDeg, 0.001, 10)
    };
}

public sealed record RobotCommandPolicy(
    int TimeoutMs = 5000,
    int MaxRetries = 1,
    int RetryDelayMs = 100,
    bool AutoConnect = true,
    bool WaitForComplete = true,
    bool AutoAck = true)
{
    public RobotCommandPolicy Normalize() => this with
    {
        TimeoutMs = Math.Clamp(TimeoutMs, 50, 120000),
        MaxRetries = Math.Clamp(MaxRetries, 0, 10),
        RetryDelayMs = Math.Clamp(RetryDelayMs, 0, 10000)
    };
}

public sealed record RobotCapabilities(
    bool ReadCurrentPose = true,
    bool SendTarget = true,
    bool Move2D = true,
    bool Stop = true,
    bool ResetFault = true,
    bool Acknowledge = true,
    bool IndustrialHandshake = true,
    double MaxLinearSpeedMmPerSec = 1000,
    double MaxAngularSpeedDegPerSec = 360);

public sealed record RobotDescriptor(
    string Id,
    string Name,
    string Vendor,
    string Model,
    string Driver,
    string BaseFrame,
    string Unit,
    RobotConnectionState ConnectionState,
    RobotHandshakeState HandshakeState,
    RobotHandshakeSignals Handshake,
    VisionCoordinatePose2D CurrentPose,
    VisionRobotTarget2D? ActiveTarget,
    long LastCommandId,
    bool Busy,
    bool InPosition,
    string? Error,
    RobotRuntimeSettings Settings,
    RobotCapabilities Capabilities,
    DateTimeOffset UpdatedAt);

public sealed record RobotCommandReceipt(
    long CommandId,
    string RobotId,
    VisionRobotTarget2D Target,
    RobotHandshakeState State,
    DateTimeOffset AcceptedAt);

public sealed record RobotCommandExecutionResult(
    string TraceId,
    int Attempts,
    RobotCommandReceipt Receipt,
    RobotDescriptor Robot,
    bool Completed,
    bool Acknowledged);

public sealed record RobotCommandTraceEvent(
    string TraceId,
    string RobotId,
    long CommandId,
    int Attempt,
    string Stage,
    DateTimeOffset Timestamp,
    string Message,
    RobotHandshakeSignals? Handshake = null,
    VisionRobotTarget2D? Target = null,
    string? Error = null);

public interface IRobotCommandObserver
{
    ValueTask OnEventAsync(RobotCommandTraceEvent traceEvent, CancellationToken cancellationToken = default);
}

public interface IRobot2DAdapter : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    string Vendor { get; }
    string Model { get; }
    string Driver { get; }
    string BaseFrame { get; }
    string Unit { get; }
    RobotCapabilities Capabilities { get; }

    RobotDescriptor Snapshot();
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default);
    Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default);
    Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ResetFaultAsync(CancellationToken cancellationToken = default);
}

public sealed class RobotCommandTimeoutException(string message) : TimeoutException(message);
