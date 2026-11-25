using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

public sealed record ProductionOperatorPreview(string RunId, DateTimeOffset StartedAt, string Disposition, int JobVersion);

public sealed partial class TraceabilityStore
{
    internal async Task<ProductionOperatorPreview?> GetOperatorPreviewAsync(string jobId, int jobVersion,
        DateTimeOffset sessionStartedAt, CancellationToken ct, bool ngOnly = false, string source = "ProductionRuntime")
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<RunTraceEntity> query = db.RunTraces
            .Where(x => x.JobId == jobId && x.JobVersion == jobVersion && x.Source == source &&
                        x.StartedAt >= sessionStartedAt && x.HasPreview);
        if (ngOnly)
            query = query.Where(x => EF.Functions.Collate(x.Disposition, "NOCASE") == "NG");

        var hit = await query
            .OrderByDescending(x => x.StartedAt)
            .ThenByDescending(x => x.RunId)
            .Select(x => new { x.RunId, x.StartedAt, x.Disposition })
            .FirstOrDefaultAsync(ct);

        return hit is null ? null : new ProductionOperatorPreview(hit.RunId, hit.StartedAt, hit.Disposition.ToUpperInvariant(), jobVersion);
    }
}
