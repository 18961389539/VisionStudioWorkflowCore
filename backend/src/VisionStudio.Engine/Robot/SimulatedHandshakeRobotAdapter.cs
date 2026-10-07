using VisionStudio.Engine.Provenance;
namespace VisionStudio.Engine.Robot;

/// <summary>
/// Shared 2D motion + six-bit industrial handshake simulator used by the Virtual ABB and PLC bridge adapters.
/// It validates software semantics only; it is not a controller safety or kinematics simulator.
/// </summary>
public abstract class SimulatedHandshakeRobotAdapter : IRobot2DAdapter, IHardwareProvenanceProvider
{
    private readonly object _gate = new();
    private readonly int _scanDelayMs;
    private CancellationTokenSource? _motionCts;
    private Task? _motionTask;
    private RobotConnectionState _connection = RobotConnectionState.Disconnected;
    private RobotHandshakeState _handshakeState = RobotHandshakeState.Disconnected;
    private RobotHandshakeSignals _signals = RobotHandshakeSignals.Empty;
    private VisionCoordinatePose2D _pose;
    private VisionRobotTarget2D? _target;
    private RobotRuntimeSettings _settings = new();
    private long _commandId;
    private string? _error;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    protected SimulatedHandshakeRobotAdapter(
        string id,
        string name,
        string vendor,
        string model,
        string driver,
        VisionCoordinatePose2D initialPose,
        int scanDelayMs = 20,
        RobotCapabilities? capabilities = null)
    {
        Id = id;
        Name = name;
        Vendor = vendor;
        Model = model;
        Driver = driver;
        BaseFrame = initialPose.Frame;
        Unit = initialPose.Unit;
        _pose = initialPose;
        _scanDelayMs = Math.Clamp(scanDelayMs, 1, 500);
        Capabilities = capabilities ?? new RobotCapabilities(MaxLinearSpeedMmPerSec: 1200, MaxAngularSpeedDegPerSec: 540);
    }

    public string Id { get; }
    public string Name { get; }
    public string Vendor { get; }
    public string Model { get; }
    public string Driver { get; }
    public string BaseFrame { get; }
    public string Unit { get; }
    public RobotCapabilities Capabilities { get; }

    public HardwareProvenanceData GetHardwareProvenance() => new(
        Manufacturer: Vendor,
        ProductName: Name,
        Model: Model,
        SerialNumber: Id,
        HardwareRevision: "sim-v1",
        FirmwareVersion: "simulated",
        ControllerVersion: "VisionStudio Robot Simulator",
        ProgramName: "Handshake2D",
        ProgramHash: "simulated-handshake-v1",
        Attributes: new Dictionary<string, string> { ["simulation"] = "true", ["driver"] = Driver });

    public RobotDescriptor Snapshot()
    {
        lock (_gate)
        {
            var inPosition = IsAtTargetNoLock() && _signals.Complete;
            return new RobotDescriptor(
                Id, Name, Vendor, Model, Driver, BaseFrame, Unit,
                _connection, _handshakeState, _signals, _pose, _target, _commandId,
                _signals.Busy,
                inPosition,
                _error,
                _settings,
                Capabilities,
                _updatedAt);
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connection == RobotConnectionState.Connected) return;
            _connection = RobotConnectionState.Connecting;
            _handshakeState = RobotHandshakeState.Disconnected;
            _signals = RobotHandshakeSignals.Empty;
            _error = null;
            TouchNoLock();
        }
        await Task.Delay(Math.Max(40, _scanDelayMs * 3), cancellationToken);
        lock (_gate)
        {
            _connection = RobotConnectionState.Connected;
            _handshakeState = RobotHandshakeState.Ready;
            _signals = RobotHandshakeSignals.Empty with { UpdatedAt = DateTimeOffset.UtcNow };
            TouchNoLock();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopMotionWorkerAsync();
        lock (_gate)
        {
            _connection = RobotConnectionState.Disconnected;
            _handshakeState = RobotHandshakeState.Disconnected;
            _signals = RobotHandshakeSignals.Empty;
            _target = null;
            TouchNoLock();
        }
    }

