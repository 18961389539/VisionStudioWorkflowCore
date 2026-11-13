using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

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

public sealed class PluginBenchmarkStore(SqliteMetadataDatabase db)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<PluginBenchmarkRun> CreateAsync(StartPluginBenchmarkRequest request, string pluginVersion, string? assemblyHash, string workflowHash, IReadOnlyList<int> poolSizes, int itemCount, int warmupCount, int concurrency, CancellationToken ct)
    {
        var runId = $"plugin-bench-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var policy = request.RegressionPolicy ?? new PluginBenchmarkRegressionPolicy();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO plugin_benchmark_runs(run_id,dataset_id,validation_run_id,plugin_id,plugin_version,plugin_assembly_sha256,workflow_hash,status,pool_sizes_json,requested_count,warmup_count,workload_concurrency,baseline_run_id,policy_json,started_at,completed_at,cancel_requested,error,recommendation_json,regression_json)
VALUES($run,$dataset,$validation,$plugin,$version,$assembly,$workflow,'Running',$pools,$requested,$warmup,$concurrency,$baseline,$policy,$started,NULL,0,NULL,NULL,NULL);
""";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$dataset", request.DatasetId);
        command.Parameters.AddWithValue("$validation", request.ValidationRunId);
        command.Parameters.AddWithValue("$plugin", request.PluginId);
        command.Parameters.AddWithValue("$version", pluginVersion);
        command.Parameters.AddWithValue("$assembly", (object?)assemblyHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflow", workflowHash);
        command.Parameters.AddWithValue("$pools", JsonSerializer.Serialize(poolSizes, _json));
        command.Parameters.AddWithValue("$requested", itemCount);
        command.Parameters.AddWithValue("$warmup", warmupCount);
        command.Parameters.AddWithValue("$concurrency", concurrency);
        command.Parameters.AddWithValue("$baseline", (object?)request.BaselineRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(policy, _json));
        command.Parameters.AddWithValue("$started", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        return await GetAsync(runId, false, ct);
    }

    public async Task AddPoolResultAsync(string runId, PluginBenchmarkPoolResult result, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT OR REPLACE INTO plugin_benchmark_pool_results(run_id,pool_size,item_count,success_count,failure_count,failure_rate,total_duration_ms,throughput_per_second,observed_working_set_bytes,workflow_duration_json,worker_performance_json,created_at)
VALUES($run,$pool,$items,$success,$failure,$failureRate,$duration,$throughput,$memory,$workflow,$performance,$created);
""";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$pool", result.PoolSize);
        command.Parameters.AddWithValue("$items", result.ItemCount);
        command.Parameters.AddWithValue("$success", result.SuccessCount);
        command.Parameters.AddWithValue("$failure", result.FailureCount);
        command.Parameters.AddWithValue("$failureRate", result.FailureRate);
        command.Parameters.AddWithValue("$duration", result.TotalDurationMs);
        command.Parameters.AddWithValue("$throughput", result.ThroughputPerSecond);
        command.Parameters.AddWithValue("$memory", result.ObservedWorkingSetBytes);
        command.Parameters.AddWithValue("$workflow", JsonSerializer.Serialize(result.WorkflowDuration, _json));
        command.Parameters.AddWithValue("$performance", JsonSerializer.Serialize(result.WorkerPerformance, _json));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(string runId, string status, PluginBenchmarkRecommendation? recommendation, PluginBenchmarkRegressionGate? regression, string? error, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plugin_benchmark_runs SET status=$status,completed_at=$completed,error=$error,recommendation_json=$recommendation,regression_json=$regression WHERE run_id=$run;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$recommendation", recommendation is null ? DBNull.Value : JsonSerializer.Serialize(recommendation, _json));
        command.Parameters.AddWithValue("$regression", regression is null ? DBNull.Value : JsonSerializer.Serialize(regression, _json));
        command.Parameters.AddWithValue("$run", runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RequestCancelAsync(string runId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plugin_benchmark_runs SET cancel_requested=1 WHERE run_id=$run AND status='Running';";
        command.Parameters.AddWithValue("$run", runId);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiConflictException("Benchmark run is not running or does not exist.");
    }

    public async Task<IReadOnlyList<PluginBenchmarkRun>> ListAsync(string? pluginId, CancellationToken ct)
    {
        var result = new List<PluginBenchmarkRun>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(pluginId)
            ? "SELECT run_id,dataset_id,validation_run_id,plugin_id,plugin_version,plugin_assembly_sha256,workflow_hash,status,pool_sizes_json,requested_count,warmup_count,workload_concurrency,baseline_run_id,policy_json,started_at,completed_at,cancel_requested,error,recommendation_json,regression_json FROM plugin_benchmark_runs ORDER BY started_at DESC LIMIT 100;"
            : "SELECT run_id,dataset_id,validation_run_id,plugin_id,plugin_version,plugin_assembly_sha256,workflow_hash,status,pool_sizes_json,requested_count,warmup_count,workload_concurrency,baseline_run_id,policy_json,started_at,completed_at,cancel_requested,error,recommendation_json,regression_json FROM plugin_benchmark_runs WHERE plugin_id=$plugin ORDER BY started_at DESC LIMIT 100;";
        if (!string.IsNullOrWhiteSpace(pluginId)) command.Parameters.AddWithValue("$plugin", pluginId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadRun(reader, null));
        var detailed = new List<PluginBenchmarkRun>(result.Count);
        foreach (var run in result) detailed.Add(await GetAsync(run.RunId, true, ct));
        return detailed;
    }

    public async Task<PluginBenchmarkRun> GetAsync(string runId, bool includeResults, CancellationToken ct)
    {
        PluginBenchmarkRun? run = null;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,dataset_id,validation_run_id,plugin_id,plugin_version,plugin_assembly_sha256,workflow_hash,status,pool_sizes_json,requested_count,warmup_count,workload_concurrency,baseline_run_id,policy_json,started_at,completed_at,cancel_requested,error,recommendation_json,regression_json FROM plugin_benchmark_runs WHERE run_id=$run;";
            command.Parameters.AddWithValue("$run", runId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) run = ReadRun(reader, null);
        }
        if (run is null) throw new ApiNotFoundException($"Plugin benchmark '{runId}' was not found.");
        return includeResults ? run with { Results = await GetResultsAsync(runId, connection, ct) } : run;
    }

    private async Task<IReadOnlyList<PluginBenchmarkPoolResult>> GetResultsAsync(string runId, SqliteConnection connection, CancellationToken ct)
    {
        var results = new List<PluginBenchmarkPoolResult>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pool_size,item_count,success_count,failure_count,failure_rate,total_duration_ms,throughput_per_second,observed_working_set_bytes,workflow_duration_json,worker_performance_json FROM plugin_benchmark_pool_results WHERE run_id=$run ORDER BY pool_size;";
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var workflow = JsonSerializer.Deserialize<PluginWorkerLatencyDistribution>(reader.GetString(8), _json) ?? new(0,0,0,0,0);
            var perf = JsonSerializer.Deserialize<PluginWorkerPerformanceProfile>(reader.GetString(9), _json) ?? throw new InvalidOperationException("Benchmark performance JSON is invalid.");
            results.Add(new PluginBenchmarkPoolResult(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetDouble(4),reader.GetDouble(5),reader.GetDouble(6),reader.GetInt64(7),workflow,perf));
        }
        return results;
    }

    private PluginBenchmarkRun ReadRun(SqliteDataReader reader, IReadOnlyList<PluginBenchmarkPoolResult>? results)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7),
            JsonSerializer.Deserialize<int[]>(reader.GetString(8), _json) ?? [], reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            JsonSerializer.Deserialize<PluginBenchmarkRegressionPolicy>(reader.GetString(13), _json) ?? new(), DateTimeOffset.Parse(reader.GetString(14)), reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)), reader.GetInt32(16) != 0,
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : JsonSerializer.Deserialize<PluginBenchmarkRecommendation>(reader.GetString(18), _json), reader.IsDBNull(19) ? null : JsonSerializer.Deserialize<PluginBenchmarkRegressionGate>(reader.GetString(19), _json), results);
}

