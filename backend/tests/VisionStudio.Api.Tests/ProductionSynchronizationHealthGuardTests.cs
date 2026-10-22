using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Tests;

public sealed class ProductionSynchronizationHealthGuardTests
{
    [Fact]
    public void ResolveGroupIds_FindsImmediateAndScheduledSynchronizationGroups()
    {
        var workflow = Workflow(Node("a", "group-a", false), Node("b", "group-b", true), Node("c", "group-a", true));
        var groups = ProductionSynchronizationHealthGuardService.ResolveGroupIds(workflow);
        Assert.Equal(2, groups.Count);
        Assert.Contains("group-a", groups);
        Assert.Contains("group-b", groups);
    }

    [Fact]
    public async Task Evaluate_WorkflowWithoutSynchronization_IsHealthyAndEmpty()
    {
        using var env = new TempWebHostEnvironment();
        var guard = CreateGuard(env);
        var snapshot = await guard.EvaluateAsync(new WorkflowDefinition("plain", "Plain", [], []), new ProductionRuntimeConfig(), default);
        Assert.True(snapshot.Healthy);
        Assert.Empty(snapshot.Groups);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public async Task Evaluate_MissingLiveCameras_IsRecoverableTransportFault()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var groups = new CameraSynchronizationGroupStore(db);
        await groups.UpsertAsync(new CameraSynchronizationGroupRequest(
            "group-a", "Group A", "basler-pylon", ["cam-a", "cam-b"], RequirePtpLocked: false), default);
        var sync = new CameraSynchronizationService(groups, new CameraSynchronizationRunStore(db), new CameraManager(), Array.Empty<ICameraActionCommandProvider>());
        var snapshot = await new ProductionSynchronizationHealthGuardService(sync)
            .EvaluateAsync(Workflow(Node("sync", "group-a", false)), new ProductionRuntimeConfig(), default);

        Assert.False(snapshot.Healthy);
        Assert.True(snapshot.LiveRecoverableOnly);
        Assert.Contains(snapshot.Issues, x => x.Code == "PROD-SYNC-CAMERA-NOT-READY" && x.LiveRecoverable);
    }

    [Fact]
    public void ComputeNativeTransport_PrefersVendorCountersAndCalculatesWindowDeltas()
    {
        var now = DateTimeOffset.UtcNow;
        var group = new CameraSynchronizationGroup("group-a", "Group A", "basler-pylon", ["cam-a", "cam-b"], 1, 1, -1, "255.255.255.255", false, 1000, 100, 100, "hash", now, now);
        CameraTransportTelemetry T(string id, long frames, long lost, long failed, long underrun, long resend, double mbps, DateTimeOffset at)
            => new(id, "basler-pylon", true, "vendor", at, ReceivedFrames: frames, LostFrames: lost, FailedFrames: failed, BufferUnderruns: underrun, ResendRequests: resend, ThroughputMbps: mbps);
        var history = new[]
        {
            new CameraSynchronizationRunRecord("r2", "group-a", "hash", "manual", false, now.AddSeconds(1), now.AddSeconds(1), 1, null, "device-ptp", 20, 100, true, "Completed", null, null, [], [], [T("cam-a", 200, 2, 1, 0, 8, 90, now.AddSeconds(1)), T("cam-b", 210, 1, 0, 0, 5, 85, now.AddSeconds(1))]),
            new CameraSynchronizationRunRecord("r1", "group-a", "hash", "manual", false, now, now, 1, null, "device-ptp", 20, 100, true, "Completed", null, null, [], [], [T("cam-a", 100, 1, 0, 0, 3, 80, now), T("cam-b", 110, 0, 0, 0, 2, 75, now)])
        };

        var transport = ProductionSynchronizationHealthGuardService.ComputeNativeTransport(group, history);

        Assert.Equal("vendor-native", transport.EvidenceMode);
        Assert.Equal(2, transport.NativeCameras);
        Assert.Equal(2, transport.NativeEvidenceRuns);
        Assert.Equal(200, transport.ReceivedFrames);
        Assert.Equal(2, transport.LostFrames);
        Assert.Equal(1, transport.FailedFrames);
        Assert.Equal(8, transport.ResendRequests);
        Assert.NotNull(transport.NativeFrameLossRate);
        Assert.InRange(transport.NativeFrameLossRate!.Value, 0.014, 0.016);
    }

    [Fact]
    public async Task Evaluate_DisabledGuard_DoesNotInspectSynchronizationDependencies()
    {
        using var env = new TempWebHostEnvironment();
        var guard = CreateGuard(env);
        var config = new ProductionRuntimeConfig(SynchronizationHealthGuardEnabled: false);
        var snapshot = await guard.EvaluateAsync(Workflow(Node("sync", "missing-group", true)), config, default);
        Assert.True(snapshot.Healthy);
        Assert.False(snapshot.Enabled);
    }

    private static ProductionSynchronizationHealthGuardService CreateGuard(TempWebHostEnvironment env)
    {
        var db = new SqliteMetadataDatabase(env);
        var sync = new CameraSynchronizationService(
            new CameraSynchronizationGroupStore(db),
            new CameraSynchronizationRunStore(db),
            new CameraManager(),
            Array.Empty<ICameraActionCommandProvider>());
        return new ProductionSynchronizationHealthGuardService(sync);
    }

    private static WorkflowDefinition Workflow(params NodeDefinition[] nodes) => new("sync-health", "Sync Health", nodes, []);

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
