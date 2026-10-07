using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
    private readonly string _artifactRoot;
    private readonly string _backupRoot;
    private readonly SqliteMetadataDatabase _database;
    private readonly StorageMaintenanceOptions _options;
    private StorageCapacityStatus? _last;
    private long _artifactReservations;

    public StorageCapacityService(IWebHostEnvironment env, SqliteMetadataDatabase database, IOptions<StorageMaintenanceOptions> options)
    {
        _contentRoot = Path.GetFullPath(env.ContentRootPath);
        _artifactRoot = Path.Combine(_contentRoot, "data", "artifacts");
        _backupRoot = Path.Combine(_contentRoot, "data", "backups");
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

    public bool IsMaintenanceActive { get { lock (_sync) return _maintenance; } }
    public string? Reason { get { lock (_sync) return _reason; } }

    public IDisposable? TryEnterRequest()
    {
        lock (_sync)
        {
            if (_maintenance) return null;
            _activeRequests++;
            return new RequestLease(this);
        }
    }

    public async Task<IAsyncDisposable> BeginExclusiveAsync(string reason, CancellationToken ct)
    {
        Task wait;
        lock (_sync)
        {
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
            _maintenance = false;
            _reason = null;
            _drained = null;
        }
    }

    private sealed class RequestLease(StorageMaintenanceCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ExitRequest(); }
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
        // The first restore request must not count itself or it would deadlock while acquiring
        // exclusive mode. Once maintenance is active, later restore requests are rejected too.
        var isRestore = context.Request.Path.StartsWithSegments("/api/storage/restore");
        if (isRestore && !coordinator.IsMaintenanceActive) { await next(context); return; }

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
        details.Extensions["correlationId"] = context.TraceIdentifier;
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
