using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

// VisionStudio 数据库维护工具（R08/F06 + 2026-10-13 全面审查 Q01/Q02/Q04 修复）：
//
//   snapshot <sourceDb> <checkpointDir>
//     用 SQLite Online Backup API 获取**一致性快照**（活动写者/checkpoint 并发下也安全），
//     执行 integrity_check、记录 schema 与内容 SHA-256 清单（manifest.json）。
//
//   restore <checkpointDir> <targetDb> [--force]
//     事务化整体替换目标数据库：
//       1. 清单校验（A05）：manifest 必须存在，内容哈希/schema 必须与检查点一致；
//       2. 维护互斥（Q04）：先取跨进程命名互斥锁 + 检查另一维护进程的活跃标记，再检查目标
//          非空 WAL/SHM——**全部早于**任何中断恢复/删除/复制（旧实现在删 sidecar 之后才做
//          活跃保护，等于先破坏再判断）；
//       3. 中断恢复（Q01/Q02/Q04）：统一协议标记 <dataRoot>/.storage-maintenance/restore-transaction.json
//          （与宿主 StorageMaintenanceCoordinator 同一事实源；旧版 .visionstudio-restore-intent.json
//          仍被识别）。恢复前先核对**意图目标身份**（target 不匹配一律拒绝，绝不错消费别的
//          目标的意图），再用哈希自证切换结果：
//            · 目标内容 == 检查点哈希 ⇒ 切换其实已完成（崩溃发生在提交标记之前）⇒ 补登记
//              committed 并退役，绝不误回退；
//            · 目标内容 == 保护副本哈希 ⇒ 切换未开始/已回滚 ⇒ 直接退役；
//            · 否则从保护副本恢复，并核对恢复结果与保护副本哈希一致；
//       4. 保护副本：目标完整文件组（主库 + WAL/SHM）先复制到同目录保护快照；
//       5. 耐久意图：切换前登记 restore-transaction.json（原子写 + WriteThrough + Flush(true)，
//          初始与提交标记同等耐久——Q02：半写标记不能靠注释保证断电安全）；
//       6. 切换：staged 先自检 → 删除 sidecar（内容已在保护副本中）→ 原子替换主库；
//       7. 任一失败：从保护副本恢复原文件组（目标始终保持可读），意图保留供人工核对；
//       8. 成功（Q01/Q02）：先登记**设备核对闸门**（production/device-action-safety.json，
//          与 API 恢复同一语义——恢复替换了数据，生产启动前必须核对物理设备），再耐久写入
//          committed 标记；**提交失败必须返回非零退出码**，绝不再被当作"清理失败"吞掉。
//          只有确认 committed 之后的退役失败才降级为残留清理（宿主启动时会兜底退役）。
//
// --force 仅为异常场景的显式逃生口：只跳过**活性启发式**（非空 sidecar）；另一维护进程的
// 活跃标记是事实而非启发式，不因 --force 放行（残留标记可由接管路径处理）。协议校验
// （manifest、意图目标身份、保护副本核对）永远不跳过——不允许用 --force 代替恢复协议。
//
// 退出码：0 = 成功；非 0 = 失败（调用方必须据此中止发布/回滚流程）。

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

