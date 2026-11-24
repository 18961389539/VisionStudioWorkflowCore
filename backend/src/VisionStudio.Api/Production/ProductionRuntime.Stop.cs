namespace VisionStudio.Api;

public sealed partial class ProductionRuntimeService
{
    public async Task<ProductionRuntimeStatus> StopAsync(CancellationToken ct)
    {
        Task<ProductionRuntimeStatus> completion;
        await _gate.WaitAsync(ct);
        try
        {
            if (_stopCompletion is null)
            {
                if (Status.State == ProductionRuntimeState.Stopped) return Status;
                int timeoutMs;
                lock (_stateSync)
                {
                    _state = ProductionRuntimeState.Stopping;
                    timeoutMs = _config.StopTimeoutMs;
                }
                var signal = new TaskCompletionSource<ProductionRuntimeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopCompletion = signal;
                // The host owns shutdown once requested. Caller cancellation only ends that caller's wait.
                _ = CompleteStopAsync(signal, _loopTask, _runtimeCts, timeoutMs);
            }
            completion = _stopCompletion.Task;
        }
        finally { _gate.Release(); }

        return await completion.WaitAsync(ct);
    }

    private async Task CompleteStopAsync(
        TaskCompletionSource<ProductionRuntimeStatus> signal, Task? loop, CancellationTokenSource? runtime, int timeoutMs)
    {
        var cancellation = runtime?.CancelAsync() ?? Task.CompletedTask;
        var work = Task.WhenAll(loop ?? Task.CompletedTask, cancellation);
        try
        {
            await work.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            const string error = "Production Runtime stop timed out; an execution loop may still be active. Restart is blocked until that loop exits.";
            string? runId;
            lock (_stateSync)
            {
                _state = ProductionRuntimeState.Faulted;
                _lastError = error;
                runId = _currentRunId;
            }
            _logger.LogError("{StopError}", error);
            try
            {
                await _alarms.RaiseAsync("PROD-STOP-TIMEOUT", AlarmSeverity.Critical, "ProductionRuntime", error, runId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not record the production stop timeout alarm.");
            }
            signal.TrySetResult(Status);
            try { await work; }
            catch (Exception exception) { LogStopFailure(exception); }
        }
        catch (Exception exception) { LogStopFailure(exception); }

        await _gate.WaitAsync();
        try
        {
            lock (_stateSync)
            {
                _lockedSnapshot = null;
                _lease?.Dispose();
                _lease = null;
                _currentRunId = null;
                _workloadLease?.Dispose();
                _workloadLease = null;
                _state = ProductionRuntimeState.Stopped;
            }
            _runtimeCts?.Dispose();
            _runtimeCts = null;
            _loopTask = null;
            _stopCompletion = null;
            signal.TrySetResult(Status);
        }
        finally { _gate.Release(); }
    }

    private void LogStopFailure(Exception exception) =>
        _logger.LogWarning(exception, "Production work ended with an error during stop; completing resource cleanup.");
}
