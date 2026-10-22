using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
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
        AlarmStore? alarms = null,
        DeviceActionSafetyStore? safetyStore = null)
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
            NullLogger<ProductionRuntimeService>.Instance,
            traceOptions: null,
            safetyStore: safetyStore);
    }

    private static DeviceActionSafetyStore SafetyStore(TempWebHostEnvironment env)
        => new(env, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<DeviceActionSafetyStore>.Instance);

    [Fact]
    public async Task PersistentFailure_WithAutoRecover_ExhaustsRecoveryBudget_AndLocksFaulted()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = Workflow("V1", 1);
        await jobs.CreateAsync(new CreateJobRequest("prod-budget", "Budget", null, workflow, "initial"), default);
        var dependencies = Dependencies(env);
        await PublishAsync(jobs, dependencies, "prod-budget", 1, workflow);

        var runner = new RecordingRunner(alwaysFail: true);
        var service = Service(env, jobs, runner, dependencies);
        // 自动恢复开启 + 纯计算流程 + 预算 2：耗尽后必须锁定 Faulted，而不是无限"恢复-再失败"。
        await service.UpdateConfigAsync(new ProductionRuntimeConfig(
            "prod-budget", false, 1, 100, 2, true, 1, MaxRecoveryAttempts: 2), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));

        Assert.Contains("recovery budget", service.Status.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var cyclesAtFault = service.Status.CycleCount;
        await Task.Delay(200);
        Assert.Equal(cyclesAtFault, service.Status.CycleCount); // 锁定后不再执行新周期
        await service.StopAsync(default);
    }

    [Fact]
    public async Task SideEffectWorkflow_FirstFailureLocksFaulted_AndDeviceActionRunsExactlyOnce()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-sidefx", "Side Fx", null, workflow, "initial"), default);
        var counting = new CountingPlcDriver();
        var dependencies = Dependencies(env, counting, out var devices);
        await PublishAsync(jobs, dependencies, "prod-sidefx", 1, workflow);

        // 默认阈值 3 + 自动恢复开启：旧实现会完整重跑至第 3 次失败才锁定，导致设备写入被执行 3 次。
        // 用"先真实写 PLC、再报告失败"的运行器模拟"写入成功→后续节点失败"的真实副作用场景。
        var runner = new DeviceWriteThenFailRunner(devices);
        var service = Service(env, jobs, runner, dependencies, safetyStore: SafetyStore(env));
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-sidefx", false, 1, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));

        // 含设备副作用：首轮失败即锁定，不得自动重放。
        Assert.True(service.Status.HasDeviceSideEffects);
        Assert.Contains("device side effects", service.Status.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, service.Status.CycleCount);

        // 关键断言：真实设备动作（写 PLC 目标标签）在失败周期内只发生一次；
        // 锁定后即使再等待也绝不再增长——设备物理动作未被自动重放。
        var writesAtFault = counting.WriteCount;
        Assert.Equal(1, writesAtFault);
        await Task.Delay(250);
        Assert.Equal(1, service.Status.CycleCount);
        Assert.Equal(writesAtFault, counting.WriteCount);

        await service.StopAsync(default);
    }

    [Fact]
    public async Task SideEffectWorkflow_DoesNotReplayEvenWhenThresholdIsThree()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-sidefx-default", "Side Fx Default", null, workflow, "initial"), default);
        var counting = new CountingPlcDriver();
        var dependencies = Dependencies(env, counting, out var devices);
        await PublishAsync(jobs, dependencies, "prod-sidefx-default", 1, workflow);

        // MaxConsecutiveFailures 缺省 = 3：阈值无关，副作用流程仍必须首轮锁定。
        var runner = new DeviceWriteThenFailRunner(devices);
        var service = Service(env, jobs, runner, dependencies, safetyStore: SafetyStore(env));
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-sidefx-default"), default);
        var config = await service.GetConfigAsync(default);
        Assert.Equal(3, config.MaxConsecutiveFailures);

        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));

        Assert.Equal(1, service.Status.CycleCount);
        Assert.Equal(1, counting.WriteCount);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task VerifyLockedDevices_FlagsUnresolvableDevice_AndAcceptsConnectedDevice()
    {
        using var env = new TempWebHostEnvironment();
        var driver = new VirtualModbusPlcDriver();
        await driver.ConnectAsync(default);
        var dependencies = Dependencies(env, driver, out var devices);
        var baseManifest = await dependencies.CaptureAsync(Workflow("V1", 1), default);

        // 设备不可解析（现场设备丢失/未注册）→ 核对失败，恢复必须被拒绝
        var missing = baseManifest with
        {
            Devices = [new DeviceRuntimeDependency("missing-plc", "modbus", "tcp", "127.0.0.1:502", "hash", null, null)]
        };
        var missingResult = dependencies.VerifyLockedDevices(missing);
        Assert.False(missingResult.Ok);
        Assert.Contains(missingResult.Issues, x => x.Contains("missing-plc", StringComparison.OrdinalIgnoreCase));

        // 已注册且 Connected 的设备 → 核对通过
        var resolvable = baseManifest with
        {
            Devices = [new DeviceRuntimeDependency("virtual-modbus-1", "modbus", "tcp", "127.0.0.1:502", "hash", null, null)]
        };
        Assert.True(dependencies.VerifyLockedDevices(resolvable).Ok);
        _ = devices;
    }

    [Fact]
    public async Task VerifyLockedDevices_RejectsDisconnectedDevice()
    {
        using var env = new TempWebHostEnvironment();
        var driver = new VirtualModbusPlcDriver();
        await driver.ConnectAsync(default);
        await driver.DisconnectAsync(default); // 已注册但 Disconnected：上一周期写入无法确认
        var dependencies = Dependencies(env, driver, out _);
        var manifest = (await dependencies.CaptureAsync(DeviceWriteWorkflow(), default)) with
        {
            Devices = [new DeviceRuntimeDependency("virtual-modbus-1", "modbus", "tcp", "127.0.0.1:502", "hash", null, null)]
        };

        var verification = dependencies.VerifyLockedDevices(manifest);

        Assert.False(verification.Ok);
        Assert.Contains(verification.Issues, x => x.Contains("not Connected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StopThenStart_CannotBypassUnknownDeviceActionGate()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-bypass", "Bypass", null, workflow, "initial"), default);
        var counting = new CountingPlcDriver();
        var dependencies = Dependencies(env, counting, out var devices);
        await PublishAsync(jobs, dependencies, "prod-bypass", 1, workflow);

        var runner = new DeviceWriteThenFailRunner(devices);
        var service = Service(env, jobs, runner, dependencies, safetyStore: SafetyStore(env));
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-bypass", false, 1, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));

        // Stop → Start 不能绕过核对：设备掉线后，未知动作标记仍在，启动必须被拒绝。
        await service.StopAsync(default);
        Assert.Equal(ProductionRuntimeState.Stopped, service.Status.State);
        await devices.DisconnectAsync("virtual-modbus-1", default);

        var rejected = await Assert.ThrowsAsync<ApiConflictException>(() => service.StartAsync(null, default));
        Assert.Contains("device", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counting.WriteCount); // 未产生任何新的设备动作
    }

    [Fact]
    public async Task SwitchToDeviceFreeWorkflow_CannotBypassUnknownDeviceActionGate()
    {
        // F02 回归：副作用失败后设备掉线，重新发布一个**无设备**的流程并启动——核对必须针对
        // 故障时锁定的清单（含设备）。旧实现对新流程的清单核对，空清单必然通过，"切换流程"
        // 即可绕过原故障设备的核对。
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-switch-a", "Switch A", null, workflow, "initial"), default);
        var counting = new CountingPlcDriver();
        var dependencies = Dependencies(env, counting, out var devices);
        await PublishAsync(jobs, dependencies, "prod-switch-a", 1, workflow);

        var runner = new DeviceWriteThenFailRunner(devices);
        var service = Service(env, jobs, runner, dependencies, safetyStore: SafetyStore(env));
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-switch-a", false, 1, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await service.StopAsync(default);

        await devices.DisconnectAsync("virtual-modbus-1", default);
        var deviceFree = Workflow("Device Free", 1);
        await jobs.CreateAsync(new CreateJobRequest("prod-switch-b", "Switch B", null, deviceFree, "initial"), default);
        await PublishAsync(jobs, dependencies, "prod-switch-b", 1, deviceFree);

        var rejected = await Assert.ThrowsAsync<ApiConflictException>(() => service.StartAsync("prod-switch-b", default));
        Assert.Contains("device", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counting.WriteCount); // 未产生任何新的设备动作
    }

    [Fact]
    public async Task DeviceActionSafetyState_SurvivesRestart_AndStillRequiresVerification()
    {
        // F01 回归：副作用失败后，即使"重启"（新 service 实例从同一数据根加载），未知动作标记
        // 与锁定清单仍然有效——设备未核对前启动必须被拒绝（旧实现标记仅存内存，重启即丢失）。
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-restart", "Restart", null, workflow, "initial"), default);
        var counting = new CountingPlcDriver();
        var dependencies = Dependencies(env, counting, out var devices);
        await PublishAsync(jobs, dependencies, "prod-restart", 1, workflow);

        var runner = new DeviceWriteThenFailRunner(devices);
        var first = Service(env, jobs, runner, dependencies, safetyStore: SafetyStore(env));
        await first.UpdateConfigAsync(new ProductionRuntimeConfig("prod-restart", false, 1, 100, 3, true, 1), default);
        await first.StartAsync(null, default);
        await WaitUntilAsync(() => first.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await first.StopAsync(default);

        var statePath = Path.Combine(env.ContentRootPath, "data", "production", "device-action-safety.json");
        Assert.True(File.Exists(statePath), "safety state must be persisted to disk");

        // "重启"：即使设备仍连接，连接状态也不能证明上一动作已经完成；普通 Start 必须要求管理员核对。
        var restarted = Service(env, jobs, new DeviceWriteThenFailRunner(devices), dependencies, safetyStore: SafetyStore(env));
        var rejected = await Assert.ThrowsAsync<ApiConflictException>(() => restarted.StartAsync(null, default));
        Assert.Contains("device", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmedSynchronousDeviceWrites_KeepRunningAndClearOnlyAfterTraceCommit()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-sync-ok", "Sync OK", null, workflow, "initial"), default);
        var counting = new CountingPlcDriver();
        var dependencies = Dependencies(env, counting, out var devices);
        await PublishAsync(jobs, dependencies, "prod-sync-ok", 1, workflow);
        var safety = SafetyStore(env);
        var service = Service(env, jobs, new DeviceWriteSuccessRunner(devices), dependencies, safetyStore: safety);

        // R06：语义一（成功且已持久提交的周期清意图）。用 3 秒周期间隔制造确定性窗口——
        // 旧版本"等待 null 后立即 Stop"，Stop 可能取消刚好开始的下一周期（安全策略保留意图），
        // 让断言随调度抖动；这里让断言全部落在"周期已收尾、下一周期未开始"的间隙内。
        // 语义二（周期间受控停止）与语义三（执行中取消保留意图，见
        // CancelledSideEffectCycle_AlsoMarksDeviceActionsUnknown）分别独立验证。
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-sync-ok", false, 3000, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.CycleCount >= 1, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => safety.Load() is null, TimeSpan.FromSeconds(5));
        Assert.Null(safety.Load()); // 每个成功周期在 trace 提交后才清除意图，可重复继续。

        // 语义二：在间隙内停止——不取消在途周期，停止后也不应留下新意图。
        await service.StopAsync(default);
        Assert.Equal(ProductionRuntimeState.Stopped, service.Status.State);
        Assert.Null(safety.Load());
        Assert.True(counting.WriteCount >= 1);
    }

    [Fact]
    public async Task SafetyStateClearFailure_KeepsInMemoryAndRestartGateEngaged()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-clear-fail", "Clear Fail", null, workflow, "initial"), default);
        var dependencies = Dependencies(env, new VirtualModbusPlcDriver(), out var devices);
        await PublishAsync(jobs, dependencies, "prod-clear-fail", 1, workflow);
        var statePath = Path.Combine(env.ContentRootPath, "data", "production", "device-action-safety.json");
        var safety = new ClearFailsSafetyStore(statePath);
        var service = Service(env, jobs, new DeviceWriteSuccessRunner(devices), dependencies, safetyStore: safety);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-clear-fail", false, 1, 100, 3, true, 1), default);

        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await service.StopAsync(default);

        Assert.NotNull(safety.Load()); // Clear threw before memory was reset; the durable intent survives restart.
        var restarted = Service(env, jobs, new DeviceWriteSuccessRunner(devices), dependencies, safetyStore: SafetyStore(env));
        await Assert.ThrowsAsync<ApiConflictException>(() => restarted.StartAsync(null, default));
    }

    [Fact]
    public async Task ManualResolutionRequiresExactAssetsEvidenceAndAuthenticatedActor_AndRetainsHistory()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-manual-resolve", "Manual Resolve", null, workflow, "initial"), default);
        var dependencies = Dependencies(env, new VirtualModbusPlcDriver(), out var devices);
        await PublishAsync(jobs, dependencies, "prod-manual-resolve", 1, workflow);
        var safety = SafetyStore(env);
        var runner = new DeviceWriteThenFailRunner(devices);
        var service = Service(env, jobs, runner, dependencies, safetyStore: safety);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-manual-resolve", false, 1, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await service.StopAsync(default);

        var state = safety.Load()!;
        var wrong = new DeviceActionResolutionRequest(state.RunId!, state.ManifestHash, ["unrelated"], [], "checked", "physical read", "PLC S/N ABC");
        await Assert.ThrowsAsync<ApiConflictException>(() => service.ResolveUnknownDeviceActionsAsync(wrong, "alice", default));
        var missingEvidence = new DeviceActionResolutionRequest(state.RunId!, state.ManifestHash, state.DeviceIds, state.RobotIds, "checked", "", "PLC S/N ABC");
        await Assert.ThrowsAsync<ApiValidationException>(() => service.ResolveUnknownDeviceActionsAsync(missingEvidence, "alice", default));

        var request = new DeviceActionResolutionRequest(state.RunId!, state.ManifestHash, state.DeviceIds, state.RobotIds,
            "Controller value verified and motion complete", "Readback statusText=Vision OK; controller event 8172", "virtual-modbus-1 at 127.0.0.1:502; PLC serial ABC");
        await service.ResolveUnknownDeviceActionsAsync(request, "alice", default);
        Assert.Null(safety.Load());
        var files = Directory.GetFiles(safety.ResolutionDirectory, "*.json");
        Assert.Single(files);
        var saved = await File.ReadAllTextAsync(files[0]);
        Assert.Contains("alice", saved);
        Assert.Contains("controller event 8172", saved);

        // A later run and reconciliation must append a second durable record instead of replacing the first.
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => runner.Cycles >= 2 && service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await service.StopAsync(default);
        var secondState = safety.Load()!;
        await service.ResolveUnknownDeviceActionsAsync(new DeviceActionResolutionRequest(
            secondState.RunId!, secondState.ManifestHash, secondState.DeviceIds, secondState.RobotIds,
            "Second physical check", "Controller event 8180", "same PLC serial ABC"), "alice", default);
        Assert.Equal(2, Directory.GetFiles(safety.ResolutionDirectory, "*.json").Length);
    }

    [Fact]
    public async Task RobotSendTargetOnly_SuccessDoesNotConfirmMotion_AndRemainsGated()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = RobotTargetWorkflow("SendTarget", waitForInPosition: true);
        await jobs.CreateAsync(new CreateJobRequest("prod-async-target", "Async Target", null, workflow, "initial"), default);
        var dependencies = Dependencies(env, new VirtualModbusPlcDriver(), out _, out var robots);
        await PublishAsync(jobs, dependencies, "prod-async-target", 1, workflow);
        var safety = SafetyStore(env);
        var service = Service(env, jobs, new RobotSendTargetRunner(robots), dependencies, safetyStore: safety);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-async-target", false, 1, 100, 3, true, 1), default);

        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await service.StopAsync(default);

        Assert.False(string.IsNullOrWhiteSpace(safety.Load()?.RunId));
        await Assert.ThrowsAsync<ApiConflictException>(() => service.StartAsync(null, default));
    }

    [Fact]
    public async Task SkippedAsyncRobotNode_DoesNotGateSuccessfulProductionCycles()
    {
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = RobotTargetWorkflow("SendTarget", waitForInPosition: true) with
        {
            Nodes = [.. RobotTargetWorkflow("SendTarget", waitForInPosition: true).Nodes,
                new NodeDefinition("branch", "flow.if", "Branch", null, new Dictionary<string, JsonElement>())]
        };
        await jobs.CreateAsync(new CreateJobRequest("prod-skipped-async", "Skipped Async", null, workflow, "initial"), default);
        var dependencies = Dependencies(env, new VirtualModbusPlcDriver(), out _, out _);
        await PublishAsync(jobs, dependencies, "prod-skipped-async", 1, workflow);
        var runner = new SkippedRobotRunner();
        var safety = SafetyStore(env);
        var service = Service(env, jobs, runner, dependencies, safetyStore: safety);
        // R06：3 秒周期间隔——断言落在"周期已收尾、下一周期未开始"的确定性窗口内。
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-skipped-async", false, 3000, 100, 3, true, 1), default);

        await service.StartAsync(null, default);
        await WaitUntilAsync(() => runner.Cycles >= 1, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => safety.Load() is null, TimeSpan.FromSeconds(5));
        Assert.Equal(ProductionRuntimeState.Running, service.Status.State);
        Assert.Null(safety.Load());
        await service.StopAsync(default);
    }

    [Fact]
    public async Task CancelledSideEffectCycle_AlsoMarksDeviceActionsUnknown()
    {
        // F01 回归：取消发生在副作用流程执行中途时，必须与失败锁定同语义（置标记 + 持久化）——
        // 旧实现从取消路径直接 break，跳过标记设置，Stop/重启后可以不经核对直接重跑。
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-cancel", "Cancel", null, workflow, "initial"), default);
        var dependencies = Dependencies(env, new VirtualModbusPlcDriver(), out var devices);
        await PublishAsync(jobs, dependencies, "prod-cancel", 1, workflow);

        var runner = new CancelAwareBlockingRunner();
        var service = Service(env, jobs, runner, dependencies, safetyStore: SafetyStore(env));
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-cancel", false, 1, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync(default); // 取消运行中的副作用周期
        Assert.Equal(ProductionRuntimeState.Stopped, service.Status.State);

        Assert.True(File.Exists(Path.Combine(env.ContentRootPath, "data", "production", "device-action-safety.json")),
            "a cancelled side-effect cycle must persist the unknown-action marker");

        // 设备掉线后启动必须被拒（旧实现取消路径不置标记，此处会直接放行）。
        await devices.DisconnectAsync("virtual-modbus-1", default);
        var rejected = await Assert.ThrowsAsync<ApiConflictException>(() => service.StartAsync(null, default));
        Assert.Contains("device", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SideEffectCycle_RecordsDurableIntentBeforeAnyDeviceAction()
    {
        // Q01 回归：副作用流程在**执行任何设备动作之前**必须已耐久登记意图（Phase=IntentRecorded +
        // RunId）。旧实现只在执行结果返回后才写安全状态——进程被杀/追溯队列故障会让"设备已执行而
        // 软件无结果"变成可以不经核对直接重跑的新周期。
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-intent", "Intent", null, workflow, "initial"), default);
        var dependencies = Dependencies(env, new VirtualModbusPlcDriver(), out _);
        await PublishAsync(jobs, dependencies, "prod-intent", 1, workflow);

        var statePath = Path.Combine(env.ContentRootPath, "data", "production", "device-action-safety.json");
        var runner = new IntentObservingRunner(statePath);
        var service = Service(env, jobs, runner, dependencies, safetyStore: SafetyStore(env));
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-intent", false, 1, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => runner.Observed is not null, TimeSpan.FromSeconds(5));
        await service.StopAsync(default);

        Assert.NotNull(runner.Observed);
        // 安全状态文件按 Web 默认（camelCase）序列化——必须用同一约定反序列化，否则枚举回落默认值。
        var state = JsonSerializer.Deserialize<DeviceActionSafetyState>(runner.Observed!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(DeviceActionSafetyPhase.IntentRecorded, state.Phase);
        Assert.False(string.IsNullOrWhiteSpace(state.RunId), "the intent must carry the run id of the executing cycle");
        Assert.Contains("virtual-modbus-1", state.DeviceIds);
    }

    [Fact]
    public async Task SideEffectCycle_RefusesToRun_WhenIntentCannotBePersisted()
    {
        // Q01 回归：安全状态无法持久化时必须**拒绝执行设备副作用**（宁可不跑，不可无记录地跑）。
        // 注入：安全状态读取为空，但任何 Save 都失败；副作用 runner 不能被调用。
        using var env = new TempWebHostEnvironment();
        var jobs = new JobStore(env);
        var workflow = DeviceWriteWorkflow();
        await jobs.CreateAsync(new CreateJobRequest("prod-intent-fail", "IntentFail", null, workflow, "initial"), default);
        var counting = new CountingPlcDriver();
        var dependencies = Dependencies(env, counting, out var devices);
        await PublishAsync(jobs, dependencies, "prod-intent-fail", 1, workflow);

        var runner = new DeviceWriteThenFailRunner(devices);
        var failingStore = new FailSaveSafetyStore(Path.Combine(env.ContentRootPath, "data", "production", "device-action-safety.json"));
        var service = Service(env, jobs, runner, dependencies, safetyStore: failingStore);
        await service.UpdateConfigAsync(new ProductionRuntimeConfig("prod-intent-fail", false, 1, 100, 3, true, 1), default);
        await service.StartAsync(null, default);
        await WaitUntilAsync(() => service.Status.State == ProductionRuntimeState.Faulted, TimeSpan.FromSeconds(5));
        await service.StopAsync(default);

        Assert.Equal(0, runner.Cycles);           // 周期体从未执行
        Assert.Equal(0, counting.WriteCount);    // 设备动作绝无发生
    }

    [Fact]
    public async Task DeviceVerification_RejectsStaleFeedback_AndDriftedDeviceIdentity()
    {
        // Q02 回归：Connected ≠ 动作结果证据——过期反馈与设备身份漂移都必须继续阻断恢复闸门
        // （旧的"只看 ConnectionState=Connected"会在设备反馈半死或换成另一台设备时放行）。
        using var env = new TempWebHostEnvironment();
        var dependencies = Dependencies(env, new VirtualModbusPlcDriver(), out var devices);
        await devices.ReadAllFreshAsync("virtual-modbus-1", true, default);

        var fresh = dependencies.VerifyLockedDevices(["virtual-modbus-1"], [], null, TimeSpan.FromMinutes(5));
        Assert.True(fresh.Ok, fresh.Summary);

        // 反馈过期（阈值 0：任何既有采样都不再算"新鲜"）→ 拒绝。
        var stale = dependencies.VerifyLockedDevices(["virtual-modbus-1"], [], null, TimeSpan.Zero);
        Assert.False(stale.Ok);
        Assert.Contains("fresh", stale.Summary, StringComparison.OrdinalIgnoreCase);

        // 设备身份漂移（登记时的 driver|protocol|endpoint 与现状不符）→ 拒绝。
        var drifted = dependencies.VerifyLockedDevices(["virtual-modbus-1"], [],
            new Dictionary<string, string> { ["virtual-modbus-1"] = "other-driver|other-protocol|other-endpoint" },
            TimeSpan.FromMinutes(5));
        Assert.False(drifted.Ok);
        Assert.Contains("identity drifted", drifted.Summary, StringComparison.OrdinalIgnoreCase);

        // 指纹一致时通过（把实际指纹作为期望值）。
        var actual = dependencies.TryGetDeviceFingerprint("virtual-modbus-1");
        Assert.False(string.IsNullOrWhiteSpace(actual));
        var matched = dependencies.VerifyLockedDevices(["virtual-modbus-1"], [],
            new Dictionary<string, string> { ["virtual-modbus-1"] = actual! }, TimeSpan.FromMinutes(5));
        Assert.True(matched.Ok, matched.Summary);
    }

    private static WorkflowDefinition DeviceWorkflow()
        => new(
            "production-device-test",
            "Device Side Effect Test",
            [new NodeDefinition("d1", "device.readTag", "Read Tag", null, new Dictionary<string, JsonElement>
            {
                ["deviceId"] = JsonSerializer.SerializeToElement("virtual-modbus-1"),
                ["tagId"] = JsonSerializer.SerializeToElement("counter")
            })],
            []);

    /// <summary>含真实写入副作用的流程：device.writeTag 会把值写入 PLC 标签（非幂等的物理动作）。</summary>
    private static WorkflowDefinition DeviceWriteWorkflow()
        => new(
            "production-device-write-test",
            "Device Write Side Effect Test",
            [new NodeDefinition("w1", "device.writeTag", "Write Tag", null, new Dictionary<string, JsonElement>
            {
                ["deviceId"] = JsonSerializer.SerializeToElement("virtual-modbus-1"),
                ["tagId"] = JsonSerializer.SerializeToElement("statusText"),
                ["valueType"] = JsonSerializer.SerializeToElement("String"),
                ["fallbackValue"] = JsonSerializer.SerializeToElement("Vision OK")
            })],
            []);

    private static WorkflowDefinition RobotTargetWorkflow(string action, bool waitForInPosition)
        => new(
            "production-robot-target-test",
            "Robot Target Test",
            [new NodeDefinition("robot", "robot.executeTarget", "Send Target", null, new Dictionary<string, JsonElement>
            {
                ["robotId"] = JsonSerializer.SerializeToElement("virtual-abb-1"),
                ["action"] = JsonSerializer.SerializeToElement(action),
                ["autoConnect"] = JsonSerializer.SerializeToElement(true),
                ["waitForInPosition"] = JsonSerializer.SerializeToElement(waitForInPosition),
                ["timeoutMs"] = JsonSerializer.SerializeToElement(1000),
                ["maxRetries"] = JsonSerializer.SerializeToElement(0),
                ["autoAck"] = JsonSerializer.SerializeToElement(true)
            })],
            []);

    private static RuntimeDependencyManifestService Dependencies(TempWebHostEnvironment env)
        => Dependencies(env, new VirtualModbusPlcDriver(), out _);

    private static RuntimeDependencyManifestService Dependencies(TempWebHostEnvironment env, IDeviceDriver driver)
        => Dependencies(env, driver, out _);

    private static RuntimeDependencyManifestService Dependencies(TempWebHostEnvironment env, IDeviceDriver driver, out DeviceManager deviceManager)
        => Dependencies(env, driver, out deviceManager, out _);

    private static RuntimeDependencyManifestService Dependencies(TempWebHostEnvironment env, IDeviceDriver driver, out DeviceManager deviceManager, out RobotManager robotManager)
    {
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new SyntheticImageNode(), "builtin");
        var pluginManager = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var db = new SqliteMetadataDatabase(env);
        var cameras = new CameraManager();
        var devices = new DeviceManager();
        devices.Register(driver);
        deviceManager = devices;
        registry.Register(BuiltInNodeCatalog.Require("device.readTag"), new DeviceReadTagNode(devices), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("device.writeTag"), new DeviceWriteTagNode(devices), "builtin");
        var robots = new RobotManager();
        robots.Register(new VirtualAbbRobotAdapter());
        registry.Register(BuiltInNodeCatalog.Require("robot.executeTarget"), new RobotExecuteTargetNode(robots), "builtin");
        // 控制流节点：依赖清单捕获会对 workflow 中**每个**节点 Require 注册信息——
        // 测试工作流用 flow.if 构造被跳过的分支，必须注册（否则 CaptureAsync 抛"未注册"）。
        registry.Register(BuiltInNodeCatalog.Require("flow.if"), new IfConditionNode(), "builtin");
        robotManager = robots;
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

    /// <summary>
    /// 统计真实写入次数的 PLC 驱动：只统计流程目标标签（statusText）的写入。
    /// 心跳标签（hostHeartbeat）由 DeviceManager 的轮询按存活周期写入，与流程动作无关，
    /// 必须排除，否则会把后台存活信号误算成设备动作重放。
    /// </summary>
    private sealed class CountingPlcDriver(string countedTag = "statusText") : IDeviceDriver
    {
        private readonly VirtualModbusPlcDriver _inner = new();
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);

        public string Id => _inner.Id;
        public string Name => _inner.Name;
        public string Vendor => _inner.Vendor;
        public string Model => _inner.Model;
        public string Driver => _inner.Driver;
        public string Protocol => _inner.Protocol;
        public string Endpoint => _inner.Endpoint;
        public DeviceConnectionState ConnectionState => _inner.ConnectionState;
        public string? Error => _inner.Error;
        public DeviceDriverCapabilities Capabilities => _inner.Capabilities;
        public IReadOnlyList<DeviceTagDefinition> Tags => _inner.Tags;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => _inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => _inner.DisconnectAsync(cancellationToken);
        public Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default) => _inner.ReadAsync(tagId, cancellationToken);

        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default)
        {
            if (string.Equals(tagId, countedTag, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _writeCount);
            return _inner.WriteAsync(tagId, value, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
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

    /// <summary>执行中途响应取消的 runner：验证"取消副作用周期同样置未知动作标记"（F01）。</summary>
    private sealed class CancelAwareBlockingRunner : IVisionWorkflowRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// 模拟"设备写入成功 → 后续节点失败"的副作用周期：每个周期真实执行一次 device.writeTag
    /// （产生物理写入），随后报告失败。用于验证副作用流程首轮即锁定、设备动作不被自动重放。
    /// </summary>
    /// <summary>Q01：在执行开始时读取安全状态文件——验证"意图先于设备动作登记在磁盘上"。</summary>
    private sealed class IntentObservingRunner(string statePath) : IVisionWorkflowRunner
    {
        public string? Observed { get; private set; }

        public Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            try { Observed = File.Exists(statePath) ? File.ReadAllText(statePath) : null; }
            catch { Observed = null; }
            return Task.FromResult(new WorkflowRunResult(runId ?? Guid.NewGuid().ToString("N"), true, 1.2, false, 0, 0, [], [], null, QualityDisposition: "OK"));
        }
    }

    private sealed class DeviceWriteThenFailRunner(DeviceManager devices) : IVisionWorkflowRunner
    {
        public int Cycles { get; private set; }

        public async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            Cycles++;
            var effectiveRunId = runId ?? Guid.NewGuid().ToString("N");
            try
            {
                // 真实执行流程定义中的设备写入节点，产生物理副作用。
                var writeNode = new VisionStudio.Engine.Nodes.DeviceWriteTagNode(devices);
                var node = workflow.Nodes.First(n => n.Type == "device.writeTag");
                var context = new NodeExecutionContext(new Dictionary<string, VisionValue>());
                await writeNode.ExecuteAsync(context, node, cancellationToken);
            }
            catch (Exception ex)
            {
                return new WorkflowRunResult(effectiveRunId, false, 5, false, 0, 0, [], [], null, $"device write failed: {ex.Message}", "Error");
            }
            // 写入已完成，随后节点失败：结果不确定，绝不能自动重放。
            return new WorkflowRunResult(effectiveRunId, false, 5, false, 0, 0, [], [], null, "post-write downstream node failed.", "Error");
        }
    }

    private sealed class DeviceWriteSuccessRunner(DeviceManager devices) : IVisionWorkflowRunner
    {
        public async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            var node = workflow.Nodes.Single(n => n.Type == "device.writeTag");
            await new DeviceWriteTagNode(devices).ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, cancellationToken);
            return new WorkflowRunResult(runId ?? Guid.NewGuid().ToString("N"), true, 1, false, 0, 0, [], [], null, QualityDisposition: "OK");
        }
    }

    private sealed class ClearFailsSafetyStore(string path)
        : DeviceActionSafetyStore(path, NullLogger<DeviceActionSafetyStore>.Instance)
    {
        public override void Clear() => throw new IOException("injected safety clear failure");
    }

    private sealed class FailSaveSafetyStore(string path)
        : DeviceActionSafetyStore(path, NullLogger<DeviceActionSafetyStore>.Instance)
    {
        public override void Save(DeviceActionSafetyState state) => throw new IOException("injected safety save failure");
    }

    private sealed class RobotSendTargetRunner(RobotManager robots) : IVisionWorkflowRunner
    {
        public async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            var target = new VisionRobotTarget2D(520, 240, 28, "RobotBase", "mm", "ABB", "Manual");
            var inputs = new Dictionary<string, VisionValue> { ["target"] = VisionValue.RobotTarget(target) };
            var node = workflow.Nodes.Single(n => n.Type == "robot.executeTarget");
            await new RobotExecuteTargetNode(robots).ExecuteAsync(new NodeExecutionContext(inputs), node, cancellationToken);
            return new WorkflowRunResult(runId ?? Guid.NewGuid().ToString("N"), true, 1, false, 0, 0, [], [], null, QualityDisposition: "OK");
        }
    }

    private sealed class SkippedRobotRunner : IVisionWorkflowRunner
    {
        public int Cycles { get; private set; }

        public Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow, VisionRunOptions? options = null, string? runId = null, CancellationToken cancellationToken = default)
        {
            Cycles++;
            // A control-flow node ran, but there is no robot report: its branch skipped the robot action.
            return Task.FromResult(new WorkflowRunResult(runId ?? Guid.NewGuid().ToString("N"), true, 1,
                false, 0, 0,
                [new NodeRunReport("branch", "flow.if", true, 1, new Dictionary<string, object?>())], [], null,
                QualityDisposition: "OK"));
        }
    }

}
