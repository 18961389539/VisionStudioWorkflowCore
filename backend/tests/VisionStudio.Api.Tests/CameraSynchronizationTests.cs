using OpenCvSharp;
using VisionStudio.Api;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Tests;

public sealed class CameraSynchronizationTests
{
    [Fact]
    public void CommissioningWorstRun_FrameEvidence_PreservesTimestampDrillDown()
    {
        var host = DateTimeOffset.Parse("2026-10-06T08:00:00Z");
        var frames = new[]
        {
            new CameraSynchronizationCommissioningFrameEvidence("cam-a", 10, host, 1_000_000, 77, 0),
            new CameraSynchronizationCommissioningFrameEvidence("cam-b", 11, host.AddTicks(500), 1_050_000, 77, 50)
        };
        var worst = new CameraSynchronizationCommissioningWorstRun("run-1", host, "Completed", 50, false, "DevicePtp", null, frames);
        Assert.Equal("DevicePtp", worst.TimestampBasis);
        Assert.Equal(50, worst.Frames[1].DeltaFromFirstUs);
        Assert.Equal(1_050_000, worst.Frames[1].DeviceTimestampNs);
        Assert.Equal(77, worst.Frames[1].TriggerId);
    }

    [Fact]
    public async Task SchemaV10_CreatesCameraSynchronizationCommissioningPtpAndTransportEvidence()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        Assert.Equal(16, db.CurrentSchemaVersion);
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_sync_groups';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_sync_runs';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_sync_commissioning_tests';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('camera_sync_runs') WHERE name='ptp_json';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('camera_sync_runs') WHERE name='transport_json';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CommissioningStore_PersistsProgressAndReportMetadata()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new CameraSynchronizationCommissioningStore(db);
        var started = DateTimeOffset.UtcNow;
        var test = new CameraSynchronizationCommissioningTest("test-1", "sync", "hash", false, 100, 0, 3000, 50, 20, "Queued", started, null, null, null);
        await store.CreateAsync(test);
        var loaded = await store.GetAsync("test-1");
        Assert.Equal(100, loaded.RequestedIterations);
        Assert.Equal("Queued", loaded.Status);
        await store.UpdateAsync(loaded with { Status = "Running", CompletedIterations = 7 });
        loaded = await store.GetAsync("test-1");
        Assert.Equal(7, loaded.CompletedIterations);
        Assert.Equal("Running", loaded.Status);
    }

    [Fact]
    public async Task SynchronizationGroupHash_IsStableAcrossCameraOrdering()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new CameraSynchronizationGroupStore(db);
        var runStore = new CameraSynchronizationRunStore(db);
        var a = await store.UpsertAsync(new CameraSynchronizationGroupRequest("sync", "Sync", "fake-sync", ["cam-b", "cam-a"]));
        var b = await store.UpsertAsync(new CameraSynchronizationGroupRequest("sync", "Sync", "fake-sync", ["cam-a", "cam-b"]));
        Assert.Equal(a.ConfigurationHash, b.ConfigurationHash);
        Assert.Equal(new[] { "cam-a", "cam-b" }, b.CameraIds);
    }

    [Fact]
    public async Task CaptureResult_ComputesDeviceTimestampSkew()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new CameraSynchronizationGroupStore(db);
        var runStore = new CameraSynchronizationRunStore(db);
        var cameras = new CameraManager();
        var a = new SyncFakeCamera("cam-a", 0);
        var b = new SyncFakeCamera("cam-b", 50_000); // 50 us
        cameras.Register(a); cameras.Register(b);
        var action = new SyncFakeActionProvider(a, b);
        var service = new CameraSynchronizationService(store, runStore, cameras, [action]);
        await service.SaveAsync(new CameraSynchronizationGroupRequest(
            "sync", "Sync", action.Driver, [a.Id, b.Id], DeviceKey: 7, GroupKey: 3, GroupMask: 15, MaxTriggerSkewUs: 100));
        await cameras.StartAsync(a.Id); await cameras.StartAsync(b.Id);

        var result = await service.TriggerAsync("sync", new CameraSynchronizationTriggerRequest(FrameTimeoutMs: 2000));

        Assert.Equal("device-ptp", result.TimestampBasis);
        Assert.Equal(50, result.TriggerSkewUs, 3);
        Assert.True(result.WithinTolerance);
        var history = await runStore.ListAsync("sync");
        Assert.Single(history);
        Assert.Equal("manual-api", history[0].Source);
        Assert.Equal(50d, history[0].TriggerSkewUs!.Value, 3);
        Assert.Equal(2, history[0].Frames.Count);
        Assert.Equal(2, history[0].PtpSnapshots.Count);
        Assert.All(history[0].PtpSnapshots, x => Assert.Equal(CameraPtpClockState.Locked, x.State));
        Assert.Equal(2, history[0].TransportSnapshots?.Count);
        await cameras.DisposeAsync();
    }

    [Fact]
    public async Task ScheduledTrigger_RequiresPtpReadyAndUsesFutureDeviceClock()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new CameraSynchronizationGroupStore(db);
        var runStore = new CameraSynchronizationRunStore(db);
        var cameras = new CameraManager();
        var a = new SyncFakeCamera("cam-a", 0);
        var b = new SyncFakeCamera("cam-b", 10_000);
        cameras.Register(a); cameras.Register(b);
        var action = new SyncFakeActionProvider(a, b);
        var service = new CameraSynchronizationService(store, runStore, cameras, [action]);
        await service.SaveAsync(new CameraSynchronizationGroupRequest("sync", "Sync", action.Driver, [a.Id, b.Id], 7, 3, 15, ScheduledLeadTimeMs: 50));
        await cameras.StartAsync(a.Id); await cameras.StartAsync(b.Id);

        var result = await service.TriggerAsync("sync", new CameraSynchronizationTriggerRequest(Scheduled: true, LeadTimeMs: 50));

        Assert.True(result.Scheduled);
        Assert.NotNull(result.ScheduledDeviceTimeNs);
        Assert.True(action.LastRequest?.ScheduledDeviceTimeNs > 10_000_000_000L);
        Assert.Equal(1_000_000_000L, action.LastRequest?.DeviceTickFrequencyHz);
        await cameras.DisposeAsync();
    }

    [Fact]
    public async Task FailedActionCommand_IsPersistedForCommissioningStatistics()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new CameraSynchronizationGroupStore(db);
        var runStore = new CameraSynchronizationRunStore(db);
        var cameras = new CameraManager();
        var a = new SyncFakeCamera("cam-a", 0);
        var b = new SyncFakeCamera("cam-b", 10_000);
        cameras.Register(a); cameras.Register(b);
        var action = new SyncFakeActionProvider(a, b) { Fail = true };
        var service = new CameraSynchronizationService(store, runStore, cameras, [action]);
        await service.SaveAsync(new CameraSynchronizationGroupRequest("sync", "Sync", action.Driver, [a.Id, b.Id], 7, 3, 15));
        await cameras.StartAsync(a.Id); await cameras.StartAsync(b.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TriggerAsync("sync", new CameraSynchronizationTriggerRequest()));

        var history = await runStore.ListAsync("sync");
        Assert.Single(history);
        Assert.Equal("Failed", history[0].Outcome);
        Assert.Contains("injected", history[0].Error, StringComparison.OrdinalIgnoreCase);
        await cameras.DisposeAsync();
    }

    [Fact]
    public void Statistics_ComputesPercentilesAndCommissioningPass()
    {
        var now = DateTimeOffset.UtcNow;
        var group = new CameraSynchronizationGroup("sync", "Sync", "fake-sync", ["a", "b"], 1, 1, -1, "255.255.255.255", true, 1_000_000, 100, 100, "hash", now, now);
        var history = Enumerable.Range(0, 20).Select(i => new CameraSynchronizationRunRecord(
            $"run-{i}", "sync", "hash", "manual-api", false, now.AddMilliseconds(i), now.AddMilliseconds(i + 1), 1,
            null, "device-ptp", 40 + i, 100, true, "Completed", null, null, [], [])).ToArray();

        var stats = CameraSynchronizationService.ComputeStatistics(group, history, 100, 20);

        Assert.Equal(20, stats.TotalRuns);
        Assert.Equal(49, stats.P50SkewUs);
        Assert.Equal(58, stats.P95SkewUs);
        Assert.Equal(59, stats.P99SkewUs);
        Assert.Equal(59, stats.MaxSkewUs);
        Assert.Equal("Pass", stats.Commissioning.Status);
        Assert.True(stats.Commissioning.Passed);
    }

    [Fact]
    public void PtpDiagnostics_ComputesReadyRateOffsetPercentileAndCorrelation()
    {
        var now = DateTimeOffset.UtcNow;
        var group = new CameraSynchronizationGroup("sync", "Sync", "fake-sync", ["a", "b"], 1, 1, -1, "255.255.255.255", true, 1_000_000, 100, 100, "hash", now, now);
        var history = Enumerable.Range(1, 10).Select(i => new CameraSynchronizationRunRecord(
            $"run-{i}", "sync", "hash", "manual-api", false, now.AddSeconds(i), now.AddSeconds(i), 1, null, "device-ptp", i * 10, 100, true, "Completed", null, null, [],
            [new CameraTimeSynchronizationStatus("a", "fake-sync", true, true, CameraPtpClockState.Locked, i * 100, MasterClockId: "master", CapturedAt: now.AddSeconds(i)),
             new CameraTimeSynchronizationStatus("b", "fake-sync", true, true, CameraPtpClockState.Locked, i * 200, MasterClockId: "master", CapturedAt: now.AddSeconds(i))])).ToArray();

        var diagnostics = CameraSynchronizationService.ComputePtpDiagnostics(group, history, 100);

        Assert.Equal(10, diagnostics.RunsWithPtpEvidence);
        Assert.Equal(0, diagnostics.PtpNotReadyRuns);
        Assert.Equal(2, diagnostics.Cameras.Count);
        Assert.Equal(1d, diagnostics.Cameras[0].ReadyRate);
        Assert.NotNull(diagnostics.OffsetSkewPearsonCorrelation);
        Assert.True(diagnostics.OffsetSkewPearsonCorrelation > 0.99);
        Assert.Equal(2000, diagnostics.MaxAbsOffsetNs);
    }

    private sealed class SyncFakeActionProvider(params SyncFakeCamera[] cameras) : ICameraActionCommandProvider
    {
        public string Driver => "fake-sync";
        public CameraActionCommandCapabilities ActionCommandCapabilities { get; } = new(true, true, true, true, true);
        public CameraActionCommandRequest? LastRequest { get; private set; }
        public bool Fail { get; set; }
        public Task<CameraActionCommandResult> IssueActionCommandAsync(CameraActionCommandRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Fail) throw new InvalidOperationException("injected Action Command failure");
            var basis = request.ScheduledDeviceTimeNs ?? 10_000_000_000L;
            foreach (var camera in cameras) camera.Signal(basis);
            return Task.FromResult(new CameraActionCommandResult(Driver, request.ScheduledDeviceTimeNs is not null, request.ScheduledDeviceTimeNs, DateTimeOffset.UtcNow, cameras.Length, ["ok"]));
        }
    }

    private sealed class SyncFakeCamera : ICameraDevice, ICameraFeatureProvider, ICameraSynchronizationProvider
    {
        private readonly SemaphoreSlim _trigger = new(0, 1);
        private readonly long _offsetNs;
        private long _nextTimestamp;
        private long _sequence;
        public SyncFakeCamera(string id, long offsetNs) { Id = id; Name = id; _offsetNs = offsetNs; }
        public string Id { get; }
        public string Name { get; }
        public string Driver => "fake-sync";
        public string? Source => Id;
        public CameraState State { get; private set; } = CameraState.Closed;
        public long FramesCaptured => Interlocked.Read(ref _sequence);
        public string? LastError => null;
        public CameraSettings Settings { get; private set; } = new(TriggerMode: CameraTriggerMode.External, ExternalTriggerSource: "Action1");
        public CameraCapabilities Capabilities { get; } = new(HostSimulatedExternalTrigger: false);
        public CameraCommissioningCapabilities CommissioningCapabilities { get; } = new(Ptp: true, ActionCommand: true);
        public string? CommissioningProfileHash => "sync-profile";
        public void Signal(long basis) { Interlocked.Exchange(ref _nextTimestamp, basis + _offsetNs); if (_trigger.CurrentCount == 0) _trigger.Release(); }
        public Task OpenAsync(CancellationToken cancellationToken = default) { State = CameraState.Open; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { State = CameraState.Closed; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken cancellationToken = default) { State = CameraState.Streaming; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { if (State != CameraState.Closed) State = CameraState.Open; return Task.CompletedTask; }
        public Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
        public Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout);
            try { await _trigger.WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new CameraFrameTimeoutException("timeout"); }
            var sequence = Interlocked.Increment(ref _sequence);
            var ts = Interlocked.Read(ref _nextTimestamp);
            return new VisionFrame(Id, sequence, DateTimeOffset.UtcNow, new Mat(2, 2, MatType.CV_8UC1, Scalar.All(sequence)), "Mono8", ts, sequence);
        }
        public Task<IReadOnlyList<CameraFeatureDescriptor>> ListFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CameraFeatureDescriptor>>([]);
        public Task<CameraCommissioningProfile> ReadCommissioningProfileAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CameraCommissioningProfile(Acquisition: Settings, PtpEnabled: true, ActionDeviceKey: 7, ActionGroupKey: 3, ActionGroupMask: 15));
        public Task<CameraCommissioningApplyResult> ApplyCommissioningProfileAsync(CameraCommissioningProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(new CameraCommissioningApplyResult(profile, "hash", [], []));
        public Task SetFeatureAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CameraTimeSynchronizationStatus> GetTimeSynchronizationStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CameraTimeSynchronizationStatus(Id, Driver, true, true, CameraPtpClockState.Locked, _offsetNs, 10_000_000_000L + _offsetNs, "master", 1_000_000_000L, CapturedAt: DateTimeOffset.UtcNow));
        public ValueTask DisposeAsync() { _trigger.Dispose(); return ValueTask.CompletedTask; }
    }
}
