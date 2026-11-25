using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class TraceabilityStore
{
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        await _cleanupGate.WaitAsync(ct);
        try
        {
            using var artifactWrite = _maintenance?.TryEnterArtifactWrite();
            if (_maintenance is not null && artifactWrite is null) return 0;

            var changed = 0;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var artifacts = await db.RunTraces
                .Where(x => x.HasPreview && x.PreviewRelativePath != null)
                .Select(x => new { x.RunId, x.StartedAt, x.Disposition, RelativePath = x.PreviewRelativePath! })
                .ToListAsync(ct);

            foreach (var item in artifacts)
            {
                if (!ArtifactExpired(item.StartedAt, item.Disposition)) continue;
                var path = ArtifactPath(item.RelativePath);
                try { if (File.Exists(path)) File.Delete(path); } catch { continue; }
                changed += await db.RunTraces
                    .Where(x => x.RunId == item.RunId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.HasPreview, false)
                        .SetProperty(x => x.PreviewRelativePath, (string?)null)
                        .SetProperty(x => x.PreviewBytes, 0L), ct);
                RemoveEmptyParents(Path.GetDirectoryName(path));
            }

            var replayArtifacts = await db.RunTraces
                .Where(x => x.HasReplayInput && x.ReplayRelativePath != null)
                .Select(x => new { x.RunId, x.StartedAt, x.Disposition, RelativePath = x.ReplayRelativePath! })
                .ToListAsync(ct);

            foreach (var item in replayArtifacts)
            {
                if (!ArtifactExpired(item.StartedAt, item.Disposition)) continue;
                var path = ArtifactPath(item.RelativePath);
                try { if (File.Exists(path)) File.Delete(path); } catch { continue; }
                changed += await db.RunTraces
                    .Where(x => x.RunId == item.RunId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.HasReplayInput, false)
                        .SetProperty(x => x.ReplayRelativePath, (string?)null)
                        .SetProperty(x => x.ReplayBytes, 0L)
                        .SetProperty(x => x.ReplaySourceNodeId, (string?)null), ct);
                RemoveEmptyParents(Path.GetDirectoryName(path));
            }

            if (_retention.MetadataRetentionDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-_retention.MetadataRetentionDays);
                var expiredRows = await db.RunTraces
                    .Where(x => x.StartedAt < cutoff && (x.PreviewRelativePath != null || x.ReplayRelativePath != null))
                    .Select(x => new { x.PreviewRelativePath, x.ReplayRelativePath })
                    .ToListAsync(ct);
                foreach (var row in expiredRows)
                {
                    foreach (var relative in new[] { row.PreviewRelativePath, row.ReplayRelativePath })
                    {
                        if (string.IsNullOrWhiteSpace(relative)) continue;
                        var path = ArtifactPath(relative);
                        try { if (File.Exists(path)) File.Delete(path); RemoveEmptyParents(Path.GetDirectoryName(path)); } catch { /* 过期附件删除失败：下一轮清理重试 */ }
                    }
                }
                changed += await db.RunTraces.Where(x => x.StartedAt < cutoff).ExecuteDeleteAsync(ct);
            }

            if (changed > 0)
            {
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(PASSIVE);", ct);
            }

            return changed;
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    private bool ShouldPersistPreview(string runId, string disposition)
        => RunArtifactSampling.Include(runId, disposition, _retention.OkPreviewSampleEvery);

    private bool ShouldPersistReplayInput(string runId, string disposition, string source)
        => source.Equals("ProductionRuntime", StringComparison.OrdinalIgnoreCase)
            ? RunArtifactSampling.Include(runId, disposition, _retention.OkReplaySampleEvery, replay: true)
            : true;

    public RunArtifactOptions GetArtifactOptions(long maxRawBytes)
        => new(DeferEncoding: true, PreviewSampleEvery: _retention.OkPreviewSampleEvery,
            ReplaySampleEvery: _retention.OkReplaySampleEvery, MaxRawBytes: maxRawBytes);

    private async Task<RunTraceRecord> PersistReplayInputAsync(RunTraceRecord record, ReplayInputArtifact replay, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (_capacity is not null && !_capacity.CanPersistArtifact(replay.Png.LongLength)) return record;
        var capacityReservation = _capacity is not null ? replay.Png.LongLength : 0L;

        using var artifactWrite = _maintenance?.TryEnterArtifactWrite();
        if (_maintenance is not null && artifactWrite is null)
        {
            const string snapshotNote = "Replay input skipped because a storage backup snapshot is in progress.";
            Interlocked.Increment(ref _snapshotSkippedReplays);
            await AppendTraceNoteAsync(record.RunId, snapshotNote);
            if (capacityReservation > 0) _capacity?.ReleaseArtifactReservation(capacityReservation);
            return record with { Note = string.IsNullOrWhiteSpace(record.Note) ? snapshotNote : record.Note + " | " + snapshotNote };
        }

        var relative = ReplayRelativePath(startedAt, record.RunId);
        var absolute = ArtifactPath(relative);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            temp = absolute + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, replay.Png, ct);
            File.Move(temp, absolute, true);
            temp = null;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.RunTraces
                .Where(x => x.RunId == record.RunId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.HasReplayInput, true)
                    .SetProperty(x => x.ReplayRelativePath, relative)
                    .SetProperty(x => x.ReplayBytes, replay.Png.LongLength)
                    .SetProperty(x => x.ReplaySourceNodeId, replay.SourceNodeId), ct);
            return record with { HasReplayInput = true, ReplaySourceNodeId = replay.SourceNodeId };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or DbUpdateException)
        {
            try { if (File.Exists(absolute)) File.Delete(absolute); } catch { /* 回放残留文件清理失败：已记录持久化失败备注 */ }
            var note = $"Replay input persistence failed: {ex.Message}";
            await AppendTraceNoteAsync(record.RunId, note);
            return record with { Note = string.IsNullOrWhiteSpace(record.Note) ? note : record.Note + " | " + note };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp))
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 临时回放文件清理失败：不影响主流程 */ }
            }
            if (capacityReservation > 0) _capacity?.ReleaseArtifactReservation(capacityReservation);
        }
    }

    private bool ArtifactExpired(DateTimeOffset startedAt, string disposition)
    {
        var days = disposition.ToUpperInvariant() switch
        {
            "NG" => _retention.NgArtifactRetentionDays,
            "ERROR" => _retention.ErrorArtifactRetentionDays,
            "REVIEW" => _retention.ReviewArtifactRetentionDays,
            _ => _retention.OkArtifactRetentionDays
        };
        return days >= 0 && startedAt < DateTimeOffset.UtcNow.AddDays(-days);
    }

    private string PreviewRelativePath(DateTimeOffset at, string runId)
        => Path.Combine("traces", at.UtcDateTime.ToString("yyyy-MM-dd"), Sanitize(runId), "preview.jpg");

    private string ReplayRelativePath(DateTimeOffset at, string runId)
        => Path.Combine("traces", at.UtcDateTime.ToString("yyyy-MM-dd"), Sanitize(runId), "replay-input.png");

    private string ArtifactPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_artifactRoot, relative));
        var root = Path.GetFullPath(_artifactRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact path escaped storage root.");
        return path;
    }

    private void RemoveEmptyParents(string? directory)
    {
        var root = Path.GetFullPath(_artifactRoot).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) break;
            try
            {
                if (Directory.EnumerateFileSystemEntries(full).Any()) break;
                Directory.Delete(full);
            }
            catch { break; }
            directory = Path.GetDirectoryName(full);
        }
    }
}
