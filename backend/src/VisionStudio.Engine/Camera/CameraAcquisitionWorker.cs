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
    /// <summary>F08：停止收尾进行中——期间的 Start 被拒绝，防止新采集任务被旧 Stop 误清或失去控制句柄。</summary>
    private bool _stopping;
    private long _stopInvocationSequence;
    /// <summary>
    /// Q03：停止未确认隔离。宽限期结束时旧采集调用仍未返回（厂商 GrabAsync 忽略取消/卡死）——
    /// 此时绝不能清句柄/解除隔离，否则下一次 Start 会与仍在执行的旧 Grab 并发调用同一设备。
    /// 仅在旧循环真正结束（下次 Stop 的收尾核对通过）后解除。
    /// </summary>
    private volatile bool _stopUnconfirmed;
    private readonly TimeSpan _stopDrainGrace;
    private CameraAcquisitionState _state = CameraAcquisitionState.Stopped;
    private long _framesPublished;
    private long _errors;
    private long _reconnects;
    private long _timeouts;
    private long _lastSequence;
    /// <summary>
    /// Q03：采集代次。每次 Start 递增，进入"停止未确认"隔离时也递增（作废旧循环的状态与帧写入权）。
    /// 旧循环是孤儿任务时，它的状态更新与迟到帧都因代次不匹配被丢弃，不会覆盖隔离/新循环状态。
    /// </summary>
    private long _generation;
    private double _actualFps;
    private DateTimeOffset? _lastFrameAt;
    private string? _runtimeError;

    // Internal scheduling seam used only by lifecycle race tests. It runs after drain and before
    // identity reconciliation, outside the lifecycle lock and without changing stop semantics.
    internal Func<long, Task>? BeforeStopFinalizeAsync { get; set; }

    public CameraAcquisitionWorker(ICameraDevice device, CameraFrameHub hub, TimeSpan? stopDrainGrace = null)
    {
        _device = device;
        _hub = hub;
        // F08：旧采集循环的独立收尾期限（默认 5 秒）——Stop 绝不无限等待，也不依赖调用方 token。
        _stopDrainGrace = stopDrainGrace ?? TimeSpan.FromSeconds(5);
    }

    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsStopUnconfirmed => _stopUnconfirmed;

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
            // Q03：停止未确认时绝不允许新采集——旧 GrabAsync 仍在对同一设备执行（隔离中），
            // 新循环会造成两条采集链并发调用厂商 SDK。必须显式失败而不是静默返回，
            // 否则调用方会把"未启动"当成"已启动"。
            if (_stopUnconfirmed)
                throw new InvalidOperationException(
                    "The previous stop could not be confirmed within the drain grace period; an acquisition call is still in flight " +
                    "on this device. Retry stop until it is confirmed (or restart the host) before starting acquisition again.");
            // F08：停止收尾进行中同样拒绝 Start——旧循环未收尾时创建新任务会被旧 Stop 的清理误伤。
            if (_stopping || IsRunning) return;
            SetState(CameraAcquisitionState.Starting, null);
            await _device.OpenAsync(cancellationToken);
            await _device.StartAsync(cancellationToken);
            DrainTrigger();
            _cts = new CancellationTokenSource();
            var generation = NextGeneration();
            _loop = Task.Run(() => LoopAsync(_cts.Token, generation), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        long stopInvocation;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            stopInvocation = ++_stopInvocationSequence;
            cts = _cts;
            loop = _loop;
            if (cts is null && loop is null)
            {
                _stopUnconfirmed = false;
                SetState(CameraAcquisitionState.Stopped, null);
                return;
            }
            // F08：进入停止收尾——期间的 Start 一律被拒（见 StartAsync），收尾完成后做身份核对再清理。
            _stopping = true;
            SetState(CameraAcquisitionState.Stopping, null);
            cts?.Cancel();
        }
        finally { _lifecycle.Release(); }

        var drainTimedOut = false;
        if (loop is not null)
        {
            // F08：独立收尾期限——不依赖调用方 token（可能永不触发取消），也绝不无限等待旧循环。
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drain.CancelAfter(_stopDrainGrace);
            try { await loop.WaitAsync(drain.Token); }
            catch (OperationCanceledException) { drainTimedOut = !cancellationToken.IsCancellationRequested; }
            catch { /* loop 自身异常不得阻断收尾清理：控制句柄必须被一致地归还 */ }
        }

        if (BeforeStopFinalizeAsync is { } beforeFinalize)
            await beforeFinalize(stopInvocation).ConfigureAwait(false);

        // F08：清理必须完成（不可取消）——状态残留会让后续 Start/Stop 语义错乱。
        await _lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            // Another Stop for this generation may have completed first, followed by a fresh Start.
            // A stale continuation must never clear or overwrite the newer generation's handles/state.
            if (!ReferenceEquals(_cts, cts) || !ReferenceEquals(_loop, loop))
                return;
            // Q03：旧循环仍未结束（宽限期已过，或调用方在收尾完成前取消了等待）⇒ 停止未确认。
            // 句柄、_cts 与隔离位全部保留：下一次 Start 会被显式拒绝，直到旧调用真正返回。
            // 取消"软件等待"不等于底层调用已终止——清理完成同样不等于。
            if (loop is { IsCompleted: false })
            {
                _stopUnconfirmed = true;
                // 作废旧循环的写入权：它若在隔离期间继续循环，状态更新与迟到帧都因代次不匹配被丢弃，
                // 不会把界面/接口从 StopUnconfirmed 覆盖回 Running。
                NextGeneration();
                var why = drainTimedOut
                    ? "the drain grace period elapsed"
                    : "the stop request was cancelled before the acquisition loop finished";
                SetState(CameraAcquisitionState.StopUnconfirmed,
                    $"Stop was not confirmed ({why}); the in-flight acquisition call blocks new starts until it returns. Retry stop to re-check.");
                return;
            }

            // F08：身份核对（与 _stopping 双保险）——只清理由本次 Stop 捕获的实例，绝不误清新任务。
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            _stopping = false;
            _stopUnconfirmed = false;
            DrainTrigger();
            SetState(CameraAcquisitionState.Stopped,
                drainTimedOut ? "Stop drainage exceeded the grace period; the acquisition loop is finishing in the background." : null);
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

    private async Task LoopAsync(CancellationToken cancellationToken, long generation)
    {
        SetStateIfCurrent(generation, CameraAcquisitionState.Running, null);
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _device.Settings.Normalize();
            var hostGatedExternal = settings.TriggerMode == CameraTriggerMode.External && _device.Capabilities.HostSimulatedExternalTrigger;
            if (settings.TriggerMode == CameraTriggerMode.Software || hostGatedExternal)
            {
                SetStateIfCurrent(generation, CameraAcquisitionState.WaitingTrigger, null);
                await _trigger.WaitAsync(cancellationToken);
                SetStateIfCurrent(generation, CameraAcquisitionState.Running, null);
            }
            else if (settings.TriggerMode == CameraTriggerMode.External)
            {
                // A real vendor adapter blocks in GrabAsync until the hardware line produces a frame.
                SetStateIfCurrent(generation, CameraAcquisitionState.WaitingTrigger, null);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                if (settings.TriggerMode == CameraTriggerMode.Software)
                    await _device.ExecuteSoftwareTriggerAsync(cancellationToken);

                using var frame = await _device.GrabAsync(TimeSpan.FromSeconds(2), cancellationToken);
                // Q03：迟到帧丢弃——若本代次已被作废（停止未确认隔离/新循环接管），
                // 这把旧帧不得进入帧环、也不得更新统计。
                if (!TryPublishFrame(generation, frame)) return;
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
                SetStateIfCurrent(generation, CameraAcquisitionState.Reconnecting, ex.Message);
                await ReconnectAsync(generation, cancellationToken);
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
        SetStateIfCurrent(generation, CameraAcquisitionState.Stopped, null);
    }

    private async Task ReconnectAsync(long generation, CancellationToken cancellationToken)
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
                SetStateIfCurrent(generation, CameraAcquisitionState.Running, null);
                return;
            }
            catch (Exception ex)
            {
                SetStateIfCurrent(generation, CameraAcquisitionState.Reconnecting, ex.Message);
            }
        }
    }

    private bool TryPublishFrame(long generation, VisionFrame frame)
    {
        lock (_statsGate)
        {
            // Q03：代次核对与帧发布处于同一临界区；一旦 Stop 作废此代次，迟到帧不能
            // 穿过检查/发布之间的竞态进入共享 FrameHub。
            if (_generation != generation) return false;
            _hub.Publish(frame);
            var sequence = frame.Sequence;
            var timestamp = frame.Timestamp;
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
            Interlocked.Increment(ref _framesPublished);
            return true;
        }
    }

    private long NextGeneration()
    {
        lock (_statsGate) return ++_generation;
    }

    /// <summary>Q03：仅当调用方仍是当前代次时才写入状态——孤儿循环不得覆盖隔离/新循环状态。</summary>
    private void SetStateIfCurrent(long generation, CameraAcquisitionState state, string? error)
    {
        lock (_statsGate)
        {
            if (_generation != generation) return;
            _state = state;
            _runtimeError = error;
        }
    }

    private void SetState(CameraAcquisitionState state, string? error)
    {
        lock (_statsGate)
        {
            _state = state;
            _runtimeError = error;
        }
    }

    /// <summary>Waits without a deadline for an unconfirmed grab to return, then reconciles its stop.</summary>
    internal async Task WaitForStopConfirmationAsync()
    {
        while (_stopUnconfirmed)
        {
            var loop = _loop;
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); } catch { }
            }
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { }
        // Q03：旧采集调用仍未返回（停止未确认）时，释放同步原语会让仍在运行的孤儿循环抛
        // ObjectDisposedException，而底层问题（厂商调用卡死）不会因此消失。保留资源交由进程回收，
        // 绝不制造"已清理"的假象。
        if (_stopUnconfirmed) return;
        _trigger.Dispose();
        _lifecycle.Dispose();
    }
}
