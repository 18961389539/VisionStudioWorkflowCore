namespace VisionStudio.Api;

public sealed record PluginWorkerLatencyDistribution(
    double AverageMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs);

public sealed record PluginWorkerPerformanceSample(
    DateTimeOffset RecordedAt,
    string NodeType,
    int WorkerIndex,
    bool Success,
    double HostInputEncodeMs,
    double QueueWaitMs,
    double IpcRoundTripMs,
    double WorkerInputDecodeMs,
    double PluginExecuteMs,
    double WorkerOutputEncodeMs,
    double HostOutputDecodeMs,
    double TotalMs)
{
    public double IpcOverheadMs => Math.Max(0, IpcRoundTripMs - WorkerInputDecodeMs - PluginExecuteMs - WorkerOutputEncodeMs);
    public double WorkerServiceMs => WorkerInputDecodeMs + PluginExecuteMs + WorkerOutputEncodeMs;
}

public sealed record PluginWorkerToolPerformanceProfile(
    string NodeType,
    int SampleCount,
    int FailureCount,
    PluginWorkerLatencyDistribution QueueWait,
    PluginWorkerLatencyDistribution PluginExecute,
    PluginWorkerLatencyDistribution Total);

public sealed record PluginWorkerPerformanceProfile(
    string PluginId,
    int PoolSize,
    int WindowCapacity,
    int SampleCount,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset? WindowStartedAt,
    DateTimeOffset? WindowEndedAt,
    double RequestsPerSecond,
    double EstimatedPoolUtilization,
    double QueuePressureRatio,
    string DominantStage,
    int RecommendedPoolSize,
    bool RecommendationReady,
    string Recommendation,
    PluginWorkerLatencyDistribution HostInputEncode,
    PluginWorkerLatencyDistribution QueueWait,
    PluginWorkerLatencyDistribution IpcRoundTrip,
    PluginWorkerLatencyDistribution IpcOverhead,
    PluginWorkerLatencyDistribution WorkerInputDecode,
    PluginWorkerLatencyDistribution PluginExecute,
    PluginWorkerLatencyDistribution WorkerOutputEncode,
    PluginWorkerLatencyDistribution HostOutputDecode,
    PluginWorkerLatencyDistribution Total,
    IReadOnlyList<PluginWorkerToolPerformanceProfile> Tools);

/// <summary>
/// Rolling in-memory profiler for one plugin worker pool. The recommendation is advisory only: V0.59 never
/// hot-resizes a production pool because pool size is part of RuntimeDependencyManifest provenance.
/// </summary>
public sealed class PluginWorkerPerformanceProfiler
{
    private readonly object _sync = new();
    private readonly Queue<PluginWorkerPerformanceSample> _samples = new();
    private readonly int _capacity;
    private readonly int _minimumSamples;
    private readonly int _maxPoolSize;
    private readonly double _targetUtilization;
    private readonly double _queuePressureRatio;

    public PluginWorkerPerformanceProfiler(
        int capacity,
        int minimumSamples,
        int maxPoolSize,
        double targetUtilization,
        double queuePressureRatio)
    {
        _capacity = Math.Clamp(capacity, 32, 4096);
        _minimumSamples = Math.Clamp(minimumSamples, 5, _capacity);
        _maxPoolSize = Math.Clamp(maxPoolSize, 1, 4);
        _targetUtilization = Math.Clamp(targetUtilization, 0.25, 0.95);
        _queuePressureRatio = Math.Clamp(queuePressureRatio, 0.01, 2.0);
    }

