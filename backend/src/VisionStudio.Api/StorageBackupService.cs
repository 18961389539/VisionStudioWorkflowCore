using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    long ArtifactBytes,
    /// <summary>媒体库/设备与来源配置/生产运行配置/插件仓库等运行依赖是否随包（可恢复系统备份）。</summary>
    bool IncludesSystemAssets = false,
    int SystemAssetCount = 0,
    long SystemAssetBytes = 0,
    IReadOnlyDictionary<string, string>? AssetSha256 = null);

public sealed record StorageBackupDescriptor(
    string BackupId,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    long ArchiveBytes,
    bool IncludesArtifacts,
    int ArtifactCount,
    long ArtifactBytes,
    bool IncludesSystemAssets = false);

public sealed record StorageRestoreRequest(string BackupId, bool RestoreArtifacts = true, bool RestoreSystemAssets = true);
public sealed record StorageRestoreResult(string BackupId, int SourceSchemaVersion, int CurrentSchemaVersion, bool ArtifactsRestored, bool RestartRecommended, bool SystemAssetsRestored = false);

/// <summary>
/// 系统资产目录切换的单步记录：在第一次移动之前就登记，并随进度更新阶段标志，
/// 使回滚能覆盖"旧目录已移走、新目录尚未安装"的半完成状态（R03）。
/// </summary>
internal sealed class SystemSwap(string target, string old)
{
    public string Target { get; } = target;
    public string Old { get; } = old;
    /// <summary>旧目录已移动到保护副本位置。</summary>
    public bool OldMoved { get; set; }
    /// <summary>staged 目录已安装到目标根。</summary>
    public bool Installed { get; set; }
}

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
    /// <summary>可恢复系统备份的资产根：媒体库、设备/来源配置、生产运行配置、插件仓库（含信任与活动指针）。</summary>
    private readonly (string Prefix, string Root)[] _systemSections;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<StorageBackupService>? _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// 测试夹具：仅用于验证"恢复失败且回滚未完成"路径（R04 失败锁定）。生产路径始终为 null。
    /// 置为非 null 时，恢复在内层 try 内、提交之前调用它；抛出即触发回滚分支。
    /// </summary>
    internal static Func<Task>? RestoreCommitFaultInjector { get; set; }

    public StorageBackupService(
        SqliteMetadataDatabase database,
        StorageCapacityService capacity,
        StorageMaintenanceCoordinator maintenance,
        ProductionRuntimeService production,
        IWebHostEnvironment env,
        IConfiguration configuration,
        IOptions<StorageMaintenanceOptions> options,
        ILogger<StorageBackupService>? logger = null)
    {
        _logger = logger;
        _database = database;
        _capacity = capacity;
        _maintenance = maintenance;
        _production = production;
        _options = options.Value;
        var data = VisionStudioDataRoot.Resolve(env.ContentRootPath);
        _artifactRoot = Path.Combine(data, "artifacts");
        _backupRoot = Path.Combine(data, "backups");
        _tempRoot = Path.Combine(data, ".storage-maintenance");
        _systemSections =
        [
            // R05：媒体段与 MediaLibraryService 共用同一根解析（含 `MediaLibrary:RootPath` 覆盖）。
            ("media", VisionStudioDataRoot.ResolveMediaRoot(env.ContentRootPath, configuration.GetValue<string>("MediaLibrary:RootPath"))),
            ("devices", Path.Combine(data, "devices")),
            ("provenance", Path.Combine(data, "provenance")),
            ("production-config", Path.Combine(data, "production")),
            // R06：托管插件包（含 `active-pointers.json` 活动版本指针）与发布者信任库
            // （`trusted-publishers.json` + *.cer）位于数据根——空白机恢复必须一并带回，
            // 否则还原后插件全部消失、签名信任全部失效。
            ("plugin-packages", Path.Combine(data, "plugin-packages")),
            ("plugin-trust", Path.Combine(data, "plugin-trust")),
            // 站点配置（生产环境覆盖文件）：随包发布但承载现场策略，需可恢复。
            ("site-config", Path.Combine(data, "site-config")),
            ("plugins", Path.Combine(env.ContentRootPath, "plugins")),
        ];
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

    /// <summary>程序版本的单一来源（程序集版本，例 "0.63.0"）：备份清单与健康端点对齐于此。</summary>
    public static string VisionStudioVersion()
        => typeof(StorageBackupService).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public async Task<StorageBackupDescriptor> CreateAsync(bool? includeArtifacts, CancellationToken ct)
    {
        // 受控维护窗口：排空在途 HTTP 请求后枚举文件，附件与系统资产在窗口内取得一致视图；
        // 数据库本身用 online snapshot。POST /api/storage/backups 与 restore 一样在中间件中
        // 豁免自持请求 lease（否则排空会等待当前请求自身而死锁）。
        //
        // R07 共享快照协议：附件写入方（生产追溯落盘/清理）持**共享**租约，备份在
        // "DB 快照 + 附件枚举 + 打包读取"窗口内持**排他**租约（见 CreateAsync 内的
        // EnterArtifactSnapshotAsync）。因此**运行中的生产也可安全备份**——窗口内不会发生
        // 半写附件被读入或"快照后附件被清理删除"的悬空引用；生产落盘在窗口内被拒（放弃本次
        // 落盘并记 note，不阻塞、不反压）。
        //
        // 仍拒绝**过渡态**（Starting/Stopping/Recovering）：此刻生产正在切换锁定快照/清理资源，
        // 与其并发备份没有意义，等其稳定后再备份。
        var runtimeState = _production.Status.State;
        if (runtimeState is ProductionRuntimeState.Starting or ProductionRuntimeState.Stopping or ProductionRuntimeState.Recovering)
            throw new ApiConflictException(
                $"Storage backup requires a stable production runtime; current state is {runtimeState}. " +
                "Wait for production to reach Running, Stopped, or Faulted before creating a backup.");

        await using var maintenance = await _maintenance.BeginExclusiveAsync("storage backup", ct);
        await _gate.WaitAsync(ct);
        var workspace = Path.Combine(_tempRoot, $"backup-{Guid.NewGuid():N}");
        try
        {
            var include = includeArtifacts ?? _options.IncludeArtifactsInBackup;

            // 系统级可恢复备份：媒体库、设备/来源配置、生产运行配置与插件仓库（含信任与活动指针）。
            // 这些是"在空白机器恢复完整运行环境"所必需的运行依赖。
            var systemFiles = new List<(string ZipPath, string File)>();
            foreach (var (prefix, root) in _systemSections)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file);
                    // 跳过激活/替换过程中的临时目录（.{id}.activate-*）：它们不是可恢复资产。
                    if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.StartsWith('.'))) continue;
                    systemFiles.Add(($"system/{prefix}/{relative.Replace('\\', '/')}", file));
                }
            }
            var systemBytes = systemFiles.Sum(x => new FileInfo(x.File).Length);

            var capacity = await _capacity.RefreshAsync(ct);
            var estimated = capacity.DatabaseBytes + (include ? capacity.ArtifactBytes : 0L) + systemBytes;
            await _capacity.EnsureBackupCapacityAsync(estimated, ct);

            Directory.CreateDirectory(workspace);
            var dbSnapshot = Path.Combine(workspace, "visionstudio.db");

            // R07 共享快照协议：DB 快照 → 附件枚举 → 打包读取 必须处在同一"附件冻结"窗口内。
            // 窗口内附件写入方被拒（放弃本次落盘，不阻塞生产）；由于生产落盘顺序是"先写文件、后写
            // DB 行"，快照在前 + 冻结窗口保证：备份清单里每条 DB 引用都能在包内找到对应文件，
            // 不会出现"快照后文件被清理删除"的悬空引用。
            await using var artifactSnapshot = await _maintenance.EnterArtifactSnapshotAsync(ct);

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
            var assetSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var artifact in artifactFiles)
                assetSha256[$"artifacts/{Path.GetRelativePath(_artifactRoot, artifact).Replace('\\', '/')}"] = ComputeFileSha256(artifact);
            foreach (var (zipPath, file) in systemFiles)
                assetSha256[zipPath] = ComputeFileSha256(file);
            var manifest = new StorageBackupManifest(
                BackupFormatVersion, id, DateTimeOffset.UtcNow, VisionStudioVersion(), validation.SchemaVersion,
                await Sha256FileAsync(dbSnapshot, ct), include, artifactFiles.Length, artifactBytes,
                true, systemFiles.Count, systemBytes, assetSha256);

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
                foreach (var (zipPath, file) in systemFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file, zipPath, CompressionLevel.Optimal);
                }
                foreach (var (prefix, _) in _systemSections)
                    zip.CreateEntry($"system/{prefix}/"); // includes empty roots in the recovery manifest
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
        // Restore remains available to an authenticated administrator while failure-locked; it is
        // the practical rollback/retry path for unresolved transactions. Ordinary maintenance stays blocked.
        await using var maintenance = await _maintenance.BeginExclusiveAsync("storage restore", ct, allowFailureLockedRestore: true);
        if (_production.Status.State != ProductionRuntimeState.Stopped)
            throw new ApiConflictException("Production Runtime changed state while storage restore was waiting for maintenance mode.");

        await _gate.WaitAsync(ct);
        var workspace = Path.Combine(_tempRoot, $"restore-{Guid.NewGuid():N}");
        // 回滚未完成时保留工作区（含 pre-restore.db 快照）与旧附件目录，绝不允许清理路径
        // 删除最后的安全副本——恢复失败必须可以再次恢复或人工取证。
        var preserveSafetyCopies = false;
        try
        {
            var archivePath = BackupPath(request.BackupId);
            if (!File.Exists(archivePath)) throw new ApiNotFoundException($"Backup '{request.BackupId}' does not exist.");
            Directory.CreateDirectory(workspace);
            var incomingDb = Path.Combine(workspace, "incoming.db");
            var stagedArtifacts = Path.Combine(workspace, "artifacts");
            var stagedSystem = Path.Combine(workspace, "system");
            StorageBackupManifest manifest;
            var extractedAssetHashes = new Dictionary<string, string>(StringComparer.Ordinal);

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

                if (manifest.IncludesArtifacts)
                {
                    Directory.CreateDirectory(stagedArtifacts);
                    foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("artifacts/", StringComparison.Ordinal) && !string.IsNullOrEmpty(x.Name)))
                    {
                        var relative = entry.FullName["artifacts/".Length..].Replace('/', Path.DirectorySeparatorChar);
                        var destination = SafeChildPath(stagedArtifacts, relative);
                        await ExtractEntryAsync(entry, destination, ct);
                        extractedAssetHashes[$"artifacts/{relative.Replace('\\', '/')}"] = ComputeFileSha256(destination);
                    }
                }

                if (manifest.IncludesSystemAssets)
                {
                    Directory.CreateDirectory(stagedSystem);
                    foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("system/", StringComparison.Ordinal) && !string.IsNullOrEmpty(x.Name)))
                    {
                        var relative = entry.FullName["system/".Length..].Replace('/', Path.DirectorySeparatorChar);
                        var destination = SafeChildPath(stagedSystem, relative);
                        await ExtractEntryAsync(entry, destination, ct);
                        extractedAssetHashes[$"system/{relative.Replace('\\', '/')}"] = ComputeFileSha256(destination);
                    }
                    foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("system/", StringComparison.Ordinal) && string.IsNullOrEmpty(x.Name)))
                    {
                        var relative = entry.FullName["system/".Length..].TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
                        Directory.CreateDirectory(SafeChildPath(stagedSystem, relative));
                    }
                }
            }

            if (manifest.AssetSha256 is not null && (manifest.AssetSha256.Count != extractedAssetHashes.Count || manifest.AssetSha256.Any(pair =>
                    !extractedAssetHashes.TryGetValue(pair.Key, out var actual) ||
                    !string.Equals(actual, pair.Value, StringComparison.OrdinalIgnoreCase))))
                throw new ApiValidationException("Backup artifact/system-asset manifest does not match the extracted file set and SHA-256 hashes.");

            if (_maintenance.IsFailureLocked || _maintenance.HasRestoreTransactionMarker)
            {
                if (!request.RestoreArtifacts || !manifest.IncludesArtifacts ||
                    !request.RestoreSystemAssets || !manifest.IncludesSystemAssets ||
                    manifest.AssetSha256 is null ||
                    _systemSections.Any(section => !Directory.Exists(Path.Combine(stagedSystem, section.Prefix))))
                    throw new ApiConflictException("A failure-locked or unresolved restore requires a complete backup with database, artifacts, and every system-asset root.");
            }

            var validation = await SqliteMetadataDatabase.ValidateSnapshotAsync(incomingDb, ct);
            if (!validation.Ok) throw new ApiValidationException($"Backup database integrity check failed: {validation.Result}");
            if (validation.SchemaVersion > _database.CurrentSchemaVersion)
                throw new ApiConflictException($"Backup schema v{validation.SchemaVersion} is newer than this host supports (v{_database.CurrentSchemaVersion}).");
            var sourceDatabaseHash = ComputeFileSha256(incomingDb);
            var sourceSchemaVersion = validation.SchemaVersion;
            var prepared = await _database.PrepareSnapshotForRestoreAsync(incomingDb, ct);
            if (!prepared.Ok || prepared.SchemaVersion != _database.CurrentSchemaVersion)
                throw new ApiValidationException($"Migrated backup snapshot failed validation: integrity={prepared.Result}, schema={prepared.SchemaVersion}.");
            var preparedDatabaseHash = ComputeFileSha256(incomingDb);
            await ValidateArtifactReferencesAsync(incomingDb,
                request.RestoreArtifacts && manifest.IncludesArtifacts ? stagedArtifacts : _artifactRoot, ct);

            // Q08：进入实际改动（DB/目录切换）之前耐久登记恢复事务意图——进程若在"数据库已替换、
            // 目录未全部切换"的中途被终止，启动检查会读到该文件并进入失败锁（见协调器构造函数），
            // 不必依赖 catch/finally 是否来得及执行。校验全部通过后才登记：此前的失败没有改动任何东西。
            var safetyDb = Path.Combine(workspace, "pre-restore.db");
            await _database.CreateSnapshotAsync(safetyDb, ct);
            var previousTransaction = _maintenance.ReadTransaction();
            var oldArtifacts = Path.Combine(_tempRoot, $"artifacts-pre-restore-{Guid.NewGuid():N}");
            var systemSwaps = _systemSections.Select(section => new SystemSwap(
                section.Root, Path.Combine(_tempRoot, $"{section.Prefix}-pre-restore-{Guid.NewGuid():N}"))).ToArray();
            var protectedAssets = new List<StorageMaintenanceCoordinator.RestoreProtectionAsset>();
            if (request.RestoreArtifacts && manifest.IncludesArtifacts)
                protectedAssets.Add(new(_artifactRoot, oldArtifacts, Directory.Exists(_artifactRoot), Directory.Exists(_artifactRoot) ? ComputeDirectorySha256(_artifactRoot) : null));
            if (request.RestoreSystemAssets && manifest.IncludesSystemAssets)
            for (var i = 0; i < _systemSections.Length; i++)
            {
                var root = _systemSections[i].Root;
                var exists = Directory.Exists(root);
                protectedAssets.Add(new(root, systemSwaps[i].Old, exists, exists ? ComputeDirectorySha256(root) : null));
            }
            var expectedAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.RestoreArtifacts && manifest.IncludesArtifacts) expectedAssets[_artifactRoot] = ComputeDirectorySha256(stagedArtifacts);
            if (request.RestoreSystemAssets && manifest.IncludesSystemAssets)
                foreach (var (prefix, root) in _systemSections)
                {
                    var stagedRoot = Path.Combine(stagedSystem, prefix);
                    if (Directory.Exists(stagedRoot)) expectedAssets[root] = ComputeDirectorySha256(stagedRoot);
                }
            // Intent must be durable before the first live database or asset replacement. A failed write
            // aborts here; the safety snapshot is still in the disposable workspace.
            _maintenance.BeginRestoreTransaction(manifest.BackupId,
                ["database-replaced", "artifacts-installed", "system-assets-installed", "committed"], safetyDb,
                ComputeFileSha256(safetyDb), sourceDatabaseHash, sourceSchemaVersion,
                preparedDatabaseHash, expectedAssets, protectedAssets);
            var movedOldArtifacts = false;
            var installedArtifacts = false;
            var rollbackFailed = false;
            var restoreAttemptFailed = false;
            try
            {
                await _database.RestoreSnapshotAsync(incomingDb, ct);
                _maintenance.AdvanceRestoreTransaction("database-replaced");
                if (request.RestoreArtifacts && manifest.IncludesArtifacts)
                {
                    if (Directory.Exists(_artifactRoot)) { Directory.Move(_artifactRoot, oldArtifacts); movedOldArtifacts = true; }
                    Directory.Move(stagedArtifacts, _artifactRoot);
                    installedArtifacts = true;
                    _maintenance.AdvanceRestoreTransaction("artifacts-installed");
                }

                // 系统资产（媒体库/设备与来源配置/生产配置/插件仓库）：与 artifacts 相同的
                // "旧根→保护副本、staged→目标根"原子切换，回滚时逆序复原。
                //
                // R03：每一项在**第一次目录移动之前**就登记进回滚清单（含阶段），因此"旧目录已移走、
                // 新目录尚未安装"的半完成状态也会被回滚覆盖——不会出现原目录缺失却不在清单中的孤儿。
                if (request.RestoreSystemAssets && manifest.IncludesSystemAssets && Directory.Exists(stagedSystem))
                {
                    foreach (var (prefix, root) in _systemSections)
                    {
                        var staged = Path.Combine(stagedSystem, prefix);
                        if (!Directory.Exists(staged)) continue;
                        var swap = systemSwaps.First(x => string.Equals(x.Target, root, StringComparison.OrdinalIgnoreCase));
                        if (Directory.Exists(root))
                        {
                            Directory.Move(root, swap.Old);
                            swap.OldMoved = true;
                        }
                        Directory.Move(staged, root);
                        swap.Installed = true;
                    }
                    _maintenance.AdvanceRestoreTransaction("system-assets-installed");
                }
                // Commit only after the installed generation matches the hashes captured in the
                // durable intent. This makes the transaction record evidence, not just a list of paths.
                // Q08：SQLite Backup API 产生的是**语义等价、字节不同**的文件（页布局/头部字段/空闲页），
                // 直接比较两份快照的文件哈希会误报——必须先把双方 VACUUM 规范化再比较逻辑内容。
                var installedSnapshot = Path.Combine(workspace, "post-restore-verified.db");
                await _database.CreateSnapshotAsync(installedSnapshot, ct);
                if (!string.Equals(await ComputeSnapshotContentSha256Async(installedSnapshot, ct),
                        await ComputeSnapshotContentSha256Async(incomingDb, ct), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Installed metadata database does not match the isolated migrated snapshot (content mismatch).");
                await ValidateArtifactReferencesAsync(installedSnapshot, _artifactRoot, ct);
                foreach (var (assetRoot, expectedHash) in expectedAssets)
                    if (!Directory.Exists(assetRoot) || !string.Equals(ComputeDirectorySha256(assetRoot), expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Installed asset root '{assetRoot}' does not match its staged manifest hash.");
                foreach (var protectedAsset in protectedAssets)
                {
                    if (protectedAsset.Existed && (!Directory.Exists(protectedAsset.PreservedPath) ||
                        !string.Equals(ComputeDirectorySha256(protectedAsset.PreservedPath), protectedAsset.Sha256, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException($"Protected rollback asset '{protectedAsset.Target}' does not match its pre-restore hash.");
                    if (!protectedAsset.Existed && Directory.Exists(protectedAsset.PreservedPath))
                        throw new IOException($"Unexpected rollback copy exists for previously absent asset '{protectedAsset.Target}'.");
                }
                // 测试夹具：在提交前注入故障（生产为 null，不产生任何行为变化）。
                if (RestoreCommitFaultInjector is { } fault) await fault();

                // R03：提交点——耐久记录"恢复已提交"。此后的任何清理失败（保护副本删除、
                // 事务文件退役）都不再触发回滚或失败锁：恢复结果已定，残留由启动清理兜底。
                _maintenance.AdvanceRestoreTransaction("committed");
            }
            catch (Exception restoreError)
            {
                restoreAttemptFailed = true;
                // 回滚：数据库、附件与系统资产各自尝试；任何一步失败都必须保留对应的保护副本。
                try { await _database.RestoreSnapshotAsync(safetyDb, CancellationToken.None); }
                catch { rollbackFailed = true; }
                try
                {
                    if (installedArtifacts && Directory.Exists(_artifactRoot)) Directory.Delete(_artifactRoot, true);
                    if (movedOldArtifacts && Directory.Exists(oldArtifacts)) Directory.Move(oldArtifacts, _artifactRoot);
                }
                catch { rollbackFailed = true; }

                for (var i = systemSwaps.Length - 1; i >= 0; i--)
                {
                    var swap = systemSwaps[i];
                    try
                    {
                        // R03：按阶段回滚半完成项——旧目录可能已移走但新目录未安装（swap.Installed=false），
                        // 此时不能删除目标根（那可能是残留的真实数据），只需把旧目录移回。
                        if (swap.Installed && Directory.Exists(swap.Target)) Directory.Delete(swap.Target, true);
                        if (swap.OldMoved && Directory.Exists(swap.Old) && !Directory.Exists(swap.Target))
                            Directory.Move(swap.Old, swap.Target);
                    }
                    catch { rollbackFailed = true; }
                }

                if (rollbackFailed)
                {
                    // 清理路径对此失败完全锁定：保留 pre-restore.db、旧附件目录与全部系统资产副本，
                    // 并把可用副本位置随异常返回，允许再次恢复或人工恢复。
                    preserveSafetyCopies = true;

                    // R04：协调器在本方法的具体操作中将被 Dispose（ExclusiveLease 结束），
                    // 若仅依赖 lease，失败锁会在异常离开作用域时被意外解除，主机会带着
                    // "半恢复"状态继续接受写入。因此把失败状态提升到协调器自身的持久锁：
                    // IsMaintenanceActive 保持 true，常规变更/生产启动一律 409，直到人工
                    // 完成恢复核对后显式 ClearFailureLock。
                    _maintenance.EnterFailureLock(
                        $"Storage restore failed and rollback was incomplete (backup '{request.BackupId}'). " +
                        $"Preserved copies: database '{safetyDb}', artifacts '{oldArtifacts}', system assets under '{_tempRoot}'.");
                    throw new ApiConflictException(
                        "Storage restore failed and rollback could not be fully completed. " +
                        $"Preserved safety copies: database snapshot '{safetyDb}', previous artifacts '{oldArtifacts}', " +
                        $"and system asset copies under '{_tempRoot}'. " +
                        "The host is now locked in maintenance mode; retry restore or recover manually from the listed copies, " +
                        "then clear the maintenance failure lock.",
                        restoreError);
                }
                throw;
            }
            finally
            {
                // 只有提交成功或回滚成功后才允许清理保护副本；回滚失败时全部保留。
                if (!rollbackFailed)
                {
                    if (restoreAttemptFailed && previousTransaction is not null)
                    {
                        // A failed retry returned to an already-locked mixed generation. Keep this
                        // retry's safety DB and all moved asset copies; its transaction nests the
                        // prior verified protection group. Do not retire the marker or delete paths it names.
                        preserveSafetyCopies = true;
                        _maintenance.AdvanceRestoreTransaction("retry-rolled-back-to-previously-locked-generation");
                    }
                    else
                    {
                        // R03：保护副本的清理顺序——先把事务推进到 committed（已在提交点完成），
                        // 再删除旧附件/系统资产副本，最后退役事务文件。事务文件退役失败**不再**
                        // 进入失败锁、也不再抛异常（旧实现把它升级为"副本已删 + 重试被拒"的
                        // 不可恢复状态）：committed 事务在下次启动时只做剩余清理，主机可继续服务。
                        try { if (Directory.Exists(oldArtifacts)) Directory.Delete(oldArtifacts, true); } catch { }
                        foreach (var swap in systemSwaps)
                        {
                            try { if (Directory.Exists(swap.Old)) Directory.Delete(swap.Old, true); } catch { }
                        }
                        try { _maintenance.CompleteRestoreTransaction(); }
                        catch (Exception transactionError)
                        {
                            // 保留工作区（含 pre-restore.db 安全快照），让启动清理/人工核对仍有完整锚点。
                            preserveSafetyCopies = true;
                            _logger.LogWarning(transactionError,
                                "The committed restore transaction marker could not be removed; startup will retry the cleanup (the restore itself succeeded).");
                        }
                    }
                }
            }

            // R04：恢复正常完成（可能覆盖了之前一次失败的失败锁）后，解除协调器失败锁，
            // 让主机重新接受常规变更与生产启动。
            _maintenance.ClearFailureLock();
            // Q08：恢复成功不是"继续服务"的许可——磁盘资产已替换，而设备适配器/插件/配置绑定
            // 仍是恢复前的内存实例。置"待重启"门：除认证/健康外一律 503（restart_required），
            // 仅进程重启后由协调器构造函数自动解除（届时全部依赖随启动重新加载并校验）。
            _maintenance.MarkRestartPending(
                $"storage restore from backup '{manifest.BackupId}' completed; restart the host to reload device, plugin and configuration state");

            try { await _capacity.RefreshAsync(ct); } catch { /* restore already committed; telemetry refresh is best-effort */ }
            return new StorageRestoreResult(manifest.BackupId, validation.SchemaVersion, _database.CurrentSchemaVersion,
                request.RestoreArtifacts && manifest.IncludesArtifacts, RestartRecommended: true,
                SystemAssetsRestored: request.RestoreSystemAssets && manifest.IncludesSystemAssets);
        }
        finally
        {
            // 回滚未完成时保留整个工作区（含 pre-restore.db 安全副本），只清理已完成/已回滚的恢复工作区。
            if (!preserveSafetyCopies)
            {
                try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { }
            }
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
        => new(manifest.BackupId, manifest.CreatedAt, manifest.SchemaVersion, archiveBytes, manifest.IncludesArtifacts, manifest.ArtifactCount, manifest.ArtifactBytes, manifest.IncludesSystemAssets);

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

    internal static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Q08：快照的**逻辑内容**指纹——按名称遍历所有用户表、按 rowid 逐行累积哈希。
    /// 与文件字节无关：SQLite 备份输出的页布局/头字段/空闲页差异不影响结果，只有真实的数据
    /// 差异才会导致不一致（此前用文件哈希比较两个独立备份，属于必然误报的比较）。
    /// </summary>
    internal static async Task<string> ComputeSnapshotContentSha256Async(string snapshotPath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);

        var tables = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in tables)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(table));
            hash.AppendData([0]);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i)
                        ? "<null>"
                        : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
                    hash.AppendData([0]);
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ComputeDirectorySha256(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var relativeBytes = System.Text.Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(relativeBytes.Length));
            hash.AppendData(relativeBytes);
            using var stream = File.OpenRead(file);
            hash.AppendData(BitConverter.GetBytes(stream.Length));
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task ValidateArtifactReferencesAsync(string databasePath, string artifactsRoot, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact FROM (" +
            "SELECT preview_relative_path AS artifact FROM run_traces WHERE has_preview=1 AND preview_relative_path IS NOT NULL " +
            "UNION ALL SELECT replay_relative_path FROM run_traces WHERE has_replay_input=1 AND replay_relative_path IS NOT NULL);";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var root = Path.GetFullPath(artifactsRoot);
        while (await reader.ReadAsync(ct))
        {
            var relative = reader.GetString(0).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is ".." or "."))
                throw new ApiValidationException($"Backup contains unsafe artifact reference '{relative}'.");
            var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                throw new ApiValidationException($"Database artifact reference '{relative}' has no file in the restored asset set.");
        }
    }
}
