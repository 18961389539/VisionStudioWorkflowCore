using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record PluginBenchmarkRegressionPolicy(
    double MaximumP95RegressionPercent = 10,
    double MaximumP99RegressionPercent = 15,
    double MinimumThroughputRatio = 0.90,
    double MaximumWorkingSetRegressionPercent = 20,
    double MaximumFailureRate = 0.0);

public sealed record StartPluginBenchmarkRequest(
    string DatasetId,
    string ValidationRunId,
    string PluginId,
    IReadOnlyList<int>? PoolSizes = null,
    int? MaxItems = null,
    int? WarmupItems = null,
    int? WorkloadConcurrency = null,
    string? BaselineRunId = null,
    PluginBenchmarkRegressionPolicy? RegressionPolicy = null,
    PluginBenchmarkBaselineSnapshot? BaselineSnapshot = null);

public sealed record PluginBenchmarkPoolResult(
    int PoolSize,
    int ItemCount,
    int SuccessCount,
    int FailureCount,
    double FailureRate,
    double TotalDurationMs,
    double ThroughputPerSecond,
    long ObservedWorkingSetBytes,
    PluginWorkerLatencyDistribution WorkflowDuration,
    PluginWorkerPerformanceProfile WorkerPerformance);

public sealed record PluginBenchmarkRecommendation(
    int RecommendedPoolSize,
    string Reason,
    double MaximumThroughputPerSecond,
    double RecommendedThroughputPerSecond,
    double RecommendedP95Ms);

public sealed record PluginBenchmarkRegressionGate(
    string Status,
    string? BaselineRunId,
    int? ComparedPoolSize,
    double? P95DeltaPercent,
    double? P99DeltaPercent,
    double? ThroughputRatio,
    double? WorkingSetDeltaPercent,
    double? CandidateFailureRate,
    IReadOnlyList<string> Reasons,
    string? BaselinePluginVersion = null,
    string? BaselinePluginAssemblySha256 = null);


public sealed record PluginBenchmarkBaselineSnapshot(
    string? SourceRunId,
    string DatasetId,
    string PluginId,
    string PluginVersion,
    string? PluginAssemblySha256,
    string WorkflowHash,
    PluginBenchmarkRecommendation Recommendation,
    IReadOnlyList<PluginBenchmarkPoolResult> Results)
{
    public static PluginBenchmarkBaselineSnapshot FromRun(PluginBenchmarkRun run)
    {
        if (!run.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || run.Results is null || run.Results.Count == 0)
            throw new ApiConflictException("Only a completed benchmark with pool results can be exported as a portable baseline.");
        return new PluginBenchmarkBaselineSnapshot(
            run.RunId, run.DatasetId, run.PluginId, run.PluginVersion, run.PluginAssemblySha256, run.WorkflowHash,
            run.Recommendation ?? PluginBenchmarkAnalysis.Recommend(run.Results), run.Results);
    }
}

public sealed record PluginBenchmarkRun(
    string RunId,
    string DatasetId,
    string ValidationRunId,
    string PluginId,
    string PluginVersion,
    string? PluginAssemblySha256,
    string WorkflowHash,
    string Status,
    IReadOnlyList<int> PoolSizes,
    int RequestedCount,
    int WarmupCount,
    int WorkloadConcurrency,
    string? BaselineRunId,
    PluginBenchmarkRegressionPolicy RegressionPolicy,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool CancelRequested,
    string? Error,
    PluginBenchmarkRecommendation? Recommendation,
    PluginBenchmarkRegressionGate? RegressionGate,
    IReadOnlyList<PluginBenchmarkPoolResult>? Results = null);

