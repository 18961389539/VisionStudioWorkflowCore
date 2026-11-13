using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

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
