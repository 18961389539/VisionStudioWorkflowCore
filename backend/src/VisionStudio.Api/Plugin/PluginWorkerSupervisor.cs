using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Workers;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

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
    /// <summary>Windows Job Object 进程私有内存上限；0 = 不启用（插件 Worker 默认保持历史行为）。</summary>
    public int JobObjectMemoryLimitMb { get; set; }
    /// <summary>Windows Job Object CPU 硬上限（百分比）；0 = 不启用。现场核数差异大，默认关闭。</summary>
    public int JobObjectCpuRatePercent { get; set; }
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

/// <summary>
/// Resolves the IPC deadline for one plugin execution. A node-level <c>__timeoutMs</c> is the
/// caller's explicit budget; the worker option remains the fallback when the node does not set one.
/// </summary>
internal static class PluginWorkerTimeoutPolicy
{
    internal static int Resolve(int nodeTimeoutMs, int configuredMs) => nodeTimeoutMs > 0 ? nodeTimeoutMs : configuredMs;
}

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
                WorkflowScope = context.WorkflowScope,
                RunId = context.RunId
            }, startup: false, cancellationToken, NodeTimeoutPolicy.Resolve(node));

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
            try { if (Directory.Exists(benchmarkRoot)) Directory.Delete(benchmarkRoot, recursive: true); } catch { /* 基准临时目录清理失败：不影响性能结果 */ }
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
