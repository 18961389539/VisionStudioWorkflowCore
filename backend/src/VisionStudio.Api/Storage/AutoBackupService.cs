using Microsoft.Extensions.Options;

namespace VisionStudio.Api;

/// <summary>
/// 每日自动存储备份（P1）：备份机制/保留策略与手工备份共用 <see cref="StorageBackupService"/>，
/// 这里只负责"到期判定 + 周期触发 + 新鲜度状态"。默认不打包附件（体积小、频次高），
/// 需要附件时改 <c>Storage:AutoBackup:IncludeArtifacts</c> 或改用手工备份。
/// </summary>
public sealed class AutoBackupOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int CheckIntervalMinutes { get; set; } = 30;
    public int StartupDelayMinutes { get; set; } = 10;
    /// <summary>最近一次备份（含手工备份）超过该时长即视为"不新鲜"；仅用于健康可见性，不阻断就绪。</summary>
    public int StaleAfterHours { get; set; } = 48;
    public bool IncludeArtifacts { get; set; } = false;
}

/// <summary>
/// 备份新鲜度快照。Latest* 来自备份目录（手工与自动备份同一口径），Last*/NextCheckAt 来自本进程调度。
/// </summary>
public sealed record AutoBackupStatus(
    DateTimeOffset? LatestBackupAt,
    string? LatestBackupId,
    bool? LatestBackupIncludesArtifacts,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextCheckAt,
    string? LastError,
    string? ListError,
    DateTimeOffset RefreshedAt);

public sealed class AutoBackupService : BackgroundService
{
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(30);

    private readonly StorageBackupService _backups;
    private readonly StorageMaintenanceCoordinator _maintenance;
    private readonly AutoBackupOptions _options;
    private readonly ILogger<AutoBackupService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleAfter;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _sync = new();
    private AutoBackupStatus _status = new(null, null, null, null, null, null, null, DateTimeOffset.MinValue);

    public AutoBackupService(
        StorageBackupService backups,
        StorageMaintenanceCoordinator maintenance,
        IOptions<AutoBackupOptions> options,
        ILogger<AutoBackupService> logger)
    {
        _backups = backups;
        _maintenance = maintenance;
        _options = options.Value;
        _logger = logger;
        _interval = TimeSpan.FromHours(Math.Clamp(_options.IntervalHours, 1, 24 * 30));
        _staleAfter = TimeSpan.FromHours(Math.Clamp(_options.StaleAfterHours, 1, 24 * 365));
    }

