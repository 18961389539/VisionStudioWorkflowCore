using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class PersistenceConcurrencyTests
{
    [Fact]
    public async Task JobStore_ConcurrentVersionCreatesAreUniqueAndV1RemainsImmutable()
    {
        using var env = new TempWebHostEnvironment();
        var store = new JobStore(env);
        await store.CreateAsync(new CreateJobRequest("job-a", "Job A", null, Workflow(1), "initial"), default);
        var v1Before = await store.GetVersionAsync("job-a", 1, default);
        Assert.NotNull(v1Before);

        const int concurrentVersions = 12;
        var tasks = Enumerable.Range(2, concurrentVersions)
            .Select(i => store.AddVersionAsync("job-a", new SaveJobVersionRequest(Workflow(i), $"v{i}"), default))
            .ToArray();
        var versions = await Task.WhenAll(tasks);

        Assert.Equal(concurrentVersions, versions.Select(x => x.Version).Distinct().Count());
        Assert.Equal(Enumerable.Range(2, concurrentVersions), versions.Select(x => x.Version).OrderBy(x => x));

        var descriptor = await store.GetAsync("job-a", default);
        Assert.NotNull(descriptor);
        Assert.Equal(concurrentVersions + 1, descriptor!.LatestVersion);
        Assert.Equal(concurrentVersions + 1, descriptor.Versions.Count);

        var v1After = await store.GetVersionAsync("job-a", 1, default);
        Assert.NotNull(v1After);
        Assert.Equal(v1Before!.WorkflowHash, v1After!.WorkflowHash);
        Assert.Equal(JsonSerializer.Serialize(v1Before.Workflow), JsonSerializer.Serialize(v1After.Workflow));
    }

    [Fact]
    public async Task CalibrationAssetStore_ConcurrentVersionCreatesAreUniqueAndPublishedPointerIsNonDestructive()
    {
        using var env = new TempWebHostEnvironment();
        var solver = new CalibrationWorkspaceService();
        var store = new CalibrationAssetStore(env, solver);
        var workspace = CalibrationWorkspace(0);
        await store.CreateAsync(new CreateCalibrationAssetRequest("calib-a", "Calib A", null, workspace, "initial"), default);
        var v1Before = await store.GetVersionAsync("calib-a", 1, default);

        const int concurrentVersions = 8;
        var tasks = Enumerable.Range(1, concurrentVersions)
            .Select(i => store.AddVersionAsync("calib-a", new SaveCalibrationVersionRequest(CalibrationWorkspace(i * 0.001), $"offset-{i}"), default))
            .ToArray();
        var versions = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(2, concurrentVersions), versions.Select(x => x.Version).OrderBy(x => x));

        var published = await store.PublishAsync("calib-a", 3, "Publish", default);
        var rolledBack = await store.PublishAsync("calib-a", 1, "Rollback", default);
        Assert.Equal(3, published.PublishedVersion);
        Assert.Equal(1, rolledBack.PublishedVersion);
        Assert.Equal(2, rolledBack.PublicationHistory.Count);

        var v1After = await store.GetVersionAsync("calib-a", 1, default);
        Assert.NotNull(v1Before);
        Assert.NotNull(v1After);
        Assert.Equal(v1Before!.SnapshotHash, v1After!.SnapshotHash);
        Assert.Equal(JsonSerializer.Serialize(v1Before.Workspace), JsonSerializer.Serialize(v1After.Workspace));
    }

    [Fact]
    public async Task TraceabilityStore_ConcurrentRecordsRemainIndividuallyReadable()
    {
        using var env = new TempWebHostEnvironment();
        var store = new TraceabilityStore(env);
        var workflow = Workflow(1);
        const int runs = 24;

        var tasks = Enumerable.Range(0, runs).Select(async i =>
        {
            var runId = $"run-{i:D3}";
            var result = new WorkflowRunResult(
                runId,
                Success: true,
                TotalDurationMs: 1.0 + i,
                PreviewAvailable: false,
                PreviewWidth: 0,
                PreviewHeight: 0,
                NodeReports: [new NodeRunReport("n1", "test", true, 0.1, new Dictionary<string, object?> { ["i"] = i })],
                Overlays: [],
                PreviewJpeg: null,
                QualityDisposition: i % 2 == 0 ? "OK" : "NG");
            return await store.RecordAsync(result, workflow, new RunTraceContext("ConcurrencyTest", "job-a", 1, "hash-a"), default);
        }).ToArray();

        var records = await Task.WhenAll(tasks);
        Assert.Equal(runs, records.Select(x => x.RunId).Distinct().Count());
        var listed = await store.ListAsync(100, "job-a", null, default);
        Assert.Equal(runs, listed.Count);

        foreach (var record in records)
        {
            var reread = await store.GetAsync(record.RunId, default);
            Assert.NotNull(reread);
            Assert.Equal(record.RunId, reread!.RunId);
            Assert.Equal(record.WorkflowHash, reread.WorkflowHash);
        }
    }

    private static WorkflowDefinition Workflow(int revision)
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            ["revision"] = JsonSerializer.SerializeToElement(revision)
        };
        return new WorkflowDefinition(
            "persistence-test",
            "Persistence Test",
            [new NodeDefinition("n1", "test.persistence", "Test", null, parameters)],
            []);
    }

    private static CalibrationWorkspaceRequest CalibrationWorkspace(double offset)
    {
        var points = new List<CalibrationWorkspacePoint>();
        var index = 1;
        foreach (var y in new[] { 100d, 240d, 380d })
        foreach (var x in new[] { 100d, 320d, 540d })
            points.Add(new CalibrationWorkspacePoint(index++, x + offset, y - offset, x * 0.1 + 5, y * 0.1 - 10));
        return new CalibrationWorkspaceRequest("ImagePixel", "Workpiece", "mm", points);
    }
}
