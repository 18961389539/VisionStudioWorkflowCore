using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// VisionStudio 数据库维护工具（R08/F06，2026-10-12 复核）：
//
//   snapshot <sourceDb> <checkpointDir>
//     用 SQLite Online Backup API 获取**一致性快照**（活动写者/checkpoint 并发下也安全，
//     无需停机），随后执行 integrity_check 并记录 schema 版本，写出 manifest.json。
//     旧发布脚本的裸文件复制在活动写入下可能产生撕裂快照（WAL 与主库不同步）。
//
//   restore <checkpointDir> <targetDb>
//     停机维护窗口内**整体替换**目标数据库：先校验检查点完整性，再暂存复制、
//     清理目标残留 WAL/SHM（新版本迁移遗留的 sidecar 不能与替换后的主库配对）、
//     原子替换、最后验证 integrity/schema。设备动作安全账本（production 目录）不在此
//     工具范围内，始终独立保留——数据回滚绝不等同于设备核对。
//
// 退出码：0 = 成功；非 0 = 失败（调用方必须据此中止发布/回滚流程）。

try
{
    return (args.Length > 0 ? args[0] : string.Empty) switch
    {
        "snapshot" when args.Length == 3 => Snapshot(args[1], args[2]),
        "restore" when args.Length == 3 => Restore(args[1], args[2]),
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
    Console.Error.WriteLine("       VisionStudio.SqliteSnapshot restore <checkpointDir> <targetDb>");
    return 2;
}

static int Snapshot(string sourceDb, string checkpointDir)
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

    if (!TryVerify(destination, out var integrity, out var schemaVersion))
    {
        Console.Error.WriteLine($"ERROR: checkpoint integrity check failed: {integrity}");
        return 1;
    }

    var manifest = new
    {
        created_at = DateTimeOffset.UtcNow,
        source_database = sourceDb,
        snapshot_sha256 = Sha256File(destination),
        schema_version = schemaVersion,
        integrity
    };
    File.WriteAllText(Path.Combine(checkpointDir, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    Console.WriteLine($"checkpoint snapshot: {destination} (schema v{schemaVersion}, integrity={integrity})");
    return 0;
}

static int Restore(string checkpointDir, string targetDb)
{
    var source = Path.Combine(checkpointDir, "visionstudio.db");
    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"ERROR: checkpoint '{checkpointDir}' has no visionstudio.db.");
        return 1;
    }
    // 先验证检查点，绝不在未验证的情况下动目标。
    if (!TryVerify(source, out var integrity, out var schemaVersion))
    {
        Console.Error.WriteLine($"ERROR: checkpoint integrity check failed: {integrity}");
        return 1;
    }

    targetDb = Path.GetFullPath(targetDb);
    var targetDir = Path.GetDirectoryName(targetDb)!;
    Directory.CreateDirectory(targetDir);
    var staged = Path.Combine(targetDir, $".{Path.GetFileName(targetDb)}.{Guid.NewGuid():N}.restore");
    try
    {
        File.Copy(source, staged, overwrite: true);
        // 目标残留 sidecar（新版本迁移后的 WAL/SHM）绝不能与替换后的主库配对。
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = targetDb + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
        File.Move(staged, targetDb, overwrite: true);
    }
    finally
    {
        try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best-effort */ }
    }

    if (!TryVerify(targetDb, out var verified, out var restoredSchema))
    {
        Console.Error.WriteLine($"ERROR: restored database failed verification: {verified}");
        return 1;
    }
    Console.WriteLine($"database restored: {targetDb} (checkpoint schema v{schemaVersion}, verified v{restoredSchema}, integrity={verified})");
    Console.WriteLine("reminder: device-action safety ledgers stay independent; re-verify physical device state before resuming production.");
    return 0;
}

static bool TryVerify(string databasePath, out string integrity, out int schemaVersion)
{
    using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
    connection.Open();
    using (var check = connection.CreateCommand())
    {
        check.CommandText = "PRAGMA integrity_check;";
        integrity = Convert.ToString(check.ExecuteScalar()) ?? "unknown";
    }
    schemaVersion = 0;
    try
    {
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='schema_info') THEN (SELECT version FROM schema_info WHERE id=1) ELSE 0 END;";
        schemaVersion = Convert.ToInt32(schema.ExecuteScalar());
    }
    catch { schemaVersion = 0; }
    return string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);
}

static string BuildConnectionString(string path, SqliteOpenMode mode)
    => new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

static string Sha256File(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
