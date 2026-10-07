using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record StorageBackupManifest(
    int FormatVersion,
    string BackupId,
    DateTimeOffset CreatedAt,
    string VisionStudioVersion,
    int SchemaVersion,
    string DatabaseSha256,
    bool IncludesArtifacts,
    int ArtifactCount,
    long ArtifactBytes);

public sealed record StorageBackupDescriptor(
    string BackupId,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    long ArchiveBytes,
    bool IncludesArtifacts,
    int ArtifactCount,
    long ArtifactBytes);

public sealed record StorageRestoreRequest(string BackupId, bool RestoreArtifacts = true);
public sealed record StorageRestoreResult(string BackupId, int SourceSchemaVersion, int CurrentSchemaVersion, bool ArtifactsRestored, bool RestartRecommended);

/// <summary>Creates and restores bounded, local storage backup bundles. No arbitrary filesystem path is accepted by the API.</summary>
public sealed class StorageBackupService
{
    public const int BackupFormatVersion = 1;
    private readonly SqliteMetadataDatabase _database;
    private readonly StorageCapacityService _capacity;
    private readonly StorageMaintenanceCoordinator _maintenance;
    private readonly ProductionRuntimeService _production;
    private readonly StorageMaintenanceOptions _options;
    private readonly string _artifactRoot;
    private readonly string _backupRoot;
    private readonly string _tempRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StorageBackupService(
        SqliteMetadataDatabase database,
        StorageCapacityService capacity,
        StorageMaintenanceCoordinator maintenance,
        ProductionRuntimeService production,
        IWebHostEnvironment env,
        IOptions<StorageMaintenanceOptions> options)
    {
        _database = database;
        _capacity = capacity;
        _maintenance = maintenance;
        _production = production;
        _options = options.Value;
        var data = Path.Combine(env.ContentRootPath, "data");
        _artifactRoot = Path.Combine(data, "artifacts");
        _backupRoot = Path.Combine(data, "backups");
        _tempRoot = Path.Combine(data, ".storage-maintenance");
        Directory.CreateDirectory(_artifactRoot);
        Directory.CreateDirectory(_backupRoot);
        Directory.CreateDirectory(_tempRoot);
    }

