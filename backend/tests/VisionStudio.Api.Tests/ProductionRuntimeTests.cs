using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Tests;

public sealed class ProductionRuntimeTests
{
    [Fact]
    public async Task Start_LocksPublishedSnapshotUntilStop()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var v1Workflow = Workflow("V1", 1);
        await jobs.CreateAsync(new CreateJobRequest("prod-a", "Prod A", null, v1Workflow, "initial"), default);
        var dependencies = Dependencies(env);
        await PublishAsync(jobs, dependencies, "prod-a", 1, v1Workflow);

        var runner = new RecordingRunner();
        var service = Service(env, jobs, runner, dependencies);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-a", false, 5, 1000, 3, true, 5), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.CycleCount >= 2, TimeSpan.FromSeconds(2));

        var v2 = await jobs.AddVersionAsync("prod-a", new SaveJobVersionRequest(Workflow("V2", 2), "v2"), default);
        await PublishAsync(jobs, dependencies, "prod-a", v2.Version, v2.Workflow);
        await WaitUntilAsync(() => service.Status.CycleCount >= 4, TimeSpan.FromSeconds(2));

        Assert.Equal(1, service.Status.LockedJobVersion);
        Assert.True(service.Status.ProductionLocked);
        Assert.All(runner.WorkflowNames.ToArray(), name => Assert.Equal("V1", name));

