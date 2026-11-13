using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
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
