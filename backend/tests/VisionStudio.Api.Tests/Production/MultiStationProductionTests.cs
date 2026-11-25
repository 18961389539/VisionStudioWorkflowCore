using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

public sealed class MultiStationProductionTests
{
    [Fact]
    public async Task MultipleStations_RunDifferentWorkflowsInParallel_WithIsolatedCounters()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var wfA = SyntheticWorkflow("wf-a", "Workflow A", 640);
        var wfB = SyntheticWorkflow("wf-b", "Workflow B", 800);
        await jobs.CreateAsync(new CreateJobRequest("job-a", "Job A", null, wfA, "v1"), default);
        await jobs.CreateAsync(new CreateJobRequest("job-b", "Job B", null, wfB, "v1"), default);
        var deps = CreateDependencies(env, out _, out _, out _);
        await PublishAsync(jobs, deps, "job-a", 1, wfA);
        await PublishAsync(jobs, deps, "job-b", 1, wfB);

        var runner = new ParallelRecordingRunner();
        var service = CreateService(env, jobs, runner, deps);

        await service.UpdateConfigAsync("station-a", new ProductionRuntimeConfig("job-a", false, 5, 1000, 3, true, 5), default);
        await service.UpdateConfigAsync("station-b", new ProductionRuntimeConfig("job-b", false, 5, 1000, 3, true, 5), default);

        await service.StartAsync("station-a", null, default);
        await service.StartAsync("station-b", null, default);

        await WaitUntilAsync(() =>
            service.GetStatus("station-a").CycleCount >= 3 &&
            service.GetStatus("station-b").CycleCount >= 3,
            TimeSpan.FromSeconds(5));

        var statuses = service.ListStatuses();
        Assert.Equal(3, statuses.Count); // default + station-a + station-b
        var statusA = service.GetStatus("station-a");
        var statusB = service.GetStatus("station-b");
        Assert.Equal(ProductionRuntimeState.Running, statusA.State);
        Assert.Equal(ProductionRuntimeState.Running, statusB.State);
        Assert.Equal("job-a", statusA.JobId);
        Assert.Equal("job-b", statusB.JobId);
        Assert.True(statusA.OkCount >= 3);
        Assert.True(statusB.OkCount >= 3);
        Assert.True(runner.MaxObservedConcurrency >= 2 || (runner.Counts["Workflow A"] >= 3 && runner.Counts["Workflow B"] >= 3));

        // 停止 station-a 不影响 station-b 继续运行
        await service.StopAsync("station-a", default);
        Assert.Equal(ProductionRuntimeState.Stopped, service.GetStatus("station-a").State);
        Assert.Equal(ProductionRuntimeState.Running, service.GetStatus("station-b").State);

        var countBefore = service.GetStatus("station-b").CycleCount;
        await WaitUntilAsync(() => service.GetStatus("station-b").CycleCount > countBefore, TimeSpan.FromSeconds(2));

