using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class TraceAnalysisTests
{
    [Fact]
    public async Task V061Compare_AutoSelectsNearestPriorOkRun_AndReportsNodeDiff()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = NewTraceStore(db, env);
        var workflow = Workflow();
        var hash = WorkflowFingerprint.Compute(workflow);

        var baselineReport = Report("inspect", true, 4.0, new Dictionary<string, object?> { ["status"] = "OK", ["count"] = 2 });
        await traces.RecordAsync(Result("ok-baseline", true, 5.0, baselineReport, "OK"), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);
        await Task.Delay(60);
        var currentReport = Report("inspect", true, 8.0, new Dictionary<string, object?> { ["status"] = "NG", ["count"] = 1 });
        await traces.RecordAsync(Result("ng-current", true, 9.0, currentReport, "NG"), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);

        var observability = new RunObservabilityService(db, traces);
        var analysis = new TraceAnalysisService(db, traces, observability);
        var comparison = await analysis.CompareAsync("ng-current", ct: default);

        Assert.True(comparison.BaselineAvailable);
        Assert.Equal("ok-baseline", comparison.BaselineRunId);
        Assert.Equal("AutoPreviousOk", comparison.BaselineSelection);
        Assert.True(comparison.SameWorkflowIdentity);
        Assert.Equal("NG", comparison.CurrentDisposition);
        var node = Assert.Single(comparison.Nodes);
        Assert.Equal("OutputChanged", node.Status);
        Assert.Contains("status", node.SummaryChangedKeys, StringComparer.OrdinalIgnoreCase);
        Assert.True(node.DurationDeltaMs > 0);
    }

    [Fact]
    public async Task V061FailureSignature_ClustersSameExecutionError_WhenVolatileNumbersDiffer()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = NewTraceStore(db, env);
        var workflow = Workflow();
        var hash = WorkflowFingerprint.Compute(workflow);

        var first = Report("inspect", false, 3.0, new Dictionary<string, object?>(), "Template matcher failed at candidate 123");
        await traces.RecordAsync(Result("error-one", false, 4.0, first, error: first.Error), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);
        await Task.Delay(60);
        var second = Report("inspect", false, 3.2, new Dictionary<string, object?>(), "Template matcher failed at candidate 456");
        await traces.RecordAsync(Result("error-two", false, 4.2, second, error: second.Error), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);

        var observability = new RunObservabilityService(db, traces);
        var analysis = new TraceAnalysisService(db, traces, observability);
        var signature = await analysis.AnalyzeFailureAsync("error-two", days: 30, take: 10, ct: default);

        Assert.True(signature.HasSignature);
        Assert.Equal("ExecutionFailure", signature.Category);
        Assert.Equal("inspect", signature.PrimaryNodeId);
        Assert.True(signature.Recurring);
        Assert.Equal(2, signature.OccurrenceCount);
        Assert.Equal(2, signature.RecentOccurrences.Count);
        Assert.Contains("candidate #", signature.Fingerprint);
    }

    private static TraceabilityStore NewTraceStore(SqliteMetadataDatabase db, TempWebHostEnvironment env)
        => new(db, env, Options.Create(new TraceRetentionOptions { OkPreviewSampleEvery = 1000, OkReplaySampleEvery = 1000 }));

    private static WorkflowDefinition Workflow() => new(
        "trace-analysis-workflow",
        "Trace Analysis Workflow",
        [new NodeDefinition("inspect", "vision.inspect", "Inspect", null, null)],
        []);

    private static NodeRunReport Report(string nodeId, bool success, double duration, IReadOnlyDictionary<string, object?> summary, string? error = null)
        => new(nodeId, "vision.inspect", success, duration, summary, error, NodeExecutionPhase.Run, ExecutionSequence: 1, StartOffsetMs: 0.5, EndOffsetMs: 0.5 + duration);

    private static WorkflowRunResult Result(string runId, bool success, double duration, NodeRunReport report, string? disposition = null, string? error = null)
        => new(runId, success, duration, false, 0, 0, [report], [], null, error, QualityDisposition: disposition);
}