    public async Task<IReadOnlyList<StorageBackupDescriptor>> ListAsync(CancellationToken ct)
    {
        var result = new List<StorageBackupDescriptor>();
        foreach (var path in Directory.EnumerateFiles(_backupRoot, "*.vsbackup", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetCreationTimeUtc))
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

    public async Task<StorageBackupDescriptor> CreateAsync(bool? includeArtifacts, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var workspace = Path.Combine(_tempRoot, $"backup-{Guid.NewGuid():N}");
        try
        {
            var include = includeArtifacts ?? _options.IncludeArtifactsInBackup;
            var capacity = await _capacity.RefreshAsync(ct);
            var estimated = capacity.DatabaseBytes + (include ? capacity.ArtifactBytes : 0L);
            await _capacity.EnsureBackupCapacityAsync(estimated, ct);

            Directory.CreateDirectory(workspace);
            var dbSnapshot = Path.Combine(workspace, "visionstudio.db");
            await _database.CreateSnapshotAsync(dbSnapshot, ct);
            var validation = await SqliteMetadataDatabase.ValidateSnapshotAsync(dbSnapshot, ct);
            if (!validation.Ok) throw new InvalidOperationException($"Backup database integrity check failed: {validation.Result}");

            var id = $"vs-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..8]}";
            var finalPath = BackupPath(id);
            var tempArchive = finalPath + ".tmp";
            if (File.Exists(tempArchive)) File.Delete(tempArchive);

            var artifactFiles = include && Directory.Exists(_artifactRoot)
                ? Directory.EnumerateFiles(_artifactRoot, "*", SearchOption.AllDirectories).ToArray()
                : [];
            var artifactBytes = artifactFiles.Sum(path => new FileInfo(path).Length);
            var manifest = new StorageBackupManifest(
                BackupFormatVersion, id, DateTimeOffset.UtcNow, "0.31", validation.SchemaVersion,
                await Sha256FileAsync(dbSnapshot, ct), include, artifactFiles.Length, artifactBytes);

            await using (var output = File.Create(tempArchive))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                zip.CreateEntryFromFile(dbSnapshot, "database/visionstudio.db", CompressionLevel.Optimal);
                foreach (var artifact in artifactFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(_artifactRoot, artifact).Replace('\\', '/');
                    zip.CreateEntryFromFile(artifact, $"artifacts/{relative}", CompressionLevel.Optimal);
                }
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, _json, ct);
            }
            File.Move(tempArchive, finalPath, true);
            try { await EnforceRetentionUnsafeAsync(ct); } catch { /* backup itself is already durable */ }
            try { await _capacity.RefreshAsync(ct); } catch { /* telemetry refresh is best-effort */ }
            return ToDescriptor(manifest, new FileInfo(finalPath).Length);
        }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { }
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string backupId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var path = BackupPath(backupId);
            if (!File.Exists(path)) throw new ApiNotFoundException($"Backup '{backupId}' does not exist.");
            File.Delete(path);
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
        var files = Directory.EnumerateFiles(_backupRoot, "*.vsbackup", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path)).OrderByDescending(x => x.CreationTimeUtc).ToArray();
        var deleted = 0;
        foreach (var file in files.Skip(keep))
        {
            ct.ThrowIfCancellationRequested();
            try { file.Delete(); deleted++; } catch (IOException) { }
        }
        return Task.FromResult(deleted);
    }

    public async Task<StorageRestoreResult> RestoreAsync(StorageRestoreRequest request, CancellationToken ct)
    {
        if (_production.Status.State != ProductionRuntimeState.Stopped)
            throw new ApiConflictException("Production Runtime must be Stopped before restoring storage.");

        // Do not hold the backup gate while waiting for in-flight API requests to drain: an
        // already-running backup/delete request may legitimately be waiting for that same gate.
        await using var maintenance = await _maintenance.BeginExclusiveAsync("storage restore", ct);
        if (_production.Status.State != ProductionRuntimeState.Stopped)
            throw new ApiConflictException("Production Runtime changed state while storage restore was waiting for maintenance mode.");

        await _gate.WaitAsync(ct);
        var workspace = Path.Combine(_tempRoot, $"restore-{Guid.NewGuid():N}");
        try
        {
            var archivePath = BackupPath(request.BackupId);
            if (!File.Exists(archivePath)) throw new ApiNotFoundException($"Backup '{request.BackupId}' does not exist.");
            Directory.CreateDirectory(workspace);
            var incomingDb = Path.Combine(workspace, "incoming.db");
            var stagedArtifacts = Path.Combine(workspace, "artifacts");
            StorageBackupManifest manifest;

            await using (var input = File.OpenRead(archivePath))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false))
            {
                manifest = await ReadManifestAsync(zip, ct);
                if (manifest.FormatVersion != BackupFormatVersion)
                    throw new ApiValidationException($"Unsupported backup format v{manifest.FormatVersion}.");
                var dbEntry = zip.GetEntry("database/visionstudio.db") ?? throw new ApiValidationException("Backup bundle has no database snapshot.");
                await ExtractEntryAsync(dbEntry, incomingDb, ct);
                var actualHash = await Sha256FileAsync(incomingDb, ct);
                if (!string.Equals(actualHash, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ApiValidationException("Backup database SHA-256 does not match manifest.");

                if (request.RestoreArtifacts && manifest.IncludesArtifacts)
                {
                    Directory.CreateDirectory(stagedArtifacts);
                    foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("artifacts/", StringComparison.Ordinal) && !string.IsNullOrEmpty(x.Name)))
                    {
                        var relative = entry.FullName["artifacts/".Length..].Replace('/', Path.DirectorySeparatorChar);
                        var destination = SafeChildPath(stagedArtifacts, relative);
                        await ExtractEntryAsync(entry, destination, ct);
                    }
                }
            }

            var validation = await SqliteMetadataDatabase.ValidateSnapshotAsync(incomingDb, ct);
            if (!validation.Ok) throw new ApiValidationException($"Backup database integrity check failed: {validation.Result}");
            if (validation.SchemaVersion > _database.CurrentSchemaVersion)
                throw new ApiConflictException($"Backup schema v{validation.SchemaVersion} is newer than this host supports (v{_database.CurrentSchemaVersion}).");

            var safetyDb = Path.Combine(workspace, "pre-restore.db");
            await _database.CreateSnapshotAsync(safetyDb, ct);
            var oldArtifacts = Path.Combine(_tempRoot, $"artifacts-pre-restore-{Guid.NewGuid():N}");
            var movedOldArtifacts = false;
            var installedArtifacts = false;
            try
            {
                await _database.RestoreSnapshotAsync(incomingDb, ct);
                if (request.RestoreArtifacts && manifest.IncludesArtifacts)
                {
                    if (Directory.Exists(_artifactRoot)) { Directory.Move(_artifactRoot, oldArtifacts); movedOldArtifacts = true; }
                    Directory.Move(stagedArtifacts, _artifactRoot);
                    installedArtifacts = true;
                }
            }
            catch
            {
                try { await _database.RestoreSnapshotAsync(safetyDb, CancellationToken.None); } catch { }
                try
                {
                    if (installedArtifacts && Directory.Exists(_artifactRoot)) Directory.Delete(_artifactRoot, true);
                    if (movedOldArtifacts && Directory.Exists(oldArtifacts)) Directory.Move(oldArtifacts, _artifactRoot);
                }
                catch { }
                throw;
            }
            finally
            {
                try { if (Directory.Exists(oldArtifacts)) Directory.Delete(oldArtifacts, true); } catch { }
            }

            try { await _capacity.RefreshAsync(ct); } catch { /* restore already committed; telemetry refresh is best-effort */ }
            return new StorageRestoreResult(manifest.BackupId, validation.SchemaVersion, _database.CurrentSchemaVersion,
                request.RestoreArtifacts && manifest.IncludesArtifacts, RestartRecommended: true);
        }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { }
            _gate.Release();
        }
    }

    private async Task<StorageBackupManifest> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new ApiValidationException("Backup bundle has no manifest.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<StorageBackupManifest>(stream, _json, ct)
            ?? throw new ApiValidationException("Backup manifest is invalid.");
    }

    private string BackupPath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ApiValidationException("Backup id is invalid.");
        return Path.Combine(_backupRoot, id + ".vsbackup");
    }

    private static StorageBackupDescriptor ToDescriptor(StorageBackupManifest manifest, long archiveBytes)
        => new(manifest.BackupId, manifest.CreatedAt, manifest.SchemaVersion, archiveBytes, manifest.IncludesArtifacts, manifest.ArtifactCount, manifest.ArtifactBytes);

    private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = entry.Open();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct);
    }

    private static string SafeChildPath(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new ApiValidationException("Backup artifact path escapes the restore root.");
        return path;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