try
{
    var command = args.Length > 0 ? args[0] : string.Empty;
    var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
    var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    return command switch
    {
        "snapshot" when positional.Length == 3 => Snapshot(positional[1], positional[2], json),
        "restore" when positional.Length == 3 => Restore(positional[1], positional[2], force, json),
        _ => Usage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: VisionStudio.SqliteSnapshot snapshot <sourceDb> <checkpointDir>");
    Console.Error.WriteLine("       VisionStudio.SqliteSnapshot restore <checkpointDir> <targetDb> [--force]");
    Console.Error.WriteLine("         (stop the VisionStudio host before restore; --force skips the active-sidecar guard)");
    return 2;
}

static int Snapshot(string sourceDb, string checkpointDir, JsonSerializerOptions json)
{
    sourceDb = Path.GetFullPath(sourceDb);
    if (!File.Exists(sourceDb))
    {
        Console.Error.WriteLine($"ERROR: source database '{sourceDb}' does not exist.");
        return 1;
    }
    Directory.CreateDirectory(checkpointDir);
    var destination = Path.Combine(checkpointDir, "visionstudio.db");

    // Online Backup API：与 VisionStudio.Api 的 CreateSnapshotAsync 相同机制——
    // 活动写者下仍能得到事务一致的快照（无需停止运行时；停机只会进一步缩短风险窗口）。
    using (var source = new SqliteConnection(BuildConnectionString(sourceDb, SqliteOpenMode.ReadWriteCreate)))
    {
        source.Open();
        using var dest = new SqliteConnection(BuildConnectionString(destination, SqliteOpenMode.ReadWriteCreate));
        dest.Open();
        source.BackupDatabase(dest);
    }

    if (!TryVerify(destination, out var integrity, out var schemaVersion, out var verifyError))
    {
        Console.Error.WriteLine($"ERROR: checkpoint integrity check failed: {integrity} ({verifyError})");
        return 1;
    }

    var manifest = new CheckpointManifest
    {
        CreatedAt = DateTimeOffset.UtcNow,
        SourceDatabase = sourceDb,
        SnapshotSha256 = Sha256File(destination),
        SchemaVersion = schemaVersion,
        Integrity = integrity
    };
    File.WriteAllText(Path.Combine(checkpointDir, "manifest.json"), JsonSerializer.Serialize(manifest, json));

    Console.WriteLine($"checkpoint snapshot: {destination} (schema v{schemaVersion}, integrity={integrity}, sha256={manifest.SnapshotSha256[..16]}...)");
    return 0;
}

static int Restore(string checkpointDir, string targetDb, bool force, JsonSerializerOptions json)
{
    var source = Path.Combine(checkpointDir, "visionstudio.db");
    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"ERROR: checkpoint '{checkpointDir}' has no visionstudio.db.");
        return 1;
    }

    // A05：清单必须存在且与检查点一致——结构完整无法证明是原来批准的恢复点。
    var manifestPath = Path.Combine(checkpointDir, "manifest.json");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"ERROR: checkpoint '{checkpointDir}' has no manifest.json; refusing to restore without a verified manifest.");
        return 1;
    }
    CheckpointManifest? manifest;
    try { manifest = JsonSerializer.Deserialize<CheckpointManifest>(File.ReadAllText(manifestPath), json); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: checkpoint manifest could not be parsed: {ex.Message}");
        return 1;
    }
    if (manifest is null || string.IsNullOrWhiteSpace(manifest.SnapshotSha256))
    {
        Console.Error.WriteLine("ERROR: checkpoint manifest is invalid (missing snapshot_sha256).");
        return 1;
    }
    var checkpointHash = Sha256File(source);
    if (!string.Equals(checkpointHash, manifest.SnapshotSha256, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"ERROR: checkpoint content does not match its manifest (expected {manifest.SnapshotSha256}, actual {checkpointHash}); refusing to restore.");
        return 1;
    }
    if (!TryVerify(source, out var integrity, out var schemaVersion, out var verifyError))
    {
        Console.Error.WriteLine($"ERROR: checkpoint integrity check failed: {integrity} ({verifyError})");
        return 1;
    }
    if (schemaVersion != manifest.SchemaVersion)
    {
        Console.Error.WriteLine($"ERROR: checkpoint schema v{schemaVersion} does not match its manifest v{manifest.SchemaVersion}.");
        return 1;
    }

    targetDb = Path.GetFullPath(targetDb);
    var targetDir = Path.GetDirectoryName(targetDb)!;
    Directory.CreateDirectory(targetDir);
    var dataRoot = targetDir;
    var maintenanceDir = Path.Combine(dataRoot, ".storage-maintenance");
    // Q01：统一维护协议——与宿主 StorageMaintenanceCoordinator 读取同一文件、同一目录。
    var transactionPath = Path.Combine(maintenanceDir, "restore-transaction.json");
    // 旧版工具写的按目录固定意图文件：仍识别（现场可能残留），但不再写入。
    var legacyIntentPath = Path.Combine(dataRoot, ".visionstudio-restore-intent.json");
    // 跨进程"维护进行中"标记（宿主 BeginExclusiveAsync 与本工具切换期都会写）。
    var maintenanceActivePath = Path.Combine(maintenanceDir, "maintenance-active.json");

    // Q04：维护互斥第一层——同数据根的命名互斥锁（工具 ↔ 工具）。持锁进程未退出即拒绝。
    using var mutexLease = TryAcquireMaintenanceMutex(dataRoot, out var mutexError);
    if (mutexLease is null)
    {
        Console.Error.WriteLine($"ERROR: {mutexError}. Another maintenance run must finish first.");
        return 1;
    }

    // Q04：维护互斥第二层 + 活跃保护——**全部早于**任何中断恢复/删除/复制。
    // 活跃维护进程是**事实**而非启发式：--force 也不豁免（残留标记——属主进程已死——允许接管）。
    if (MaintenanceProcessActive(maintenanceActivePath, out var activeDetail))
    {
        Console.Error.WriteLine($"ERROR: another storage maintenance process is active for this data root ({activeDetail}). " +
            "Wait for it to finish (or kill it and retry, which turns its marker into a takeover-able residual).");
        return 1;
    }
    // --force 只跳过 sidecar 活性启发式；协议校验（manifest/目标身份/保护副本）永远不跳过。
    if (!force)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = targetDb + suffix;
            if (File.Exists(sidecar) && new FileInfo(sidecar).Length > 0)
            {
                Console.Error.WriteLine($"ERROR: '{sidecar}' is non-empty - the VisionStudio host may still be running. Stop the host before restoring (or pass --force to override with full responsibility).");
                return 1;
            }
        }
    }

    // Q01/Q02/Q04：中断恢复——先核对意图目标身份，再用哈希自证切换结果。
    // 标记存在但不可解析 ⇒ 目标身份无从核对 ⇒ 保守拒绝（绝不当作"无标记"放行覆盖）。
    var marker = ReadRestoreMarker(transactionPath, legacyIntentPath, json);
    if (marker is not null)
    {
        // Q04：意图必须属于当前目标——不错消费别的目标的意图（意图文件按目录固定一个）。
        var markerTarget = string.IsNullOrWhiteSpace(marker.TargetDatabase) ? null : Path.GetFullPath(marker.TargetDatabase);
        if (markerTarget is null || !string.Equals(markerTarget, targetDb, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"ERROR: an unfinished restore intent targets '{marker.TargetDatabase}', not '{targetDb}'. " +
                "Refusing to consume an intent that belongs to a different target; resolve that target's restore first (or remove the stale intent manually after verification).");
            return 1;
        }
        if (string.Equals(marker.Stage, "committed", StringComparison.OrdinalIgnoreCase))
        {
            RetireMarker(transactionPath, legacyIntentPath, "a previously committed restore was cleaned up");
        }
        else
        {
            // Q02：哈希自证——目标内容 == 检查点哈希说明切换其实已完成（崩溃发生在 committed
            // 标记写入之前），必须补登记 committed 并退役，绝不能把已成功的恢复回退。
            var targetHash = File.Exists(targetDb) ? Sha256File(targetDb) : null;
            if (targetHash is not null && string.Equals(targetHash, marker.CheckpointSha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("an interrupted restore was detected; the target already matches the checkpoint content - the switch had completed before the interruption. Marking it committed instead of rolling it back.");
                // R2 复审（2026-10-13）：这次恢复**已事实完成**（哈希证据）——核对闸门必须先于
                // commit/退役登记：即使随后的重放失败回滚，也不会留下"数据已替换却无任何核对门"
                // 的窗口。登记失败则保持 switching 标记以待重试，绝不在缺闸门的情况下宣告完成。
                if (!TryRegisterDeviceReconciliationGate(dataRoot, marker.BackupId, out var reconcileGateError))
                {
                    Console.Error.WriteLine($"ERROR: the previously completed restore could not register its device reconciliation gate ({reconcileGateError}). " +
                        "Resolve the filesystem issue and re-run; the intent was preserved and the database was left unchanged.");
                    return 1;
                }
                MarkCommitted(transactionPath, marker, json);
                RetireMarker(transactionPath, legacyIntentPath, "the completed restore was reconciled");
            }
            else if (targetHash is not null && !string.IsNullOrWhiteSpace(marker.SafetyDatabaseSha256) &&
                     string.Equals(targetHash, marker.SafetyDatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                // 目标仍是切换前的保护副本内容：切换未开始或已完整回滚，直接退役。
                Console.WriteLine("an interrupted restore was detected; the target still matches its pre-restore content - nothing was switched. Retiring the intent.");
                RetireMarker(transactionPath, legacyIntentPath, "the unstarted restore intent was retired");
            }
            else if (!string.IsNullOrWhiteSpace(marker.BackupDir) && Directory.Exists(marker.BackupDir))
            {
                Console.WriteLine($"an interrupted restore was detected; restoring the previous database from '{marker.BackupDir}' first.");
                if (!TryRestoreFromBackup(marker.BackupDir, targetDb, marker, out var recoverError))
                {
                    Console.Error.WriteLine($"ERROR: could not recover the interrupted restore from its backup: {recoverError}. Manual recovery required; all files preserved.");
                    return 1;
                }
                if (!string.IsNullOrWhiteSpace(marker.SafetyDatabaseSha256) &&
                    !string.Equals(Sha256File(targetDb), marker.SafetyDatabaseSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("ERROR: the recovered database does not match the protected pre-restore hash recorded in the intent. Manual recovery required; all files preserved.");
                    return 1;
                }
                RetireMarker(transactionPath, legacyIntentPath, "the interrupted restore was recovered from its protected copy");
            }
            else
            {
                Console.Error.WriteLine("ERROR: an interrupted restore intent exists but neither the checkpoint content nor a valid protection copy can be verified; manual recovery required.");
                return 1;
            }
        }
    }

    // A01：保护副本——目标的完整文件组先复制到同目录快照，任何后续失败/进程退出都可据此恢复。
    var backupId = $"cli:{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    var backupDir = Path.Combine(targetDir, ".visionstudio-restore-backup", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
    Directory.CreateDirectory(backupDir);
    var protectedFiles = new List<string>();
    if (File.Exists(targetDb)) { File.Copy(targetDb, Path.Combine(backupDir, Path.GetFileName(targetDb)), overwrite: true); protectedFiles.Add(Path.GetFileName(targetDb)); }
    foreach (var suffix in new[] { "-wal", "-shm" })
    {
        var sidecar = targetDb + suffix;
        if (File.Exists(sidecar)) { File.Copy(sidecar, Path.Combine(backupDir, Path.GetFileName(sidecar)), overwrite: true); protectedFiles.Add(Path.GetFileName(sidecar)); }
    }
    var safetyDatabaseHash = protectedFiles.Count > 0 && File.Exists(Path.Combine(backupDir, Path.GetFileName(targetDb)))
        ? Sha256File(Path.Combine(backupDir, Path.GetFileName(targetDb)))
        : string.Empty;

    // Q04：切换期写入跨进程"维护进行中"标记（宿主 BeginExclusiveAsync 会因它拒绝新维护），
    // 崩溃后由 pid + 进程启动时间的活性判定识别为残留并允许接管。
    WriteMaintenanceActiveMarker(maintenanceActivePath, json);

    // A01/Q02：耐久意图——切换前登记统一协议文件（原子写 + 落盘）。
    // 写入失败必须中止：此时尚未改动任何东西。
    var intentRecord = new RestoreTransactionRecord
    {
        BackupId = backupId,
        StartedAt = DateTimeOffset.UtcNow,
        Stage = "switching",
        PlannedStages = ["database-replaced", "committed"],
        UpdatedAt = DateTimeOffset.UtcNow,
        Failure = null,
        TargetDatabase = targetDb,
        SafetyDatabase = Path.Combine(backupDir, Path.GetFileName(targetDb)),
        SafetyDatabaseSha256 = safetyDatabaseHash,
        SourceDatabaseSha256 = checkpointHash,
        SourceSchemaVersion = schemaVersion,
        ExpectedDatabaseSha256 = checkpointHash,
        ExpectedAssetSha256 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ProtectedAssets = [],
        PreviousTransaction = null
    };
    try
    {
        WriteFileDurably(transactionPath, JsonSerializer.SerializeToUtf8Bytes(intentRecord, json));
    }
    catch (Exception ex)
    {
        TryDeleteFile(maintenanceActivePath);
        Console.Error.WriteLine($"ERROR: could not durably record the restore intent at '{transactionPath}': {ex.Message}. Nothing was modified.");
        return 1;
    }

    var staged = Path.Combine(targetDir, $".{Path.GetFileName(targetDb)}.{Guid.NewGuid():N}.restore");
    try
    {
        File.Copy(source, staged, overwrite: true);
        // staged 必须自身可验证，拒绝把坏文件换进目标。
        if (!TryVerify(staged, out var stagedIntegrity, out _, out var stagedError))
            throw new InvalidOperationException($"staged copy failed verification: {stagedIntegrity} ({stagedError})");
        // sidecar 删除在此刻执行且其内容已在保护副本中：即使随后的主库替换失败，也能完整恢复。
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = targetDb + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
        File.Move(staged, targetDb, overwrite: true);
    }
    catch (Exception ex)
    {
        // A01：失败回滚——从保护副本恢复原文件组，目标始终保持可读。
        // 复审（2026-10-13 R2）：错误信息带上目标路径与可操作提示——"Access denied" 这类
        // 失败的最常见原因是仍有进程（宿主/调试器/SQLite 工具）持有数据库句柄。
        Console.Error.WriteLine($"ERROR: restore failed: {ex.Message} (target: '{targetDb}'). " +
            "If another process still has this database open (host, debugger, SQLite tool), stop it and retry. Rolling back from the protected copy...");
        if (!TryRestoreFromBackup(backupDir, targetDb, ToMarker(intentRecord), out var rollbackError))
        {
            Console.Error.WriteLine($"ERROR: rollback from '{backupDir}' failed: {rollbackError}. All files preserved; manual recovery required.");
            return 1;
        }
        Console.Error.WriteLine($"restore rolled back: the original database file group is intact. Protected copy kept at: {backupDir}");
        // 回滚成功：意图使命完成（数据库已恢复原状），退役切换期标记。
        RetireMarker(transactionPath, legacyIntentPath, "the failed restore was rolled back");
        TryDeleteFile(maintenanceActivePath);
        return 1;
    }
    finally
    {
        try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best-effort */ }
    }

    if (!TryVerify(targetDb, out var verified, out var restoredSchema, out var restoredError))
    {
        Console.Error.WriteLine($"ERROR: restored database failed verification: {verified} ({restoredError}). Protected copy: {backupDir}");
        return 1;
    }

    // Q01：恢复成功即登记设备核对闸门（与 API 恢复同一语义）——写失败直接回滚，
    // 绝不出现"数据已替换但没有任何核对门"的提交。已存在的安全状态一律保留
    // （旧账本表达的是既有未闭合事实，不能被合成状态覆盖）。
    if (!TryRegisterDeviceReconciliationGate(dataRoot, backupId, out var gateError))
    {
        Console.Error.WriteLine($"ERROR: the device reconciliation gate could not be registered ({gateError}). Rolling back the restore...");
        if (!TryRestoreFromBackup(backupDir, targetDb, ToMarker(intentRecord), out var rollbackError))
        {
            Console.Error.WriteLine($"ERROR: rollback from '{backupDir}' failed: {rollbackError}. All files preserved; manual recovery required.");
            return 1;
        }
        RetireMarker(transactionPath, legacyIntentPath, "the restore was rolled back because the reconciliation gate could not be registered");
        TryDeleteFile(maintenanceActivePath);
        return 1;
    }

    // Q02：耐久提交——committed 标记与"退役清理"分离。提交失败**必须**返回非零退出码：
    // 旧的实现把提交与删除放在同一个 try 里无条件吞掉异常，文件锁一幕即让一次已成功的
    // 恢复带着 stage=switching 残留退出 0，下一次运行把它当中断并误回退。提交失败时标记
    // 保持 switching，下一次运行经哈希自证（目标==检查点）即可正确识别为已完成。
    try
    {
        WriteFileDurably(transactionPath, JsonSerializer.SerializeToUtf8Bytes(intentRecord with { Stage = "committed", UpdatedAt = DateTimeOffset.UtcNow }, json));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: the database was replaced, but the committed marker could not be written durably ({ex.Message}). " +
            "The restore itself succeeded; re-run this tool to reconcile the intent (the target content is verified against the checkpoint hash).");
        return 1;
    }

    // 退役（清理）失败不再影响结果：宿主启动时会把 committed 残留只做清理，绝不锁定。
    RetireMarker(transactionPath, legacyIntentPath, "the committed restore intent was retired");
    TryDeleteFile(maintenanceActivePath);

    Console.WriteLine($"database restored: {targetDb} (checkpoint schema v{schemaVersion}, verified v{restoredSchema}, integrity={verified})");
    Console.WriteLine($"protected copy of the pre-restore file group kept at: {backupDir}");
    Console.WriteLine($"device reconciliation gate registered at: {Path.Combine(dataRoot, "production", "device-action-safety.json")}");
    Console.WriteLine("reminder: production start stays blocked until an administrator reconciles the device-action safety state with recorded physical evidence.");
    return 0;
}