    public Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireConnectedNoLock();
            _settings = settings.Normalize(Capabilities);
            TouchNoLock();
        }
        return Task.CompletedTask;
    }

    public Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireConnectedNoLock();
            ValidateTargetNoLock(target);
            if (_signals.Busy)
                throw new InvalidOperationException($"Robot '{Id}' is already executing command {_commandId}.");

            _target = target;
            _commandId++;
            _handshakeState = RobotHandshakeState.TargetAccepted;
            _error = null;
            _signals = new RobotHandshakeSignals(
                TargetReady: true,
                Execute: false,
                Busy: false,
                Complete: false,
                Error: false,
                Ack: false,
                CommandId: _commandId,
                UpdatedAt: DateTimeOffset.UtcNow);
            TouchNoLock();
            return Task.FromResult(new RobotCommandReceipt(_commandId, Id, target, _handshakeState, _updatedAt));
        }
    }

    public async Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
    {
        RobotCommandReceipt? receipt = null;
        lock (_gate)
        {
            var canExecutePrepared = _target is not null && _signals.TargetReady && !_signals.Busy && SameTargetNoLock(target);
            if (canExecutePrepared)
                receipt = new RobotCommandReceipt(_commandId, Id, target, RobotHandshakeState.TargetAccepted, _updatedAt);
        }
        receipt ??= await SendTargetAsync(target, cancellationToken);

        await StartMotionWorkerAsync(receipt.CommandId, target, cancellationToken);
        return receipt with { State = RobotHandshakeState.Executing };
    }

    public Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireConnectedNoLock();
            if (commandId != _commandId)
                throw new InvalidOperationException($"Robot '{Id}' cannot acknowledge command #{commandId}; current command is #{_commandId}.");
            if (!_signals.Complete && !_signals.Error)
                throw new InvalidOperationException($"Robot '{Id}' command #{commandId} is not complete/error and cannot be acknowledged.");

            _signals = _signals with
            {
                TargetReady = false,
                Execute = false,
                Busy = false,
                Complete = false,
                Error = false,
                Ack = true,
                ErrorCode = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _handshakeState = RobotHandshakeState.Ready;
            _error = null;
            TouchNoLock();
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopMotionWorkerAsync();
        lock (_gate)
        {
            if (_connection == RobotConnectionState.Connected)
            {
                _handshakeState = RobotHandshakeState.Stopped;
                _signals = _signals with { Execute = false, Busy = false, UpdatedAt = DateTimeOffset.UtcNow };
            }
            TouchNoLock();
        }
    }

    public Task ResetFaultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_connection != RobotConnectionState.Connected)
                throw new InvalidOperationException($"Robot '{Id}' is not connected.");
            _error = null;
            _handshakeState = RobotHandshakeState.Ready;
            _signals = RobotHandshakeSignals.Empty with { CommandId = _commandId, UpdatedAt = DateTimeOffset.UtcNow };
            TouchNoLock();
        }
        return Task.CompletedTask;
    }

    private Task StartMotionWorkerAsync(long commandId, VisionRobotTarget2D target, CancellationToken cancellationToken)
    {
        CancellationTokenSource cts;
        VisionCoordinatePose2D start;
        RobotRuntimeSettings settings;
        lock (_gate)
        {
            RequireConnectedNoLock();
            _motionCts?.Cancel();
            _motionCts?.Dispose();
            _motionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts = _motionCts;
            start = _pose;
            settings = _settings;
            _handshakeState = RobotHandshakeState.Executing;
            _signals = _signals with
            {
                TargetReady = true,
                Execute = true,
                Busy = true,
                Complete = false,
                Error = false,
                Ack = false,
                CommandId = commandId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            TouchNoLock();
        }

        _motionTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_scanDelayMs, cts.Token);
                lock (_gate)
                {
                    if (commandId == _commandId)
                        _signals = _signals with { Execute = false, UpdatedAt = DateTimeOffset.UtcNow };
                }

                var dx = target.X - start.X;
                var dy = target.Y - start.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var angleDelta = NormalizeAngle(target.RDeg - start.ThetaDeg);
                var translationSeconds = distance / Math.Max(1, settings.LinearSpeedMmPerSec);
                var rotationSeconds = Math.Abs(angleDelta) / Math.Max(1, settings.AngularSpeedDegPerSec);
                var duration = Math.Max(0.08, Math.Max(translationSeconds, rotationSeconds));
                var started = DateTimeOffset.UtcNow;

                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var t = Math.Clamp((DateTimeOffset.UtcNow - started).TotalSeconds / duration, 0, 1);
                    var smooth = t * t * (3 - 2 * t);
                    lock (_gate)
                    {
                        if (commandId != _commandId) return;
                        _pose = new VisionCoordinatePose2D(
                            start.X + dx * smooth,
                            start.Y + dy * smooth,
                            NormalizeAngle(start.ThetaDeg + angleDelta * smooth),
                            BaseFrame,
                            Unit);
                        TouchNoLock();
                    }
                    if (t >= 1) break;
                    await Task.Delay(Math.Max(10, _scanDelayMs), cts.Token);
                }

                lock (_gate)
                {
                    if (commandId == _commandId)
                    {
                        _pose = new VisionCoordinatePose2D(target.X, target.Y, NormalizeAngle(target.RDeg), BaseFrame, Unit);
                        _handshakeState = RobotHandshakeState.InPosition;
                        _signals = _signals with
                        {
                            TargetReady = true,
                            Execute = false,
                            Busy = false,
                            Complete = true,
                            Error = false,
                            Ack = false,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        TouchNoLock();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop/replace commands are expected control flow, not faults.
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _error = ex.Message;
                    _connection = RobotConnectionState.Faulted;
                    _handshakeState = RobotHandshakeState.Faulted;
                    _signals = _signals with
                    {
                        Execute = false,
                        Busy = false,
                        Complete = false,
                        Error = true,
                        Ack = false,
                        ErrorCode = "SIM-MOTION",
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    TouchNoLock();
                }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task StopMotionWorkerAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _motionCts;
            task = _motionTask;
            _motionCts = null;
            _motionTask = null;
        }
        if (cts is not null)
        {
            cts.Cancel();
            if (task is not null)
            {
                try { await task; } catch (OperationCanceledException) { }
            }
            cts.Dispose();
        }
    }

    private bool SameTargetNoLock(VisionRobotTarget2D target)
        => _target is { } currentTarget
           && Math.Abs(currentTarget.X - target.X) < 1e-9
           && Math.Abs(currentTarget.Y - target.Y) < 1e-9
           && Math.Abs(NormalizeAngle(currentTarget.RDeg - target.RDeg)) < 1e-9
           && string.Equals(currentTarget.Frame, target.Frame, StringComparison.OrdinalIgnoreCase)
           && string.Equals(currentTarget.Unit, target.Unit, StringComparison.OrdinalIgnoreCase);

    private bool IsAtTargetNoLock()
    {
        if (_target is not { } currentTarget) return false;
        var dx = _pose.X - currentTarget.X;
        var dy = _pose.Y - currentTarget.Y;
        var dr = Math.Abs(NormalizeAngle(_pose.ThetaDeg - currentTarget.RDeg));
        return Math.Sqrt(dx * dx + dy * dy) <= _settings.PositionToleranceMm && dr <= _settings.AngleToleranceDeg;
    }

    private void ValidateTargetNoLock(VisionRobotTarget2D target)
    {
        if (!target.Frame.Equals(BaseFrame, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Robot '{Id}' expects frame '{BaseFrame}', got '{target.Frame}'.");
        if (!target.Unit.Equals(Unit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Robot '{Id}' expects unit '{Unit}', got '{target.Unit}'.");
    }

    private void RequireConnectedNoLock()
    {
        if (_connection != RobotConnectionState.Connected)
            throw new InvalidOperationException($"Robot '{Id}' is not connected.");
    }

    private void TouchNoLock() => _updatedAt = DateTimeOffset.UtcNow;

    protected static double NormalizeAngle(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle <= -180) angle += 360;
        return angle;
    }

    public async ValueTask DisposeAsync()
    {
        await StopMotionWorkerAsync();
        lock (_gate)
        {
            _connection = RobotConnectionState.Disconnected;
            _handshakeState = RobotHandshakeState.Disconnected;
            _signals = RobotHandshakeSignals.Empty;
        }
    }
}
