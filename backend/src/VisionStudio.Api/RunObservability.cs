
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record NodeTimelineObservation(
    string RunId,
    long ExecutionSequence,
    string NodeId,
    string NodeType,
    string Phase,
    bool Success,
    double StartOffsetMs,
    double EndOffsetMs,
    double DurationMs,
    string? Error);

public sealed record NodePerformanceBaseline(
    string NodeId,
    string NodeType,
    int SampleCount,
    double CurrentDurationMs,
    double P50Ms,
    double P95Ms,
    double MaxMs,
    double? RatioToMedian,
    string Status);

public sealed record RunObservabilitySnapshot(
    string RunId,
    bool TimelineAvailable,
    double TimelineSpanMs,
    double RuntimeOverheadMs,
    int PeakConcurrency,
    string? SlowestNodeId,
    int SlowNodeCount,
    int BaselineRunCount,
    IReadOnlyList<NodeTimelineObservation> Timeline,
    IReadOnlyList<NodePerformanceBaseline> Baselines);

/// <summary>
/// V0.60 read model for one persisted run. Timeline data is exact only for V0.60+
/// runs because older NodeRunReport payloads did not contain monotonic start offsets.
/// Baselines intentionally use prior completed runs with the same semantic workflow
/// identity and never mix a future run into the current comparison.
/// </summary>
public sealed class RunObservabilityService(SqliteMetadataDatabase db, TraceabilityStore traces)
{
    public async Task<RunObservabilitySnapshot> GetAsync(string runId, int days = 7, int history = 100, CancellationToken ct = default)
    {
        var trace = await traces.GetAsync(runId, ct)
            ?? throw new ApiNotFoundException($"Trace '{runId}' was not found.");

        days = Math.Clamp(days, 1, 3650);
        history = Math.Clamp(history, 5, 500);
        var timeline = await ReadTimelineAsync(runId, ct);
        if (timeline.Count == 0)
            return new RunObservabilitySnapshot(runId, false, 0, trace.TotalDurationMs, 0, null, 0, 0, [], []);

        var baseline = await ReadBaselineSamplesAsync(trace, days, history, ct);
        var baselines = BuildBaselines(timeline, baseline.Samples);
        var span = Math.Max(0, timeline.Max(x => x.EndOffsetMs) - timeline.Min(x => x.StartOffsetMs));
        var slowest = timeline
            .Where(x => x.Phase.Equals("Run", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.DurationMs)
            .FirstOrDefault()?.NodeId;
        var slowCount = baselines.Count(x => x.Status is "Slow" or "VerySlow");

        return new RunObservabilitySnapshot(
            runId,
            true,
            span,
            Math.Max(0, trace.TotalDurationMs - span),
            PeakConcurrency(timeline),
            slowest,
            slowCount,
            baseline.RunCount,
            timeline,
            baselines);
    }

    private async Task<IReadOnlyList<NodeTimelineObservation>> ReadTimelineAsync(string runId, CancellationToken ct)
    {
        var result = new List<NodeTimelineObservation>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT run_id,execution_sequence,node_id,node_type,phase,success,start_offset_ms,end_offset_ms,duration_ms,error
FROM run_node_observations
WHERE run_id=$run
ORDER BY execution_sequence;
""";
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new NodeTimelineObservation(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5) != 0,
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return result;
    }

    private async Task<(int RunCount, IReadOnlyDictionary<string, List<double>> Samples)> ReadBaselineSamplesAsync(
        RunTraceRecord trace,
        int days,
        int history,
        CancellationToken ct)
    {
        var cutoff = trace.StartedAt.AddDays(-days).ToUniversalTime().ToString("O");
        var current = trace.StartedAt.ToUniversalTime().ToString("O");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        var identityClause = !string.IsNullOrWhiteSpace(trace.WorkflowHash)
            ? "t.workflow_hash=$workflowHash"
            : "t.workflow_id=$workflowId";
        var jobClause = !string.IsNullOrWhiteSpace(trace.JobId)
            ? "AND t.job_id=$jobId AND t.job_version=$jobVersion"
            : "AND t.job_id IS NULL";
        command.CommandText = $"""
WITH comparable AS (
    SELECT t.run_id
    FROM run_traces t
    WHERE {identityClause}
      AND t.source=$source
      {jobClause}
      AND t.started_at < $current
      AND t.started_at >= $cutoff
      AND t.execution_status='Complete'
    ORDER BY t.started_at DESC
    LIMIT $history
)
SELECT o.run_id,o.node_id,o.node_type,o.duration_ms
FROM run_node_observations o
JOIN comparable c ON c.run_id=o.run_id
WHERE o.success=1 AND o.phase='Run'
ORDER BY o.run_id,o.execution_sequence;
""";
        if (!string.IsNullOrWhiteSpace(trace.WorkflowHash)) command.Parameters.AddWithValue("$workflowHash", trace.WorkflowHash);
        else command.Parameters.AddWithValue("$workflowId", trace.WorkflowId);
        command.Parameters.AddWithValue("$source", trace.Source);
        if (!string.IsNullOrWhiteSpace(trace.JobId))
        {
            command.Parameters.AddWithValue("$jobId", trace.JobId);
            command.Parameters.AddWithValue("$jobVersion", trace.JobVersion ?? 0);
        }
        command.Parameters.AddWithValue("$current", current);
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$history", history);

        var samples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var runIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runIds.Add(reader.GetString(0));
            var key = Key(reader.GetString(1), reader.GetString(2));
            if (!samples.TryGetValue(key, out var values)) samples[key] = values = [];
            values.Add(reader.GetDouble(3));
        }
        return (runIds.Count, samples);
    }

    private static IReadOnlyList<NodePerformanceBaseline> BuildBaselines(
        IReadOnlyList<NodeTimelineObservation> timeline,
        IReadOnlyDictionary<string, List<double>> samples)
    {
        var result = new List<NodePerformanceBaseline>();
        foreach (var current in timeline.Where(x => x.Phase.Equals("Run", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.ExecutionSequence))
        {
            if (!samples.TryGetValue(Key(current.NodeId, current.NodeType), out var history) || history.Count == 0)
            {
                result.Add(new NodePerformanceBaseline(current.NodeId, current.NodeType, 0, current.DurationMs, 0, 0, 0, null, "Insufficient"));
                continue;
            }

            var ordered = history.OrderBy(x => x).ToArray();
            var p50 = Percentile(ordered, 0.50);
            var p95 = Percentile(ordered, 0.95);
            var max = ordered[^1];
            double? ratio = p50 > 0 ? current.DurationMs / p50 : null;
            var status = ordered.Length < 5
                ? "Insufficient"
                : current.DurationMs > p95 * 2.0 && current.DurationMs - p95 > 0.1
                    ? "VerySlow"
                    : current.DurationMs > p95 * 1.2 && current.DurationMs - p95 > 0.1
                        ? "Slow"
                        : "Normal";
            result.Add(new NodePerformanceBaseline(current.NodeId, current.NodeType, ordered.Length, current.DurationMs, p50, p95, max, ratio, status));
        }
        return result;
    }

    private static double Percentile(IReadOnlyList<double> ordered, double p)
    {
        if (ordered.Count == 0) return 0;
        if (ordered.Count == 1) return ordered[0];
        var rank = p * (ordered.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return ordered[lower];
        var weight = rank - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * weight;
    }

    private static int PeakConcurrency(IReadOnlyList<NodeTimelineObservation> timeline)
    {
        var events = timeline
            .Where(x => x.EndOffsetMs >= x.StartOffsetMs)
            .SelectMany(x => new[] { (At: x.StartOffsetMs, Delta: 1), (At: x.EndOffsetMs, Delta: -1) })
            .OrderBy(x => x.At)
            .ThenBy(x => x.Delta)
            .ToArray();
        var active = 0;
        var peak = 0;
        foreach (var e in events)
        {
            active = Math.Max(0, active + e.Delta);
            peak = Math.Max(peak, active);
        }
        return peak;
    }

    private static string Key(string nodeId, string nodeType) => nodeId + "\u001f" + nodeType;
}
