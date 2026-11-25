using System.IO.Compression;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class StorageBackupService
{
    public async Task<IReadOnlyList<StorageBackupDescriptor>> ListAsync(CancellationToken ct)
    {
        var result = new List<StorageBackupDescriptor>();
        var backups = EnumerateBackupFiles()
            .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(path =>
                string.Equals(Path.GetDirectoryName(path), _backupRoot, StringComparison.OrdinalIgnoreCase)) ?? group.First())
            .OrderByDescending(File.GetCreationTimeUtc);
        foreach (var path in backups)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                var manifest = await ReadManifestAsync(zip, ct);
                result.Add(ToDescriptor(manifest, new FileInfo(path).Length));
            }
            catch { /* one corrupt backup must not hide healthy backup sets */ }
        }
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public bool SecondaryBackupConfigured => _secondaryBackupRoot is not null;

    public async Task<IReadOnlyList<StorageBackupDescriptor>> ListSecondaryAsync(CancellationToken ct)
    {
        if (_secondaryBackupRoot is null) return [];
        if (!Directory.Exists(_secondaryBackupRoot))
            throw new DirectoryNotFoundException($"Configured secondary backup root '{_secondaryBackupRoot}' is not available.");
        var result = new List<StorageBackupDescriptor>();
        foreach (var path in Directory.EnumerateFiles(_secondaryBackupRoot, "*.vsbackup", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetCreationTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                var manifest = await ReadManifestAsync(zip, ct);
                result.Add(ToDescriptor(manifest, new FileInfo(path).Length));
            }
            catch { /* a corrupt secondary copy must not hide other recovery candidates */ }
        }
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task DeleteAsync(string backupId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var path = BackupPath(backupId);
            if (!File.Exists(path)) throw new ApiNotFoundException($"Backup '{backupId}' does not exist.");
            foreach (var candidate in BackupPaths(backupId))
                if (File.Exists(candidate)) File.Delete(candidate);
            await _capacity.RefreshAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> EnforceRetentionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await EnforceRetentionUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    private Task<int> EnforceRetentionUnsafeAsync(CancellationToken ct)
    {
        var keep = Math.Max(1, _options.BackupRetentionCount);
        var deleted = EnforceRetentionAt(_backupRoot, keep, ct);
        if (_secondaryBackupRoot is not null)
            deleted += EnforceRetentionAt(_secondaryBackupRoot, keep, ct);
        return Task.FromResult(deleted);
    }

    private static int EnforceRetentionAt(string root, int keep, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return 0;
        var files = Directory.EnumerateFiles(root, "*.vsbackup", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path)).OrderByDescending(x => x.CreationTimeUtc).ToArray();
        var deleted = 0;
        foreach (var file in files.Skip(keep))
        {
            ct.ThrowIfCancellationRequested();
            try { file.Delete(); deleted++; } catch (IOException) { /* 文件被占用/只读：保留该备份，下一轮再清 */ }
        }
        return deleted;
    }

    private IEnumerable<string> EnumerateBackupFiles()
    {
        if (Directory.Exists(_backupRoot))
            foreach (var path in Directory.EnumerateFiles(_backupRoot, "*.vsbackup", SearchOption.TopDirectoryOnly)) yield return path;
        if (_secondaryBackupRoot is not null && Directory.Exists(_secondaryBackupRoot))
            foreach (var path in Directory.EnumerateFiles(_secondaryBackupRoot, "*.vsbackup", SearchOption.TopDirectoryOnly)) yield return path;
    }

    private async Task MirrorBackupAsync(string sourcePath, CancellationToken ct)
    {
        if (_secondaryBackupRoot is null) return;
        Directory.CreateDirectory(_secondaryBackupRoot);
        var destination = Path.Combine(_secondaryBackupRoot, Path.GetFileName(sourcePath));
        var temp = destination + ".tmp";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, ct);
                await target.FlushAsync(ct);
                target.Flush(flushToDisk: true);
            }
            if (!string.Equals(ComputeFileSha256(sourcePath), ComputeFileSha256(temp), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Secondary backup copy failed SHA-256 verification.");
            File.Move(temp, destination, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* 临时镜像文件清理失败不影响主流程告警 */ }
            throw new IOException($"Primary backup '{Path.GetFileName(sourcePath)}' was created, but its configured secondary copy could not be verified.", ex);
        }
    }

    private IEnumerable<string> BackupPaths(string id)
    {
        yield return Path.Combine(_backupRoot, id + ".vsbackup");
        if (_secondaryBackupRoot is not null)
            yield return Path.Combine(_secondaryBackupRoot, id + ".vsbackup");
    }

    private static bool IsPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private string ResolveBackupPath(string id)
    {
        var local = Path.Combine(_backupRoot, id + ".vsbackup");
        if (File.Exists(local)) return local;
        if (_secondaryBackupRoot is not null)
        {
            var secondary = Path.Combine(_secondaryBackupRoot, id + ".vsbackup");
            if (File.Exists(secondary)) return secondary;
        }
        return local;
    }
}
