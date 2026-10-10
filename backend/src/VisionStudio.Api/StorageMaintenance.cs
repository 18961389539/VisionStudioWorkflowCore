using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed class StorageMaintenanceOptions
{
    public long WarningFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long CriticalFreeBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxArtifactBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public long MaxBackupBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public int BackupRetentionCount { get; set; } = 10;
    public int MonitorIntervalSeconds { get; set; } = 30;
    public bool AutoCleanupOnCritical { get; set; } = true;
    public bool IncludeArtifactsInBackup { get; set; } = true;
}

public enum StorageCapacityLevel { Normal, Warning, Critical }

public sealed record StorageCapacityStatus(
    StorageCapacityLevel Level,
    long AvailableFreeBytes,
    long TotalBytes,
    long DatabaseBytes,
    long ArtifactBytes,
    long BackupBytes,
    bool ArtifactQuotaExceeded,
    bool BackupQuotaExceeded,
    bool ProductionStartAllowed,
    bool ArtifactWritesAllowed,
    DateTimeOffset SampledAt);

public sealed class StorageCapacityService
{
    private readonly string _contentRoot;
    private readonly string _dataRoot;
    private readonly string _artifactRoot;
    private readonly string _backupRoot;
    private readonly SqliteMetadataDatabase _database;
    private readonly StorageMaintenanceOptions _options;
    private StorageCapacityStatus? _last;
    private long _artifactReservations;

    public StorageCapacityService(IWebHostEnvironment env, SqliteMetadataDatabase database, IOptions<StorageMaintenanceOptions> options)
    {
        // R05：容量核算必须跟随实际数据根（VISIONSTUDIO_DATA_ROOT），否则发布后数据目录在包外时
        // 会量错卷、量错占用（甚至把整个打包目录算进来）。与 StorageBackupService/媒体服务同源。
        _dataRoot = VisionStudioDataRoot.Resolve(env.ContentRootPath);
        _contentRoot = _dataRoot;
        _artifactRoot = Path.Combine(_dataRoot, "artifacts");
        _backupRoot = Path.Combine(_dataRoot, "backups");
        _database = database;
        _options = options.Value;
        Directory.CreateDirectory(_artifactRoot);
        Directory.CreateDirectory(_backupRoot);
    }

    public StorageCapacityStatus? LastStatus => Volatile.Read(ref _last);

    public Task<StorageCapacityStatus> RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var drive = new DriveInfo(Path.GetPathRoot(_contentRoot)!);
        var available = drive.AvailableFreeSpace;
        var pendingArtifactReservations = Interlocked.Read(ref _artifactReservations);
        var artifactBytes = DirectoryBytes(_artifactRoot) + Math.Max(0, pendingArtifactReservations);
        var backupBytes = DirectoryBytes(_backupRoot);
        static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        var databaseBytes = FileBytes(_database.DatabasePath) + FileBytes(_database.DatabasePath + "-wal") + FileBytes(_database.DatabasePath + "-shm");
        var artifactQuota = _options.MaxArtifactBytes > 0 && artifactBytes >= _options.MaxArtifactBytes;
        var backupQuota = _options.MaxBackupBytes > 0 && backupBytes >= _options.MaxBackupBytes;
        var level = available <= _options.CriticalFreeBytes || artifactQuota
            ? StorageCapacityLevel.Critical
            : available <= _options.WarningFreeBytes || backupQuota
                ? StorageCapacityLevel.Warning
                : StorageCapacityLevel.Normal;
        var status = new StorageCapacityStatus(
            level, available, drive.TotalSize, databaseBytes, artifactBytes, backupBytes,
            artifactQuota, backupQuota,
            level != StorageCapacityLevel.Critical,
            available > _options.CriticalFreeBytes && !artifactQuota,
            DateTimeOffset.UtcNow);
        Volatile.Write(ref _last, status);
        return Task.FromResult(status);
    }

    public async Task EnsureProductionStartAllowedAsync(CancellationToken ct = default)
    {
        var status = await RefreshAsync(ct);
        if (!status.ProductionStartAllowed)
            throw new ApiUnavailableException($"Storage capacity is critical: {status.AvailableFreeBytes / 1024 / 1024} MiB free, artifacts {status.ArtifactBytes / 1024 / 1024} MiB.");
    }

    public bool CanPersistArtifact(long incomingBytes)
    {
        var current = LastStatus;
        if (current is null) return true;
        if (!current.ArtifactWritesAllowed) return false;
        var reserved = Interlocked.Read(ref _artifactReservations);
        if (_options.MaxArtifactBytes > 0 && current.ArtifactBytes + reserved + incomingBytes > _options.MaxArtifactBytes) return false;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_contentRoot)!);
            if (drive.AvailableFreeSpace - incomingBytes <= _options.CriticalFreeBytes) return false;
        }
        catch (IOException) { return false; }
        Interlocked.Add(ref _artifactReservations, Math.Max(0, incomingBytes));
        return true;
    }

    public void ReleaseArtifactReservation(long bytes)
    {
        if (bytes <= 0) return;
        var after = Interlocked.Add(ref _artifactReservations, -bytes);
        if (after < 0) Interlocked.Exchange(ref _artifactReservations, 0);
    }

    public async Task EnsureBackupCapacityAsync(long estimatedBytes, CancellationToken ct = default)
    {
        var status = await RefreshAsync(ct);
        if (status.AvailableFreeBytes - Math.Max(estimatedBytes, 0) <= _options.CriticalFreeBytes)
            throw new ApiUnavailableException("Not enough free disk space to create a safe backup while preserving the critical free-space reserve.");
        if (_options.MaxBackupBytes > 0 && status.BackupBytes + Math.Max(estimatedBytes, 0) > _options.MaxBackupBytes)
            throw new ApiConflictException("Backup quota would be exceeded. Delete older backups before creating another backup.");
    }

    private static long DirectoryBytes(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }
}

