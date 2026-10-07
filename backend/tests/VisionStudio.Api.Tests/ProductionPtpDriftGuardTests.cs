using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Tests;

public sealed class ProductionPtpDriftGuardTests
{
    [Fact]
    public void ResolveRequirements_FindsScheduledSynchronizedCapture()
    {
        var workflow = Workflow(Node("sync", "group-a", scheduled: true));
        var result = ProductionPtpDriftGuardService.ResolveRequirements(workflow);
        Assert.True(result["group-a"].Scheduled);
        Assert.Equal("sync", result["group-a"].NodeId);
    }

    [Fact]
    public void ResolveRequirements_DoesNotInventScheduledRequirementForImmediateCapture()
    {
        var workflow = Workflow(Node("sync", "group-a", scheduled: false));
        var result = ProductionPtpDriftGuardService.ResolveRequirements(workflow);
        Assert.False(result["group-a"].Scheduled);
    }

    [Fact]
    public void ResolveRequirements_MergesDuplicateGroupAndKeepsStrictestPolicy()
    {
        var workflow = Workflow(Node("a", "group-a", scheduled: false), Node("b", "group-a", scheduled: true));
        var result = ProductionPtpDriftGuardService.ResolveRequirements(workflow);
        Assert.Single(result);
        Assert.True(result["group-a"].Scheduled);
    }

    [Fact]
    public async Task Evaluate_ImmediateGroupWithoutPtpPolicy_IsNotGuarded()
    {
        using var env = new TempWebHostEnvironment();
        var guard = await CreateGuardAsync(env, requirePtp: false);
        var snapshot = await guard.EvaluateAsync(Workflow(Node("sync", "group-a", scheduled: false)), new ProductionRuntimeConfig(), default);
        Assert.True(snapshot.Healthy);
        Assert.Empty(snapshot.Groups);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public async Task Evaluate_ScheduledGroupWithoutLiveCameras_BlocksAsNotReady()
    {
        using var env = new TempWebHostEnvironment();
        var guard = await CreateGuardAsync(env, requirePtp: false);
        var snapshot = await guard.EvaluateAsync(Workflow(Node("sync", "group-a", scheduled: true)), new ProductionRuntimeConfig(), default);
        Assert.False(snapshot.Healthy);
        Assert.Contains(snapshot.Issues, x => x.Code == "PROD-PTP-NOT-READY");
    }

    private static async Task<ProductionPtpDriftGuardService> CreateGuardAsync(TempWebHostEnvironment env, bool requirePtp)
    {
        var db = new SqliteMetadataDatabase(env);
        var groups = new CameraSynchronizationGroupStore(db);
        await groups.UpsertAsync(new CameraSynchronizationGroupRequest(
            "group-a", "Group A", "basler-pylon", ["cam-a", "cam-b"], RequirePtpLocked: requirePtp), default);
        var synchronization = new CameraSynchronizationService(
            groups,
            new CameraSynchronizationRunStore(db),
            new CameraManager(),
            Array.Empty<ICameraActionCommandProvider>());
        return new ProductionPtpDriftGuardService(synchronization);
    }

    private static WorkflowDefinition Workflow(params NodeDefinition[] nodes) => new("ptp-guard-test", "PTP Guard Test", nodes, []);

    private static NodeDefinition Node(string id, string groupId, bool scheduled) => new(
        id,
        "camera.syncCapture",
        id,
        null,
        new Dictionary<string, JsonElement>
        {
            ["groupId"] = JsonSerializer.SerializeToElement(groupId),
            ["scheduled"] = JsonSerializer.SerializeToElement(scheduled)
        });
}