    public void Record(PluginWorkerPerformanceSample sample)
    {
        lock (_sync)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > _capacity) _samples.Dequeue();
        }
    }

    public void Reset()
    {
        lock (_sync) _samples.Clear();
    }

    public PluginWorkerPerformanceProfile Snapshot(string pluginId, int poolSize)
    {
        PluginWorkerPerformanceSample[] all;
        lock (_sync) all = _samples.ToArray();
        var successful = all.Where(x => x.Success).ToArray();
        var started = all.FirstOrDefault()?.RecordedAt;
        var ended = all.LastOrDefault()?.RecordedAt;
        var spanSeconds = started is not null && ended is not null ? Math.Max(0, (ended.Value - started.Value).TotalSeconds) : 0;
        var requestsPerSecond = spanSeconds >= 0.5 ? all.Length / spanSeconds : 0;

        var hostEncode = Dist(successful.Select(x => x.HostInputEncodeMs));
        var queue = Dist(successful.Select(x => x.QueueWaitMs));
        var roundTrip = Dist(successful.Select(x => x.IpcRoundTripMs));
        var ipcOverhead = Dist(successful.Select(x => x.IpcOverheadMs));
        var workerDecode = Dist(successful.Select(x => x.WorkerInputDecodeMs));
        var execute = Dist(successful.Select(x => x.PluginExecuteMs));
        var workerEncode = Dist(successful.Select(x => x.WorkerOutputEncodeMs));
        var hostDecode = Dist(successful.Select(x => x.HostOutputDecodeMs));
        var total = Dist(successful.Select(x => x.TotalMs));
        var service = Dist(successful.Select(x => x.WorkerServiceMs));

        var utilization = requestsPerSecond > 0 && service.P95Ms > 0
            ? Math.Clamp(requestsPerSecond * service.P95Ms / 1000d / Math.Max(1, poolSize), 0, 9.99)
            : 0;
        var pressure = queue.P95Ms / Math.Max(1, service.P50Ms);
        var ready = successful.Length >= _minimumSamples;
        var recommendation = Recommend(poolSize, ready, spanSeconds, requestsPerSecond, utilization, pressure, queue, service);

        var stageAverages = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Host input encode"] = hostEncode.AverageMs,
            ["Queue wait"] = queue.AverageMs,
            ["IPC/process overhead"] = ipcOverhead.AverageMs,
            ["Worker input decode"] = workerDecode.AverageMs,
            ["Plugin execute"] = execute.AverageMs,
            ["Worker output encode"] = workerEncode.AverageMs,
            ["Host output decode"] = hostDecode.AverageMs
        };
        var dominant = successful.Length == 0 ? "No samples" : stageAverages.MaxBy(x => x.Value).Key;

        var tools = all.GroupBy(x => x.NodeType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var good = group.Where(x => x.Success).ToArray();
                return new PluginWorkerToolPerformanceProfile(
                    group.Key,
                    group.Count(),
                    group.Count(x => !x.Success),
                    Dist(good.Select(x => x.QueueWaitMs)),
                    Dist(good.Select(x => x.PluginExecuteMs)),
                    Dist(good.Select(x => x.TotalMs)));
            }).ToArray();

        return new PluginWorkerPerformanceProfile(
            pluginId,
            poolSize,
            _capacity,
            all.Length,
            successful.Length,
            all.Length - successful.Length,
            started,
            ended,
            Round(requestsPerSecond),
            Round(utilization),
            Round(pressure),
            dominant,
            recommendation.Size,
            ready,
            recommendation.Reason,
            hostEncode,
            queue,
            roundTrip,
            ipcOverhead,
            workerDecode,
            execute,
            workerEncode,
            hostDecode,
            total,
            tools);
    }

    private (int Size, string Reason) Recommend(
        int current,
        bool ready,
        double spanSeconds,
        double requestsPerSecond,
        double utilization,
        double pressure,
        PluginWorkerLatencyDistribution queue,
        PluginWorkerLatencyDistribution service)
    {
        if (!ready)
            return (current, $"Collect at least {_minimumSamples} successful executions before changing pool size.");

        var allowed = new[] { 1, 2, 4 }.Where(x => x <= _maxPoolSize).ToArray();
        if (allowed.Length == 0) allowed = [1];
        var requiredWorkers = requestsPerSecond > 0 && service.P95Ms > 0
            ? Math.Max(1, (int)Math.Ceiling(requestsPerSecond * service.P95Ms / 1000d / _targetUtilization))
            : 1;
        var demandSize = allowed.FirstOrDefault(x => x >= requiredWorkers);
        if (demandSize == 0) demandSize = allowed[^1];

        var next = allowed.FirstOrDefault(x => x > current);
        if (next == 0) next = current;
        if (queue.P95Ms >= 2 && pressure >= _queuePressureRatio)
            demandSize = Math.Max(demandSize, next);

        // Down-size only after a long enough quiet window; short benchmark bursts should never recommend shrinkage.
        if (spanSeconds >= 30 && current > 1 && utilization < 0.25 && queue.P95Ms < 0.5)
        {
            var previous = allowed.LastOrDefault(x => x < current);
            if (previous > 0) demandSize = Math.Min(demandSize, previous);
        }

        if (demandSize > current)
            return (demandSize, $"Increase to pool {demandSize}: p95 queue wait {queue.P95Ms:F2} ms, queue/service ratio {pressure:F2}, estimated current utilization {utilization:P0}.");
        if (demandSize < current)
            return (demandSize, $"Pool {current} appears over-provisioned across this window; estimated utilization {utilization:P0} and p95 queue wait {queue.P95Ms:F2} ms.");
        return (current, $"Keep pool {current}: measured queue pressure and estimated utilization are within the advisory target ({_targetUtilization:P0}).");
    }

    private static PluginWorkerLatencyDistribution Dist(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).Select(x => Math.Max(0, x)).OrderBy(x => x).ToArray();
        if (values.Length == 0) return new(0, 0, 0, 0, 0);
        return new(
            Round(values.Average()),
            Round(Percentile(values, 0.50)),
            Round(Percentile(values, 0.95)),
            Round(Percentile(values, 0.99)),
            Round(values[^1]));
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = (sorted.Length - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