/// <summary>
/// Q04：获取同数据根的跨进程维护互斥锁。优先 Global\（跨会话，服务/控制台场景），
/// 无权限时退到 Local\（同会话）。两侧使用同一套回退顺序。
/// </summary>
static IDisposable? TryAcquireMaintenanceMutex(string dataRoot, out string error)
{
    error = string.Empty;
    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant())))[..16];
    foreach (var prefix in new[] { "Global", "Local" })
    {
        var mutex = new Mutex(false, $@"{prefix}\VisionStudio.StorageMaintenance.{key}");
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                error = $"another maintenance process holds the maintenance mutex for data root '{dataRoot}'";
                return null;
            }
            return new MutexLease(mutex);
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者进程已死且未释放：所有权转移给本次运行。
            return new MutexLease(mutex);
        }
        catch (Exception)
        {
            // 该作用域不可用（如无 SeCreateGlobalPrivilege）：尝试下一个作用域。
            mutex.Dispose();
        }
    }
    error = "no maintenance mutex scope is available on this machine";
    return null;
}

/// <summary>Q04：判定另一维护进程是否仍在工作。不可读/无法判定时保守视为"活跃"。</summary>
static bool MaintenanceProcessActive(string path, out string detail)
{
    detail = string.Empty;
    if (!File.Exists(path)) return false;
    try
    {
        var marker = JsonSerializer.Deserialize<MaintenanceActiveMarker>(File.ReadAllText(path));
        if (marker is null) { detail = "unreadable marker"; return true; }
        Process? process;
        try { process = Process.GetProcessById(marker.Pid); }
        catch (ArgumentException) { process = null; } // pid 不存在 ⇒ 残留
        catch (Exception) { process = null; }
        if (process is null)
        {
            detail = $"stale marker from pid {marker.Pid} (process not running)";
            return false;
        }
        try
        {
            var startedAt = process.StartTime.ToUniversalTime();
            if (Math.Abs((startedAt - marker.ProcessStartedAt).TotalSeconds) > 2)
            {
                detail = $"stale marker from pid {marker.Pid} (start time mismatch)";
                return false;
            }
        }
        catch (Exception)
        {
            // 无法读取进程启动时间（权限等）：保守视为活跃。
        }
        detail = $"pid {marker.Pid} since {marker.ProcessStartedAt:O}";
        return true;
    }
    catch
    {
        detail = "unreadable marker";
        return true;
    }
}

