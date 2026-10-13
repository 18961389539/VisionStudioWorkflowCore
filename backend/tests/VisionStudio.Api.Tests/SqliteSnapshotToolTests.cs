using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 2026-10-13 全面审查 Q01/Q02/Q04：离线维护工具（VisionStudio.SqliteSnapshot）的集成回归。
/// 以**真实子进程**运行工具 DLL，验证：
///   · Q02：意图/提交标记写入失败 ⇒ 非零退出码，且不谎称成功、不修改数据；
///   · Q02：切换已完成但 committed 标记缺失（进程退出窗口）⇒ 哈希自证补登记，绝不误回退；
///   · Q04：意图目标不匹配 / 活跃 sidecar / 另一维护进程 ⇒ 在任何修改之前拒绝；
///   · Q01：成功后登记设备核对闸门，退役统一协议标记。
/// </summary>
public sealed class SqliteSnapshotToolTests : IDisposable
{
    private readonly string _lab = Path.Combine(Path.GetTempPath(), "vs-snapshot-tool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_lab, recursive: true); } catch { /* 临时目录尽力清理 */ }
    }

    [Fact]
    public async Task Restore_Succeeds_RetiresUnifiedIntent_AndRegistersDeviceReconciliationGate()
    {
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        var result = RunTool("restore", checkpoint, target);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("CHECKPOINT", ReadProbeLabel(target));
        // Q01：统一协议标记与旧版意图文件都已退役；跨进程维护标记同样撤销。
        Assert.False(File.Exists(Path.Combine(_lab, "data", ".storage-maintenance", "restore-transaction.json")));
        Assert.False(File.Exists(Path.Combine(_lab, "data", ".visionstudio-restore-intent.json")));
        Assert.False(File.Exists(Path.Combine(_lab, "data", ".storage-maintenance", "maintenance-active.json")));
        // Q01：设备核对闸门已登记（Phase=Unknown ⇒ 生产启动前必须核对）。
        var gate = Path.Combine(_lab, "data", "production", "device-action-safety.json");
        Assert.True(File.Exists(gate), "device reconciliation gate must be registered after a successful restore");
        using var document = JsonDocument.Parse(File.ReadAllText(gate));
        Assert.Equal(0, document.RootElement.GetProperty("phase").GetInt32());
        Assert.StartsWith("storage-restore:", document.RootElement.GetProperty("runId").GetString()!, StringComparison.Ordinal);
        // 保护副本保留（人工取证/再次恢复锚点）。
        Assert.True(Directory.Exists(Path.Combine(_lab, "data", ".visionstudio-restore-backup")));
    }

