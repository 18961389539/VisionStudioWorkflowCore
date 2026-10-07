using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Options;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed class PluginWorkerOptions
{
    public int StartupTimeoutMs { get; set; } = 10_000;
    public int ExecutionTimeoutMs { get; set; } = 30_000;
    public int MaxWorkingSetMb { get; set; } = 2048;
    public int MaxRestartsPerMinute { get; set; } = 5;
    public string? WorkerAssemblyPath { get; set; }
    public int DefaultPoolSize { get; set; } = 1;
    public int MaxPoolSize { get; set; } = 4;
    public bool SharedMemoryEnabled { get; set; } = true;
    public int SharedMemoryThresholdBytes { get; set; } = 1_048_576;
    public string SharedMemoryRootPath { get; set; } = "data/plugin-workers/shared-memory";
    public int SharedMemoryStaleMinutes { get; set; } = 30;
    public int PerformanceWindowSize { get; set; } = 512;
    public int PerformanceMinimumSamples { get; set; } = 30;
    public double AdaptiveTargetUtilization { get; set; } = 0.70;
    public double AdaptiveQueuePressureRatio { get; set; } = 0.15;
}

public sealed record PluginWorkerInstanceStatus(
    int WorkerIndex,
    string State,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastRequestAt,
    int RestartCount,
    long WorkingSetBytes,
    string? LastError,
    bool CircuitOpen,
    bool Busy);

public sealed record PluginWorkerStatus(
    string PluginId,
    string State,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastRequestAt,
    int RestartCount,
    long WorkingSetBytes,
    string AssemblyPath,
    string? LastError,
    bool CircuitOpen,
    int PoolSize,
    int RunningWorkers,
    int BusyWorkers,
    long RequestCount,
    long SharedMemoryTransfers,
    string ImageTransport,
    int SharedMemoryThresholdBytes,
    IReadOnlyList<PluginWorkerInstanceStatus> Workers);

public sealed record PluginWorkerBenchmarkExecution<T>(
    T Result,
    PluginWorkerPerformanceProfile Performance,
    PluginWorkerStatus Status);

public sealed class PluginWorkerSupervisor : IDisposable
{
    private readonly ConcurrentDictionary<string, WorkerPool> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginWorkerOptions _options;
    private readonly ILogger<PluginWorkerSupervisor> _logger;
    private readonly string _workerAssemblyPath;
    private readonly string _sharedMemoryRoot;
    private readonly AsyncLocal<BenchmarkOverride?> _benchmarkOverride = new();

