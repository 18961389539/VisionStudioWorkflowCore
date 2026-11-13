using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class InvestigationCaseTests
{
    [Fact]
    public async Task V063TrackFromTrace_DeduplicatesSignatureAndPersistsSchemaV19()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = NewTraceStore(db, env);
        var workflow = Workflow();
        var hash = WorkflowFingerprint.Compute(workflow);

        var first = Report("inspect", false, 3.0, "Template matcher failed at candidate 123");
        await traces.RecordAsync(Result("case-error-one", first), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);
        await Task.Delay(50);
        var second = Report("inspect", false, 3.2, "Template matcher failed at candidate 456");
        await traces.RecordAsync(Result("case-error-two", second), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);

        var observability = new RunObservabilityService(db, traces);
        var analysis = new TraceAnalysisService(db, traces, observability);
        var cases = new InvestigationCaseService(db, traces, analysis);
        var tracked = await cases.TrackFromTraceAsync("case-error-two", new TrackInvestigationRequest(), default);
        var same = await cases.TrackFromTraceAsync("case-error-one", new TrackInvestigationRequest(), default);

        // 断言跟随实现常量：schema 随迁移演进（当前 20），避免每次迁移都需同步测试硬编码
        Assert.Equal(db.CurrentSchemaVersion, (await db.GetSchemaStatusAsync()).CurrentVersion);
        Assert.Equal(tracked.Case.Id, same.Case.Id);
        Assert.Equal("Open", tracked.Case.Status);
        Assert.Equal("ExecutionFailure", tracked.Case.Category);
        Assert.Equal(2, tracked.Case.OccurrenceCount);
        Assert.Equal(2, tracked.EvidenceRuns.Count);
        Assert.Empty(tracked.VerificationEvidence);
        Assert.Single(await cases.ListAsync());
    }

    [Fact]
    public async Task V063Verified_RequiresFreshPassedDatasetRegressionGate()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = NewTraceStore(db, env);
        var workflow = Workflow();
        var hash = WorkflowFingerprint.Compute(workflow);
        var failure = Report("inspect", false, 3.0, "Camera frame decode failed 1001");
        await traces.RecordAsync(Result("case-lifecycle-one", failure), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);

        var analysis = new TraceAnalysisService(db, traces, new RunObservabilityService(db, traces));
        var cases = new InvestigationCaseService(db, traces, analysis);
        var tracked = await cases.TrackFromTraceAsync("case-lifecycle-one", new TrackInvestigationRequest(), default);
        var id = tracked.Case.Id;
        tracked = await cases.UpdateAsync(id, new UpdateInvestigationCaseRequest(Status: "Investigating"), default);
        tracked = await cases.UpdateAsync(id, new UpdateInvestigationCaseRequest(Status: "Resolved", ResolutionNote: "Guard corrupt payload before matcher."), default);

        await Assert.ThrowsAsync<ApiConflictException>(() => cases.UpdateAsync(id,
            new UpdateInvestigationCaseRequest(Status: "Verified", VerificationNote: "Manual note only."), default));

        // Baseline replay reproduces the exact execution-failure signature; candidate processes the same NG sample successfully.
        await traces.RecordAsync(Result("validation-baseline-replay", Report("inspect", false, 3.1, "Camera frame decode failed 2002")), workflow,
            new RunTraceContext("Validation", WorkflowHash: hash), default);
        var candidateReport = new NodeRunReport("inspect", "vision.inspect", true, 2.1, new Dictionary<string, object?>(), null,
            NodeExecutionPhase.Run, ExecutionSequence: 1, StartOffsetMs: 0.2, EndOffsetMs: 2.3);
        var candidateResult = new WorkflowRunResult("validation-candidate-replay", true, 3.0, false, 0, 0, [candidateReport], [], null,
            Error: null, QualityDisposition: "NG");
        await traces.RecordAsync(candidateResult, workflow, new RunTraceContext("Validation", WorkflowHash: hash), default);

        var now = DateTimeOffset.UtcNow;
        var baselineSummary = DatasetValidationStore.BuildSummary([
            new ValidationResultRecord("validation-baseline", "item-1", "TRACE", "case-lifecycle-one", "NG", "ERROR", "ERROR", "validation-baseline-replay", false, 4.0, ["inspect"], "Camera frame decode failed 2002", now)
        ]);
        var candidateSummary = DatasetValidationStore.BuildSummary([
            new ValidationResultRecord("validation-candidate", "item-1", "TRACE", "case-lifecycle-one", "NG", "NG", "TRUE_NG", "validation-candidate-replay", true, 3.0, [], null, now)
        ]);
        await SeedValidationEvidenceAsync(db, hash, baselineSummary, candidateSummary, now);

        var gate = await cases.EvaluateVerificationAsync(id,
            new CreateInvestigationVerificationRequest("dataset-verification", "validation-baseline", "validation-candidate", "Regression after decoder guard."), default);
        Assert.Equal("Passed", gate.GateStatus);
        Assert.Equal(1, gate.BaselineSignatureHits);
        Assert.Equal(0, gate.CandidateSignatureHits);

        tracked = await cases.UpdateAsync(id, new UpdateInvestigationCaseRequest(Status: "Verified", VerificationNote: "A/B regression gate passed."), default);
        Assert.Equal("Verified", tracked.Case.Status);
        Assert.Equal("validation-candidate", tracked.Case.VerificationRunId);
        Assert.Single(tracked.VerificationEvidence);
    }

    [Fact]
    public async Task V063TrendDashboard_AggregatesTrackedSignatureEvidence()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = NewTraceStore(db, env);
        var workflow = Workflow();
        var hash = WorkflowFingerprint.Compute(workflow);
        var report = Report("inspect", false, 3.0, "Matcher timeout 123");
        await traces.RecordAsync(Result("trend-one", report), workflow, new RunTraceContext("Test", WorkflowHash: hash), default);

        var cases = new InvestigationCaseService(db, traces, new TraceAnalysisService(db, traces, new RunObservabilityService(db, traces)));
        await cases.TrackFromTraceAsync("trend-one", new TrackInvestigationRequest(), default);
        var dashboard = await cases.TrendsAsync(30, 8, default);

        Assert.Equal(1, dashboard.TrackedCases);
        Assert.Equal(1, dashboard.ActiveCases);
        Assert.Equal(1, dashboard.EvidenceOccurrences);
        Assert.Single(dashboard.TopSignatures);
        Assert.Single(dashboard.Timeline);
        Assert.Equal(1, dashboard.Timeline[0].ExecutionFailure);
    }

    private static async Task SeedValidationEvidenceAsync(SqliteMetadataDatabase db, string workflowHash, ValidationRunSummary baseline, ValidationRunSummary candidate, DateTimeOffset now)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using var connection = await db.OpenConnectionAsync();
        await using var tx = connection.BeginTransaction();
        await using (var dataset = connection.CreateCommand())
        {
            dataset.Transaction = tx;
            dataset.CommandText = "INSERT INTO validation_datasets(id,name,description,topology_hash,created_at,updated_at) VALUES('dataset-verification','Case regression','test',NULL,$at,$at);";
            dataset.Parameters.AddWithValue("$at", now.ToString("O"));
            await dataset.ExecuteNonQueryAsync();
        }
        foreach (var (runId, summary) in new[] { ("validation-baseline", baseline), ("validation-candidate", candidate) })
        {
            await using var run = connection.CreateCommand();
            run.Transaction = tx;
            run.CommandText = """
INSERT INTO validation_runs(run_id,dataset_id,status,source_workflow_run_id,workflow_hash,candidate_workflow_json,requested_count,completed_count,started_at,completed_at,cancel_requested,error,summary_json)
VALUES($run,'dataset-verification','Completed','case-lifecycle-one',$hash,'{}',1,1,$at,$at,0,NULL,$summary);
""";
            run.Parameters.AddWithValue("$run", runId); run.Parameters.AddWithValue("$hash", workflowHash);
            run.Parameters.AddWithValue("$at", now.ToString("O")); run.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(summary, json));
            await run.ExecuteNonQueryAsync();
        }
        await using (var baselineResult = connection.CreateCommand())
        {
            baselineResult.Transaction = tx;
            baselineResult.CommandText = """
INSERT INTO validation_results(run_id,item_id,source_kind,source_ref,expected_disposition,actual_disposition,classification,replay_run_id,success,duration_ms,failed_nodes_json,error,completed_at)
VALUES('validation-baseline','item-1','TRACE','case-lifecycle-one','NG','ERROR','ERROR','validation-baseline-replay',0,4.0,'["inspect"]','Camera frame decode failed 2002',$at);
""";
            baselineResult.Parameters.AddWithValue("$at", now.ToString("O")); await baselineResult.ExecuteNonQueryAsync();
        }
        await using (var candidateResult = connection.CreateCommand())
        {
            candidateResult.Transaction = tx;
            candidateResult.CommandText = """
INSERT INTO validation_results(run_id,item_id,source_kind,source_ref,expected_disposition,actual_disposition,classification,replay_run_id,success,duration_ms,failed_nodes_json,error,completed_at)
VALUES('validation-candidate','item-1','TRACE','case-lifecycle-one','NG','NG','TRUE_NG','validation-candidate-replay',1,3.0,'[]',NULL,$at);
""";
            candidateResult.Parameters.AddWithValue("$at", now.ToString("O")); await candidateResult.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private static TraceabilityStore NewTraceStore(SqliteMetadataDatabase db, TempWebHostEnvironment env)
        => new(db, env, Options.Create(new TraceRetentionOptions { OkPreviewSampleEvery = 1000, OkReplaySampleEvery = 1000 }));

    private static WorkflowDefinition Workflow() => new(
        "investigation-workflow",
        "Investigation Workflow",
        [new NodeDefinition("inspect", "vision.inspect", "Inspect", null, null)],
        []);

    private static NodeRunReport Report(string nodeId, bool success, double duration, string error)
        => new(nodeId, "vision.inspect", success, duration, new Dictionary<string, object?>(), error, NodeExecutionPhase.Run, ExecutionSequence: 1, StartOffsetMs: 0.5, EndOffsetMs: 0.5 + duration);

    private static WorkflowRunResult Result(string runId, NodeRunReport report)
        => new(runId, false, report.DurationMs + 1, false, 0, 0, [report], [], null, report.Error);
}