public static class PluginBenchmarkAnalysis
{
    public static PluginBenchmarkRecommendation Recommend(IReadOnlyList<PluginBenchmarkPoolResult> results)
    {
        var usable = results.Where(x => x.ItemCount > 0 && x.FailureCount == 0).OrderBy(x => x.PoolSize).ToArray();
        if (usable.Length == 0)
            return new PluginBenchmarkRecommendation(results.OrderBy(x => x.PoolSize).FirstOrDefault()?.PoolSize ?? 1,
                "No error-free pool result is available; fix execution failures before capacity tuning.", 0, 0, 0);

        var maxThroughput = usable.Max(x => x.ThroughputPerSecond);
        var threshold = maxThroughput * 0.95;
        var efficient = usable.Where(x => x.ThroughputPerSecond >= threshold).OrderBy(x => x.PoolSize).First();
        var fastest = usable.OrderByDescending(x => x.ThroughputPerSecond).ThenBy(x => x.PoolSize).First();
        var reason = efficient.PoolSize == fastest.PoolSize
            ? $"Pool {efficient.PoolSize} delivers the highest measured throughput ({efficient.ThroughputPerSecond:F2} items/s) without execution failures."
            : $"Pool {efficient.PoolSize} is the smallest pool within 95% of maximum measured throughput ({efficient.ThroughputPerSecond:F2} vs {maxThroughput:F2} items/s), avoiding unnecessary worker memory/process cost.";
        return new PluginBenchmarkRecommendation(efficient.PoolSize, reason, Round(maxThroughput), Round(efficient.ThroughputPerSecond), efficient.WorkflowDuration.P95Ms);
    }

    public static PluginBenchmarkRegressionGate EvaluateRegression(
        PluginBenchmarkRun? baseline,
        IReadOnlyList<PluginBenchmarkPoolResult> candidate,
        PluginBenchmarkRegressionPolicy policy)
        => EvaluateRegression(baseline is null ? null : PluginBenchmarkBaselineSnapshot.FromRun(baseline), candidate, policy);

    public static PluginBenchmarkRegressionGate EvaluateRegression(
        PluginBenchmarkBaselineSnapshot? baseline,
        IReadOnlyList<PluginBenchmarkPoolResult> candidate,
        PluginBenchmarkRegressionPolicy policy)
    {
        if (baseline is null || baseline.Results.Count == 0)
            return new PluginBenchmarkRegressionGate("NO_BASELINE", baseline?.SourceRunId, null, null, null, null, null, null, ["No completed baseline benchmark was selected."], baseline?.PluginVersion, baseline?.PluginAssemblySha256);

        var pool = baseline.Recommendation.RecommendedPoolSize;
        var before = baseline.Results.FirstOrDefault(x => x.PoolSize == pool);
        var after = candidate.FirstOrDefault(x => x.PoolSize == pool);
        if (before is null || after is null)
        {
            var common = baseline.Results.Select(x => x.PoolSize).Intersect(candidate.Select(x => x.PoolSize)).OrderBy(x => x).FirstOrDefault();
            if (common <= 0)
                return new PluginBenchmarkRegressionGate("NOT_COMPARABLE", baseline.SourceRunId, null, null, null, null, null, null, ["Baseline and candidate have no common pool size."], baseline.PluginVersion, baseline.PluginAssemblySha256);
            pool = common;
            before = baseline.Results.First(x => x.PoolSize == pool);
            after = candidate.First(x => x.PoolSize == pool);
        }

        var p95Delta = PercentDelta(before.WorkflowDuration.P95Ms, after.WorkflowDuration.P95Ms);
        var p99Delta = PercentDelta(before.WorkflowDuration.P99Ms, after.WorkflowDuration.P99Ms);
        var throughputRatio = before.ThroughputPerSecond <= 0 ? 0 : after.ThroughputPerSecond / before.ThroughputPerSecond;
        var memoryDelta = PercentDelta(before.ObservedWorkingSetBytes, after.ObservedWorkingSetBytes);
        var reasons = new List<string>();
        if (p95Delta > policy.MaximumP95RegressionPercent) reasons.Add($"p95 latency regressed {p95Delta:F1}% (budget {policy.MaximumP95RegressionPercent:F1}%).");
        if (p99Delta > policy.MaximumP99RegressionPercent) reasons.Add($"p99 latency regressed {p99Delta:F1}% (budget {policy.MaximumP99RegressionPercent:F1}%).");
        if (throughputRatio < policy.MinimumThroughputRatio) reasons.Add($"Throughput ratio is {throughputRatio:P1} (minimum {policy.MinimumThroughputRatio:P1}).");
        if (memoryDelta > policy.MaximumWorkingSetRegressionPercent) reasons.Add($"Observed worker working set increased {memoryDelta:F1}% (budget {policy.MaximumWorkingSetRegressionPercent:F1}%).");
        if (after.FailureRate > policy.MaximumFailureRate) reasons.Add($"Failure rate is {after.FailureRate:P2} (maximum {policy.MaximumFailureRate:P2}).");
        return new PluginBenchmarkRegressionGate(reasons.Count == 0 ? "PASS" : "FAIL", baseline.SourceRunId, pool,
            Round(p95Delta), Round(p99Delta), Round(throughputRatio), Round(memoryDelta), Round(after.FailureRate), reasons, baseline.PluginVersion, baseline.PluginAssemblySha256);
    }

