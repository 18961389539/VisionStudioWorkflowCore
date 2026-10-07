using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record TraceNodeDelta(
    string NodeId,
    string NodeType,
    bool? BaselineSuccess,
    bool? CurrentSuccess,
    double? BaselineDurationMs,
    double? CurrentDurationMs,
    double? DurationDeltaMs,
    double? DurationRatio,
    bool SummaryChanged,
    IReadOnlyList<string> SummaryChangedKeys,
    bool ErrorChanged,
    string PerformanceStatus,
    string Status);

public sealed record RunTraceComparison(
    string RunId,
    string? BaselineRunId,
    string BaselineSelection,
    bool BaselineAvailable,
    bool SameWorkflowIdentity,
    bool WorkflowHashChanged,
    string CurrentDisposition,
    string? BaselineDisposition,
    double CurrentDurationMs,
    double? BaselineDurationMs,
    double? DurationDeltaMs,
    double? DurationRatio,
    int ChangedNodeCount,
    int StatusChangedNodeCount,
    int PerformanceRegressionNodeCount,
    IReadOnlyList<TraceNodeDelta> Nodes);

public sealed record FailureSignatureOccurrence(
    string RunId,
    DateTimeOffset StartedAt,
    string Disposition,
    double TotalDurationMs,
    double? NodeDurationMs = null);

public sealed record FailureSignatureAnalysis(
    string RunId,
    bool HasSignature,
    string? SignatureId,
    string Category,
    string? PrimaryNodeId,
    string? PrimaryNodeType,
    string Fingerprint,
    int OccurrenceCount,
    bool Recurring,
    int WindowDays,
    IReadOnlyList<FailureSignatureOccurrence> RecentOccurrences,
    string? RecommendedAction,
    string? ReplayNodeId);

