using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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
            // R07：清理会删除附件文件。持共享附件写租约 → 备份冻结窗口（排他）期间不执行，
            // 从而杜绝"快照已含某 run 的路径、清理随后删掉该文件"的悬空引用。
            // 备份窗口进行中直接跳过本轮清理（下次宿主 tick 会重试）。
            using var artifactWrite = _maintenance?.TryEnterArtifactWrite();
            if (_maintenance is not null && artifactWrite is null) return 0;

            var changed = 0;
            await using var connection = await _db.OpenConnectionAsync(ct);

        var artifacts = new List<(string RunId, DateTimeOffset StartedAt, string Disposition, string RelativePath)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,started_at,disposition,preview_relative_path FROM run_traces WHERE has_preview=1 AND preview_relative_path IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) artifacts.Add((reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }

        foreach (var item in artifacts)
        {
            if (!ArtifactExpired(item.StartedAt, item.Disposition)) continue;
            var path = ArtifactPath(item.RelativePath);
            try { if (File.Exists(path)) File.Delete(path); } catch { continue; }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_traces SET has_preview=0, preview_relative_path=NULL, preview_bytes=0 WHERE run_id=$run;";
            update.Parameters.AddWithValue("$run", item.RunId);
            changed += await update.ExecuteNonQueryAsync(ct);
            RemoveEmptyParents(Path.GetDirectoryName(path));
        }

        var replayArtifacts = new List<(string RunId, DateTimeOffset StartedAt, string Disposition, string RelativePath)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,started_at,disposition,replay_relative_path FROM run_traces WHERE has_replay_input=1 AND replay_relative_path IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) replayArtifacts.Add((reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }
        foreach (var item in replayArtifacts)
        {
            if (!ArtifactExpired(item.StartedAt, item.Disposition)) continue;
            var path = ArtifactPath(item.RelativePath);
            try { if (File.Exists(path)) File.Delete(path); } catch { continue; }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_traces SET has_replay_input=0, replay_relative_path=NULL, replay_bytes=0, replay_source_node_id=NULL WHERE run_id=$run;";
            update.Parameters.AddWithValue("$run", item.RunId);
            changed += await update.ExecuteNonQueryAsync(ct);
            RemoveEmptyParents(Path.GetDirectoryName(path));
        }

        if (_retention.MetadataRetentionDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_retention.MetadataRetentionDays);
            var expiredPaths = new List<string>();
            await using (var paths = connection.CreateCommand())
            {
                paths.CommandText = "SELECT artifact_path FROM (SELECT preview_relative_path AS artifact_path FROM run_traces WHERE started_at < $cutoff AND preview_relative_path IS NOT NULL UNION ALL SELECT replay_relative_path AS artifact_path FROM run_traces WHERE started_at < $cutoff AND replay_relative_path IS NOT NULL);";
                paths.Parameters.AddWithValue("$cutoff", Iso(cutoff));
                await using var reader = await paths.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) expiredPaths.Add(reader.GetString(0));
            }
            foreach (var relative in expiredPaths)
            {
                var path = ArtifactPath(relative);
                try { if (File.Exists(path)) File.Delete(path); RemoveEmptyParents(Path.GetDirectoryName(path)); } catch { /* 过期附件删除失败：下一轮清理重试 */ }
            }
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM run_traces WHERE started_at < $cutoff;";
            delete.Parameters.AddWithValue("$cutoff", Iso(cutoff));
            changed += await delete.ExecuteNonQueryAsync(ct);
        }

            if (changed > 0)
            {
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                await checkpoint.ExecuteNonQueryAsync(ct);
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
        // 生产循环（ProductionRuntime）的 OK trace 按采样留存 replay 素材以控制磁盘占用；
        // 交互式运行（ad-hoc / debug / 手动作业）是用户主动留下、供数据集素材与离线重放使用的
        // 证据链，一律持久化——采样曾将其一并剔除，导致参数调优的数据集引用被 409 拒绝。
        => source.Equals("ProductionRuntime", StringComparison.OrdinalIgnoreCase)
            ? RunArtifactSampling.Include(runId, disposition, _retention.OkReplaySampleEvery, replay: true)
            : true;

    public RunArtifactOptions GetArtifactOptions(long maxRawBytes)
        => new(DeferEncoding: true, PreviewSampleEvery: _retention.OkPreviewSampleEvery,
            ReplaySampleEvery: _retention.OkReplaySampleEvery, MaxRawBytes: maxRawBytes);

    private async Task<RunTraceRecord> PersistReplayInputAsync(RunTraceRecord record, ReplayInputArtifact replay, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (_capacity is not null && !_capacity.CanPersistArtifact(replay.Png.LongLength)) return record;
        // N01：CanPersistArtifact 通过即已占用容量预留。与 preview 分支对称——所有提前返回路径
        // 都必须归还预留，否则冻结窗口反复出现时预留泄漏会累积成虚假占用。
        var capacityReservation = _capacity is not null ? replay.Png.LongLength : 0L;

        // R07：与 preview 同理，持共享附件写租约；备份冻结窗口内跳过落盘（保留素材可重生成/重新采集）。
        // N01：跳过路径归还容量预留，并把跳过原因写入数据库（与 preview 分支一致），保证历史记录可诊断。
        using var artifactWrite = _maintenance?.TryEnterArtifactWrite();
        if (_maintenance is not null && artifactWrite is null)
        {
            const string snapshotNote = "Replay input skipped because a storage backup snapshot is in progress.";
            Interlocked.Increment(ref _snapshotSkippedReplays);
            try
            {
                await using var noteConnection = await _db.OpenConnectionAsync(CancellationToken.None);
                await using var noteUpdate = noteConnection.CreateCommand();
                noteUpdate.CommandText = "UPDATE run_traces SET note=COALESCE(note || ' | ','') || $note WHERE run_id=$run;";
                noteUpdate.Parameters.AddWithValue("$note", snapshotNote);
                noteUpdate.Parameters.AddWithValue("$run", record.RunId);
                await noteUpdate.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { /* 备份快照备注写入失败：返回值仍携带跳过原因 */ }
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

            await using var connection = await _db.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE run_traces SET has_replay_input=1,replay_relative_path=$path,replay_bytes=$bytes,replay_source_node_id=$source WHERE run_id=$run;";
            update.Parameters.AddWithValue("$path", relative);
            update.Parameters.AddWithValue("$bytes", replay.Png.LongLength);
            update.Parameters.AddWithValue("$source", replay.SourceNodeId);
            update.Parameters.AddWithValue("$run", record.RunId);
            await update.ExecuteNonQueryAsync(ct);
            return record with { HasReplayInput = true, ReplaySourceNodeId = replay.SourceNodeId };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            try { if (File.Exists(absolute)) File.Delete(absolute); } catch { /* 回放残留文件清理失败：已记录持久化失败备注 */ }
            var note = $"Replay input persistence failed: {ex.Message}";
            try
            {
                await using var connection = await _db.OpenConnectionAsync(CancellationToken.None);
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE run_traces SET note=COALESCE(note || ' | ','') || $note WHERE run_id=$run;";
                update.Parameters.AddWithValue("$note", note);
                update.Parameters.AddWithValue("$run", record.RunId);
                await update.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { /* 回放失败备注写入失败：返回值仍携带原因 */ }
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
