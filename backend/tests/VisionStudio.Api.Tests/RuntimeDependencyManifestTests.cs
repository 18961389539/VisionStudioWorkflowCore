using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Tests;

public sealed class RuntimeDependencyManifestTests
{
    [Fact]
    public async Task Capture_IsDeterministic_ForUnchangedReferencedRuntime()
    {
        using var env = new TempWebHostEnvironment();
        var runtime = CreateRuntime(env);
        var workflow = CameraWorkflow();

        var first = await runtime.Dependencies.CaptureAsync(workflow, default);
        await Task.Delay(5);
        var second = await runtime.Dependencies.CaptureAsync(workflow, default);

        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.NotEqual(first.CapturedAt, second.CapturedAt);
        var camera = Assert.Single(first.Cameras);
        Assert.Equal("virtual-1", camera.Id);
        Assert.Empty(first.Devices);
        Assert.Empty(first.Robots);
        Assert.Empty(first.Plugins);
    }

    [Fact]
    public async Task CameraSettingsChange_IsReportedAsDependencyDrift()
    {
        using var env = new TempWebHostEnvironment();
        var runtime = CreateRuntime(env);
        var workflow = CameraWorkflow();
        var published = await runtime.Dependencies.CaptureAsync(workflow, default);

        await runtime.Cameras.ApplySettingsAsync("virtual-1", new CameraSettings(ExposureUs: 12000, GainDb: 2, TargetFps: 8), default);
        var validation = await runtime.Dependencies.ValidateAsync(published, workflow, default);

        Assert.False(validation.Compatible);
        Assert.NotEqual(validation.ExpectedManifestHash, validation.CurrentManifestHash);
        Assert.Contains(validation.Drifts, x => x.Kind == "Camera" && x.Id == "virtual-1");
    }

    [Fact]
    public async Task RepublishingSameJobVersion_CreatesNewPublicationManifestWithoutMutatingJobVersion()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        var runtime = CreateRuntime(env, db);
        var workflow = CameraWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("dep-job", "Dependency Job", null, workflow, "v1"), default);

        var firstManifest = await runtime.Dependencies.CaptureAsync(workflow, default);
        await jobs.PublishAsync("dep-job", 1, "Publish", firstManifest, default);
        await runtime.Cameras.ApplySettingsAsync("virtual-1", new CameraSettings(ExposureUs: 9000, GainDb: 1, TargetFps: 12), default);
        var secondManifest = await runtime.Dependencies.CaptureAsync(workflow, default);
        var descriptor = await jobs.PublishAsync("dep-job", 1, "RePublish", secondManifest, default);

        Assert.NotEqual(firstManifest.ManifestHash, secondManifest.ManifestHash);
        Assert.Equal(secondManifest.ManifestHash, descriptor.PublishedDependencyManifestHash);
        Assert.Equal(2, descriptor.PublicationHistory.Count);
        Assert.Equal(firstManifest.ManifestHash, descriptor.PublicationHistory[0].DependencyManifestHash);
        Assert.Equal(secondManifest.ManifestHash, descriptor.PublicationHistory[1].DependencyManifestHash);
        Assert.Single(descriptor.Versions);

        await using var connection = await db.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM runtime_dependency_manifests;";
        Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task LegacyPublishedJobWithoutManifest_MustBeRepublishedBeforeProduction()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var jobs = new JobStore(db);
        var workflow = CameraWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("legacy-published", "Legacy", null, workflow, "v1"), default);

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE jobs SET published_version=1,published_dependency_manifest_hash=NULL WHERE id='legacy-published';";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<ApiConflictException>(() => jobs.GetPublishedSnapshotAsync("legacy-published", default));
        Assert.Contains("Re-publish", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeBundle CreateRuntime(TempWebHostEnvironment env, SqliteMetadataDatabase? db = null)
    {
        var cameras = new CameraManager();
        cameras.Register(new VirtualCameraDevice());
        cameras.Register(new VirtualCameraDevice("virtual-unused", "Unused Camera"));
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.acquire"), new AcquireImageNode(cameras), "builtin");
        var plugins = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var devices = new DeviceManager();
        var profiles = new DeviceProfileStore(Path.Combine(env.ContentRootPath, "data", "devices"));
        var robots = new RobotManager();
        var calibrations = new CalibrationAssetStore(db ?? new SqliteMetadataDatabase(env), new CalibrationWorkspaceService());
        var hardware = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")), cameras, devices, robots, new VendorProvenanceProbeRegistry());
        var dependencies = new RuntimeDependencyManifestService(registry, plugins, cameras, devices, profiles, robots, calibrations, hardware);
        return new RuntimeBundle(cameras, dependencies);
    }

    private static WorkflowDefinition CameraWorkflow()
        => new(
            "dependency-camera",
            "Dependency Camera",
            [new NodeDefinition("acquire", "image.acquire", "Acquire", null, null)],
            []);

    private sealed record RuntimeBundle(CameraManager Cameras, RuntimeDependencyManifestService Dependencies);
}
