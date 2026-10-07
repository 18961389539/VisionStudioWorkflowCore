using System.Diagnostics;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// Long-running producer for one camera. It owns the grab loop/reconnect policy, while CameraManager owns device lifecycle.
/// Software trigger is host-gated and invokes the adapter command; External trigger is host-gated only for simulators.
/// Real vendor adapters keep GrabAsync blocked until the hardware SDK delivers an externally-triggered frame.
/// </summary>
public sealed class CameraAcquisitionWorker : IAsyncDisposable
{
    private readonly ICameraDevice _device;
    private readonly CameraFrameHub _hub;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private readonly object _statsGate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private CameraAcquisitionState _state = CameraAcquisitionState.Stopped;
    private long _framesPublished;
    private long _errors;
    private long _reconnects;
    private long _timeouts;
    private long _lastSequence;
    private double _actualFps;
    private DateTimeOffset? _lastFrameAt;
    private string? _runtimeError;

    public CameraAcquisitionWorker(ICameraDevice device, CameraFrameHub hub)
    {
        _device = device;
        _hub = hub;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public CameraAcquisitionStats Snapshot()
    {
        lock (_statsGate)
        {
            var telemetry = _device is ICameraTelemetryProvider provider
                ? provider.GetTelemetry()
                : new CameraDeviceTelemetry();
            return new CameraAcquisitionStats(
                _state,
                Interlocked.Read(ref _framesPublished),
                _hub.RingOverwrites,
                Interlocked.Read(ref _errors),
                Interlocked.Read(ref _reconnects),
                Interlocked.Read(ref _lastSequence),
                _actualFps,
                _lastFrameAt,
                _runtimeError,
                Interlocked.Read(ref _timeouts),
                telemetry.DriverDroppedFrames,
                telemetry.NativePixelFormat,
                telemetry.Transport);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return;
            SetState(CameraAcquisitionState.Starting, null);
            await _device.OpenAsync(cancellationToken);
            await _device.StartAsync(cancellationToken);
            DrainTrigger();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_cts is null)
            {
                SetState(CameraAcquisitionState.Stopped, null);
                return;
            }
            _cts.Cancel();
            loop = _loop;
        }
        finally { _lifecycle.Release(); }

        if (loop is not null)
        {
            try { await loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            DrainTrigger();
            SetState(CameraAcquisitionState.Stopped, null);
        }
        finally { _lifecycle.Release(); }
    }

    public void Trigger()
    {
        var mode = _device.Settings.TriggerMode;
        if (mode == CameraTriggerMode.Continuous) return;
        if (mode == CameraTriggerMode.External && !_device.Capabilities.HostSimulatedExternalTrigger)
            throw new InvalidOperationException($"Camera '{_device.Id}' is configured for hardware external trigger; the host cannot synthesize that line trigger.");
        Signal();
    }

    public void Wake() => Signal();

    private void Signal()
    {
        if (_trigger.CurrentCount == 0)
        {
            try { _trigger.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private void DrainTrigger()
    {
        while (_trigger.Wait(0)) { }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        SetState(CameraAcquisitionState.Running, null);
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _device.Settings.Normalize();
            var hostGatedExternal = settings.TriggerMode == CameraTriggerMode.External && _device.Capabilities.HostSimulatedExternalTrigger;
            if (settings.TriggerMode == CameraTriggerMode.Software || hostGatedExternal)
            {
                SetState(CameraAcquisitionState.WaitingTrigger, null);
                await _trigger.WaitAsync(cancellationToken);
                SetState(CameraAcquisitionState.Running, null);
            }
            else if (settings.TriggerMode == CameraTriggerMode.External)
            {
                // A real vendor adapter blocks in GrabAsync until the hardware line produces a frame.
                SetState(CameraAcquisitionState.WaitingTrigger, null);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                if (settings.TriggerMode == CameraTriggerMode.Software)
                    await _device.ExecuteSoftwareTriggerAsync(cancellationToken);

                using var frame = await _device.GrabAsync(TimeSpan.FromSeconds(2), cancellationToken);
                var sequence = frame.Sequence;
                var timestamp = frame.Timestamp;
                _hub.Publish(frame);
                PublishStats(sequence, timestamp);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (CameraFrameTimeoutException)
            {
                // No external/software-triggered frame arrived during this wait. This is not a disconnect.
                Interlocked.Increment(ref _timeouts);
                continue;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                SetState(CameraAcquisitionState.Reconnecting, ex.Message);
                await ReconnectAsync(cancellationToken);
                continue;
            }

            if (settings.TriggerMode == CameraTriggerMode.Continuous)
            {
                var period = TimeSpan.FromSeconds(1d / settings.TargetFps);
                var remaining = period - sw.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellationToken);
            }
        }
        SetState(CameraAcquisitionState.Stopped, null);
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            var delayMs = Math.Min(5000, 250 * (1 << Math.Min(4, attempt - 1)));
            await Task.Delay(delayMs, cancellationToken);
            try
            {
                try { await _device.StopAsync(cancellationToken); } catch { }
                try { await _device.CloseAsync(cancellationToken); } catch { }
                await _device.OpenAsync(cancellationToken);
                await _device.StartAsync(cancellationToken);
                Interlocked.Increment(ref _reconnects);
                SetState(CameraAcquisitionState.Running, null);
                return;
            }
            catch (Exception ex)
            {
                SetState(CameraAcquisitionState.Reconnecting, ex.Message);
            }
        }
    }

    private void PublishStats(long sequence, DateTimeOffset timestamp)
    {
        lock (_statsGate)
        {
            if (_lastFrameAt is { } previous)
            {
                var seconds = (timestamp - previous).TotalSeconds;
                if (seconds > 0)
                {
                    var instant = 1d / seconds;
                    _actualFps = _actualFps <= 0 ? instant : (_actualFps * 0.8 + instant * 0.2);
                }
            }
            _lastFrameAt = timestamp;
            _lastSequence = sequence;
            _runtimeError = null;
            _state = _device.Settings.TriggerMode is CameraTriggerMode.Software or CameraTriggerMode.External
                ? CameraAcquisitionState.WaitingTrigger
                : CameraAcquisitionState.Running;
        }
        Interlocked.Increment(ref _framesPublished);
    }

    private void SetState(CameraAcquisitionState state, string? error)
    {
        lock (_statsGate)
        {
            _state = state;
            _runtimeError = error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { }
        _trigger.Dispose();
        _lifecycle.Dispose();
    }
}
