using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Storage.EfCore;

namespace VisionStudio.Api;

/// <summary>
/// Asynchronous runs (dataset validation, camera-sync commissioning, plugin benchmark) are executed by
/// in-process workers and only their rows outlive the process.
/// </summary>
public sealed class InterruptedRunRecovery(
    IVisionStudioDbFactory dbFactory,
    ILogger<InterruptedRunRecovery> logger)
{
    public InterruptedRunRecovery(SqliteMetadataDatabase database, ILogger<InterruptedRunRecovery> logger)
        : this(new VisionStudioDbFactory(database), logger) { }

    public const string Status = "Interrupted";

    internal const string Reason =
        "Host restarted before this run finished; the run no longer has an owner and its results are partial.";

    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var recovered = 0;

        recovered += await db.ValidationRuns
            .Where(x => x.Status == "Running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, Status)
                .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? completedAt)
                .SetProperty(x => x.Error, x => x.Error ?? Reason), cancellationToken);

        recovered += await db.CameraSyncCommissioningTests
            .Where(x => x.Status == "Queued" || x.Status == "Running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, Status)
                .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? completedAt)
                .SetProperty(x => x.Error, x => x.Error ?? Reason), cancellationToken);

        recovered += await db.PluginBenchmarkRuns
            .Where(x => x.Status == "Running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, Status)
                .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? completedAt)
                .SetProperty(x => x.Error, x => x.Error ?? Reason), cancellationToken);

        if (recovered > 0)
        {
            logger.LogWarning(
                "Marked {Count} unfinished run(s) from a previous host as '{Status}'; their datasets and groups are usable again.",
                recovered, Status);
        }

        return recovered;
    }
}

/// <summary>
/// Runs recovery first (after storage migration, before anything can start a new run) so the API never
/// serves a 'Running' row that this process does not own.
/// </summary>
public sealed class InterruptedRunRecoveryHostedService(InterruptedRunRecovery recovery) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
        => await recovery.RecoverAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
