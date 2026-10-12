using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

// VisionStudio 数据库维护工具（R08/F06 + 第三轮复核 A01/A05，2026-10-12）：
//
//   snapshot <sourceDb> <checkpointDir>
//     用 SQLite Online Backup API 获取**一致性快照**（活动写者/checkpoint 并发下也安全），
//     执行 integrity_check、记录 schema 与内容 SHA-256 清单（manifest.json）。
//
//   restore <checkpointDir> <targetDb> [--force]
//     事务化整体替换目标数据库：
//       1. 清单校验（A05）：manifest 必须存在，内容哈希/schema 必须与检查点一致——
//          "结构完整"不等于"是原来批准的恢复点"，清单不只写还要读；
//       2. 中断恢复（A01）：上次 restore 若在切换中途终止（意图文件 Stage=switching），
//          先从保护副本自动还原目标；Stage=committed 的残留只做清理；
//       3. 活跃保护（A01）：目标存在非空 WAL/SHM（宿主可能仍在运行）时拒绝，--force 显式绕过；
//       4. 保护副本（A01）：目标的完整文件组（主库 + WAL/SHM）先复制到同目录保护快照；
//       5. 耐久意图：切换前登记 restore-intent.json；
//       6. 切换：staged 先自检 → 删除 sidecar（其内容已在保护副本中）→ 原子替换主库；
//       7. 任一失败：从保护副本恢复原文件组（目标始终保持可读），意图保留供人工核对；
//       8. 成功：先标记 committed 再退役意图，保护副本保留并输出路径。
//     设备动作安全账本（production 目录）不在此工具范围内，始终独立保留。
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
    var actualHash = Sha256File(source);
    if (!string.Equals(actualHash, manifest.SnapshotSha256, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"ERROR: checkpoint content does not match its manifest (expected {manifest.SnapshotSha256}, actual {actualHash}); refusing to restore.");
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
    var intentPath = Path.Combine(targetDir, ".visionstudio-restore-intent.json");

    // A01：中断恢复——上次 restore 若在切换中途终止，意图文件仍在（Stage=switching）：
    // 先从保护副本自动还原目标；committed 的残留只做清理，绝不把已成功的恢复回退。
    if (File.Exists(intentPath))
    {
        var intent = TryReadIntent(intentPath, json);
        if (string.Equals(intent?.Stage, "committed", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(intentPath); } catch { /* 残留的 committed 意图无副作用，下次再清理 */ }
            Console.WriteLine("a previously committed restore was cleaned up.");
        }
        else if (intent?.BackupDir is { Length: > 0 } interruptedBackup && Directory.Exists(interruptedBackup))
        {
            Console.WriteLine($"an interrupted restore was detected; restoring the previous database from '{interruptedBackup}' first.");
            if (!TryRestoreFromBackup(interruptedBackup, targetDb, intent, out var recoverError))
            {
                Console.Error.WriteLine($"ERROR: could not recover the interrupted restore from its backup: {recoverError}. Manual recovery required; all files preserved.");
                return 1;
            }
            try { File.Delete(intentPath); } catch { /* 还原完成后退役；失败不阻塞（下次按备份存在处理） */ }
        }
        else
        {
            Console.Error.WriteLine("ERROR: an interrupted restore intent exists but its backup directory is missing; manual recovery required.");
            return 1;
        }
    }

    // A01：活跃保护——目标存在非空 WAL/SHM 通常意味着宿主仍在运行（或异常退出未清理）。
    // 停机维护窗口是回滚的正确前提；--force 仅为异常场景的显式逃生口。
    foreach (var suffix in new[] { "-wal", "-shm" })
    {
        var sidecar = targetDb + suffix;
        if (File.Exists(sidecar) && new FileInfo(sidecar).Length > 0 && !force)
        {
            Console.Error.WriteLine($"ERROR: '{sidecar}' is non-empty - the VisionStudio host may still be running. Stop the host before restoring (or pass --force to override with full responsibility).");
            return 1;
        }
    }

    // A01：保护副本——目标的完整文件组先复制到同目录快照，任何后续失败/进程退出都可据此恢复。
    var backupDir = Path.Combine(targetDir, ".visionstudio-restore-backup", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
    Directory.CreateDirectory(backupDir);
    var protectedFiles = new List<string>();
    if (File.Exists(targetDb)) { File.Copy(targetDb, Path.Combine(backupDir, Path.GetFileName(targetDb)), overwrite: true); protectedFiles.Add(Path.GetFileName(targetDb)); }
    foreach (var suffix in new[] { "-wal", "-shm" })
    {
        var sidecar = targetDb + suffix;
        if (File.Exists(sidecar)) { File.Copy(sidecar, Path.Combine(backupDir, Path.GetFileName(sidecar)), overwrite: true); protectedFiles.Add(Path.GetFileName(sidecar)); }
    }

    // A01：耐久意图——切换前登记；进程在切换中途退出时，下次运行据此恢复。
    var intentRecord = new RestoreIntent
    {
        TargetDatabase = targetDb,
        BackupDir = backupDir,
        ProtectedFiles = protectedFiles,
        CheckpointDir = checkpointDir,
        CheckpointSha256 = manifest.SnapshotSha256,
        Stage = "switching",
        CreatedAt = DateTimeOffset.UtcNow
    };
    File.WriteAllText(intentPath, JsonSerializer.Serialize(intentRecord, json));

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
        // A01：失败回滚——从保护副本恢复原文件组，目标始终保持可读；意图保留供人工核对。
        Console.Error.WriteLine($"ERROR: restore failed: {ex.Message}. Rolling back from the protected copy...");
        if (!TryRestoreFromBackup(backupDir, targetDb, intentRecord, out var rollbackError))
        {
            Console.Error.WriteLine($"ERROR: rollback from '{backupDir}' failed: {rollbackError}. All files preserved; manual recovery required.");
            return 1;
        }
        Console.Error.WriteLine($"restore rolled back: the original database file group is intact. Protected copy kept at: {backupDir}");
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
    // 成功：先标记 committed 再退役意图——残留的 committed 意图在下次运行只做清理，
    // 绝不把一次已成功的恢复当作"中断的切换"回退。
    try
    {
        intentRecord = intentRecord with { Stage = "committed" };
        File.WriteAllText(intentPath, JsonSerializer.Serialize(intentRecord, json));
        File.Delete(intentPath);
    }
    catch { /* 退役失败不改变已成功的结果 */ }
    Console.WriteLine($"database restored: {targetDb} (checkpoint schema v{schemaVersion}, verified v{restoredSchema}, integrity={verified})");
    Console.WriteLine($"protected copy of the pre-restore file group kept at: {backupDir}");
    Console.WriteLine("reminder: device-action safety ledgers stay independent; re-verify physical device state before resuming production.");
    return 0;
}

static RestoreIntent? TryReadIntent(string path, JsonSerializerOptions json)
{
    try { return JsonSerializer.Deserialize<RestoreIntent>(File.ReadAllText(path), json); }
    catch { return null; }
}

static bool TryRestoreFromBackup(string backupDir, string targetDb, RestoreIntent? intent, out string error)
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
        foreach (var name in intent?.ProtectedFiles ?? [])
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

internal sealed record RestoreIntent
{
    [JsonPropertyName("target_database")] public string TargetDatabase { get; init; } = string.Empty;
    [JsonPropertyName("backup_dir")] public string BackupDir { get; init; } = string.Empty;
    [JsonPropertyName("protected_files")] public IReadOnlyList<string>? ProtectedFiles { get; init; }
    [JsonPropertyName("checkpoint_dir")] public string CheckpointDir { get; init; } = string.Empty;
    [JsonPropertyName("checkpoint_sha256")] public string CheckpointSha256 { get; init; } = string.Empty;
    [JsonPropertyName("stage")] public string Stage { get; init; } = "switching";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}
