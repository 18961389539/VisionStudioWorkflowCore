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
public sealed partial class StorageBackupService
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
    /// <summary>R01（2026-10-12 复审）：设备动作安全账本（device-action-safety.json / manual-action-intents.json
    /// 及核对证据目录）——属于"当前事实"，既不能由旧备份回退覆盖，也不参与可回滚备份。</summary>
    private readonly DeviceActionSafetyStore _safetyStore;
    private readonly ManualActionIntentStore _intentStore;
    /// <summary>production-config 系统段的根（安全账本的保护/复刻都锚定在它内部）。</summary>
    private readonly string _productionConfigRoot;
    /// <summary>可恢复系统备份的资产根：媒体库、设备/来源配置、生产运行配置、工作流文档、插件仓库（含信任与活动指针）。</summary>
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
        DeviceActionSafetyStore safetyStore,
        ManualActionIntentStore intentStore,
        ILogger<StorageBackupService>? logger = null)
    {
        _logger = logger;
        _database = database;
        _capacity = capacity;
        _maintenance = maintenance;
        _production = production;
        _options = options.Value;
        _safetyStore = safetyStore;
        _intentStore = intentStore;
        var data = VisionStudioDataRoot.Resolve(env.ContentRootPath);
        _artifactRoot = Path.Combine(data, "artifacts");
        _backupRoot = Path.Combine(data, "backups");
        _tempRoot = Path.Combine(data, ".storage-maintenance");
        _productionConfigRoot = Path.Combine(data, "production");
        _systemSections =
        [
            // R05：媒体段与 MediaLibraryService 共用同一根解析（含 `MediaLibrary:RootPath` 覆盖）。
            ("media", VisionStudioDataRoot.ResolveMediaRoot(env.ContentRootPath, configuration.GetValue<string>("MediaLibrary:RootPath"))),
            ("devices", Path.Combine(data, "devices")),
            ("provenance", Path.Combine(data, "provenance")),
            ("production-config", _productionConfigRoot),
            // R04（2026-10-12 复审）：编辑器保存的工作流文档（data/workflows）是"空白机恢复"必需资产——
            // 未发布为 Job 版本的流程只存在于此，不能依赖 Job 快照反推。
            ("workflows", Path.Combine(data, "workflows")),
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
            // R01：安全账本是"当前事实"，不是可回滚业务配置——备份排除，恢复时另行严格复刻。
            var safetyLedgerPaths = SafetyLedgerPaths().Select(Path.GetFullPath).ToArray();
            foreach (var (prefix, root) in _systemSections)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file);
                    // 跳过激活/替换过程中的临时目录（.{id}.activate-*）：它们不是可恢复资产。
                    if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.StartsWith('.'))) continue;
                    if (IsSafetyLedgerPath(Path.GetFullPath(file), safetyLedgerPaths)) continue;
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
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { /* 工作区清理失败：保留待启动清理，不影响已提交结果 */ }
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
            try { file.Delete(); deleted++; } catch (IOException) { /* 文件被占用/只读：保留该备份，下一轮再清 */ }
        }
        return Task.FromResult(deleted);
    }
}