static void WriteMaintenanceActiveMarker(string path, JsonSerializerOptions json)
{
    try
    {
        var marker = new MaintenanceActiveMarker(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime(), DateTimeOffset.UtcNow);
        WriteFileDurably(path, JsonSerializer.SerializeToUtf8Bytes(marker, json));
    }
    catch (Exception ex)
    {
        // 标记写失败不阻断恢复本身（互斥锁 + sidecar 检查仍在），但必须可见。
        Console.Error.WriteLine($"WARNING: could not write the maintenance-active marker at '{path}': {ex.Message}");
    }
}

/// <summary>Q02：原子耐久写——临时文件 + WriteThrough + Flush(true) + 整体替换。
/// 复审（2026-10-13 R2）：替换失败时清理临时文件（旧实现会在标记目录残留 .tmp）。</summary>
static void WriteFileDurably(string path, byte[] bytes)
{
    var directory = Path.GetDirectoryName(path)!;
    Directory.CreateDirectory(directory);
    var temp = $"{path}.{Guid.NewGuid():N}.tmp";
    try
    {
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
    catch
    {
        try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 尽力清理 */ }
        throw;
    }
}

static void TryDeleteFile(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex)
    {
        Console.Error.WriteLine($"WARNING: could not delete '{path}': {ex.Message}. It will be retired by the host on its next startup.");
    }
}