    private static double PercentDelta(double before, double after) => before <= 0 ? (after <= 0 ? 0 : 100) : (after - before) / before * 100d;
    private static double PercentDelta(long before, long after) => before <= 0 ? (after <= 0 ? 0 : 100) : (after - before) / (double)before * 100d;
    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}

public sealed class PluginBenchmarkStore(IVisionStudioDbFactory dbFactory)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public PluginBenchmarkStore(SqliteMetadataDatabase db) : this(new VisionStudioDbFactory(db)) { }

    public async Task<PluginBenchmarkRun> CreateAsync(StartPluginBenchmarkRequest request, string pluginVersion, string? assemblyHash, string workflowHash, IReadOnlyList<int> poolSizes, int itemCount, int warmupCount, int concurrency, CancellationToken ct)
    {
        var runId = $"plugin-bench-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var policy = request.RegressionPolicy ?? new PluginBenchmarkRegressionPolicy();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = new PluginBenchmarkRunEntity
        {
            RunId = runId,
            DatasetId = request.DatasetId,
            ValidationRunId = request.ValidationRunId,
            PluginId = request.PluginId,
            PluginVersion = pluginVersion,
            PluginAssemblySha256 = assemblyHash,
            WorkflowHash = workflowHash,
            Status = "Running",
            PoolSizesJson = JsonSerializer.Serialize(poolSizes, _json),
            RequestedCount = itemCount,
            WarmupCount = warmupCount,
            WorkloadConcurrency = concurrency,
            BaselineRunId = request.BaselineRunId,
            PolicyJson = JsonSerializer.Serialize(policy, _json),
            StartedAt = now,
            CancelRequested = false
        };
        db.PluginBenchmarkRuns.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToRun(entity, null);
    }

    public async Task AddPoolResultAsync(string runId, PluginBenchmarkPoolResult result, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var workflowJson = JsonSerializer.Serialize(result.WorkflowDuration, _json);
        var perfJson = JsonSerializer.Serialize(result.WorkerPerformance, _json);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            var existing = await db.PluginBenchmarkPoolResults
                .AsTracking()
                .FirstOrDefaultAsync(x => x.RunId == runId && x.PoolSize == result.PoolSize, ct);
            if (existing is null)
            {
                db.PluginBenchmarkPoolResults.Add(new PluginBenchmarkPoolResultEntity
                {
                    RunId = runId,
                    PoolSize = result.PoolSize,
                    ItemCount = result.ItemCount,
                    SuccessCount = result.SuccessCount,
                    FailureCount = result.FailureCount,
                    FailureRate = result.FailureRate,
                    TotalDurationMs = result.TotalDurationMs,
                    ThroughputPerSecond = result.ThroughputPerSecond,
                    ObservedWorkingSetBytes = result.ObservedWorkingSetBytes,
                    WorkflowDurationJson = workflowJson,
                    WorkerPerformanceJson = perfJson,
                    CreatedAt = now
                });
            }
            else
            {
                existing.ItemCount = result.ItemCount;
                existing.SuccessCount = result.SuccessCount;
                existing.FailureCount = result.FailureCount;
                existing.FailureRate = result.FailureRate;
                existing.TotalDurationMs = result.TotalDurationMs;
                existing.ThroughputPerSecond = result.ThroughputPerSecond;
                existing.ObservedWorkingSetBytes = result.ObservedWorkingSetBytes;
                existing.WorkflowDurationJson = workflowJson;
                existing.WorkerPerformanceJson = perfJson;
                existing.CreatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CompleteAsync(string runId, string status, PluginBenchmarkRecommendation? recommendation, PluginBenchmarkRegressionGate? regression, string? error, CancellationToken ct)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var recJson = recommendation is null ? null : JsonSerializer.Serialize(recommendation, _json);
        var regJson = regression is null ? null : JsonSerializer.Serialize(regression, _json);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PluginBenchmarkRuns
            .Where(x => x.RunId == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.CompletedAt, completedAt)
                .SetProperty(x => x.Error, error)
                .SetProperty(x => x.RecommendationJson, recJson)
                .SetProperty(x => x.RegressionJson, regJson), ct);
    }

    public async Task RequestCancelAsync(string runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.PluginBenchmarkRuns
            .Where(x => x.RunId == runId && x.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelRequested, true), ct);
        if (affected == 0) throw new ApiConflictException("Benchmark run is not running or does not exist.");
    }

    public async Task<IReadOnlyList<PluginBenchmarkRun>> ListAsync(string? pluginId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<PluginBenchmarkRunEntity> query = db.PluginBenchmarkRuns;
        if (!string.IsNullOrWhiteSpace(pluginId)) query = query.Where(x => x.PluginId == pluginId);
        var runs = await query
            .OrderByDescending(x => x.StartedAt)
            .Take(100)
            .ToListAsync(ct);
        if (runs.Count == 0) return [];
        var runIds = runs.Select(x => x.RunId).ToList();
        var poolResults = await db.PluginBenchmarkPoolResults
            .Where(x => runIds.Contains(x.RunId))
            .OrderBy(x => x.PoolSize)
            .ToListAsync(ct);
        var byRun = poolResults.GroupBy(x => x.RunId).ToDictionary(g => g.Key, g => (IReadOnlyList<PluginBenchmarkPoolResult>)g.Select(ToPoolResult).ToList());
        return runs.Select(r => ToRun(r, byRun.TryGetValue(r.RunId, out var list) ? list : [])).ToList();
    }

    public async Task<PluginBenchmarkRun> GetAsync(string runId, bool includeResults, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.PluginBenchmarkRuns.FirstOrDefaultAsync(x => x.RunId == runId, ct)
            ?? throw new ApiNotFoundException($"Plugin benchmark '{runId}' was not found.");
        if (!includeResults) return ToRun(run, null);
        var results = await db.PluginBenchmarkPoolResults
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.PoolSize)
            .ToListAsync(ct);
        return ToRun(run, results.Select(ToPoolResult).ToList());
    }

    private PluginBenchmarkPoolResult ToPoolResult(PluginBenchmarkPoolResultEntity e)
    {
        var workflow = JsonSerializer.Deserialize<PluginWorkerLatencyDistribution>(e.WorkflowDurationJson, _json) ?? new(0, 0, 0, 0, 0);
        var perf = JsonSerializer.Deserialize<PluginWorkerPerformanceProfile>(e.WorkerPerformanceJson, _json)
            ?? throw new InvalidOperationException("Benchmark performance JSON is invalid.");
        return new PluginBenchmarkPoolResult(e.PoolSize, e.ItemCount, e.SuccessCount, e.FailureCount, e.FailureRate, e.TotalDurationMs, e.ThroughputPerSecond, e.ObservedWorkingSetBytes, workflow, perf);
    }

    private PluginBenchmarkRun ToRun(PluginBenchmarkRunEntity e, IReadOnlyList<PluginBenchmarkPoolResult>? results)
        => new(
            e.RunId, e.DatasetId, e.ValidationRunId, e.PluginId, e.PluginVersion, e.PluginAssemblySha256, e.WorkflowHash, e.Status,
            JsonSerializer.Deserialize<int[]>(e.PoolSizesJson, _json) ?? [], e.RequestedCount, e.WarmupCount, e.WorkloadConcurrency, e.BaselineRunId,
            JsonSerializer.Deserialize<PluginBenchmarkRegressionPolicy>(e.PolicyJson, _json) ?? new(), e.StartedAt, e.CompletedAt, e.CancelRequested,
            e.Error, e.RecommendationJson is null ? null : JsonSerializer.Deserialize<PluginBenchmarkRecommendation>(e.RecommendationJson, _json),
            e.RegressionJson is null ? null : JsonSerializer.Deserialize<PluginBenchmarkRegressionGate>(e.RegressionJson, _json), results);
}