public sealed class PluginPerformanceBenchmarkService(
    PluginBenchmarkStore store,
    DatasetValidationStore datasets,
    MediaLibraryService media,
    OfflineReplayService replay,
    PluginWorkerSupervisor workers,
    PluginManager plugins,
    IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);

    public bool HasActiveRuns => !_active.IsEmpty;

    public async Task<PluginBenchmarkRun> StartAsync(StartPluginBenchmarkRequest request, CancellationToken ct)
    {
        var dataset = await datasets.GetDatasetAsync(request.DatasetId, ct);
        if (dataset.Items.Count == 0) throw new ApiConflictException("Benchmark Dataset has no items.");
        if (dataset.Items.Any(x => !x.ReplayReady)) throw new ApiConflictException("Benchmark Dataset contains samples that are not replay-ready.");
        var validation = await datasets.GetRunAsync(request.ValidationRunId, false, ct);
        if (!validation.DatasetId.Equals(request.DatasetId, StringComparison.OrdinalIgnoreCase)) throw new ApiValidationException("ValidationRunId must belong to the selected Dataset.");
        if (!validation.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) throw new ApiConflictException("Benchmark requires a completed Dataset Validation run so the candidate Workflow is frozen and reproducible.");
        var workflow = await datasets.GetRunWorkflowAsync(request.ValidationRunId, ct);
        if (workflow.Nodes.Any(x => x.Type.Equals("camera.syncCapture", StringComparison.OrdinalIgnoreCase))) throw new ApiConflictException("Plugin benchmark does not execute synchronized hardware acquisition workflows.");

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
        if (!_active.TryAdd(run.RunId, cts)) throw new InvalidOperationException("Benchmark runner ID collision.");
        _ = Task.Run(() => ExecuteAsync(run.RunId, dataset, workflow, pools, count, warmup, concurrency, portableBaseline, run.RegressionPolicy, cts.Token));
        return run;
    }

    public async Task CancelAsync(string runId, CancellationToken ct)
    {
        await store.RequestCancelAsync(runId, ct);
        if (_active.TryGetValue(runId, out var cts)) cts.Cancel();
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
            if (_active.TryRemove(runId, out var cts)) cts.Dispose();
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