    [Fact]
    public void Restore_DurableMarkerWriteRefused_FailsWithoutClaimingSuccessOrTouchingData()
    {
        // Q02：意图标记写入被拒（文件锁）必须返回非零退出码——旧实现在提交/清理共用的 catch 里
        // 无条件吞掉异常，带着 stage=switching 的残留退出 0，把已成功的恢复误判为中断。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        var dataDir = Path.Combine(_lab, "data");
        var markerPath = Path.Combine(dataDir, ".storage-maintenance", "restore-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        // 先放一个"切换未开始"的有效 switching 意图（目标匹配、保护副本哈希=当前内容），
        // 再用独占句柄锁住标记文件——任何写入/替换/删除都会失败。
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new
        {
            BackupId = "cli:test",
            StartedAt = DateTimeOffset.UtcNow,
            Stage = "switching",
            PlannedStages = new[] { "database-replaced", "committed" },
            UpdatedAt = DateTimeOffset.UtcNow,
            Failure = (string?)null,
            TargetDatabase = target,
            SafetyDatabase = Path.Combine(_lab, "missing-backup", "visionstudio.db"),
            SafetyDatabaseSha256 = Sha256(target),
            SourceDatabaseSha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            SourceSchemaVersion = 1,
            ExpectedDatabaseSha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            ExpectedAssetSha256 = new Dictionary<string, string>(),
            ProtectedAssets = Array.Empty<object>(),
            PreviousTransaction = (object?)null
        }));
        using var lockHandle = File.Open(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = RunTool("restore", checkpoint, target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("intent", result.StandardError + result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        // 数据未被修改：一次被拒绝的标记写入绝不允许伴随数据替换。
        Assert.Equal("ORIGINAL", ReadProbeLabel(target));
    }

    [Fact]
    public void Restore_InterruptedAfterSwitch_ReconcilesInsteadOfRollingBack()
    {
        // Q02：进程在"主库已替换、committed 标记未写"的窗口退出——目标内容已等于检查点。
        // 下一次运行必须经哈希自证识别为"已完成"并补登记 committed，绝不能把已成功的恢复回退。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        // 模拟"切换已完成"：目标=检查点内容；保护副本里放着切换前的 ORIGINAL。
        var checkpointHash = Sha256(Path.Combine(checkpoint, "visionstudio.db"));
        File.Copy(Path.Combine(checkpoint, "visionstudio.db"), target, overwrite: true);
        var backupDir = Path.Combine(_lab, "data", ".visionstudio-restore-backup", "20261013000000000");
        Directory.CreateDirectory(backupDir);
        File.Copy(Path.Combine(_lab, "source.db"), Path.Combine(backupDir, "visionstudio.db"), overwrite: true);
        var markerPath = Path.Combine(_lab, "data", ".storage-maintenance", "restore-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new
        {
            BackupId = "cli:test",
            StartedAt = DateTimeOffset.UtcNow,
            Stage = "switching",
            PlannedStages = new[] { "database-replaced", "committed" },
            UpdatedAt = DateTimeOffset.UtcNow,
            Failure = (string?)null,
            TargetDatabase = target,
            SafetyDatabase = Path.Combine(backupDir, "visionstudio.db"),
            SafetyDatabaseSha256 = Sha256(Path.Combine(backupDir, "visionstudio.db")),
            SourceDatabaseSha256 = checkpointHash,
            SourceSchemaVersion = 1,
            ExpectedDatabaseSha256 = checkpointHash,
            ExpectedAssetSha256 = new Dictionary<string, string>(),
            ProtectedAssets = Array.Empty<object>(),
            PreviousTransaction = (object?)null
        }));

        var result = RunTool("restore", checkpoint, target);

        Assert.Equal(0, result.ExitCode);
        // 关键断言：没有被回退成 ORIGINAL——旧实现会在这里丢失一次已成功的恢复。
        Assert.Equal("CHECKPOINT", ReadProbeLabel(target));
        Assert.False(File.Exists(markerPath));
        // 自证路径同样补登记设备核对闸门。
        Assert.True(File.Exists(Path.Combine(_lab, "data", "production", "device-action-safety.json")));
    }

    [Fact]
    public void Restore_IntentForAnotherTarget_IsRefusedWithoutTouchingData()
    {
        // Q04：意图属于另一个目标 ⇒ 拒绝消费（旧实现会拿别人的保护副本覆盖当前目标）。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        var otherTarget = Path.Combine(_lab, "data", "other.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        CreateSyntheticDatabase(otherTarget, "OTHER");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        var backupDir = Path.Combine(_lab, "data", ".visionstudio-restore-backup", "20261013000000001");
        Directory.CreateDirectory(backupDir);
        File.Copy(otherTarget, Path.Combine(backupDir, "visionstudio.db"), overwrite: true);
        var markerPath = Path.Combine(_lab, "data", ".storage-maintenance", "restore-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new
        {
            BackupId = "cli:test",
            StartedAt = DateTimeOffset.UtcNow,
            Stage = "switching",
            PlannedStages = new[] { "database-replaced", "committed" },
            UpdatedAt = DateTimeOffset.UtcNow,
            Failure = (string?)null,
            TargetDatabase = otherTarget,
            SafetyDatabase = Path.Combine(backupDir, "visionstudio.db"),
            SafetyDatabaseSha256 = Sha256(otherTarget),
            SourceDatabaseSha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            SourceSchemaVersion = 1,
            ExpectedDatabaseSha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            ExpectedAssetSha256 = new Dictionary<string, string>(),
            ProtectedAssets = Array.Empty<object>(),
            PreviousTransaction = (object?)null
        }));

        var result = RunTool("restore", checkpoint, target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("different target", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ORIGINAL", ReadProbeLabel(target));
    }

    [Fact]
    public void Restore_ActiveHostSidecar_RefusesBeforeInterruptRecoveryModifiesAnything()
    {
        // Q04：活跃保护（非空 WAL/SHM ⇒ 宿主可能在运行）必须**早于**中断恢复的删除/复制。
        // 旧实现先删 sidecar、回拷保护副本，之后才检查活跃性——先破坏后判断。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        // 非空 WAL（宿主仍在运行的启发式信号）。
        var wal = target + "-wal";
        File.WriteAllText(wal, new string('x', 4096));
        // 同时放一个"可回滚"的中断意图：旧代码会在这里先执行回拷（修改数据）。
        var backupDir = Path.Combine(_lab, "data", ".visionstudio-restore-backup", "20261013000000002");
        Directory.CreateDirectory(backupDir);
        CreateSyntheticDatabase(Path.Combine(backupDir, "visionstudio.db"), "STALE-PRERESTORE");
        var markerPath = Path.Combine(_lab, "data", ".storage-maintenance", "restore-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new
        {
            BackupId = "cli:test",
            StartedAt = DateTimeOffset.UtcNow,
            Stage = "switching",
            PlannedStages = new[] { "database-replaced", "committed" },
            UpdatedAt = DateTimeOffset.UtcNow,
            Failure = (string?)null,
            TargetDatabase = target,
            SafetyDatabase = Path.Combine(backupDir, "visionstudio.db"),
            SafetyDatabaseSha256 = Sha256(Path.Combine(backupDir, "visionstudio.db")),
            SourceDatabaseSha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            SourceSchemaVersion = 1,
            ExpectedDatabaseSha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            ExpectedAssetSha256 = new Dictionary<string, string>(),
            ProtectedAssets = Array.Empty<object>(),
            PreviousTransaction = (object?)null
        }));

        var result = RunTool("restore", checkpoint, target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("non-empty", result.StandardError, StringComparison.OrdinalIgnoreCase);
        // 中断恢复没有发生：目标与 WAL 都保持原样。
        Assert.Equal("ORIGINAL", ReadProbeLabel(target));
        Assert.True(new FileInfo(wal).Length > 0);
    }

    [Fact]
    public void Restore_AnotherMaintenanceProcessActive_IsRefused()
    {
        // Q04：另一维护进程的活跃标记（pid + 启动时间均在运行）⇒ 拒绝。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        var markerPath = Path.Combine(_lab, "data", ".storage-maintenance", "maintenance-active.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new
        {
            Pid = Environment.ProcessId,
            ProcessStartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            At = DateTimeOffset.UtcNow,
            Reason = "test holder"
        }));

        var result = RunTool("restore", checkpoint, target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("another storage maintenance process is active", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ORIGINAL", ReadProbeLabel(target));

        // R2 复审：活跃维护是**事实**而非启发式——--force 也不得越过（防止两个维护流同时
        // 替换数据库/系统资产造成撕裂状态）。
        var forced = RunTool("restore", checkpoint, target, "--force");
        Assert.NotEqual(0, forced.ExitCode);
        Assert.Contains("another storage maintenance process is active", forced.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ORIGINAL", ReadProbeLabel(target));
    }

    [Fact]
    public void Restore_LegacyCommittedIntent_IsRetiredAndRestoreProceeds()
    {
        // Q01：旧版工具的 committed 残留只做清理退役，不锁定、不回退。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        var legacyPath = Path.Combine(_lab, "data", ".visionstudio-restore-intent.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
        {
            target_database = target,
            backup_dir = Path.Combine(_lab, "missing-backup"),
            protected_files = Array.Empty<string>(),
            checkpoint_dir = checkpoint,
            checkpoint_sha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            stage = "committed",
            created_at = DateTimeOffset.UtcNow
        }));

        var result = RunTool("restore", checkpoint, target);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(legacyPath));
        Assert.Equal("CHECKPOINT", ReadProbeLabel(target));
    }