public sealed class PluginPerformanceBenchmarkService(
    PluginBenchmarkStore store,
    DatasetValidationStore datasets,
    MediaLibraryService media,
    OfflineReplayService replay,
    PluginWorkerSupervisor workers,
    PluginManager plugins,
    IHostApplicationLifetime lifetime,
    RuntimeWorkloadGate workloads,
    ILogger<PluginPerformanceBenchmarkService> logger)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PluginBenchmarkRun> StartAsync(StartPluginBenchmarkRequest request, CancellationToken ct)
    {
        var admission = workloads.AcquireBenchmark();
        try { return await StartCoreAsync(request, admission, ct); }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    private async Task<PluginBenchmarkRun> StartCoreAsync(StartPluginBenchmarkRequest request, IDisposable admission, CancellationToken ct)
    {
        var dataset = await datasets.GetDatasetAsync(request.DatasetId, ct);
        if (dataset.Items.Count == 0) throw new ApiConflictException("Benchmark Dataset has no items.");
        if (dataset.Items.Any(x => !x.ReplayReady)) throw new ApiConflictException("Benchmark Dataset contains samples that are not replay-ready.");
        var validation = await datasets.GetRunAsync(request.ValidationRunId, false, ct);
        if (!validation.DatasetId.Equals(request.DatasetId, StringComparison.OrdinalIgnoreCase)) throw new ApiValidationException("ValidationRunId must belong to the selected Dataset.");
        if (!validation.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) throw new ApiConflictException("Benchmark requires a completed Dataset Validation run so the candidate Workflow is frozen and reproducible.");
        var workflow = await datasets.GetRunWorkflowAsync(request.ValidationRunId, ct);
        if (workflow.Nodes.Any(x => BuiltInNodeTypes.Is(x, BuiltInNodeTypes.CameraSyncCapture))) throw new ApiConflictException("Plugin benchmark does not execute synchronized hardware acquisition workflows.");

        var plugin = plugins.Plugins.FirstOrDefault(x => x.Id.Equals(request.PluginId, StringComparison.OrdinalIgnoreCase) && x.Loaded)
            ?? throw new ApiNotFoundException($"Loaded plugin '{request.PluginId}' was not found.");
        if (plugin.IsolationMode != VisionPluginIsolationMode.WorkerProcess) throw new ApiConflictException("V0.59 headless/CI benchmark applies only to WorkerProcess plugins.");
        var pluginNodeTypes = plugin.Tools is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : plugin.Tools.Select(x => x.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!workflow.Nodes.Any(x => pluginNodeTypes.Contains(x.Type))) throw new ApiConflictException("Selected Validation Workflow does not reference a Tool from the selected plugin.");

        var pools = (request.PoolSizes is { Count: > 0 } ? request.PoolSizes : [1,2,4]).Distinct().OrderBy(x => x).ToArray();
        if (pools.Any(x => x is not (1 or 2 or 4))) throw new ApiValidationException("Benchmark pool sizes must be selected from 1, 2 or 4.");
        var count = Math.Clamp(request.MaxItems ?? Math.Min(dataset.Items.Count, 100), 1, dataset.Items.Count);
        var warmup = Math.Clamp(request.WarmupItems ?? Math.Min(5, count), 0, Math.Min(50, dataset.Items.Count));
        var concurrency = Math.Clamp(request.WorkloadConcurrency ?? 4, 1, 16);
        if (!string.IsNullOrWhiteSpace(request.BaselineRunId) && request.BaselineSnapshot is not null)
            throw new ApiValidationException("Specify either BaselineRunId or BaselineSnapshot, not both.");

        PluginBenchmarkBaselineSnapshot? portableBaseline = request.BaselineSnapshot;
        if (!string.IsNullOrWhiteSpace(request.BaselineRunId))
        {
            var baseline = await store.GetAsync(request.BaselineRunId, true, ct);
            if (!baseline.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) throw new ApiConflictException("Baseline benchmark must be completed.");
            portableBaseline = PluginBenchmarkBaselineSnapshot.FromRun(baseline);
        }
        if (portableBaseline is not null)
        {
            if (!portableBaseline.DatasetId.Equals(request.DatasetId, StringComparison.OrdinalIgnoreCase) || !portableBaseline.WorkflowHash.Equals(validation.WorkflowHash, StringComparison.OrdinalIgnoreCase))
                throw new ApiValidationException("Baseline benchmark must use the same Dataset and frozen Workflow hash.");
            if (!portableBaseline.PluginId.Equals(request.PluginId, StringComparison.OrdinalIgnoreCase))
                throw new ApiValidationException("Portable baseline PluginId does not match the candidate PluginId.");
            if (portableBaseline.Results.Count == 0) throw new ApiValidationException("Portable baseline does not contain pool results.");
        }

        var run = await store.CreateAsync(request, plugin.Version, plugin.AssemblySha256, validation.WorkflowHash, pools, count, warmup, concurrency, ct);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        if (!_active.TryAdd(run.RunId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException("Benchmark runner ID collision.");
        }
        _ = Task.Run(async () =>
        {
            using (admission)
            {
                try
                {
                    await ExecuteAsync(run.RunId, dataset, workflow, pools, count, warmup, concurrency,
                        portableBaseline, run.RegressionPolicy, cts.Token);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not finalize plugin benchmark {RunId}.", run.RunId);
                }
                finally
                {
                    if (_active.TryRemove(run.RunId, out var completed)) completed.Dispose();
                }
            }
        });
        return run;
    }

    public async Task CancelAsync(string runId, CancellationToken ct)
    {
        await store.RequestCancelAsync(runId, ct);
        if (_active.TryGetValue(runId, out var cts))
        {
            try { await cts.CancelAsync(); }
            catch (ObjectDisposedException) { /* The benchmark finished while cancellation was being requested. */ }
        }
    }

    private async Task ExecuteAsync(string runId, ValidationDatasetDetail dataset, WorkflowDefinition workflow, IReadOnlyList<int> pools, int count, int warmup, int concurrency, PluginBenchmarkBaselineSnapshot? baseline, PluginBenchmarkRegressionPolicy policy, CancellationToken ct)
    {
        var status = "Completed";
        string? error = null;
        PluginBenchmarkRecommendation? recommendation = null;
        PluginBenchmarkRegressionGate? regression = null;
        try
        {
            var run = await store.GetAsync(runId, false, ct);
            var measuredItems = dataset.Items.Take(count).ToArray();
            var warmupItems = dataset.Items.Take(warmup).ToArray();
            foreach (var poolSize in pools)
            {
                ct.ThrowIfCancellationRequested();
                var execution = await workers.RunBenchmarkAsync(
                    run.PluginId,
                    poolSize,
                    async token => { if (warmupItems.Length > 0) await ExecuteItemsAsync(warmupItems, workflow, Math.Min(concurrency, Math.Max(1, poolSize)), token); },
                    token => ExecuteMeasuredAsync(measuredItems, workflow, concurrency, token),
                    ct);
                var workload = execution.Result;
                var result = new PluginBenchmarkPoolResult(poolSize, workload.Durations.Length, workload.SuccessCount, workload.FailureCount,
                    workload.Durations.Length == 0 ? 1 : workload.FailureCount / (double)workload.Durations.Length,
                    workload.ElapsedMs,
                    workload.ElapsedMs <= 0 ? 0 : workload.Durations.Length / (workload.ElapsedMs / 1000d),
                    execution.Status.WorkingSetBytes,
                    Dist(workload.Durations),
                    execution.Performance);
                await store.AddPoolResultAsync(runId, result, CancellationToken.None);
            }

            var completed = await store.GetAsync(runId, true, CancellationToken.None);
            recommendation = PluginBenchmarkAnalysis.Recommend(completed.Results ?? []);
            regression = PluginBenchmarkAnalysis.EvaluateRegression(baseline, completed.Results ?? [], policy);
        }
        catch (OperationCanceledException) { status = "Cancelled"; }
        catch (Exception ex) { status = "Failed"; error = ex.Message; }
        finally
        {
            await store.CompleteAsync(runId, status, recommendation, regression, error, CancellationToken.None);
        }
    }

    private async Task<MeasuredWorkload> ExecuteMeasuredAsync(IReadOnlyList<ValidationDatasetItem> items, WorkflowDefinition workflow, int concurrency, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var durations = new ConcurrentBag<double>();
        var successes = 0;
        var failures = 0;
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var itemSw = Stopwatch.StartNew();
                try
                {
                    var result = await ExecuteItemAsync(item, workflow, ct);
                    if (result.Success) Interlocked.Increment(ref successes); else Interlocked.Increment(ref failures);
                }
                catch { Interlocked.Increment(ref failures); }
                finally { itemSw.Stop(); durations.Add(itemSw.Elapsed.TotalMilliseconds); }
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();
        return new MeasuredWorkload(durations.OrderBy(x => x).ToArray(), successes, failures, sw.Elapsed.TotalMilliseconds);
    }

    private async Task ExecuteItemsAsync(IReadOnlyList<ValidationDatasetItem> items, WorkflowDefinition workflow, int concurrency, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try { _ = await ExecuteItemAsync(item, workflow, ct); }
            finally { gate.Release(); }
        }));
    }

    private Task<OfflineReplayResult> ExecuteItemAsync(ValidationDatasetItem item, WorkflowDefinition workflow, CancellationToken ct)
    {
        if (item.SourceKind == "TRACE")
            return replay.ExecuteAsync(item.SourceRef, new OfflineReplayRequest(workflow, new VisionRunOptions(DebugRunMode.Full)), "PluginBenchmark", ct, persistReplayInput: false, persistTrace: false);
        var path = media.ResolveItemPath(item.SourceRef);
        return replay.ExecuteImageAsync(item.SourceRef, path, workflow, "PluginBenchmark", new VisionRunOptions(DebugRunMode.Full), ct, persistReplayInput: false, persistTrace: false);
    }

    private static PluginWorkerLatencyDistribution Dist(double[] sorted)
    {
        if (sorted.Length == 0) return new(0,0,0,0,0);
        double P(double q) { var pos=(sorted.Length-1)*q; var lo=(int)Math.Floor(pos); var hi=(int)Math.Ceiling(pos); return lo==hi?sorted[lo]:sorted[lo]+(sorted[hi]-sorted[lo])*(pos-lo); }
        return new(Math.Round(sorted.Average(),4),Math.Round(P(.5),4),Math.Round(P(.95),4),Math.Round(P(.99),4),Math.Round(sorted[^1],4));
    }

    private sealed record MeasuredWorkload(double[] Durations, int SuccessCount, int FailureCount, double ElapsedMs);
}
