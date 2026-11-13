using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using VisionStudio.Api;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api.Workers;

// Generic worker process pool extracted from PluginWorkerSupervisor (Phase 0, pure move).
// Plugin-specific assembly loading/discovery stays in PluginWorkerSupervisor; the pool/client
// below only own process lifecycle, IPC, timeouts, restart circuit and shared-memory bookkeeping.

internal sealed record WorkerPoolExchange(PluginWorkerResponse Response, double QueueWaitMs, double IpcRoundTripMs, int WorkerIndex);
internal sealed record WorkerClientExchange(PluginWorkerResponse Response, double IpcRoundTripMs, int WorkerIndex);

internal sealed class WorkerPool : IDisposable
{
    private readonly ConcurrentQueue<WorkerClient> _available = new();
    private readonly SemaphoreSlim _availableCount;
    private readonly WorkerClient[] _clients;
    private long _requestCount;
    private long _sharedMemoryTransfers;
    private readonly TimeSpan _sharedMemoryStaleAge;
    private readonly PluginWorkerPerformanceProfiler _performance;

    public WorkerPool(
        string pluginId,
        string assemblyPath,
        string workerAssemblyPath,
        int poolSize,
        int sharedMemoryThresholdBytes,
        string sharedMemoryRoot,
        PluginWorkerOptions options,
        ILogger logger)
    {
        PluginId = pluginId;
        AssemblyPath = assemblyPath;
        PoolSize = poolSize;
        SharedMemoryThresholdBytes = sharedMemoryThresholdBytes;
        _sharedMemoryStaleAge = TimeSpan.FromMinutes(Math.Max(1, options.SharedMemoryStaleMinutes));
        _performance = new PluginWorkerPerformanceProfiler(
            options.PerformanceWindowSize,
            options.PerformanceMinimumSamples,
            options.MaxPoolSize,
            options.AdaptiveTargetUtilization,
            options.AdaptiveQueuePressureRatio);
        if (sharedMemoryThresholdBytes > 0)
        {
            SharedMemory = new PluginWorkerSharedMemoryStore(sharedMemoryRoot, sharedMemoryThresholdBytes);
            SharedMemory.CleanupStaleFiles(_sharedMemoryStaleAge);
        }
        _clients = Enumerable.Range(0, poolSize)
            .Select(index => new WorkerClient(pluginId, index, assemblyPath, workerAssemblyPath, SharedMemory?.RootPath, sharedMemoryThresholdBytes, options, logger))
            .ToArray();
        foreach (var client in _clients) _available.Enqueue(client);
        _availableCount = new SemaphoreSlim(poolSize, poolSize);
    }

    public string PluginId { get; }
    public string AssemblyPath { get; }
    public int PoolSize { get; }
    public int SharedMemoryThresholdBytes { get; }
    public PluginWorkerSharedMemoryStore? SharedMemory { get; }
    public PluginWorkerPerformanceProfile Performance => _performance.Snapshot(PluginId, PoolSize);

    public void RecordPerformance(PluginWorkerPerformanceSample sample) => _performance.Record(sample);
    public void ResetPerformance() => _performance.Reset();

    public PluginWorkerStatus Status
    {
        get
        {
            var instances = _clients.Select(x => x.Status).ToArray();
            var running = instances.Count(x => string.Equals(x.State, "Running", StringComparison.OrdinalIgnoreCase));
            var busy = instances.Count(x => x.Busy);
            var circuit = instances.Any(x => x.CircuitOpen);
            var state = circuit ? "Degraded" : running > 0 ? "Running" : "Stopped";
            return new PluginWorkerStatus(
                PluginId,
                state,
                instances.FirstOrDefault(x => x.ProcessId is not null)?.ProcessId,
                instances.Where(x => x.StartedAt is not null).Select(x => x.StartedAt).Min(),
                instances.Where(x => x.LastRequestAt is not null).Select(x => x.LastRequestAt).Max(),
                instances.Sum(x => x.RestartCount),
                instances.Sum(x => x.WorkingSetBytes),
                AssemblyPath,
                instances.Select(x => x.LastError).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                circuit,
                PoolSize,
                running,
                busy,
                Interlocked.Read(ref _requestCount),
                Interlocked.Read(ref _sharedMemoryTransfers),
                SharedMemory is null ? nameof(PluginWorkerImageTransport.Png) : nameof(PluginWorkerImageTransport.FileBackedSharedMemory),
                SharedMemoryThresholdBytes,
                instances);
        }
    }

