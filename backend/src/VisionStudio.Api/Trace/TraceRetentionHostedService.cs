using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed class TraceRetentionHostedService : BackgroundService
{
    /// <summary>Runs older than this without finalization are treated as orphaned by periodic maintenance.</summary>
    private static readonly TimeSpan OrphanedRunAge = TimeSpan.FromHours(1);

    private readonly TraceabilityStore _traces;
    private readonly TraceRetentionOptions _options;
    private readonly ILogger<TraceRetentionHostedService> _logger;
    private readonly StorageMaintenanceCoordinator _maintenance;

    public TraceRetentionHostedService(TraceabilityStore traces, IOptions<TraceRetentionOptions> options, ILogger<TraceRetentionHostedService> logger, StorageMaintenanceCoordinator maintenance)
    {
        _traces = traces;
        _options = options.Value;
        _logger = logger;
        _maintenance = maintenance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup reconciliation: a row still "Running" belongs to a previous process that never finalized it.
        try
        {
            var interrupted = await _traces.SweepStaleRunsAsync(TimeSpan.Zero, stoppingToken);
            if (interrupted > 0) _logger.LogWarning("Marked {Count} interrupted run trace(s) as Unknown after startup.", interrupted);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation of running trace records failed.");
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var maintenanceLease = _maintenance.TryEnterRequest();
                if (maintenanceLease is null) continue;
                var orphaned = await _traces.SweepStaleRunsAsync(OrphanedRunAge, stoppingToken);
                if (orphaned > 0) _logger.LogWarning("Marked {Count} orphaned run trace(s) as Unknown.", orphaned);
                var changed = await _traces.CleanupAsync(stoppingToken);
                if (changed > 0) _logger.LogInformation("Trace retention cleanup changed {Count} records/artifacts.", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trace retention cleanup failed.");
            }
        }
    }
}