/// <summary>
/// Prevents new API requests from entering while a destructive storage maintenance
/// operation waits for already-running requests to drain.
/// </summary>
public sealed class StorageMaintenanceCoordinator
{
    private readonly object _sync = new();
    private int _activeRequests;
    private bool _maintenance;
    private string? _reason;
    private TaskCompletionSource<bool>? _drained;
    /// <summary>
    /// R04：恢复失败后的持久锁定。与临时排他 lease 生命周期无关——即使 BeginExclusiveAsync 的
    /// await using 离开方法触发 EndExclusive，锁定依然生效，普通变更/生产启动继续被拒绝，
    /// 直到运维显式清除（表示已人工核对/完成恢复）。防止数据库与系统资产不一致时放行新写入。
    /// F03：锁定同时持久化到磁盘（failureLockPath），重启后仍然生效。
    /// </summary>
    private bool _failureLocked;
    private string? _failureReason;

    private readonly string? _failureLockPath;
    private readonly ILogger<StorageMaintenanceCoordinator>? _logger;

    /// <param name="failureLockPath">
    /// F03：失败锁持久化路径（null = 仅内存，单元测试用）。文件存在 ⇒ 锁定；删除 ⇒ 解除。
    /// </param>
    public StorageMaintenanceCoordinator(string? failureLockPath = null, ILogger<StorageMaintenanceCoordinator>? logger = null)
    {
        _failureLockPath = failureLockPath;
        _logger = logger;
        if (failureLockPath is null || !File.Exists(failureLockPath)) return;
        // F03：加载持久化的失败锁——重启后普通写入继续被阻断，直到受控恢复入口完成核对并解除。
        try
        {
            var state = JsonSerializer.Deserialize<FailureLockState>(File.ReadAllText(failureLockPath));
            _failureLocked = true;
            _failureReason = state?.Reason ?? "restore previously failed (persisted state)";
            logger?.LogWarning("Persisted storage failure lock loaded: {Reason}", _failureReason);
        }
        catch (Exception ex)
        {
            // 文件存在但不可读：绝不能当作"无锁"——保守锁定，等待人工/恢复入口处理。
            _failureLocked = true;
            _failureReason = "a persisted storage failure lock exists but could not be read";
            logger?.LogError(ex, "Storage failure lock state at {Path} is unreadable; the host stays locked until manual resolution.", failureLockPath);
        }
    }

    /// <summary>
    /// R07：附件一致性读/写门。附件写入方（生产追溯落盘/清理）持**共享**租约；
    /// 备份在"DB 快照 + 枚举附件 + 打包读取"窗口内持**排他**租约。这样保证：
    /// 备份清单里每条 DB 引用（preview_relative_path/replay_relative_path）都对应包内真实文件，
    /// 不会出现"快照后附件被清理删除"或"读到半写文件"的悬空引用。
    /// 与 <see cref="_maintenance"/> 不同——备份要求生产停机，但**停机不是本门的职责**；
    /// 本门只保证"备份期间附件集合冻结"，从而把 R07 从"必须停机"放宽为"允许在线备份"。
    /// </summary>
    private int _activeArtifactWriters;
    private bool _artifactSnapshotActive;
    private TaskCompletionSource<bool>? _artifactWritersDrained;

    public bool IsMaintenanceActive { get { lock (_sync) return _maintenance || _failureLocked; } }
    public string? Reason { get { lock (_sync) return _failureReason ?? _reason; } }
    public bool IsFailureLocked { get { lock (_sync) return _failureLocked; } }
    /// <summary>当前是否有备份冻结了附件集合（排他附件窗口进行中或等待中）。</summary>
    public bool IsArtifactSnapshotActive { get { lock (_sync) return _artifactSnapshotActive; } }

