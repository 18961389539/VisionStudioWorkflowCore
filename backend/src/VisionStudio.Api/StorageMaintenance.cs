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

    /// <summary>
    /// Q08：恢复成功的"待重启"标记。恢复会替换数据库与设备/生产/插件/站点文件，但已注册的
    /// 适配器、已加载插件与已绑定配置不会随之重新初始化——继续服务会形成"磁盘是恢复版本、
    /// 内存是恢复前版本"的混合运行环境。恢复成功即置位并持久化，仅进程重启后自动解除。
    /// </summary>
    private bool _restartPending;
    private string? _restartPendingReason;

    private readonly string? _failureLockPath;
    private readonly string? _restartPendingPath;
    private readonly string? _restoreTransactionPath;
    private readonly ILogger<StorageMaintenanceCoordinator>? _logger;

    /// <param name="failureLockPath">
    /// F03：失败锁持久化路径（null = 仅内存，单元测试用）。文件存在 ⇒ 锁定；删除 ⇒ 解除。
    /// </param>
    /// <param name="restartPendingPath">
    /// Q08：待重启标记路径（null = 仅内存）。本进程写入；下一次进程启动时清除——"启动"本身就是
    /// 重启已发生的证据，恢复的资产已随启动重新加载。
    /// </param>
    /// <param name="restoreTransactionPath">
    /// Q08：恢复事务意图路径（null = 仅内存）。恢复开始即写入、成功才删除；启动时发现未完成事务
    /// ⇒ 上次进程在恢复中途被终止（DB/目录可能处于混合状态）⇒ 直接进入失败锁，需人工核对。
    /// </param>
    public StorageMaintenanceCoordinator(
        string? failureLockPath = null,
        ILogger<StorageMaintenanceCoordinator>? logger = null,
        string? restartPendingPath = null,
        string? restoreTransactionPath = null)
    {
        _failureLockPath = failureLockPath;
        _restartPendingPath = restartPendingPath;
        _restoreTransactionPath = restoreTransactionPath;
        _logger = logger;

        // Q08：进程启动时若存在未完成的恢复事务，说明上次恢复在"数据库已替换、目录未全部切换"、
        // 或"已安装新资产、尚未提交"的中间态被终止。catch/finally 没有执行，失败锁可能缺失——
        // 这里补上：直接进入失败锁，要求核对后经受控入口解除。
        // R03：唯一例外是阶段已推进到 committed——恢复本身已经完成且已耐久提交，残留只可能是
        // 保护副本清理或事务文件退役未完成（例如文件被短暂占用）。此时只做剩余清理，绝不锁定主机。
        if (restoreTransactionPath is not null && File.Exists(restoreTransactionPath))
        {
            var transaction = ReadTransaction();
            if (string.Equals(transaction?.Stage, "committed", StringComparison.Ordinal))
            {
                RetireCommittedTransaction(restoreTransactionPath, transaction, logger);
            }
            else
            {
                _failureLocked = true;
                _failureReason = "an interrupted storage restore transaction was detected on startup; " +
                                 "the database and on-disk assets may be in a mixed state";
                logger?.LogError("Interrupted restore transaction detected at {Path}; the host stays locked until manual verification.", restoreTransactionPath);
            }
        }
        // Q08：待重启标记在"新进程启动"时自动解除——恢复后的资产已随本次启动重新加载。
        if (restartPendingPath is not null && File.Exists(restartPendingPath))
        {
            try
            {
                File.Delete(restartPendingPath);
                logger?.LogInformation("Restart pending marker at {Path} cleared: the process restart completed the restore.", restartPendingPath);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Restart pending marker at {Path} could not be deleted; it will be retried on the next startup.", restartPendingPath);
            }
        }

        if (failureLockPath is null || !File.Exists(failureLockPath)) return;
        // F03：加载持久化的失败锁——重启后普通写入继续被阻断，直到受控恢复入口完成核对并解除。
        try
        {
            var state = JsonSerializer.Deserialize<FailureLockState>(File.ReadAllText(failureLockPath));
            _failureLocked = true;
            _failureReason ??= state?.Reason ?? "restore previously failed (persisted state)";
            logger?.LogWarning("Persisted storage failure lock loaded: {Reason}", _failureReason);
        }
        catch (Exception ex)
        {
            // 文件存在但不可读：绝不能当作"无锁"——保守锁定，等待人工/恢复入口处理。
            _failureLocked = true;
            _failureReason ??= "a persisted storage failure lock exists but could not be read";
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
    public string? Reason { get { lock (_sync) return _failureReason ?? _restartPendingReason ?? _reason; } }
    public bool IsFailureLocked { get { lock (_sync) return _failureLocked; } }
    /// <summary>Q08：恢复成功但进程尚未重启——业务请求必须继续被阻断（503 restart_required）。</summary>
    public bool IsRestartPending { get { lock (_sync) return _restartPending; } }
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
            // Q08：待重启门与维护/失败锁同为阻断语义（纵深防御——中间件已单独拦截）。
            if (_maintenance || _failureLocked || _restartPending) return null;
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

    /// <summary>Q08：恢复事务的耐久意图/阶段记录（跨进程恢复审计的最小载体）。</summary>
    public sealed record RestoreTransaction(
        string BackupId,
        DateTimeOffset StartedAt,
        string Stage,
        IReadOnlyList<string> PlannedStages,
        DateTimeOffset UpdatedAt,
        string? Failure,
        string SafetyDatabase,
        string SafetyDatabaseSha256,
        string SourceDatabaseSha256,
        int SourceSchemaVersion,
        string ExpectedDatabaseSha256,
        IReadOnlyDictionary<string, string> ExpectedAssetSha256,
        IReadOnlyList<RestoreProtectionAsset> ProtectedAssets,
        RestoreTransaction? PreviousTransaction = null);

    public sealed record RestoreProtectionAsset(string Target, string PreservedPath, bool Existed, string? Sha256);

    /// <summary>
    /// Q08：恢复成功 → 进入"待重启"门并持久化。此后除认证/健康读取外的业务请求一律 503
    /// （restart_required），直到进程重启后由构造函数自动清除。绝不允许"磁盘已恢复、内存未重载"
    /// 的混合环境继续服务设备动作。
    /// </summary>
    public void MarkRestartPending(string reason)
    {
        lock (_sync)
        {
            _restartPending = true;
            _restartPendingReason = reason;
        }
        if (_restartPendingPath is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FailureLockState(reason, DateTimeOffset.UtcNow)));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            // 持久化失败：内存门仍生效（本进程不放行）；重启后文件缺失会解除门——但重启本身
            // 正是解除条件，因此该失败不降低安全性，仅需记录。
            _logger?.LogWarning(ex, "Failed to persist the restart-pending marker; the in-memory gate stays in force for this process.");
        }
    }

    /// <summary>测试/运维显式清除待重启门（生产路径由进程重启自动清除）。</summary>
    public void ClearRestartPending()
    {
        lock (_sync)
        {
            _restartPending = false;
            _restartPendingReason = null;
        }
        DeleteMarkerFile(_restartPendingPath, "restart-pending marker");
    }

    /// <summary>
    /// Q08：恢复事务意图登记——恢复开始即耐久写入（backupId + 阶段）。若进程在恢复中途被终止，
    /// 启动检查会读到该文件并进入失败锁；成功完成时由 <see cref="CompleteRestoreTransaction"/> 删除。
    /// </summary>
    public void BeginRestoreTransaction(string backupId, IReadOnlyList<string> plannedStages, string safetyDatabase,
        string safetyDatabaseSha256, string sourceDatabaseSha256, int sourceSchemaVersion, string expectedDatabaseSha256,
        IReadOnlyDictionary<string, string> expectedAssetSha256,
        IReadOnlyList<RestoreProtectionAsset> protectedAssets)
    {
        var previous = ReadTransaction();
        // A retry may replace an unresolved intent only while retaining a verified rollback anchor.
        // The prior generation remains nested in the new durable record until the new restore commits.
        if (HasRestoreTransactionMarker && previous is null)
            throw new InvalidOperationException("Cannot replace an unreadable restore intent; preserve it for manual recovery.");
        if (previous is not null)
        {
            var databaseAnchorOk = File.Exists(previous.SafetyDatabase) &&
                string.Equals(StorageBackupService.ComputeFileSha256(previous.SafetyDatabase), previous.SafetyDatabaseSha256, StringComparison.OrdinalIgnoreCase);
            var assetsAnchorOk = previous.ProtectedAssets.All(asset =>
            {
                // Existed=false is durable evidence about the baseline. A later interrupted install
                // may have created the target; full restore replaces that tainted root and captures
                // its current contents in the new transaction's protection group.
                if (!asset.Existed) return !Directory.Exists(asset.PreservedPath);
                var preservedMatches = Directory.Exists(asset.PreservedPath) &&
                    string.Equals(StorageBackupService.ComputeDirectorySha256(asset.PreservedPath), asset.Sha256, StringComparison.OrdinalIgnoreCase);
                var targetStillMatches = Directory.Exists(asset.Target) &&
                    string.Equals(StorageBackupService.ComputeDirectorySha256(asset.Target), asset.Sha256, StringComparison.OrdinalIgnoreCase);
                return preservedMatches || targetStillMatches;
            });
            if (!databaseAnchorOk || !assetsAnchorOk)
                throw new InvalidOperationException("Cannot replace an unresolved restore intent: its verified safety database or protected asset group is missing or has changed.");
        }
        WriteTransaction(new RestoreTransaction(backupId, DateTimeOffset.UtcNow, "starting", plannedStages, DateTimeOffset.UtcNow, null,
            safetyDatabase, safetyDatabaseSha256, sourceDatabaseSha256, sourceSchemaVersion,
            expectedDatabaseSha256, expectedAssetSha256, protectedAssets, previous));
    }

    /// <summary>推进事务阶段（每个 DB/目录切换边界调用）。</summary>
    public void AdvanceRestoreTransaction(string stage)
    {
        RestoreTransaction? current = ReadTransaction();
        if (current is null) throw new InvalidOperationException("Restore transaction intent is missing; refusing to continue.");
        WriteTransaction(current with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow });
    }

    /// <summary>恢复成功：事务完结（随后进入待重启门）。</summary>
    public void CompleteRestoreTransaction()
        => DeleteTransactionMarker();

    public void RestorePreviousTransaction(RestoreTransaction previous)
        => WriteTransaction(previous);

    /// <summary>事务残留只能在验证完整资产清单后由受控恢复端点清理。</summary>
    public void ClearRestoreTransaction()
        => DeleteTransactionMarker();

    private void DeleteTransactionMarker()
    {
        if (_restoreTransactionPath is { } path && File.Exists(path)) File.Delete(path);
    }

    public RestoreTransaction? ReadTransaction()
    {
        if (_restoreTransactionPath is not { } path || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RestoreTransaction>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public bool HasRestoreTransactionMarker => _restoreTransactionPath is { } path && File.Exists(path);

    private void WriteTransaction(RestoreTransaction transaction)
    {
        if (_restoreTransactionPath is not { } path) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonSerializer.Serialize(transaction));
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }

    private void DeleteMarkerFile(string? path, string label)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not delete the {Label} at {Path}.", label, path);
        }
    }

    /// <summary>
    /// R03：退役一个已提交但残留的恢复事务——清理它声明的保护副本（幂等），然后删除事务文件。
    /// 任何一步失败都只记录（下次启动重试），**绝不锁定主机**：恢复本身已经提交完成，
    /// 残留只是清理未做完（典型原因：文件被短暂占用）。
    /// </summary>
    private static void RetireCommittedTransaction(string path, RestoreTransaction? transaction, ILogger? logger)
    {
        try
        {
            foreach (var asset in transaction?.ProtectedAssets ?? [])
            {
                if (string.IsNullOrWhiteSpace(asset.PreservedPath) || !Directory.Exists(asset.PreservedPath)) continue;
                try { Directory.Delete(asset.PreservedPath, true); }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Committed restore cleanup could not remove preserved path {Path}; it will be retried on the next startup.", asset.PreservedPath);
                }
            }
            File.Delete(path);
            logger?.LogInformation("A committed restore transaction was retired on startup (residual cleanup completed).");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "A committed restore transaction could not be retired on startup; it will be retried next start.");
        }
    }

    public async Task<IAsyncDisposable> BeginExclusiveAsync(string reason, CancellationToken ct, bool allowFailureLockedRestore = false)
    {
        Task wait;
        lock (_sync)
        {
            if (_failureLocked && !allowFailureLockedRestore) throw new ApiConflictException($"Storage is locked after a failed restore ({_failureReason}). Complete recovery or clear the lock before running maintenance.");
            if (_restartPending) throw new ApiConflictException("Storage was restored and is awaiting a process restart; restart the host before running further maintenance.");
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
        // Restart-pending takes precedence over every storage maintenance exception. Authentication
        // and health remain reachable; restore/backup/recover cannot mutate disk while runtime state
        // still reflects the pre-restore generation.
        if (coordinator.IsRestartPending)
        {
            if (IsRestartPendingReachabilityPath(context.Request))
            {
                await next(context);
                return;
            }
            await WriteUnavailableAsync(context, problemDetails, restartPending: true, coordinator.Reason);
            return;
        }
        // F03：受控恢复入口在失败锁激活（或维护进行中）时也必须可达——否则锁定后没有任何受认证
        // 路径可以完成核对并解锁。它自行做状态检查与审计，不持有请求 lease。
        if (IsRecoveryEntrypoint(context.Request))
        {
            await next(context);
            return;
        }
        // Q06：失败锁是**持久**状态（重启后仍在）。只豁免恢复端点还不够——认证与最小健康诊断
        // 也必须可达，否则新浏览器/会话过期/重启后无法登录（login 返回 503），恢复入口形同虚设。
        // 仅放行认证与健康读取；普通业务写入、设备操作继续被维护状态阻断。
        if (coordinator.IsFailureLocked && IsFailureLockReachabilityPath(context.Request))
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

        await WriteUnavailableAsync(context, problemDetails, restartPending: false, coordinator.Reason);
    }

    private static async Task WriteUnavailableAsync(HttpContext context, IProblemDetailsService problemDetails, bool restartPending, string? reason)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = restartPending ? "Restart required" : "Storage maintenance in progress",
            Detail = restartPending
                ? "Storage was restored successfully; the host must be restarted before it can serve requests " +
                  "(on-disk assets and the in-memory runtime would otherwise disagree). Restart the service to complete the recovery."
                : $"The host is temporarily quiesced for storage maintenance ({reason ?? "maintenance"}).",
            Instance = context.Request.Path
        };
        details.Extensions["code"] = restartPending ? "restart_required" : "storage_maintenance";
        details.Extensions["correlationId"] = RequestCorrelation.Get(context);
        await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details });
    }

    private static bool IsRecoveryEntrypoint(HttpRequest request)
        => HttpMethods.IsPost(request.Method) &&
           request.Path.Equals("/api/storage/maintenance/recover", StringComparison.OrdinalIgnoreCase);

    /// <summary>失败锁（无维护 lease 语义）下必须保留的最小可达路径：登录/登出、安全状态、健康读取，
    /// 以及 **受管理员认证的备份清单**（R05：重启后的新浏览器必须能列出并选择可恢复的备份，
    /// 否则恢复入口要求"事先知道 backupId"，现场无法在产品内完成恢复）。</summary>
    private static bool IsFailureLockReachabilityPath(HttpRequest request)
    {
        var path = request.Path;
        if (HttpMethods.IsGet(request.Method))
            return path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/storage/backups", StringComparison.OrdinalIgnoreCase);
        if (HttpMethods.IsPost(request.Method))
            return path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/storage/restore", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>待重启期间仍需可达的路径：认证（登录以查看状态）、健康读取、以及维护端点自查。</summary>
    private static bool IsRestartPendingReachabilityPath(HttpRequest request)
    {
        var path = request.Path;
        if (HttpMethods.IsGet(request.Method))
            return path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase);
        if (HttpMethods.IsPost(request.Method))
            return path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
        return false;
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
