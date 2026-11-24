using System.Globalization;

namespace VisionStudio.Api;

public sealed record ProductionOperatorPreview(string RunId, DateTimeOffset StartedAt, string Disposition, int JobVersion);

public sealed partial class TraceabilityStore
{
    internal async Task<ProductionOperatorPreview?> GetOperatorPreviewAsync(string jobId, int jobVersion,
        DateTimeOffset sessionStartedAt, CancellationToken ct, bool ngOnly = false)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT run_id,started_at,disposition
FROM run_traces
WHERE job_id=$job AND job_version=$version AND source='ProductionRuntime'
  AND started_at >= $started AND has_preview=1
""" + (ngOnly ? "\nAND disposition='NG' COLLATE NOCASE" : "") + "\nORDER BY started_at DESC,run_id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$version", jobVersion);
        command.Parameters.AddWithValue("$started", sessionStartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ProductionOperatorPreview(reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2).ToUpperInvariant(), jobVersion);
    }
}