/// <summary>Q01：读取统一协议标记（优先）或旧版意图标记；都读不到返回 null。
/// 标记存在但不可解析时返回 Stage="unreadable" 的标记——目标身份无从核对，调用方必须拒绝。</summary>
static RestoreMarker? ReadRestoreMarker(string transactionPath, string legacyIntentPath, JsonSerializerOptions json)
{
    if (File.Exists(transactionPath))
    {
        try
        {
            var record = JsonSerializer.Deserialize<RestoreTransactionRecord>(File.ReadAllText(transactionPath), json);
            if (record is not null)
            {
                return new RestoreMarker(
                    record.Stage, record.TargetDatabase ?? string.Empty, DirectoryOf(record.SafetyDatabase),
                    string.IsNullOrWhiteSpace(record.ExpectedDatabaseSha256) ? record.SourceDatabaseSha256 : record.ExpectedDatabaseSha256,
                    record.SafetyDatabaseSha256,
                    [],
                    transactionPath,
                    string.IsNullOrWhiteSpace(record.BackupId) ? "cli:reconciled" : record.BackupId);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: the unified restore transaction at '{transactionPath}' could not be parsed ({ex.Message}).");
        }
        // 统一标记存在但不可解析：不能当作"无标记"——保守拒绝，等人工核对。
        return new RestoreMarker("unreadable", string.Empty, string.Empty, string.Empty, string.Empty, [], transactionPath);
    }
    if (File.Exists(legacyIntentPath))
    {
        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyRestoreIntent>(File.ReadAllText(legacyIntentPath), json);
            if (legacy is not null)
                return new RestoreMarker(legacy.Stage, legacy.TargetDatabase, legacy.BackupDir, legacy.CheckpointSha256, string.Empty, legacy.ProtectedFiles ?? [], legacyIntentPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: the legacy restore intent at '{legacyIntentPath}' could not be parsed ({ex.Message}).");
        }
        // 旧版标记存在但不可解析：目标身份无从核对，保守拒绝。
        return new RestoreMarker("unreadable", string.Empty, string.Empty, string.Empty, string.Empty, [], legacyIntentPath);
    }
    return null;
}

/// <summary>Q04：把统一协议记录转换为内存标记（回滚核对用）。</summary>
static RestoreMarker ToMarker(RestoreTransactionRecord record)
    => new(record.Stage, record.TargetDatabase ?? string.Empty, DirectoryOf(record.SafetyDatabase),
        record.ExpectedDatabaseSha256, record.SafetyDatabaseSha256, [], string.Empty);

static string DirectoryOf(string? path)
    => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

/// <summary>
/// Q02：把切换中的标记升级为 committed（哈希自证路径用）。**JSON 补丁式**更新——
/// 只改 Stage/UpdatedAt 两个字段，其余字段（含宿主 API 写的嵌套 PreviousTransaction、
/// ProtectedAssets 等回滚锚点）原样保留；绝不用本工具的局部记录覆盖完整事务。
/// </summary>
static void MarkCommitted(string transactionPath, RestoreMarker marker, JsonSerializerOptions json)
{
    if (!string.Equals(marker.RawPath, transactionPath, StringComparison.OrdinalIgnoreCase))
    {
        // 旧版格式标记：以统一协议补写 committed 后再退役旧文件。
        var record = new RestoreTransactionRecord
        {
            BackupId = $"cli:reconciled:{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
            StartedAt = DateTimeOffset.UtcNow,
            Stage = "committed",
            PlannedStages = ["database-replaced", "committed"],
            UpdatedAt = DateTimeOffset.UtcNow,
            TargetDatabase = marker.TargetDatabase,
            SafetyDatabase = marker.BackupDir,
            SafetyDatabaseSha256 = marker.SafetyDatabaseSha256,
            SourceDatabaseSha256 = marker.CheckpointSha256,
            ExpectedDatabaseSha256 = marker.CheckpointSha256
        };
        WriteFileDurably(transactionPath, JsonSerializer.SerializeToUtf8Bytes(record, json));
        TryDeleteFile(marker.RawPath);
        return;
    }
    // 统一格式：读回原 JSON、只补丁 Stage/UpdatedAt 后耐久重写（保留所有未知字段）。
    using var document = JsonDocument.Parse(File.ReadAllText(transactionPath));
    var patched = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText()) ?? [];
    patched["Stage"] = JsonSerializer.SerializeToElement("committed");
    patched["UpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
    WriteFileDurably(transactionPath, JsonSerializer.SerializeToUtf8Bytes(patched, json));
}

/// <summary>退役标记：统一协议文件与旧版意图文件都删除（幂等；失败仅警告）。</summary>
static void RetireMarker(string transactionPath, string legacyIntentPath, string reason)
{
    TryDeleteFile(transactionPath);
    TryDeleteFile(legacyIntentPath);
    Console.WriteLine($"{reason}.");
}

/// <summary>
/// Q01：登记设备核对闸门——与宿主 DeviceActionSafetyStore 同一文件与语义
/// （&lt;dataRoot&gt;/production/device-action-safety.json，Phase=Unknown ⇒ 未闭合 ⇒
/// 生产启动/设备动作被阻断，直到管理员带证据核对）。已存在的状态一律保留。
/// </summary>
static bool TryRegisterDeviceReconciliationGate(string dataRoot, string backupId, out string error)
{
    error = string.Empty;
    var path = Path.Combine(dataRoot, "production", "device-action-safety.json");
    try
    {
        if (File.Exists(path))
        {
            // 已有未闭合/待核对状态：绝不覆盖（它记录的是既有事实）。
            error = "an existing device-action safety state is already present and was preserved";
            Console.WriteLine($"device reconciliation gate already present at '{path}'; the existing state was preserved.");
            return true;
        }
        var runId = $"storage-restore:{backupId}";
        var state = new DeviceActionSafetyStateRecord(
            runId, [], [], DateTimeOffset.UtcNow,
            $"a database restore from checkpoint '{backupId}' replaced the metadata store; " +
            "verify that the physical device state matches the restored configuration before starting production.",
            Unreadable: false,
            Phase: 0,
            RunId: runId,
            DeviceFingerprints: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ProcessId: null,
            CameraIds: []);
        WriteFileDurably(path, JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}


static bool TryRestoreFromBackup(string backupDir, string targetDb, RestoreMarker marker, out string error)
{
    error = string.Empty;
    try
    {
        var targetDir = Path.GetDirectoryName(targetDb)!;
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = targetDb + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
        var mainName = Path.GetFileName(targetDb);
        var backupMain = Path.Combine(backupDir, mainName);
        if (!File.Exists(backupMain))
        {
            error = $"protected copy '{backupMain}' is missing";
            return false;
        }
        File.Copy(backupMain, targetDb, overwrite: true);
        foreach (var name in marker.ProtectedFiles)
        {
            if (string.Equals(name, mainName, StringComparison.OrdinalIgnoreCase)) continue;
            var backupFile = Path.Combine(backupDir, name);
            if (File.Exists(backupFile)) File.Copy(backupFile, Path.Combine(targetDir, name), overwrite: true);
        }
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

static bool TryVerify(string databasePath, out string integrity, out int schemaVersion, out string error)
{
    error = string.Empty;
    integrity = "verify-error";
    schemaVersion = 0;
    try
    {
        using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        connection.Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            integrity = Convert.ToString(check.ExecuteScalar()) ?? "unknown";
        }
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='schema_info') THEN (SELECT version FROM schema_info WHERE id=1) ELSE 0 END;";
        schemaVersion = Convert.ToInt32(schema.ExecuteScalar());
        return string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex)
    {
        // A05：验证失败（含 schema 查询异常）必须显式失败，绝不静默按 v0 处理。
        error = ex.Message;
        return false;
    }
}

static string BuildConnectionString(string path, SqliteOpenMode mode)
    => new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

static string Sha256File(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

internal sealed record CheckpointManifest
{
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("source_database")] public string SourceDatabase { get; init; } = string.Empty;
    [JsonPropertyName("snapshot_sha256")] public string SnapshotSha256 { get; init; } = string.Empty;
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("integrity")] public string Integrity { get; init; } = string.Empty;
}

/// <summary>Q04：命名互斥锁租约（进程退出/崩溃时由内核自动释放，不会永久阻塞后续维护）。</summary>
internal sealed class MutexLease(Mutex mutex) : IDisposable
{
    public void Dispose()
    {
        try { mutex.ReleaseMutex(); } catch { /* 进程正在退出时尽力释放 */ }
        mutex.Dispose();
    }
}

/// <summary>Q04：跨进程"维护进行中"标记的内容（pid + 进程启动时间用于残留判定）。</summary>
internal sealed record MaintenanceActiveMarker(int Pid, DateTimeOffset ProcessStartedAt, DateTimeOffset At);

/// <summary>
/// Q01：统一恢复标记的内存表示（兼容宿主 restore-transaction.json 与本工具旧版
/// .visionstudio-restore-intent.json 两种格式）。目标身份、检查点哈希与保护副本哈希
/// 是中断恢复判定的三个事实来源；BackupId 用于自证和解分支登记设备核对闸门（R2 复审）。
/// </summary>
internal sealed record RestoreMarker(
    string Stage,
    string TargetDatabase,
    string BackupDir,
    string CheckpointSha256,
    string SafetyDatabaseSha256,
    IReadOnlyList<string> ProtectedFiles,
    string RawPath,
    string BackupId = "cli:reconciled");

/// <summary>宿主协议记录（PascalCase 与 StorageMaintenanceCoordinator.RestoreTransaction 对齐）。</summary>
internal sealed record RestoreTransactionRecord
{
    [JsonPropertyName("BackupId")] public string BackupId { get; init; } = string.Empty;
    [JsonPropertyName("StartedAt")] public DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("Stage")] public string Stage { get; init; } = "switching";
    [JsonPropertyName("PlannedStages")] public IReadOnlyList<string> PlannedStages { get; init; } = [];
    [JsonPropertyName("UpdatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("Failure")] public string? Failure { get; init; }
    /// <summary>Q04（2026-10-13）：意图的目标数据库——中断恢复前必须与当前目标核对。</summary>
    [JsonPropertyName("TargetDatabase")] public string? TargetDatabase { get; init; }
    [JsonPropertyName("SafetyDatabase")] public string SafetyDatabase { get; init; } = string.Empty;
    [JsonPropertyName("SafetyDatabaseSha256")] public string SafetyDatabaseSha256 { get; init; } = string.Empty;
    [JsonPropertyName("SourceDatabaseSha256")] public string SourceDatabaseSha256 { get; init; } = string.Empty;
    [JsonPropertyName("SourceSchemaVersion")] public int SourceSchemaVersion { get; init; }
    [JsonPropertyName("ExpectedDatabaseSha256")] public string ExpectedDatabaseSha256 { get; init; } = string.Empty;
    [JsonPropertyName("ExpectedAssetSha256")] public IReadOnlyDictionary<string, string> ExpectedAssetSha256 { get; init; } = new Dictionary<string, string>();
    /// <summary>宿主侧是对象数组（RestoreProtectionAsset）；此处只按通用元素读取，本工具只写空数组。</summary>
    [JsonPropertyName("ProtectedAssets")] public IReadOnlyList<JsonElement> ProtectedAssets { get; init; } = [];
    [JsonPropertyName("PreviousTransaction")] public object? PreviousTransaction { get; init; }
}

/// <summary>旧版工具意图文件（snake_case，仅读取兼容）。</summary>
internal sealed record LegacyRestoreIntent
{
    [JsonPropertyName("target_database")] public string TargetDatabase { get; init; } = string.Empty;
    [JsonPropertyName("backup_dir")] public string BackupDir { get; init; } = string.Empty;
    [JsonPropertyName("protected_files")] public IReadOnlyList<string>? ProtectedFiles { get; init; }
    [JsonPropertyName("checkpoint_dir")] public string CheckpointDir { get; init; } = string.Empty;
    [JsonPropertyName("checkpoint_sha256")] public string CheckpointSha256 { get; init; } = string.Empty;
    [JsonPropertyName("stage")] public string Stage { get; init; } = "switching";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>设备安全状态记录（camelCase，与 DeviceActionSafetyState 的 Web 默认序列化对齐）。</summary>
internal sealed record DeviceActionSafetyStateRecord(
    [property: JsonPropertyName("manifestHash")] string ManifestHash,
    [property: JsonPropertyName("deviceIds")] IReadOnlyList<string> DeviceIds,
    [property: JsonPropertyName("robotIds")] IReadOnlyList<string> RobotIds,
    [property: JsonPropertyName("setAt")] DateTimeOffset SetAt,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("unreadable")] bool Unreadable = false,
    [property: JsonPropertyName("phase")] int Phase = 0,
    [property: JsonPropertyName("runId")] string? RunId = null,
    [property: JsonPropertyName("deviceFingerprints")] IReadOnlyDictionary<string, string>? DeviceFingerprints = null,
    [property: JsonPropertyName("processId")] string? ProcessId = null,
    [property: JsonPropertyName("cameraIds")] IReadOnlyList<string>? CameraIds = null);