/// <summary>
/// V0.61 software-only trace analysis. It compares a persisted run with either an
/// explicit baseline or the nearest previous OK run from the same semantic cohort,
/// and derives stable failure signatures without changing runtime execution behavior.
/// </summary>
public sealed class TraceAnalysisService(
    SqliteMetadataDatabase db,
    TraceabilityStore traces,
    RunObservabilityService observability)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly Regex GuidRegex = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HexRegex = new(@"\b0x[0-9a-f]+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled);

    public async Task<RunTraceComparison> CompareAsync(string runId, string? baselineRunId = null, CancellationToken ct = default)
    {
        var current = await traces.GetAsync(runId, ct)
            ?? throw new ApiNotFoundException($"Trace '{runId}' was not found.");

        var baselineSelection = string.IsNullOrWhiteSpace(baselineRunId) ? "AutoPreviousOk" : "Explicit";
        var resolvedBaselineId = string.IsNullOrWhiteSpace(baselineRunId)
            ? await FindPreviousOkBaselineAsync(current, ct)
            : baselineRunId;
        var baseline = string.IsNullOrWhiteSpace(resolvedBaselineId) ? null : await traces.GetAsync(resolvedBaselineId, ct);
        if (!string.IsNullOrWhiteSpace(baselineRunId) && baseline is null)
            throw new ApiNotFoundException($"Baseline trace '{baselineRunId}' was not found.");

        if (baseline is null)
        {
            return new RunTraceComparison(
                current.RunId, null, baselineSelection, false, false, false,
                current.Disposition, null, current.TotalDurationMs, null, null, null,
                0, 0, 0, []);
        }

        var observabilitySnapshot = await observability.GetAsync(current.RunId, ct: ct);
        var performance = observabilitySnapshot.Baselines.ToDictionary(
            x => NodeKey(x.NodeId, x.NodeType),
            x => x.Status,
            StringComparer.OrdinalIgnoreCase);

        var before = LastRunNodes(baseline.NodeReports);
        var after = LastRunNodes(current.NodeReports);
        var ids = after.Values.OrderBy(NodeOrder).Select(x => x.NodeId)
            .Concat(before.Values.OrderBy(NodeOrder).Select(x => x.NodeId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deltas = new List<TraceNodeDelta>(ids.Length);
        foreach (var id in ids)
        {
            before.TryGetValue(id, out var baselineNode);
            after.TryGetValue(id, out var currentNode);
            var nodeType = currentNode?.NodeType ?? baselineNode?.NodeType ?? "unknown";
            var summaryKeys = baselineNode is not null && currentNode is not null
                ? SummaryChangedKeys(baselineNode.Summary, currentNode.Summary)
                : [];
            var summaryChanged = summaryKeys.Count > 0;
            var errorChanged = baselineNode is not null && currentNode is not null
                && !string.Equals(NormalizeText(baselineNode.Error), NormalizeText(currentNode.Error), StringComparison.Ordinal);
            double? deltaMs = baselineNode is not null && currentNode is not null
                ? currentNode.DurationMs - baselineNode.DurationMs
                : null;
            double? ratio = baselineNode is not null && currentNode is not null && baselineNode.DurationMs > 0
                ? currentNode.DurationMs / baselineNode.DurationMs
                : null;
            var performanceStatus = currentNode is not null && performance.TryGetValue(NodeKey(currentNode.NodeId, currentNode.NodeType), out var p)
                ? p
                : "Insufficient";
            var status = baselineNode is null ? "Added"
                : currentNode is null ? "Missing"
                : !string.Equals(baselineNode.NodeType, currentNode.NodeType, StringComparison.OrdinalIgnoreCase) ? "TypeChanged"
                : baselineNode.Success != currentNode.Success ? "StatusChanged"
                : summaryChanged || errorChanged ? "OutputChanged"
                : performanceStatus is "Slow" or "VerySlow" ? "PerformanceRegression"
                : "Same";

            deltas.Add(new TraceNodeDelta(
                id,
                nodeType,
                baselineNode?.Success,
                currentNode?.Success,
                baselineNode?.DurationMs,
                currentNode?.DurationMs,
                deltaMs,
                ratio,
                summaryChanged,
                summaryKeys,
                errorChanged,
                performanceStatus,
                status));
        }

        var sameWorkflow = SameWorkflowIdentity(current, baseline);
        var hashChanged = !string.IsNullOrWhiteSpace(current.WorkflowHash)
            && !string.IsNullOrWhiteSpace(baseline.WorkflowHash)
            && !string.Equals(current.WorkflowHash, baseline.WorkflowHash, StringComparison.OrdinalIgnoreCase);
        var totalDelta = current.TotalDurationMs - baseline.TotalDurationMs;
        double? totalRatio = baseline.TotalDurationMs > 0 ? current.TotalDurationMs / baseline.TotalDurationMs : null;

        return new RunTraceComparison(
            current.RunId,
            baseline.RunId,
            baselineSelection,
            true,
            sameWorkflow,
            hashChanged,
            current.Disposition,
            baseline.Disposition,
            current.TotalDurationMs,
            baseline.TotalDurationMs,
            totalDelta,
            totalRatio,
            deltas.Count(x => x.Status != "Same"),
            deltas.Count(x => x.Status is "StatusChanged" or "Added" or "Missing" or "TypeChanged"),
            deltas.Count(x => x.PerformanceStatus is "Slow" or "VerySlow"),
            deltas);
    }

    public async Task<FailureSignatureAnalysis> AnalyzeFailureAsync(string runId, int days = 30, int take = 12, CancellationToken ct = default)
    {
        var current = await traces.GetAsync(runId, ct)
            ?? throw new ApiNotFoundException($"Trace '{runId}' was not found.");
        days = Math.Clamp(days, 1, 3650);
        take = Math.Clamp(take, 1, 500);

        var observation = await observability.GetAsync(runId, ct: ct);
        var descriptor = BuildSignature(current, observation);
        if (descriptor is null)
        {
            return new FailureSignatureAnalysis(
                runId, false, null, "None", null, null, string.Empty,
                0, false, days, [], null, null);
        }

        IReadOnlyList<FailureSignatureOccurrence> occurrences = descriptor.Category == "PerformanceRegression"
            ? await ReadPerformanceOccurrencesAsync(current, descriptor, days, take, ct)
            : await ReadFailureOccurrencesAsync(current, descriptor, days, take, ct);

        return new FailureSignatureAnalysis(
            runId,
            true,
            descriptor.SignatureId,
            descriptor.Category,
            descriptor.PrimaryNodeId,
            descriptor.PrimaryNodeType,
            descriptor.Fingerprint,
            descriptor.OccurrenceCount,
            descriptor.OccurrenceCount > 1,
            days,
            occurrences,
            Recommendation(descriptor.Category, descriptor.PrimaryNodeType),
            descriptor.PrimaryNodeId);
    }

    private async Task<string?> FindPreviousOkBaselineAsync(RunTraceRecord current, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        var identity = !string.IsNullOrWhiteSpace(current.WorkflowHash)
            ? "workflow_hash=$hash"
            : "workflow_id=$workflow";
        var job = !string.IsNullOrWhiteSpace(current.JobId)
            ? "AND job_id=$job AND job_version=$jobVersion"
            : "AND job_id IS NULL";
        command.CommandText = $"""
SELECT run_id
FROM run_traces
WHERE {identity}
  AND source=$source
  {job}
  AND run_id<>$run
  AND started_at < $started
  AND execution_status='Complete'
  AND UPPER(disposition)='OK'
ORDER BY started_at DESC
LIMIT 1;
""";
        if (!string.IsNullOrWhiteSpace(current.WorkflowHash)) command.Parameters.AddWithValue("$hash", current.WorkflowHash);
        else command.Parameters.AddWithValue("$workflow", current.WorkflowId);
        command.Parameters.AddWithValue("$source", current.Source);
        if (!string.IsNullOrWhiteSpace(current.JobId))
        {
            command.Parameters.AddWithValue("$job", current.JobId);
            command.Parameters.AddWithValue("$jobVersion", current.JobVersion ?? 0);
        }
        command.Parameters.AddWithValue("$run", current.RunId);
        command.Parameters.AddWithValue("$started", current.StartedAt.ToUniversalTime().ToString("O"));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private async Task<IReadOnlyList<FailureSignatureOccurrence>> ReadFailureOccurrencesAsync(
        RunTraceRecord current,
        SignatureDescriptor descriptor,
        int days,
        int take,
        CancellationToken ct)
    {
        var cutoff = current.StartedAt.AddDays(-days).ToUniversalTime().ToString("O");
        var until = current.StartedAt.ToUniversalTime().ToString("O");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        var identity = !string.IsNullOrWhiteSpace(current.WorkflowHash)
            ? "t.workflow_hash=$hash"
            : "t.workflow_id=$workflow";
        var job = !string.IsNullOrWhiteSpace(current.JobId)
            ? "AND t.job_id=$job AND t.job_version=$jobVersion"
            : "AND t.job_id IS NULL";
        var categoryFilter = descriptor.Category == "ExecutionFailure"
            ? "AND (t.execution_status<>'Complete' OR UPPER(t.disposition)='ERROR')"
            : "AND UPPER(t.disposition)='NG'";
        command.CommandText = $"""
SELECT t.run_id,t.started_at,t.disposition,t.total_duration_ms,t.execution_status,t.error,t.node_reports_json
FROM run_traces t
WHERE {identity}
  AND t.source=$source
  {job}
  AND t.started_at >= $cutoff
  AND t.started_at <= $until
  {categoryFilter}
ORDER BY t.started_at DESC;
""";
        AddCohortParameters(command, current, cutoff, until);

        var result = new List<FailureSignatureOccurrence>();
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var reports = JsonSerializer.Deserialize<List<NodeRunReport>>(reader.GetString(6), _json) ?? [];
            var candidate = BuildNonPerformanceSignature(
                reader.GetString(4),
                reader.GetString(2),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reports);
            if (candidate is null || !string.Equals(candidate.SignatureId, descriptor.SignatureId, StringComparison.Ordinal)) continue;
            count++;
            if (result.Count < take)
                result.Add(new FailureSignatureOccurrence(
                    reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetDouble(3)));
        }
        descriptor.OccurrenceCount = count;
        return result;
    }

    private async Task<IReadOnlyList<FailureSignatureOccurrence>> ReadPerformanceOccurrencesAsync(
        RunTraceRecord current,
        SignatureDescriptor descriptor,
        int days,
        int take,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(descriptor.PrimaryNodeId) || string.IsNullOrWhiteSpace(descriptor.PrimaryNodeType) || descriptor.PerformanceThresholdMs is null)
        {
            descriptor.OccurrenceCount = 1;
            return [new FailureSignatureOccurrence(current.RunId, current.StartedAt, current.Disposition, current.TotalDurationMs)];
        }

        var cutoff = current.StartedAt.AddDays(-days).ToUniversalTime().ToString("O");
        var until = current.StartedAt.ToUniversalTime().ToString("O");
        await using var connection = await db.OpenConnectionAsync(ct);
        var identity = !string.IsNullOrWhiteSpace(current.WorkflowHash)
            ? "t.workflow_hash=$hash"
            : "t.workflow_id=$workflow";
        var job = !string.IsNullOrWhiteSpace(current.JobId)
            ? "AND t.job_id=$job AND t.job_version=$jobVersion"
            : "AND t.job_id IS NULL";
        var where = $"""
{identity}
  AND t.source=$source
  {job}
  AND t.started_at >= $cutoff
  AND t.started_at <= $until
  AND o.node_id=$nodeId
  AND o.node_type=$nodeType
  AND o.phase='Run'
  AND o.success=1
  AND o.duration_ms > $threshold
""";

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM run_node_observations o JOIN run_traces t ON t.run_id=o.run_id WHERE {where};";
            AddCohortParameters(count, current, cutoff, until);
            count.Parameters.AddWithValue("$nodeId", descriptor.PrimaryNodeId);
            count.Parameters.AddWithValue("$nodeType", descriptor.PrimaryNodeType);
            count.Parameters.AddWithValue("$threshold", descriptor.PerformanceThresholdMs.Value);
            descriptor.OccurrenceCount = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT t.run_id,t.started_at,t.disposition,t.total_duration_ms,o.duration_ms
FROM run_node_observations o
JOIN run_traces t ON t.run_id=o.run_id
WHERE {where}
ORDER BY t.started_at DESC
LIMIT $take;
""";
        AddCohortParameters(command, current, cutoff, until);
        command.Parameters.AddWithValue("$nodeId", descriptor.PrimaryNodeId);
        command.Parameters.AddWithValue("$nodeType", descriptor.PrimaryNodeType);
        command.Parameters.AddWithValue("$threshold", descriptor.PerformanceThresholdMs.Value);
        command.Parameters.AddWithValue("$take", take);
        var result = new List<FailureSignatureOccurrence>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new FailureSignatureOccurrence(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetDouble(4)));
        return result;
    }

    private void AddCohortParameters(Microsoft.Data.Sqlite.SqliteCommand command, RunTraceRecord current, string cutoff, string until)
    {
        if (!string.IsNullOrWhiteSpace(current.WorkflowHash)) command.Parameters.AddWithValue("$hash", current.WorkflowHash);
        else command.Parameters.AddWithValue("$workflow", current.WorkflowId);
        command.Parameters.AddWithValue("$source", current.Source);
        if (!string.IsNullOrWhiteSpace(current.JobId))
        {
            command.Parameters.AddWithValue("$job", current.JobId);
            command.Parameters.AddWithValue("$jobVersion", current.JobVersion ?? 0);
        }
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$until", until);
    }

    private SignatureDescriptor? BuildSignature(RunTraceRecord trace, RunObservabilitySnapshot observation)
    {
        var failure = BuildNonPerformanceSignature(trace.ExecutionStatus, trace.Disposition, trace.Error, trace.NodeReports);
        if (failure is not null) return failure;

        var slow = observation.Baselines
            .Where(x => x.Status is "Slow" or "VerySlow")
            .OrderByDescending(x => x.Status == "VerySlow")
            .ThenByDescending(x => x.RatioToMedian ?? 0)
            .FirstOrDefault();
        if (slow is null) return null;
        var fingerprint = $"performance|{slow.NodeType.ToLowerInvariant()}|{slow.NodeId.ToLowerInvariant()}";
        var threshold = Math.Max(slow.P95Ms * 1.2, slow.P95Ms + 0.1);
        return new SignatureDescriptor(
            HashFingerprint(fingerprint),
            "PerformanceRegression",
            slow.NodeId,
            slow.NodeType,
            fingerprint,
            threshold);
    }

    private SignatureDescriptor? BuildNonPerformanceSignature(
        string executionStatus,
        string disposition,
        string? traceError,
        IReadOnlyList<NodeRunReport> reports)
    {
        var runReports = reports.Where(x => x.Phase == NodeExecutionPhase.Run).ToArray();
        var failed = runReports.FirstOrDefault(x => !x.Success || !string.IsNullOrWhiteSpace(x.Error));
        if (!executionStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase)
            || disposition.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || failed is not null)
        {
            var primary = failed ?? runReports.LastOrDefault();
            var normalized = NormalizeError(primary?.Error ?? traceError ?? "execution failure");
            var type = primary?.NodeType ?? "workflow";
            var fingerprint = $"execution|{type.ToLowerInvariant()}|{normalized}";
            return new SignatureDescriptor(HashFingerprint(fingerprint), "ExecutionFailure", primary?.NodeId, primary?.NodeType, fingerprint);
        }

        if (disposition.Equals("NG", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var report in runReports.Reverse())
            {
                if (!TryQualitySignal(report.Summary, out var key, out var value)) continue;
                var fingerprint = $"quality|{report.NodeType.ToLowerInvariant()}|{key}:{value}";
                return new SignatureDescriptor(HashFingerprint(fingerprint), "QualityNG", report.NodeId, report.NodeType, fingerprint);
            }
            var primary = runReports.LastOrDefault();
            var fallback = $"quality|{(primary?.NodeType ?? "workflow").ToLowerInvariant()}|ng";
            return new SignatureDescriptor(HashFingerprint(fallback), "QualityNG", primary?.NodeId, primary?.NodeType, fallback);
        }

        return null;
    }

    private static bool TryQualitySignal(IReadOnlyDictionary<string, object?> summary, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        foreach (var pair in summary.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedKey = pair.Key.Trim().ToLowerInvariant();
            if (!(normalizedKey.Contains("status") || normalizedKey.Contains("result") || normalizedKey.Contains("reason")
                || normalizedKey.Contains("quality") || normalizedKey is "ok" or "pass" or "passed" or "decision")) continue;
            var token = CategoricalToken(pair.Value);
            if (token is null || token is "ok" or "pass" or "passed" or "true" or "good" or "accept" or "accepted") continue;
            key = normalizedKey;
            value = token;
            return true;
        }
        return false;
    }

    private static string? CategoricalToken(object? value)
    {
        if (value is null) return null;
        if (value is bool b) return b ? "true" : "false";
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.True) return "true";
            if (element.ValueKind == JsonValueKind.False) return "false";
            if (element.ValueKind != JsonValueKind.String) return null;
            value = element.GetString();
        }
        if (value is not string text) return null;
        var normalized = SpaceRegex.Replace(text.Trim().ToLowerInvariant(), " ");
        return normalized.Length == 0 ? null : normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private IReadOnlyList<string> SummaryChangedKeys(IReadOnlyDictionary<string, object?> baseline, IReadOnlyDictionary<string, object?> current)
    {
        var keys = baseline.Keys.Concat(current.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        foreach (var key in keys)
        {
            baseline.TryGetValue(key, out var left);
            current.TryGetValue(key, out var right);
            if (!string.Equals(JsonSerializer.Serialize(left, _json), JsonSerializer.Serialize(right, _json), StringComparison.Ordinal)) changed.Add(key);
        }
        return changed;
    }

    private static Dictionary<string, NodeRunReport> LastRunNodes(IReadOnlyList<NodeRunReport> reports)
        => reports.Where(x => x.Phase == NodeExecutionPhase.Run)
            .GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(NodeOrder).Last(), StringComparer.OrdinalIgnoreCase);

    private static long NodeOrder(NodeRunReport report) => report.ExecutionSequence > 0 ? report.ExecutionSequence : long.MaxValue;

    private static bool SameWorkflowIdentity(RunTraceRecord left, RunTraceRecord right)
    {
        if (!string.IsNullOrWhiteSpace(left.WorkflowHash) && !string.IsNullOrWhiteSpace(right.WorkflowHash))
            return string.Equals(left.WorkflowHash, right.WorkflowHash, StringComparison.OrdinalIgnoreCase);
        return string.Equals(left.WorkflowId, right.WorkflowId, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeError(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = GuidRegex.Replace(normalized, "{guid}");
        normalized = HexRegex.Replace(normalized, "0x#");
        normalized = NumberRegex.Replace(normalized, "#");
        normalized = SpaceRegex.Replace(normalized, " ");
        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : SpaceRegex.Replace(value.Trim(), " ");

    private static string HashFingerprint(string fingerprint)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant()[..16];

    private static string NodeKey(string nodeId, string nodeType) => nodeId + "\u001f" + nodeType;

    private static string Recommendation(string category, string? nodeType) => category switch
    {
        "ExecutionFailure" => $"Replay this run and inspect the failed {nodeType ?? "workflow"} node inputs/outputs before changing parameters.",
        "QualityNG" => $"Compare this NG against the latest OK baseline, then replay the suspect {nodeType ?? "quality"} node with the persisted input.",
        "PerformanceRegression" => $"Use Run Observatory to confirm the latency shift, then replay the slow {nodeType ?? "node"} in isolation.",
        _ => "Inspect the trace before changing the workflow."
    };

    private sealed class SignatureDescriptor(
        string signatureId,
        string category,
        string? primaryNodeId,
        string? primaryNodeType,
        string fingerprint,
        double? performanceThresholdMs = null)
    {
        public string SignatureId { get; } = signatureId;
        public string Category { get; } = category;
        public string? PrimaryNodeId { get; } = primaryNodeId;
        public string? PrimaryNodeType { get; } = primaryNodeType;
        public string Fingerprint { get; } = fingerprint;
        public double? PerformanceThresholdMs { get; } = performanceThresholdMs;
        public int OccurrenceCount { get; set; }
    }
}
