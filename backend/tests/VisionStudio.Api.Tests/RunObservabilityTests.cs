using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class RunObservabilityTests
{
    [Fact]
    public async Task V060Trace_PersistsExactNodeTimeline_InSchemaV17()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = new TraceabilityStore(db, env, Options.Create(new TraceRetentionOptions { OkPreviewSampleEvery = 1000, OkReplaySampleEvery = 1000 }));
        var workflow = Workflow();
        var report = new NodeRunReport("n1", "test.observe", true, 4.0, new Dictionary<string, object?>(), ExecutionSequence: 1, StartOffsetMs: 1.5, EndOffsetMs: 5.5);
        await traces.RecordAsync(Result("timeline-run", 6.0, report), workflow, new RunTraceContext("Test", WorkflowHash: WorkflowFingerprint.Compute(workflow)), default);

        var service = new RunObservabilityService(db, traces);
        var snapshot = await service.GetAsync("timeline-run", ct: default);

        // schema 版本随迁移演进（当前 20）：断言 status 与实现常量一致而非硬编码快照
        Assert.Equal(db.CurrentSchemaVersion, (await db.GetSchemaStatusAsync()).CurrentVersion);
        Assert.True(snapshot.TimelineAvailable);
        var node = Assert.Single(snapshot.Timeline);
        Assert.Equal(1L, node.ExecutionSequence);
        Assert.Equal(1.5, node.StartOffsetMs, 3);
        Assert.Equal(5.5, node.EndOffsetMs, 3);
        Assert.Equal(1, snapshot.PeakConcurrency);
    }

    [Fact]
    public async Task V060Observability_UsesPriorSameWorkflowRuns_ForLatencyBaseline()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = new TraceabilityStore(db, env, Options.Create(new TraceRetentionOptions { OkPreviewSampleEvery = 1000, OkReplaySampleEvery = 1000 }));
        var workflow = Workflow();
        var hash = WorkflowFingerprint.Compute(workflow);
        var baselineDurations = new[] { 9d, 10d, 10d, 10.5d, 11d, 12d };
        for (var i = 0; i < baselineDurations.Length; i++)
        {
            var duration = baselineDurations[i];
            var report = new NodeRunReport("n1", "test.observe", true, duration, new Dictionary<string, object?>(), ExecutionSequence: 1, StartOffsetMs: 0.5, EndOffsetMs: 0.5 + duration);
            await traces.RecordAsync(Result($"baseline-{i}", duration + 1, report), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);
        }

        await Task.Delay(60);
        var currentReport = new NodeRunReport("n1", "test.observe", true, 30, new Dictionary<string, object?>(), ExecutionSequence: 1, StartOffsetMs: 1, EndOffsetMs: 31);
        await traces.RecordAsync(Result("current", 32, currentReport), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);

        var service = new RunObservabilityService(db, traces);
        var snapshot = await service.GetAsync("current", days: 7, history: 100, ct: default);
        var baseline = Assert.Single(snapshot.Baselines);

        Assert.Equal(6, baseline.SampleCount);
        Assert.Equal("VerySlow", baseline.Status);
        Assert.True(baseline.P95Ms < baseline.CurrentDurationMs);
        Assert.Equal(6, snapshot.BaselineRunCount);
        Assert.Equal(1, snapshot.SlowNodeCount);
    }

    private static WorkflowDefinition Workflow() => new(
        "observe-workflow",
        "Observe Workflow",
        [new NodeDefinition("n1", "test.observe", "Observe", null, null)],
        []);

    private static WorkflowRunResult Result(string runId, double totalDurationMs, NodeRunReport report)
        => new(runId, true, totalDurationMs, false, 0, 0, [report], [], null, QualityDisposition: "OK");
}