    /// <summary>
    /// 附件写入方获取**共享**租约。备份冻结窗口进行中/等待中时返回 null（写入方应放弃本次落盘并记 note，
    /// 而不是阻塞——附件是可选证据，绝不反压生产循环）。返回的租约 Dispose 即释放。
    /// </summary>
    public IDisposable? TryEnterArtifactWrite()
    {
        lock (_sync)
        {
            // 冻结窗口进行中或已有排他请求在等待 → 拒绝新写入，避免饿死排他窗口。
            if (_artifactSnapshotActive) return null;
            _activeArtifactWriters++;
            return new ArtifactWriteLease(this);
        }
    }

    /// <summary>
    /// 备份获取**排他**附件租约：等待在途附件写入排空后返回。窗口内不再接受新的附件写入，
    /// 因此 DB 快照、附件枚举与打包读取看到的是同一冻结集合。
    /// </summary>
    public async Task<IAsyncDisposable> EnterArtifactSnapshotAsync(CancellationToken ct)
    {
        Task wait;
        lock (_sync)
        {
            _artifactSnapshotActive = true;
            if (_activeArtifactWriters == 0) wait = Task.CompletedTask;
            else
            {
                _artifactWritersDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _artifactWritersDrained.Task;
            }
        }
        try { await wait.WaitAsync(ct); }
        catch { ExitArtifactSnapshot(); throw; }
        return new ArtifactSnapshotLease(this);
    }

    private void ExitArtifactWrite()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_sync)
        {
            _activeArtifactWriters = Math.Max(0, _activeArtifactWriters - 1);
            if (_artifactSnapshotActive && _activeArtifactWriters == 0) { signal = _artifactWritersDrained; _artifactWritersDrained = null; }
        }
        signal?.TrySetResult(true);
    }

    private void ExitArtifactSnapshot()
    {
        lock (_sync)
        {
            _artifactSnapshotActive = false;
            _artifactWritersDrained = null;
        }
    }

    public IDisposable? TryEnterRequest()
    {
        lock (_sync)
        {
            if (_maintenance || _failureLocked) return null;
            _activeRequests++;
            return new RequestLease(this);
        }
    }

    /// <summary>
    /// 恢复失败后进入持久锁定：数据库/系统资产可能不一致，禁止一切普通请求进入，
    /// 直到通过 <see cref="ClearFailureLock"/> 显式解除（表示已完成人工恢复核对）。
    /// </summary>
    public void EnterFailureLock(string reason)
    {
        lock (_sync)
        {
            _failureLocked = true;
            _failureReason = reason;
        }
        PersistFailureLock(reason);
    }

    /// <summary>显式清除恢复失败锁定；只有确认恢复完成（或经受控恢复入口核对完毕）才允许调用。</summary>
    public void ClearFailureLock()
    {
        lock (_sync)
        {
            _failureLocked = false;
            _failureReason = null;
        }
        if (_failureLockPath is { } path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                // 删除失败会让重启后仍要求核对（保守方向）；记录以便运维手动清理。
                _logger?.LogWarning(ex, "Storage failure lock file at {Path} could not be deleted; the lock will re-engage after a restart.", path);
            }
        }
    }

    /// <summary>F03：原子写入失败锁状态（temp + move）——绝不让半写文件被下一次启动读到。</summary>
    private void PersistFailureLock(string reason)
    {
        if (_failureLockPath is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FailureLockState(reason, DateTimeOffset.UtcNow)));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            // 持久化失败：内存锁仍生效（本进程内约束不变），但重启会丢失——必须告警。
            _logger?.LogError(ex, "Failed to persist the storage failure lock; the in-memory lock stays in force but a restart would lose it.");
        }
    }

    private sealed record FailureLockState(string Reason, DateTimeOffset SetAt);

    public async Task<IAsyncDisposable> BeginExclusiveAsync(string reason, CancellationToken ct)
    {
        Task wait;
        lock (_sync)
        {
            if (_failureLocked) throw new ApiConflictException($"Storage is locked after a failed restore ({_failureReason}). Complete recovery or clear the lock before running maintenance.");
            if (_maintenance) throw new ApiConflictException($"Storage maintenance is already active ({_reason}).");
            _maintenance = true;
            _reason = reason;
            if (_activeRequests == 0) wait = Task.CompletedTask;
            else
            {
                _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _drained.Task;
            }
        }
        try { await wait.WaitAsync(ct); }
        catch { EndExclusive(); throw; }
        return new ExclusiveLease(this);
    }

    private void ExitRequest()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_sync)
        {
            _activeRequests = Math.Max(0, _activeRequests - 1);
            if (_maintenance && _activeRequests == 0) { signal = _drained; _drained = null; }
        }
        signal?.TrySetResult(true);
    }

    private void EndExclusive()
    {
        lock (_sync)
        {
            // 失败锁定不随排他 lease 结束而解除：否则回滚失败后立刻放行新写入，
            // 与"host must stay in maintenance mode"的语义矛盾（R04）。
            _maintenance = false;
            if (!_failureLocked) { _reason = null; _drained = null; }
        }
    }

    private sealed class RequestLease(StorageMaintenanceCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ExitRequest(); }
    }

    private sealed class ArtifactWriteLease(StorageMaintenanceCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ExitArtifactWrite(); }
    }

    private sealed class ArtifactSnapshotLease(StorageMaintenanceCoordinator owner) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ExitArtifactSnapshot();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ExclusiveLease(StorageMaintenanceCoordinator owner) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndExclusive(); return ValueTask.CompletedTask; }
    }
}