    public void RecordSharedMemoryTransfers(int count)
    {
        if (count > 0) Interlocked.Add(ref _sharedMemoryTransfers, count);
    }

    public async Task<WorkerPoolExchange> SendAsync(PluginWorkerRequest request, bool startup, CancellationToken cancellationToken, int? nodeTimeoutMs = null)
    {
        var queueTimer = Stopwatch.StartNew();
        await _availableCount.WaitAsync(cancellationToken);
        WorkerClient? client = null;
        for (var attempt = 0; attempt < PoolSize; attempt++)
        {
            if (!_available.TryDequeue(out var candidate)) break;
            if (!candidate.Status.CircuitOpen || attempt == PoolSize - 1)
            {
                client = candidate;
                break;
            }
            _available.Enqueue(candidate);
        }
        queueTimer.Stop();
        if (client is null)
        {
            _availableCount.Release();
            throw new InvalidOperationException($"Plugin worker pool '{PluginId}' availability queue is inconsistent.");
        }
        try
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (SharedMemory is not null && requestNumber % 128 == 0)
                SharedMemory.CleanupStaleFiles(_sharedMemoryStaleAge);
            var exchange = await client.SendAsync(request, startup, cancellationToken, nodeTimeoutMs);
            return new WorkerPoolExchange(exchange.Response, queueTimer.Elapsed.TotalMilliseconds, exchange.IpcRoundTripMs, exchange.WorkerIndex);
        }
        finally
        {
            _available.Enqueue(client);
            _availableCount.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        foreach (var client in _clients) await client.RestartAsync(cancellationToken);
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _availableCount.Dispose();
    }
}