    public bool Enabled => _options.Enabled;
    public int IntervalHours => (int)_interval.TotalHours;
    public int StaleAfterHours => (int)_staleAfter.TotalHours;
    public AutoBackupStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// 健康端点读取入口：短 TTL 缓存避免每次探测都解包全部备份清单；刷新失败只记录在
    /// <see cref="AutoBackupStatus.ListError"/>，绝不让健康端点整体失败。
    /// </summary>
    public async Task<AutoBackupStatus> GetStatusAsync(CancellationToken ct)
    {
        var status = Status;
        if (DateTimeOffset.UtcNow - status.RefreshedAt < StatusCacheTtl) return status;
        await _refreshGate.WaitAsync(ct);
        try
        {
            status = Status;
            if (DateTimeOffset.UtcNow - status.RefreshedAt < StatusCacheTtl) return status;
            return await RefreshLatestAsync(ct);
        }
        finally { _refreshGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 即使自动备份被禁用也先刷新一次：健康端点仍需回答"最近一次备份是什么时候"。
        await RefreshLatestAsync(stoppingToken);
        if (!_options.Enabled)
        {
            _logger.LogInformation("Automatic storage backups are disabled (Storage:AutoBackup:Enabled=false); backup freshness is still reported on /api/health.");
            return;
        }

        try
        {
            var startupDelay = TimeSpan.FromMinutes(Math.Clamp(_options.StartupDelayMinutes, 0, 24 * 60));
            if (startupDelay > TimeSpan.Zero) await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        var checkInterval = TimeSpan.FromMinutes(Math.Clamp(_options.CheckIntervalMinutes, 1, 24 * 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            Update(s => s with { NextCheckAt = DateTimeOffset.UtcNow + checkInterval });
            try
            {
                await RunOnceAsync(force: false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // RunOnceAsync 已记录业务失败；这里兜底"判定/列表"层面的异常，调度不能停摆。
                _logger.LogWarning(ex, "Automatic storage backup cycle failed; retrying in {Minutes} minutes.", checkInterval.TotalMinutes);
            }
            try { await Task.Delay(checkInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 执行一次到期检查（force=true 跳过判定，用于测试/手动触发）。返回新建的备份描述；
    /// 未到期或维护状态下跳过时返回 null。备份自身失败记录 LastError 后返回 null。
    /// </summary>
    internal async Task<StorageBackupDescriptor?> RunOnceAsync(bool force, CancellationToken ct)
    {
        // Enabled=false 是"不自动创建备份"的硬开关；手工备份入口不受影响。
        if (!_options.Enabled) return null;

        var latest = force ? Status : await RefreshLatestAsync(ct);
        if (!force && !IsDue(DateTimeOffset.UtcNow, latest.LatestBackupAt, _interval))
            return null;

        // 维护/失败锁/待重启期间创建备份会与恢复或既有维护流竞争（BeginExclusiveAsync 也会拒绝）；
        // 跳过本周期，下一个检查间隔再试。
        if (_maintenance.IsFailureLocked || _maintenance.IsRestartPending || _maintenance.IsMaintenanceActive)
        {
            _logger.LogInformation("Automatic storage backup skipped: storage maintenance state is active ({Reason}).", _maintenance.Reason);
            return null;
        }

        Update(s => s with { LastAttemptAt = DateTimeOffset.UtcNow });
        try
        {
            var created = await _backups.CreateAsync(_options.IncludeArtifacts, ct);
            Update(s => s with
            {
                LatestBackupAt = created.CreatedAt,
                LatestBackupId = created.BackupId,
                LatestBackupIncludesArtifacts = created.IncludesArtifacts,
                LastError = null,
                RefreshedAt = DateTimeOffset.UtcNow
            });
            _logger.LogInformation(
                "Automatic storage backup {BackupId} created ({ArchiveBytes} bytes, artifacts included: {IncludesArtifacts}).",
                created.BackupId, created.ArchiveBytes, created.IncludesArtifacts);
            return created;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Update(s => s with { LastError = ex.Message });
            _logger.LogWarning(ex, "Automatic storage backup failed.");
            return null;
        }
    }

    private async Task<AutoBackupStatus> RefreshLatestAsync(CancellationToken ct)
    {
        try
        {
            var latest = (await _backups.ListAsync(ct)).FirstOrDefault();
            return Update(s => s with
            {
                LatestBackupAt = latest?.CreatedAt,
                LatestBackupId = latest?.BackupId,
                LatestBackupIncludesArtifacts = latest?.IncludesArtifacts,
                ListError = null,
                RefreshedAt = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Existing backups could not be listed for freshness reporting.");
            return Update(s => s with { ListError = ex.Message, RefreshedAt = DateTimeOffset.UtcNow });
        }
    }

    private AutoBackupStatus Update(Func<AutoBackupStatus, AutoBackupStatus> update)
    {
        lock (_sync)
        {
            _status = update(_status);
            return _status;
        }
    }

    /// <summary>到期判定：从未备份或距最近一次（任意来源）备份已超过间隔。</summary>
    public static bool IsDue(DateTimeOffset now, DateTimeOffset? latestBackupAt, TimeSpan interval)
        => latestBackupAt is null || now - latestBackupAt >= interval;

    /// <summary>新鲜度判定：从未备份或已超过陈旧阈值。仅供健康披露，不参与 ready/productionReady。</summary>
    public static bool IsStale(DateTimeOffset now, DateTimeOffset? latestBackupAt, TimeSpan staleAfter)
        => latestBackupAt is null || now - latestBackupAt > staleAfter;
}