    [Fact]
    public void Restore_LegacySwitchingIntent_IsRecoveredFromItsProtectedCopy()
    {
        // Q01/Q04：旧版 switching 意图（目标匹配、保护副本在）⇒ 先从保护副本恢复再继续。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "HALF-SWITCHED");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        var backupDir = Path.Combine(_lab, "data", ".visionstudio-restore-backup", "20261013000000003");
        Directory.CreateDirectory(backupDir);
        CreateSyntheticDatabase(Path.Combine(backupDir, "visionstudio.db"), "ORIGINAL");
        var legacyPath = Path.Combine(_lab, "data", ".visionstudio-restore-intent.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
        {
            target_database = target,
            backup_dir = backupDir,
            protected_files = new[] { "visionstudio.db" },
            checkpoint_dir = checkpoint,
            checkpoint_sha256 = Sha256(Path.Combine(checkpoint, "visionstudio.db")),
            stage = "switching",
            created_at = DateTimeOffset.UtcNow
        }));

        var result = RunTool("restore", checkpoint, target);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(legacyPath));
        // 恢复的中断被回退到保护副本内容后，本次恢复再把检查点换上。
        Assert.Equal("CHECKPOINT", ReadProbeLabel(target));
    }

    [Fact]
    public void Restore_ReconciledCompletedRestore_RegistersGateBeforeReapply_AndRetriesWhenBlocked()
    {
        // R2 复审（2026-10-13）：自证和解分支必须先登记核对闸门——否则"和解后的重放失败回滚"
        // 会留下"数据已替换却无任何核对门"的窗口。闸门写入被阻塞时必须保留 switching 标记
        // 并以非零退出码报告，绝不宣告完成。
        var checkpoint = Path.Combine(_lab, "checkpoint");
        var target = Path.Combine(_lab, "data", "visionstudio.db");
        CreateSyntheticDatabase(Path.Combine(_lab, "source.db"), "CHECKPOINT");
        CreateSyntheticDatabase(target, "ORIGINAL");
        Assert.Equal(0, RunTool("snapshot", Path.Combine(_lab, "source.db"), checkpoint).ExitCode);

        var checkpointHash = Sha256(Path.Combine(checkpoint, "visionstudio.db"));
        File.Copy(Path.Combine(checkpoint, "visionstudio.db"), target, overwrite: true);
        var backupDir = Path.Combine(_lab, "data", ".visionstudio-restore-backup", "20261013210000000");
        Directory.CreateDirectory(backupDir);
        CreateSyntheticDatabase(Path.Combine(backupDir, "visionstudio.db"), "ORIGINAL");
        var markerPath = Path.Combine(_lab, "data", ".storage-maintenance", "restore-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new
        {
            BackupId = "cli:20261013210000000",
            StartedAt = DateTimeOffset.UtcNow,
            Stage = "switching",
            PlannedStages = new[] { "database-replaced", "committed" },
            UpdatedAt = DateTimeOffset.UtcNow,
            Failure = (string?)null,
            TargetDatabase = target,
            SafetyDatabase = Path.Combine(backupDir, "visionstudio.db"),
            SafetyDatabaseSha256 = Sha256(Path.Combine(backupDir, "visionstudio.db")),
            SourceDatabaseSha256 = checkpointHash,
            SourceSchemaVersion = 1,
            ExpectedDatabaseSha256 = checkpointHash,
            ExpectedAssetSha256 = new Dictionary<string, string>(),
            ProtectedAssets = Array.Empty<object>(),
            PreviousTransaction = (object?)null
        }));

        // 阻塞闸门写入：闸门路径先被一个目录占据（WriteFileDurably 的原子替换必然失败）。
        var gatePath = Path.Combine(_lab, "data", "production", "device-action-safety.json");
        Directory.CreateDirectory(gatePath);
        var blocked = RunTool("restore", checkpoint, target);
        Assert.NotEqual(0, blocked.ExitCode);
        Assert.Contains("reconciliation gate", blocked.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(markerPath), "the intent must be preserved for retry");
        Assert.Equal("CHECKPOINT", ReadProbeLabel(target));

        // 解除阻塞后重跑：闸门以文件落盘，重放成功。
        Directory.Delete(gatePath, true);
        var retry = RunTool("restore", checkpoint, target);
        Assert.Equal(0, retry.ExitCode);
        Assert.True(File.Exists(gatePath));
        Assert.Equal("CHECKPOINT", ReadProbeLabel(target));
        Assert.False(File.Exists(markerPath));
    }

    private static void CreateSyntheticDatabase(string path, string label)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Pooling=False：连接关闭后句柄立即释放——否则池化的连接会一直持有文件，
        // 让测试内的 Sha256/独占锁断言随机失败。
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
CREATE TABLE schema_info(id INTEGER PRIMARY KEY, version INTEGER NOT NULL);
INSERT INTO schema_info(id,version) VALUES(1,1);
CREATE TABLE probe(value TEXT);
INSERT INTO probe(value) VALUES($label);
""";
        command.Parameters.AddWithValue("$label", label);
        command.ExecuteNonQuery();
    }

    private static string ReadProbeLabel(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM probe;";
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string LocateToolDll()
    {
        var candidates = new List<string>();
        // 1) 测试输出目录：项目引用会把工具 DLL 复制到这里（当前构建，永远最新）。
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "VisionStudio.SqliteSnapshot.dll"));
        // 2) 经典布局与 --artifacts-path 布局：向上查找 backend 根（含 VisionStudio.slnx）。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, "src", "VisionStudio.SqliteSnapshot", "bin", "Release", "net10.0", "VisionStudio.SqliteSnapshot.dll"));
            if (File.Exists(Path.Combine(directory.FullName, "VisionStudio.slnx")))
            {
                foreach (var artifacts in Directory.EnumerateDirectories(directory.FullName, ".artifacts*"))
                {
                    var bin = Path.Combine(artifacts, "bin", "VisionStudio.SqliteSnapshot");
                    if (Directory.Exists(bin))
                        candidates.AddRange(Directory.EnumerateFiles(bin, "VisionStudio.SqliteSnapshot.dll", SearchOption.AllDirectories));
                }
            }
            directory = directory.Parent;
        }
        // 优先使用测试输出目录中的当前构建；其余候选取最近构建（避免拿到历史遗留 DLL）。
        var preferred = candidates.FirstOrDefault(File.Exists);
        if (preferred is not null) return preferred;
        var newest = candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (newest is not null) return newest;
        throw new InvalidOperationException("VisionStudio.SqliteSnapshot.dll was not found; build the backend solution before running this test.");
    }

    private sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

    private static ToolResult RunTool(params string[] arguments)
    {
        var dll = LocateToolDll();
        var start = new ProcessStartInfo("dotnet", string.Join(' ', arguments.Prepend(dll).Select(Quote)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) throw new TimeoutException("the maintenance tool did not exit in time");
        Assert.True(process.ExitCode is 0 or 1 or 2, $"unexpected tool exit code {process.ExitCode}");
        return new ToolResult(process.ExitCode, stdout.Result, stderr.Result);

        static string Quote(string value)
            => value.Contains(' ') ? $"\"{value}\"" : value;
    }
}