    public PluginWorkerSupervisor(IWebHostEnvironment environment, IOptions<PluginWorkerOptions> options, ILogger<PluginWorkerSupervisor> logger)
    {
        _options = options.Value;
        _logger = logger;
        _workerAssemblyPath = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.WorkerAssemblyPath)
            ? Path.Combine(AppContext.BaseDirectory, "VisionStudio.Plugin.Worker.dll")
            : Path.IsPathRooted(_options.WorkerAssemblyPath)
                ? _options.WorkerAssemblyPath
                : Path.Combine(environment.ContentRootPath, _options.WorkerAssemblyPath));
        _sharedMemoryRoot = Path.GetFullPath(Path.IsPathRooted(_options.SharedMemoryRootPath)
            ? _options.SharedMemoryRootPath
            : Path.Combine(environment.ContentRootPath, _options.SharedMemoryRootPath));
        Directory.CreateDirectory(_sharedMemoryRoot);
    }

    public IReadOnlyList<PluginWorkerStatus> Status => _workers.Values
        .Select(x => x.Status)
        .OrderBy(x => x.PluginId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<PluginWorkerPerformanceProfile> Performance => _workers.Values
        .Select(x => x.Performance)
        .OrderBy(x => x.PluginId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<PluginWorkerDiscovery> RegisterAndDiscoverAsync(
        string pluginId,
        string assemblyPath,
        int? requestedPoolSize = null,
        int? requestedSharedMemoryThresholdBytes = null,
        CancellationToken cancellationToken = default)
    {
        var poolSize = requestedPoolSize ?? _options.DefaultPoolSize;
        var maxPool = Math.Max(1, _options.MaxPoolSize);
        if (poolSize < 1 || poolSize > maxPool)
            throw new InvalidOperationException($"Plugin worker pool size {poolSize} is invalid; allowed range is 1..{maxPool}.");

        var threshold = _options.SharedMemoryEnabled
            ? requestedSharedMemoryThresholdBytes ?? _options.SharedMemoryThresholdBytes
            : 0;
        if (threshold < 0) throw new InvalidOperationException("Plugin worker shared-memory threshold cannot be negative.");

        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        var created = false;
        var pool = _workers.GetOrAdd(pluginId, _ =>
        {
            created = true;
            return new WorkerPool(
                pluginId,
                fullAssemblyPath,
                _workerAssemblyPath,
                poolSize,
                threshold,
                PluginSharedRoot(pluginId),
                _options,
                _logger);
        });

        if (!string.Equals(fullAssemblyPath, pool.AssemblyPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Worker plugin '{pluginId}' is already bound to '{pool.AssemblyPath}'. Replacing an active package requires host restart.");
        if (pool.PoolSize != poolSize || pool.SharedMemoryThresholdBytes != threshold)
            throw new InvalidOperationException($"Worker plugin '{pluginId}' is already active with pool={pool.PoolSize}, sharedMemoryThreshold={pool.SharedMemoryThresholdBytes}; changing worker execution settings requires host restart.");

        try
        {
            var exchange = await pool.SendAsync(new PluginWorkerRequest { Operation = "discover" }, startup: true, cancellationToken);
            return exchange.Response.Discovery ?? throw new InvalidOperationException($"Worker plugin '{pluginId}' returned no discovery metadata.");
        }
        catch
        {
            if (created && _workers.TryRemove(pluginId, out var removed)) removed.Dispose();
            throw;
        }
    }

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        string pluginId,
        string nodeType,
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        WorkerPool pool;
        var benchmark = _benchmarkOverride.Value;
        if (benchmark is not null && benchmark.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            pool = benchmark.Pool;
        else if (!_workers.TryGetValue(pluginId, out pool!))
            throw new InvalidOperationException($"Plugin worker '{pluginId}' is not registered.");

        var totalTimer = Stopwatch.StartNew();
        var encodeTimer = Stopwatch.StartNew();
        var inputs = context.Inputs.ToDictionary(
            pair => pair.Key,
            pair => PluginWorkerValueCodec.Encode(pair.Value, pool.SharedMemory),
            StringComparer.OrdinalIgnoreCase);
        encodeTimer.Stop();
        var inputEncodeMs = encodeTimer.Elapsed.TotalMilliseconds;
        pool.RecordSharedMemoryTransfers(inputs.Values.Count(x => x.SharedImage is not null));
        WorkerPoolExchange? exchange = null;
        double outputDecodeMs = 0;

        try
        {
            exchange = await pool.SendAsync(new PluginWorkerRequest
            {
                Operation = "execute",
                NodeType = nodeType,
                Node = node,
                Inputs = inputs,
                // Worker-side PerNode caches key by the same workflow scope as the host, so two workflows that
                // reuse a node id never share a worker-cached instance either.
                WorkflowScope = context.WorkflowScope
            }, startup: false, cancellationToken);

            var response = exchange.Response;
            var result = response.Result ?? throw new InvalidOperationException($"Plugin worker '{pluginId}' returned no execution result.");
            pool.RecordSharedMemoryTransfers(result.Outputs.Values.Count(x => x.SharedImage is not null));
            try
            {
                var decodeTimer = Stopwatch.StartNew();
                var outputs = result.Outputs.ToDictionary(
                    pair => pair.Key,
                    pair => PluginWorkerValueCodec.Decode(pair.Value, pool.SharedMemory),
                    StringComparer.OrdinalIgnoreCase);
                var summary = PluginWorkerValueCodec.DecodeSummary(result.Summary);
                decodeTimer.Stop();
                outputDecodeMs = decodeTimer.Elapsed.TotalMilliseconds;
                totalTimer.Stop();
                var timing = response.Timing ?? new PluginWorkerTiming(0, 0, 0);
                pool.RecordPerformance(new PluginWorkerPerformanceSample(
                    DateTimeOffset.UtcNow,
                    nodeType,
                    exchange.WorkerIndex,
                    true,
                    inputEncodeMs,
                    exchange.QueueWaitMs,
                    exchange.IpcRoundTripMs,
                    timing.InputDecodeMs,
                    timing.PluginExecuteMs,
                    timing.OutputEncodeMs,
                    outputDecodeMs,
                    totalTimer.Elapsed.TotalMilliseconds));
                return new NodeExecutorResult(outputs, summary, result.Overlays);
            }
            finally
            {
                if (pool.SharedMemory is not null)
                    foreach (var value in result.Outputs.Values) pool.SharedMemory.Delete(value);
            }
        }
        catch
        {
            totalTimer.Stop();
            var timing = exchange?.Response.Timing ?? new PluginWorkerTiming(0, 0, 0);
            pool.RecordPerformance(new PluginWorkerPerformanceSample(
                DateTimeOffset.UtcNow,
                nodeType,
                exchange?.WorkerIndex ?? -1,
                false,
                inputEncodeMs,
                exchange?.QueueWaitMs ?? 0,
                exchange?.IpcRoundTripMs ?? 0,
                timing.InputDecodeMs,
                timing.PluginExecuteMs,
                timing.OutputEncodeMs,
                outputDecodeMs,
                totalTimer.Elapsed.TotalMilliseconds));
            throw;
        }
        finally
        {
            if (pool.SharedMemory is not null)
                foreach (var value in inputs.Values) pool.SharedMemory.Delete(value);
        }
    }

    /// <summary>
    /// Executes a repeatable benchmark against a temporary worker pool without mutating the active plugin pool.
    /// The AsyncLocal override flows through replay/workflow tasks, so only calls for the selected plugin are redirected.
    /// </summary>
    public async Task<PluginWorkerBenchmarkExecution<T>> RunBenchmarkAsync<T>(
        string pluginId,
        int poolSize,
        Func<CancellationToken, Task> warmup,
        Func<CancellationToken, Task<T>> workload,
        CancellationToken cancellationToken = default)
    {
        if (!_workers.TryGetValue(pluginId, out var active))
            throw new KeyNotFoundException($"Plugin worker '{pluginId}' is not registered.");
        if (poolSize is not (1 or 2 or 4) || poolSize > Math.Max(1, _options.MaxPoolSize))
            throw new InvalidOperationException($"Benchmark pool size {poolSize} is invalid; allowed benchmark sizes are 1/2/4 up to configured MaxPoolSize {_options.MaxPoolSize}.");
        if (_benchmarkOverride.Value is not null)
            throw new InvalidOperationException("Nested plugin worker benchmark scopes are not supported.");

        var benchmarkRoot = Path.Combine(PluginSharedRoot(pluginId), "benchmark-" + Guid.NewGuid().ToString("N"));
        var pool = new WorkerPool(pluginId, active.AssemblyPath, _workerAssemblyPath, poolSize, active.SharedMemoryThresholdBytes, benchmarkRoot, _options, _logger);
        var previous = _benchmarkOverride.Value;
        _benchmarkOverride.Value = new BenchmarkOverride(pluginId, pool);
        try
        {
            await warmup(cancellationToken);
            pool.ResetPerformance();
            var result = await workload(cancellationToken);
            return new PluginWorkerBenchmarkExecution<T>(result, pool.Performance, pool.Status);
        }
        finally
        {
            _benchmarkOverride.Value = previous;
            pool.Dispose();
            try { if (Directory.Exists(benchmarkRoot)) Directory.Delete(benchmarkRoot, recursive: true); } catch { }
        }
    }

    public async Task<PluginWorkerStatus> RestartAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (!_workers.TryGetValue(pluginId, out var pool)) throw new KeyNotFoundException($"Plugin worker '{pluginId}' is not registered.");
        await pool.RestartAsync(cancellationToken);
        return pool.Status;
    }

    public PluginWorkerPerformanceProfile ResetPerformance(string pluginId)
    {
        if (!_workers.TryGetValue(pluginId, out var pool)) throw new KeyNotFoundException($"Plugin worker '{pluginId}' is not registered.");
        pool.ResetPerformance();
        return pool.Performance;
    }

    public void Dispose()
    {
        foreach (var worker in _workers.Values) worker.Dispose();
        _workers.Clear();
    }

    private string PluginSharedRoot(string pluginId)
    {
        var safe = string.Concat(pluginId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
        return Path.Combine(_sharedMemoryRoot, safe);
    }

    private sealed record BenchmarkOverride(string PluginId, WorkerPool Pool);

    private sealed record WorkerPoolExchange(PluginWorkerResponse Response, double QueueWaitMs, double IpcRoundTripMs, int WorkerIndex);
    private sealed record WorkerClientExchange(PluginWorkerResponse Response, double IpcRoundTripMs, int WorkerIndex);

    private sealed class WorkerPool : IDisposable
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

        public async Task<WorkerPoolExchange> SendAsync(PluginWorkerRequest request, bool startup, CancellationToken cancellationToken)
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
                var exchange = await client.SendAsync(request, startup, cancellationToken);
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

    private sealed class WorkerClient : IDisposable
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

        public async Task<WorkerClientExchange> SendAsync(PluginWorkerRequest request, bool startup, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            Interlocked.Exchange(ref _busy, 1);
            try
            {
                if (_circuitOpen) throw new InvalidOperationException($"Plugin worker '{_pluginId}'[{_workerIndex}] restart circuit is open. Use the worker restart endpoint after correcting the plugin.");
                await EnsureStartedAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(startup ? _options.StartupTimeoutMs : _options.ExecutionTimeoutMs));
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
                    var message = $"Plugin worker '{_pluginId}'[{_workerIndex}] timed out after {(startup ? _options.StartupTimeoutMs : _options.ExecutionTimeoutMs)} ms.";
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
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                try
                {
                    if (_process is not null && _process.ExitCode != 0)
                        _lastError = $"Worker process {_workerIndex} exited with code {_process.ExitCode}.";
                }
                catch { }
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
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            if (_process is not null)
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
                try { _process.Dispose(); } catch { }
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
}

public sealed class WorkerPluginNodeExecutor(
    string pluginId,
    string nodeType,
    PluginWorkerSupervisor supervisor) : IVisionNodeExecutor
{
    public string Type => nodeType;

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken) =>
        supervisor.ExecuteAsync(pluginId, nodeType, context, node, cancellationToken);
}