        await service.StopAsync(default);
        Assert.Equal(ProductionRuntimeState.Stopped, service.Status.State);
        Assert.False(service.Status.ProductionLocked);
    }

    [Fact]
    public async Task ConsecutiveWatchdogFailures_FaultRuntimeAndRaiseAlarm_WhenAutoRecoverDisabled()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = Workflow("Fail", 1);
        await jobs.CreateAsync(new CreateJobRequest("prod-fail", "Prod Fail", null, workflow, "initial"), default);
        var dependencies = Dependencies(env);
        await PublishAsync(jobs, dependencies, "prod-fail", 1, workflow);

        var runner = new RecordingRunner(alwaysFail: true);
        var alarms = new AlarmStore(env);
        var service = Service(env, jobs, runner, dependencies, alarms);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-fail", false, 1, 100, 2, false, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(2));

        Assert.True(service.Status.WatchdogTrips >= 2);
        Assert.True(service.Status.ErrorCount >= 2);
        var active = await alarms.ListAsync(true, default);
        Assert.Contains(active, x => x.Code == "PROD-WATCHDOG");
        Assert.Contains(active, x => x.Code == "PROD-FAULTED");

        await service.StopAsync(default);
    }

    [Fact]
    public async Task LegacyRuntimeConfig_UpgradesMissingProductionGuardFieldsToV044Defaults()
    {
        using var env = new TempWebHostEnvironment();
        var dir = Path.Combine(env.ContentRootPath, "data", "production");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "runtime.json"), """{"jobId":"legacy","autoStart":false,"cycleDelayMs":25,"maxCycleMs":15000,"maxConsecutiveFailures":3,"autoRecover":true,"recoveryDelayMs":1000,"stopTimeoutMs":5000}""");
        var config = await new ProductionRuntimeConfigStore(env).GetAsync(default);
        Assert.True(config.PtpDriftGuardEnabled);
        Assert.Equal(20, config.PtpGuardWindowRuns);
        Assert.Equal(5, config.PtpGuardMinimumEvidenceRuns);
        Assert.Equal(0.99, config.PtpGuardMinimumReadyRate, 3);
        Assert.Equal(10, config.PtpGuardCheckEveryCycles);
        Assert.True(config.PtpGuardFaultOnMasterClockChange);
        Assert.True(config.SynchronizationHealthGuardEnabled);
        Assert.Equal(20, config.SynchronizationGuardWindowRuns);
        Assert.Equal(5, config.SynchronizationGuardMinimumEvidenceRuns);
        Assert.Equal(0.05, config.SynchronizationGuardMaximumFailureRate, 3);
        Assert.Equal(2, config.SynchronizationGuardUnhealthyChecksToFault);
        Assert.Equal(3, config.SynchronizationGuardHealthyChecksToRecover);
    }

    [Fact]
    public async Task V044RuntimeConfig_UpgradesMissingNativeTransportGuardFieldsToV045Defaults()
    {
        using var env = new TempWebHostEnvironment();
        var dir = Path.Combine(env.ContentRootPath, "data", "production");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "runtime.json"), """{"jobId":"legacy-v44","synchronizationHealthGuardEnabled":true,"synchronizationGuardWindowRuns":20,"synchronizationGuardMinimumEvidenceRuns":5,"synchronizationGuardMaximumFailureRate":0.05,"synchronizationGuardMaximumFrameTimeoutRate":0.05,"synchronizationGuardMaxConsecutiveFailures":2,"synchronizationGuardMaxConsecutiveSkewViolations":3,"synchronizationGuardMaximumSequenceGapRate":0.01,"synchronizationGuardCheckEveryCycles":5,"synchronizationGuardUnhealthyChecksToFault":2,"synchronizationGuardHealthyChecksToRecover":3,"synchronizationGuardRecoveryTimeoutMs":30000}""");

        var config = await new ProductionRuntimeConfigStore(env).GetAsync(default);

        Assert.Equal(0.005, config.SynchronizationGuardMaximumNativeFrameLossRate, 4);
        Assert.Equal(0, config.SynchronizationGuardMaximumBufferUnderruns);
        Assert.Equal(0, config.SynchronizationGuardMaximumResynchronizations);
        Assert.Equal(0.01, config.SynchronizationGuardMaximumSequenceGapRate, 3);
    }

    [Fact]
    public async Task UpdateConfig_IsRejectedWhileProductionIsLocked()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = Workflow("V1", 1);
        await jobs.CreateAsync(new CreateJobRequest("prod-config", "Prod Config", null, workflow, "initial"), default);
        var dependencies = Dependencies(env);
        await PublishAsync(jobs, dependencies, "prod-config", 1, workflow);
        var service = Service(env, jobs, new RecordingRunner(), dependencies);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-config", false, 100, 1000, 3, true, 100), default);
        await service.StartAsync(null, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateConfigAsync(new ProductionRuntimeConfig("other", false, 100, 1000, 3, true, 100), default));

        await service.StopAsync(default);
    }


    [Fact]
    public async Task StopTimeout_DoesNotClaimStopped_WhileRunnerIsStillExecuting()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = Workflow("V1", 1);
        await jobs.CreateAsync(new CreateJobRequest("prod-block", "Prod Block", null, workflow, "initial"), default);
        var dependencies = Dependencies(env);
        await PublishAsync(jobs, dependencies, "prod-block", 1, workflow);

        var runner = new BlockingRunner(TimeSpan.FromMilliseconds(450));
        var alarms = new AlarmStore(env);
        var service = Service(env, jobs, runner, dependencies, alarms);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-block", false, 1, 1000, 3, true, 1, 100), default);
        await service.StartAsync(null, default);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var status = await service.StopAsync(default);
        Assert.Equal(ProductionRuntimeState.Faulted, status.State);
        Assert.True(status.ProductionLocked);
        Assert.Contains("stop timed out", status.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(await alarms.ListAsync(true, default), x => x.Code == "PROD-STOP-TIMEOUT");

        await Task.Delay(500);
        status = await service.StopAsync(default);
        Assert.Equal(ProductionRuntimeState.Stopped, status.State);
        Assert.False(status.ProductionLocked);
    }

    private static ProductionRuntimeService Service(
        TempWebHostEnvironment env,
        JobStore jobs,
        IVisionWorkflowRunner runner,
        RuntimeDependencyManifestService dependencies,
        AlarmStore? alarms = null)
    {
        var db = new SqliteMetadataDatabase(env);
        var sync = new CameraSynchronizationService(
            new CameraSynchronizationGroupStore(db),
            new CameraSynchronizationRunStore(db),
            new CameraManager(),
            Array.Empty<ICameraActionCommandProvider>());
        return new ProductionRuntimeService(
            jobs,
            runner,
            new TraceabilityStore(env),
            new RunStore(),
            new ProductionRuntimeConfigStore(env),
            alarms ?? new AlarmStore(env),
            dependencies,
            new StorageCapacityService(env, db, Options.Create(new StorageMaintenanceOptions())),
            new ProductionPtpDriftGuardService(sync),
            new ProductionSynchronizationHealthGuardService(sync),
            new DeviceLeaseRegistry(),
            NullLogger<ProductionRuntimeService>.Instance);
    }

    private static RuntimeDependencyManifestService Dependencies(TempWebHostEnvironment env)
    {
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new SyntheticImageNode(), "builtin");
        var pluginManager = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var db = new SqliteMetadataDatabase(env);
        var cameras = new CameraManager();
        var devices = new DeviceManager();
        var robots = new RobotManager();
        var hardware = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")), cameras, devices, robots, new VendorProvenanceProbeRegistry());
        return new RuntimeDependencyManifestService(
            registry,
            pluginManager,
            cameras,
            devices,
            new DeviceProfileStore(Path.Combine(env.ContentRootPath, "data", "devices")),
            robots,
            new CalibrationAssetStore(db, new CalibrationWorkspaceService()),
            hardware);
    }

    private static async Task PublishAsync(
        JobStore jobs,
        RuntimeDependencyManifestService dependencies,
        string jobId,
        int version,
        WorkflowDefinition workflow)
    {
        var manifest = await dependencies.CaptureAsync(workflow, default);
        await jobs.PublishAsync(jobId, version, "Publish", manifest, default);
    }

    private static WorkflowDefinition Workflow(string name, int revision)
        => new(
            "production-test",
            name,
            [new NodeDefinition("n1", "image.synthetic", "Test", null, new Dictionary<string, JsonElement> { ["width"] = JsonSerializer.SerializeToElement(640 + revision) })],
            []);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not reached in time.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingRunner(bool alwaysFail = false) : IVisionWorkflowRunner
    {
        public ConcurrentQueue<string> WorkflowNames { get; } = new();

        public Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            WorkflowNames.Enqueue(workflow.Name);
            var effectiveRunId = runId ?? Guid.NewGuid().ToString("N");
            if (alwaysFail)
                return Task.FromResult(new WorkflowRunResult(effectiveRunId, false, 101, false, 0, 0, [], [], null, $"Workflow Core execution timed out after {options?.TimeoutMs ?? 10000} ms.", "Error"));
            return Task.FromResult(new WorkflowRunResult(effectiveRunId, true, 1.2, false, 0, 0, [], [], null, QualityDisposition: "OK"));
        }
    }
    private sealed class BlockingRunner(TimeSpan delay) : IVisionWorkflowRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(delay); // deliberately ignores cancellation to simulate a blocked vendor/native call.
            return new WorkflowRunResult(runId ?? Guid.NewGuid().ToString("N"), true, delay.TotalMilliseconds, false, 0, 0, [], [], null, QualityDisposition: "OK");
        }
    }

}
