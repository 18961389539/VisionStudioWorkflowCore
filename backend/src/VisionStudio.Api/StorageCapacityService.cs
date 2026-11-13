using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

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
                catch (IOException) { /* 单个文件被占用/消失：跳过 */ }
                catch (UnauthorizedAccessException) { /* 单个文件无权限：跳过 */ }
            }
        }
        catch (IOException) { /* 目录枚举失败：返回已统计部分 */ }
        catch (UnauthorizedAccessException) { /* 目录无权限：返回已统计部分 */ }
        return total;
    }
}