internal sealed class WorkerClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _pluginId;
    private readonly int _workerIndex;
    private readonly string _workerAssemblyPath;
    private readonly string? _sharedMemoryRoot;
    private readonly int _sharedMemoryThresholdBytes;
    private readonly PluginWorkerOptions _options;
    private readonly ILogger _logger;
    private readonly Queue<DateTimeOffset> _restartWindow = new();
    private Process? _process;
    private WindowsJobObject? _job;
    private NamedPipeServerStream? _pipe;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastRequestAt;
    private string? _lastError;
    private long _workingSet;
    private int _restartCount;
    private bool _circuitOpen;
    private int _busy;

    public WorkerClient(
        string pluginId,
        int workerIndex,
        string assemblyPath,
        string workerAssemblyPath,
        string? sharedMemoryRoot,
        int sharedMemoryThresholdBytes,
        PluginWorkerOptions options,
        ILogger logger)
    {
        _pluginId = pluginId;
        _workerIndex = workerIndex;
        AssemblyPath = Path.GetFullPath(assemblyPath);
        _workerAssemblyPath = workerAssemblyPath;
        _sharedMemoryRoot = sharedMemoryRoot;
        _sharedMemoryThresholdBytes = sharedMemoryThresholdBytes;
        _options = options;
        _logger = logger;
    }

    public string AssemblyPath { get; }
    public int WorkerIndex => _workerIndex;
    public PluginWorkerInstanceStatus Status => new(
        _workerIndex,
        _circuitOpen ? "CircuitOpen" : _process is { HasExited: false } && _pipe?.IsConnected == true ? "Running" : "Stopped",
        _process is { HasExited: false } ? _process.Id : null,
        _startedAt,
        _lastRequestAt,
        _restartCount,
        _workingSet,
        _lastError,
        _circuitOpen,
        Volatile.Read(ref _busy) != 0);

    public async Task<WorkerClientExchange> SendAsync(PluginWorkerRequest request, bool startup, CancellationToken cancellationToken, int? nodeTimeoutMs = null)
    {
        await _gate.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _busy, 1);
        try
        {
            if (_circuitOpen) throw new InvalidOperationException($"Plugin worker '{_pluginId}'[{_workerIndex}] restart circuit is open. Use the worker restart endpoint after correcting the plugin.");
            await EnsureStartedAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var effectiveTimeoutMs = startup
                ? _options.StartupTimeoutMs
                : PluginWorkerTimeoutPolicy.Resolve(nodeTimeoutMs ?? 0, _options.ExecutionTimeoutMs);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeoutMs));
            try
            {
                var ipcTimer = Stopwatch.StartNew();
                await PluginWorkerWire.WriteAsync(_pipe!, request, timeout.Token);
                var response = await PluginWorkerWire.ReadAsync<PluginWorkerResponse>(_pipe!, timeout.Token);
                ipcTimer.Stop();
                if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Plugin worker response request id mismatch.");
                _lastRequestAt = DateTimeOffset.UtcNow;
                _workingSet = response.WorkingSetBytes;
                if (_options.MaxWorkingSetMb > 0 && _workingSet > (long)_options.MaxWorkingSetMb * 1024 * 1024)
                {
                    var message = $"Plugin worker '{_pluginId}'[{_workerIndex}] exceeded working-set limit {_options.MaxWorkingSetMb} MB (actual {_workingSet / 1024d / 1024d:F1} MB).";
                    KillWorker(message);
                    throw new InvalidOperationException(message);
                }
                if (!response.Success) throw new InvalidOperationException($"Plugin worker '{_pluginId}'[{_workerIndex}] failed: {response.Error}");
                _lastError = null;
                return new WorkerClientExchange(response, ipcTimer.Elapsed.TotalMilliseconds, _workerIndex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillWorker($"Plugin worker '{_pluginId}'[{_workerIndex}] request was cancelled by the host; worker terminated to keep the single-stream IPC protocol synchronized.");
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var message = $"Plugin worker '{_pluginId}'[{_workerIndex}] timed out after {effectiveTimeoutMs} ms.";
                KillWorker(message);
                throw new TimeoutException(message);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException)
            {
                KillWorker(ex.Message);
                throw new InvalidOperationException($"Plugin worker '{_pluginId}'[{_workerIndex}] IPC failed and the worker was terminated.", ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StopWorker();
            _circuitOpen = false;
            _restartWindow.Clear();
            _lastError = null;
            await EnsureStartedAsync(cancellationToken);
            var response = await ExchangeNoLockAsync(new PluginWorkerRequest { Operation = "ping" }, _options.StartupTimeoutMs, cancellationToken);
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "Worker ping failed after restart.");
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _pipe?.IsConnected == true) return;
        StopWorker();
        RegisterRestart();
        if (_circuitOpen) throw new InvalidOperationException($"Plugin worker '{_pluginId}'[{_workerIndex}] exceeded {_options.MaxRestartsPerMinute} restarts per minute; circuit opened.");
        if (!File.Exists(_workerAssemblyPath))
            throw new FileNotFoundException("VisionStudio.Plugin.Worker.dll was not found. Publish/copy the worker beside the API host or configure PluginWorkers:WorkerAssemblyPath.", _workerAssemblyPath);
        if (!File.Exists(AssemblyPath)) throw new FileNotFoundException("Plugin assembly was not found.", AssemblyPath);

        var pipeName = "visionstudio-plugin-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = false,
            RedirectStandardOutput = false
        };
        start.ArgumentList.Add(_workerAssemblyPath);
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--assembly"); start.ArgumentList.Add(AssemblyPath);
        start.ArgumentList.Add("--plugin-id"); start.ArgumentList.Add(_pluginId);
        if (!string.IsNullOrWhiteSpace(_sharedMemoryRoot) && _sharedMemoryThresholdBytes > 0)
        {
            start.ArgumentList.Add("--shared-memory-root"); start.ArgumentList.Add(_sharedMemoryRoot);
            start.ArgumentList.Add("--shared-memory-threshold-bytes"); start.ArgumentList.Add(_sharedMemoryThresholdBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start plugin worker process.");
        var memoryLimitMb = _options.JobObjectMemoryLimitMb;
        var cpuRatePercent = _options.JobObjectCpuRatePercent;
        if (memoryLimitMb > 0 || cpuRatePercent > 0)
        {
            _job = WindowsJobObject.TryAssign(_process, memoryLimitMb, cpuRatePercent, out var jobWarning);
            if (jobWarning is not null)
                _logger.LogWarning("Worker {PluginId}[{WorkerIndex}] job-object hardening is inactive: {Warning}", _pluginId, _workerIndex, jobWarning);
            else
                _logger.LogInformation("Worker {PluginId}[{WorkerIndex}] job-object limits applied (memory={MemoryMb} MB, cpu={CpuPercent}%).",
                    _pluginId, _workerIndex, memoryLimitMb, cpuRatePercent);
        }
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            try
            {
                if (_process is not null && _process.ExitCode != 0)
                    _lastError = $"Worker process {_workerIndex} exited with code {_process.ExitCode}.";
            }
            catch { /* 事件回调里读取 ExitCode 失败不影响进程生命周期；真正错误已由 _lastError 与日志记录 */ }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.StartupTimeoutMs));
        try { await _pipe.WaitForConnectionAsync(timeout.Token); }
        catch
        {
            KillWorker("Worker failed to connect before the startup timeout.");
            throw;
        }
        _startedAt = DateTimeOffset.UtcNow;
        _restartCount++;
        _logger.LogInformation("Started plugin worker {PluginId}[{WorkerIndex}] pid {Pid} for {AssemblyPath}", _pluginId, _workerIndex, _process.Id, AssemblyPath);
    }

    private async Task<PluginWorkerResponse> ExchangeNoLockAsync(PluginWorkerRequest request, int timeoutMs, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        await PluginWorkerWire.WriteAsync(_pipe!, request, timeout.Token);
        return await PluginWorkerWire.ReadAsync<PluginWorkerResponse>(_pipe!, timeout.Token);
    }

    private void RegisterRestart()
    {
        var now = DateTimeOffset.UtcNow;
        while (_restartWindow.Count > 0 && now - _restartWindow.Peek() > TimeSpan.FromMinutes(1)) _restartWindow.Dequeue();
        _restartWindow.Enqueue(now);
        if (_options.MaxRestartsPerMinute > 0 && _restartWindow.Count > _options.MaxRestartsPerMinute)
        {
            _circuitOpen = true;
            _lastError = $"Restart circuit opened after {_restartWindow.Count} starts in one minute.";
        }
    }

    private void KillWorker(string reason)
    {
        _lastError = reason;
        _logger.LogWarning("Terminating plugin worker {PluginId}[{WorkerIndex}]: {Reason}", _pluginId, _workerIndex, reason);
        StopWorker();
    }

    private void StopWorker()
    {
        try { _pipe?.Dispose(); } catch { /* 管道句柄随进程退出回收；Dispose 失败没有可恢复动作 */ }
        _pipe = null;
        try { _job?.Dispose(); } catch { /* Job Object 句柄同样由进程退出回收；失败不阻断清理 */ }
        _job = null;
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出或被系统回收；Kill 失败不阻断后续 Dispose */ }
            try { _process.Dispose(); } catch { /* 句柄已失效时 Dispose 会抛异常；清理路径继续 */ }
        }
        _process = null;
    }

    public void Dispose()
    {
        _gate.Wait();
        try { StopWorker(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