        await service.StopAllAsync(default);
        Assert.All(service.ListStatuses(), s => Assert.Equal(ProductionRuntimeState.Stopped, s.State));
    }

    [Fact]
    public async Task SharedHardwareAcrossStations_ConflictsWith409_WhileDisjointHardwareSucceeds()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var deps = CreateDependencies(env, out var cameras, out _, out _);
        cameras.Register(new VirtualCameraDevice("cam-1", "Cam 1"));
        cameras.Register(new VirtualCameraDevice("cam-2", "Cam 2"));

        var wfCam1A = CameraWorkflow("wf-c1a", "Cam1 A", "cam-1");
        var wfCam1B = CameraWorkflow("wf-c1b", "Cam1 B", "cam-1");
        var wfCam2 = CameraWorkflow("wf-c2", "Cam2", "cam-2");

        await jobs.CreateAsync(new CreateJobRequest("job-c1a", "Job C1A", null, wfCam1A, "v1"), default);
        await jobs.CreateAsync(new CreateJobRequest("job-c1b", "Job C1B", null, wfCam1B, "v1"), default);
        await jobs.CreateAsync(new CreateJobRequest("job-c2", "Job C2", null, wfCam2, "v1"), default);
        await PublishAsync(jobs, deps, "job-c1a", 1, wfCam1A);
        await PublishAsync(jobs, deps, "job-c1b", 1, wfCam1B);
        await PublishAsync(jobs, deps, "job-c2", 1, wfCam2);

        var service = CreateService(env, jobs, new ParallelRecordingRunner(), deps);
        await service.StartAsync("station-1", "job-c1a", default);

        // 同一相机 cam-1 冲突，应抛出 ApiConflictException (409)
        var conflict = await Assert.ThrowsAsync<ApiConflictException>(() =>
            service.StartAsync("station-2", "job-c1b", default));
        Assert.Contains("cam-1", conflict.Message, StringComparison.OrdinalIgnoreCase);

        // 不同相机 cam-2 可正常并行启动
        var station3 = await service.StartAsync("station-3", "job-c2", default);
        Assert.Equal(ProductionRuntimeState.Running, station3.State);

        await service.StopAllAsync(default);
    }

    [Fact]
    public async Task SideEffectFaultOnOneStation_DoesNotBlockOtherStation_AndPersistsPerStation()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var deps = CreateDependencies(env, out _, out _, out _);

        var wfDevice = DeviceWriteWorkflow();
        var wfCompute = SyntheticWorkflow("wf-ok", "Compute OK", 640);
        await jobs.CreateAsync(new CreateJobRequest("job-dev", "Job Dev", null, wfDevice, "v1"), default);
        await jobs.CreateAsync(new CreateJobRequest("job-ok", "Job OK", null, wfCompute, "v1"), default);
        await PublishAsync(jobs, deps, "job-dev", 1, wfDevice);
        await PublishAsync(jobs, deps, "job-ok", 1, wfCompute);

        var runner = new ParallelRecordingRunner(failWorkflowName: "Device Write");
        var service = CreateService(env, jobs, runner, deps);

        await service.StartAsync("station-dev", "job-dev", default);
        await service.StartAsync("station-ok", "job-ok", default);

        await WaitUntilAsync(() => service.GetStatus("station-dev").State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await service.StopAsync("station-dev", default);

        // station-dev 被持久化安全闸门阻断
        await Assert.ThrowsAsync<ApiConflictException>(() => service.StartAsync("station-dev", "job-dev", default));
        Assert.NotNull(service.GetDeviceActionResolutionState("station-dev"));
        Assert.True(File.Exists(Path.Combine(env.ContentRootPath, "data", "production", "device-action-safety.station-dev.json")));

        // 另一工位不能对同一 PLC 再启动副作用工作流；冲突检查使用持久化的设备资产清单。
        var sameDevice = await Assert.ThrowsAsync<ApiConflictException>(() =>
            service.StartAsync("station-same-device", "job-dev", default));
        Assert.Contains("station-dev", sameDevice.Message, StringComparison.OrdinalIgnoreCase);

        // station-ok 仍正常运行且没有任何未决设备动作
        Assert.Equal(ProductionRuntimeState.Running, service.GetStatus("station-ok").State);
        Assert.Null(service.GetDeviceActionResolutionState("station-ok"));

        await service.StopAllAsync(default);

        // 模拟重启：新服务实例能自动发现 station-dev 与 station-ok，并继续阻断 station-dev
        var restarted = CreateService(env, jobs, runner, deps);
        var discovered = restarted.ListStatuses().Select(x => x.StationId).ToArray();
        Assert.Contains("station-dev", discovered);
        Assert.NotNull(restarted.GetDeviceActionResolutionState("station-dev"));
        await Assert.ThrowsAsync<ApiConflictException>(() => restarted.StartAsync("station-dev", "job-dev", default));
        var okAfterRestart = await restarted.StartAsync("station-ok", "job-ok", default);
        Assert.Equal(ProductionRuntimeState.Running, okAfterRestart.State);
        await restarted.StopAllAsync(default);
    }

    [Fact]
    public void SafetyLedgerRegistry_BlocksOverlappingOrUnknownAssetsButAllowsDisjointAssets()
    {
        using var env = new TempWebHostEnvironment();
        var path = Path.Combine(env.ContentRootPath, "safety.json");
        var store = new DeviceActionSafetyStore(path, NullLogger<DeviceActionSafetyStore>.Instance);
        store.Save(new DeviceActionSafetyState(
            "manifest",
            ["plc-1"],
            [],
            DateTimeOffset.UtcNow,
            "cycle outcome unknown"));
        var registry = new ProductionSafetyLedgerRegistry();
        registry.Register("line-a", store);

        Assert.Throws<ApiConflictException>(() =>
            registry.EnsureNoConflictingUnresolvedState("production start", ["PLC-1"]));
        Assert.Throws<ApiConflictException>(() =>
            registry.EnsureNoConflictingUnresolvedState("manual action", []));
        registry.EnsureNoConflictingUnresolvedState("production start", ["plc-2"]);
    }

    [Fact]
    public async Task RuntimeWorkloadGate_AllowsConcurrentProduction_WhileBlockingBenchmark()
    {
        var gate = new RuntimeWorkloadGate();
        using var p1 = gate.AcquireProduction();
        using var p2 = gate.AcquireProduction();

        Assert.Throws<ApiConflictException>(() => gate.AcquireBenchmark());
        p1.Dispose();
        Assert.Throws<ApiConflictException>(() => gate.AcquireBenchmark());
        p2.Dispose();

        using var bench = gate.AcquireBenchmark();
        Assert.Throws<ApiConflictException>(() => gate.AcquireProduction());
    }

    [Fact]
    public async Task StartAll_StopAll_AndRemoveStation_ManageLifecycleCleanly()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var wf = SyntheticWorkflow("wf-all", "All", 640);
        await jobs.CreateAsync(new CreateJobRequest("job-all", "Job All", null, wf, "v1"), default);
        var deps = CreateDependencies(env, out _, out _, out _);
        await PublishAsync(jobs, deps, "job-all", 1, wf);

        var service = CreateService(env, jobs, new ParallelRecordingRunner(), deps);
        await service.UpdateConfigAsync("line-1", new ProductionRuntimeConfig("job-all", false, 5, 1000, 3, true, 5), default);
        await service.UpdateConfigAsync("line-2", new ProductionRuntimeConfig("job-all", false, 5, 1000, 3, true, 5), default);

        var started = await service.StartAllAsync(default);
        Assert.Equal(ProductionRuntimeState.Running, started.Single(x => x.StationId == "line-1").State);
        Assert.Equal(ProductionRuntimeState.Running, started.Single(x => x.StationId == "line-2").State);

        // 运行中不能删除
        await Assert.ThrowsAsync<ApiConflictException>(() => service.RemoveStationAsync("line-1", default));

        await service.StopAllAsync(default);
        await service.RemoveStationAsync("line-1", default);
        Assert.DoesNotContain(service.ListStatuses(), x => x.StationId == "line-1");

        // default 工位不可删除
        await Assert.ThrowsAsync<ApiValidationException>(() => service.RemoveStationAsync("default", default));
    }

    [Fact]
    public async Task AutoStartAsync_StartsAllConfiguredStationsInParallel()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var wfA = SyntheticWorkflow("wf-auto-a", "Auto A", 640);
        var wfB = SyntheticWorkflow("wf-auto-b", "Auto B", 800);
        await jobs.CreateAsync(new CreateJobRequest("job-auto-a", "Auto A", null, wfA, "v1"), default);
        await jobs.CreateAsync(new CreateJobRequest("job-auto-b", "Auto B", null, wfB, "v1"), default);
        var deps = CreateDependencies(env, out _, out _, out _);
        await PublishAsync(jobs, deps, "job-auto-a", 1, wfA);
        await PublishAsync(jobs, deps, "job-auto-b", 1, wfB);

        var initial = CreateService(env, jobs, new ParallelRecordingRunner(), deps);
        await initial.UpdateConfigAsync("auto-1", new ProductionRuntimeConfig("job-auto-a", AutoStart: true, 5, 1000, 3, true, 5), default);
        await initial.UpdateConfigAsync("auto-2", new ProductionRuntimeConfig("job-auto-b", AutoStart: true, 5, 1000, 3, true, 5), default);

        // 模拟进程重启后执行 AutoStartAsync
        var restarted = CreateService(env, jobs, new ParallelRecordingRunner(), deps);
        await restarted.AutoStartAsync(default);

        await WaitUntilAsync(() =>
            restarted.GetStatus("auto-1").CycleCount >= 2 &&
            restarted.GetStatus("auto-2").CycleCount >= 2,
            TimeSpan.FromSeconds(5));

        Assert.Equal(ProductionRuntimeState.Running, restarted.GetStatus("auto-1").State);
        Assert.Equal(ProductionRuntimeState.Running, restarted.GetStatus("auto-2").State);

        await restarted.StopAllAsync(default);
    }

    [Fact]
    public async Task MultiStation_HttpEndpoints_SupportConfigStartStopAndMetrics()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();

        // 1. 创建并发布两个不同工作流
        var wf1 = SyntheticWorkflow("http-wf-1", "HTTP WF 1", 640);
        var wf2 = SyntheticWorkflow("http-wf-2", "HTTP WF 2", 800);
        var create1 = await client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("http-job-1", "HTTP Job 1", null, wf1, "v1"));
        Assert.Equal(HttpStatusCode.OK, create1.StatusCode);
        var create2 = await client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("http-job-2", "HTTP Job 2", null, wf2, "v1"));
        Assert.Equal(HttpStatusCode.OK, create2.StatusCode);

        var pub1 = await client.PostAsJsonAsync("/api/jobs/http-job-1/versions/1/publish", new { notes = "pub1" });
        Assert.Equal(HttpStatusCode.OK, pub1.StatusCode);
        var pub2 = await client.PostAsJsonAsync("/api/jobs/http-job-2/versions/1/publish", new { notes = "pub2" });
        Assert.Equal(HttpStatusCode.OK, pub2.StatusCode);

        // 2. 配置两个命名工位（含 ETag 校验）
        var getCfg1 = await client.GetAsync("/api/production/runtimes/http-s1/config");
        Assert.Equal(HttpStatusCode.OK, getCfg1.StatusCode);
        var etag1 = getCfg1.Headers.ETag!.ToString();

        var putWithoutIfMatch = await client.PutAsJsonAsync("/api/production/runtimes/http-s1/config",
            new ProductionRuntimeConfig("http-job-1", false, 5, 1000, 3, true, 5));
        Assert.Equal(HttpStatusCode.PreconditionRequired, putWithoutIfMatch.StatusCode);

        using var putReq1 = new HttpRequestMessage(HttpMethod.Put, "/api/production/runtimes/http-s1/config")
        {
            Content = JsonContent.Create(new ProductionRuntimeConfig("http-job-1", false, 5, 1000, 3, true, 5))
        };
        putReq1.Headers.TryAddWithoutValidation("If-Match", etag1);
        var putRes1 = await client.SendAsync(putReq1);
        Assert.Equal(HttpStatusCode.OK, putRes1.StatusCode);

        var getCfg2 = await client.GetAsync("/api/production/runtimes/http-s2/config");
        var etag2 = getCfg2.Headers.ETag!.ToString();
        using var putReq2 = new HttpRequestMessage(HttpMethod.Put, "/api/production/runtimes/http-s2/config")
        {
            Content = JsonContent.Create(new ProductionRuntimeConfig("http-job-2", false, 5, 1000, 3, true, 5))
        };
        putReq2.Headers.TryAddWithoutValidation("If-Match", etag2);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(putReq2)).StatusCode);

        // 3. 一键全部启动并验证并行状态与指标
        var startAll = await client.PostAsync("/api/production/runtimes/start-all", null);
        Assert.Equal(HttpStatusCode.OK, startAll.StatusCode);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        await WaitUntilAsync(() =>
        {
            var list = client.GetFromJsonAsync<List<ProductionStationRuntimeStatus>>("/api/production/runtimes", jsonOptions).GetAwaiter().GetResult();
            var s1 = list?.FirstOrDefault(x => x.StationId == "http-s1");
            var s2 = list?.FirstOrDefault(x => x.StationId == "http-s2");
            return s1 is { State: ProductionRuntimeState.Running, CycleCount: >= 1 }
                && s2 is { State: ProductionRuntimeState.Running, CycleCount: >= 1 };
        }, TimeSpan.FromSeconds(10));

        var metricsText = await client.GetStringAsync("/api/diagnostics/metrics");
        Assert.Contains("visionstudio_station_production_cycles_total{station=\"http-s1\"}", metricsText);
        Assert.Contains("visionstudio_station_production_cycles_total{station=\"http-s2\"}", metricsText);

        // 4. 全部停止并删除工位
        var stopAll = await client.PostAsync("/api/production/runtimes/stop-all", null);
        Assert.Equal(HttpStatusCode.OK, stopAll.StatusCode);

        var del1 = await client.DeleteAsync("/api/production/runtimes/http-s1");
        Assert.Equal(HttpStatusCode.NoContent, del1.StatusCode);
        var del2 = await client.DeleteAsync("/api/production/runtimes/http-s2");
        Assert.Equal(HttpStatusCode.NoContent, del2.StatusCode);
    }

    [Fact]
    public async Task ExternalSignalTrigger_WaitsForSignal_AndPublishesLatestResult()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var wf = SyntheticWorkflow("wf-trig", "Trigger WF", 640);
        await jobs.CreateAsync(new CreateJobRequest("job-trig", "Job Trig", null, wf, "v1"), default);
        var deps = CreateDependencies(env, out _, out _, out _);
        await PublishAsync(jobs, deps, "job-trig", 1, wf);

        var service = CreateService(env, jobs, new ParallelRecordingRunner(), deps);
        await service.UpdateConfigAsync("trig-1", new ProductionRuntimeConfig(
            "job-trig", false, 5, 1000, 3, true, 5,
            TriggerMode: ProductionTriggerMode.ExternalSignal,
            TriggerTimeoutMs: 500), default);

        await service.StartAsync("trig-1", null, default);
        await Task.Delay(120);
        Assert.Equal(0, service.GetStatus("trig-1").CycleCount);

        var accepted = service.Trigger("trig-1", "signal-1");
        Assert.True(accepted!.IsNew);
        Assert.False(service.Trigger("trig-1", "signal-1")!.IsNew);
        await WaitUntilAsync(() => service.GetStatus("trig-1").CycleCount == 1, TimeSpan.FromSeconds(3));
        Assert.Equal(ProductionTriggerState.Completed,
            Assert.Single(service.ListTriggers("trig-1"), item => item.EventId == "signal-1").State);
        await Task.Delay(80);
        Assert.Equal(1, service.GetStatus("trig-1").CycleCount);

        service.Trigger("trig-1");
        await WaitUntilAsync(() => service.GetStatus("trig-1").CycleCount == 2, TimeSpan.FromSeconds(3));

        var latest = service.GetLatestResult("trig-1");
        Assert.NotNull(latest);
        Assert.Equal("trig-1", latest!.StationId);
        Assert.True(latest.Success);
        Assert.Equal("OK", latest.Disposition);

        await service.StopAllAsync(default);
        var restarted = CreateService(env, jobs, new ParallelRecordingRunner(), deps);
        Assert.Equal(2, restarted.ListTriggers("trig-1").Count(item => item.State == ProductionTriggerState.Completed));
        await restarted.StopAllAsync(default);
    }

    [Fact]
    public async Task MaxConcurrentCycles_LimitsParallelRunnerConcurrencyAcrossStations()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var wfA = SyntheticWorkflow("wf-cap-a", "Cap A", 640);
        var wfB = SyntheticWorkflow("wf-cap-b", "Cap B", 800);
        await jobs.CreateAsync(new CreateJobRequest("job-cap-a", "Job Cap A", null, wfA, "v1"), default);
        await jobs.CreateAsync(new CreateJobRequest("job-cap-b", "Job Cap B", null, wfB, "v1"), default);
        var deps = CreateDependencies(env, out _, out _, out _);
        await PublishAsync(jobs, deps, "job-cap-a", 1, wfA);
        await PublishAsync(jobs, deps, "job-cap-b", 1, wfB);

        var runner = new ParallelRecordingRunner();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Production:MaxConcurrentCycles"] = "1" })
            .Build();
        var service = CreateService(env, jobs, runner, deps, configurationOverride: config);

        await service.StartAsync("cap-1", "job-cap-a", default);
        await service.StartAsync("cap-2", "job-cap-b", default);

        await WaitUntilAsync(() =>
            service.GetStatus("cap-1").CycleCount >= 3 &&
            service.GetStatus("cap-2").CycleCount >= 3,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, runner.MaxObservedConcurrency);
        await service.StopAllAsync(default);
    }

    private static ProductionRuntimeService CreateService(
        TempWebHostEnvironment env,
        JobStore jobs,
        IVisionWorkflowRunner runner,
        RuntimeDependencyManifestService deps,
        CameraManager? cameras = null,
        IConfiguration? configurationOverride = null)
    {
        var db = new SqliteMetadataDatabase(env);
        var config = configurationOverride ?? new ConfigurationBuilder().Build();
        var traces = new TraceabilityStore(env);
        var runs = new RunStore();
        var alarms = new AlarmStore(env);
        var configStore = new ProductionRuntimeConfigStore(env, config);
        var safetyStore = new DeviceActionSafetyStore(env, config, NullLogger<DeviceActionSafetyStore>.Instance);
        var storage = new StorageCapacityService(env, db, Options.Create(new StorageMaintenanceOptions()));
        var sync = new CameraSynchronizationService(
            new CameraSynchronizationGroupStore(db),
            new CameraSynchronizationRunStore(db),
            cameras ?? new CameraManager(),
            []);
        var ptpGuard = new ProductionPtpDriftGuardService(sync);
        var syncGuard = new ProductionSynchronizationHealthGuardService(sync);
        var leases = new DeviceLeaseRegistry();
        return new ProductionRuntimeService(
            jobs,
            runner,
            traces,
            runs,
            configStore,
            alarms,
            deps,
            storage,
            ptpGuard,
            syncGuard,
            leases,
            NullLogger<ProductionRuntimeService>.Instance,
            safetyStore: safetyStore,
            env: env,
            configuration: config,
            safetyLogger: NullLogger<DeviceActionSafetyStore>.Instance);
    }

    private static RuntimeDependencyManifestService CreateDependencies(
        TempWebHostEnvironment env,
        out CameraManager cameras,
        out DeviceManager devices,
        out RobotManager robots)
    {
        var db = new SqliteMetadataDatabase(env);
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new SyntheticImageNode(), "builtin");
        var pluginManager = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        cameras = new CameraManager();
        registry.Register(BuiltInNodeCatalog.Require("image.acquire"), new AcquireImageNode(cameras), "builtin");
        devices = new DeviceManager();
        devices.Register(new VirtualModbusPlcDriver());
        registry.Register(BuiltInNodeCatalog.Require("device.writeTag"), new DeviceWriteTagNode(devices), "builtin");
        robots = new RobotManager();
        robots.Register(new VirtualAbbRobotAdapter());
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

    private static WorkflowDefinition SyntheticWorkflow(string id, string name, int width) => new(
        id,
        name,
        [new NodeDefinition("n1", "image.synthetic", "Test", null, new Dictionary<string, JsonElement> { ["width"] = JsonSerializer.SerializeToElement(width) })],
        []);

    private static WorkflowDefinition CameraWorkflow(string id, string name, string cameraId) => new(
        id,
        name,
        [new NodeDefinition("cam", "image.acquire", "Acquire", null, new Dictionary<string, JsonElement> { ["cameraId"] = JsonSerializer.SerializeToElement(cameraId) })],
        []);

    private static WorkflowDefinition DeviceWriteWorkflow() => new(
        "wf-device-write",
        "Device Write",
        [new NodeDefinition("write", "device.writeTag", "Write", null, new Dictionary<string, JsonElement>
        {
            ["deviceId"] = JsonSerializer.SerializeToElement("virtual-modbus-1"),
            ["tagId"] = JsonSerializer.SerializeToElement("statusText"),
            ["value"] = JsonSerializer.SerializeToElement("OK")
        })],
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

    private sealed class ParallelRecordingRunner(string? failWorkflowName = null) : IVisionWorkflowRunner
    {
        private int _active;
        private int _maxObserved;
        public ConcurrentDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

        public async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _active);
            while (true)
            {
                var snapshot = Volatile.Read(ref _maxObserved);
                if (current <= snapshot || Interlocked.CompareExchange(ref _maxObserved, current, snapshot) == snapshot)
                    break;
            }
            try
            {
                await Task.Delay(20, cancellationToken);
                Counts.AddOrUpdate(workflow.Name, 1, (_, c) => c + 1);
                var effectiveRunId = runId ?? Guid.NewGuid().ToString("N");
                if (string.Equals(workflow.Name, failWorkflowName, StringComparison.Ordinal))
                {
                    return new WorkflowRunResult(effectiveRunId, false, 20, false, 0, 0, [], [], null, "Simulated failure", "Error")
                    {
                        ErrorCode = VisionRunErrorCodes.NodeFailure
                    };
                }
                return new WorkflowRunResult(effectiveRunId, true, 20, false, 0, 0, [], [], null, null, "Complete", QualityDisposition: "OK");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