public sealed class StorageMaintenanceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, StorageMaintenanceCoordinator coordinator, IProblemDetailsService problemDetails)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        // F03：受控恢复入口在失败锁激活（或维护进行中）时也必须可达——否则锁定后没有任何受认证
        // 路径可以完成核对并解锁。它自行做状态检查与审计，不持有请求 lease。
        if (HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Equals("/api/storage/maintenance/recover", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        // The first restore request must not count itself or it would deadlock while acquiring
        // exclusive mode. Once maintenance is active, later restore requests are rejected too.
        // 排他操作（恢复与创建备份）自行进入维护窗口：若它们持有请求 lease，BeginExclusiveAsync
        // 排空时会等待当前请求自身而死锁——与 restore 相同地豁免（维护已激活时仍走拒绝路径）。
        var isExclusiveOperation = context.Request.Path.StartsWithSegments("/api/storage/restore") ||
            (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals("/api/storage/backups", StringComparison.OrdinalIgnoreCase));
        if (isExclusiveOperation && !coordinator.IsMaintenanceActive) { await next(context); return; }

        using var lease = coordinator.TryEnterRequest();
        if (lease is not null) { await next(context); return; }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Storage maintenance in progress",
            Detail = $"The host is temporarily quiesced for storage maintenance ({coordinator.Reason ?? "maintenance"}).",
            Instance = context.Request.Path
        };
        details.Extensions["code"] = "storage_maintenance";
        details.Extensions["correlationId"] = RequestCorrelation.Get(context);
        await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details });
    }
}

public sealed class StorageCapacityHostedService(
    StorageCapacityService capacity,
    TraceabilityStore traces,
    AlarmStore alarms,
    StorageMaintenanceCoordinator maintenance,
    IOptions<StorageMaintenanceOptions> options,
    ILogger<StorageCapacityHostedService> logger) : BackgroundService
{
    private readonly StorageMaintenanceOptions _options = options.Value;
    private StorageCapacityLevel? _lastLevel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.MonitorIntervalSeconds, 5, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var maintenanceLease = maintenance.TryEnterRequest())
                {
                    if (maintenanceLease is not null)
                    {
                        var status = await capacity.RefreshAsync(stoppingToken);
                        if (status.Level != _lastLevel)
                        {
                            await PublishAlarmTransitionAsync(status, stoppingToken);
                            _lastLevel = status.Level;
                        }
                        if (status.Level == StorageCapacityLevel.Critical && _options.AutoCleanupOnCritical)
                        {
                            var changed = await traces.CleanupAsync(stoppingToken);
                            if (changed > 0)
                                logger.LogWarning("Low-space cleanup changed {TraceChanges} trace records/artifacts.", changed);
                            await capacity.RefreshAsync(stoppingToken);
                        }
                    }
                }
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Storage capacity monitor failed.");
                try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task PublishAlarmTransitionAsync(StorageCapacityStatus status, CancellationToken ct)
    {
        const string source = "StorageCapacity";
        if (status.Level == StorageCapacityLevel.Critical)
        {
            await alarms.RecoverAsync("STORAGE-DISK-WARNING", source, ct);
            await alarms.RaiseAsync("STORAGE-DISK-CRITICAL", AlarmSeverity.Critical, source,
                $"Storage critical: {status.AvailableFreeBytes / 1024 / 1024} MiB free; artifacts {status.ArtifactBytes / 1024 / 1024} MiB.", null, ct);
        }
        else if (status.Level == StorageCapacityLevel.Warning)
        {
            await alarms.RecoverAsync("STORAGE-DISK-CRITICAL", source, ct);
            await alarms.RaiseAsync("STORAGE-DISK-WARNING", AlarmSeverity.Warning, source,
                $"Storage warning: {status.AvailableFreeBytes / 1024 / 1024} MiB free.", null, ct);
        }
        else
        {
            await alarms.RecoverAsync("STORAGE-DISK-WARNING", source, ct);
            await alarms.RecoverAsync("STORAGE-DISK-CRITICAL", source, ct);
        }
    }
}
