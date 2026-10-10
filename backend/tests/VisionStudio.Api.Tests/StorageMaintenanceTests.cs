using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class StorageMaintenanceTests
{
    [Fact]
    public async Task FreshDatabase_RunsOrderedMigrations_AndRecordsChecksummedHistory()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var status = await db.GetSchemaStatusAsync();

        // schema 版本随迁移演进（当前 20）：断言跟随实现常量而非硬编码快照
        Assert.Equal(db.CurrentSchemaVersion, status.CurrentVersion);
        Assert.Equal(db.CurrentSchemaVersion, status.TargetVersion);
        Assert.True(status.UpToDate);
        Assert.Equal(db.CurrentSchemaVersion, status.History.Count);
        Assert.All(status.History, item => Assert.False(item.Baselined));
        Assert.All(status.History, item => Assert.Equal(64, item.Checksum.Length));
    }

    [Fact]
    public async Task ExistingV3Database_IsBaselined_ThenMigratedForwardWithoutReplayingOldDdl()
    {
        using var env = new TempWebHostEnvironment();
        var data = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "visionstudio.db");
        await using (var seed = new SqliteConnection($"Data Source={path}"))
        {
            await seed.OpenAsync();
            await using var command = seed.CreateCommand();
            command.CommandText = """
CREATE TABLE schema_info(id INTEGER PRIMARY KEY, version INTEGER NOT NULL);
INSERT INTO schema_info(id,version) VALUES(1,3);
CREATE TABLE jobs(id TEXT PRIMARY KEY,name TEXT NOT NULL DEFAULT '',description TEXT NOT NULL DEFAULT '',created_at TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL DEFAULT '',latest_version INTEGER NOT NULL DEFAULT 1,published_version INTEGER NULL,published_dependency_manifest_hash TEXT NULL);
CREATE TABLE job_versions(job_id TEXT NOT NULL,version INTEGER NOT NULL,workflow_hash TEXT NOT NULL,created_at TEXT NOT NULL,note TEXT NOT NULL,workflow_json TEXT NOT NULL,PRIMARY KEY(job_id,version));
CREATE TABLE run_traces(run_id TEXT PRIMARY KEY,started_at TEXT NOT NULL DEFAULT '',completed_at TEXT NOT NULL DEFAULT '',duration_ms REAL NOT NULL DEFAULT 0,success INTEGER NOT NULL DEFAULT 0,disposition TEXT NOT NULL DEFAULT '',source TEXT NOT NULL DEFAULT '',job_id TEXT NULL,job_version INTEGER NULL,workflow_hash TEXT NOT NULL DEFAULT '',workflow_json TEXT NULL,node_reports_json TEXT NOT NULL DEFAULT '[]',overlays_json TEXT NOT NULL DEFAULT '[]',has_preview INTEGER NOT NULL DEFAULT 0,preview_relative_path TEXT NULL,preview_bytes INTEGER NOT NULL DEFAULT 0,error TEXT NULL,dependency_manifest_hash TEXT NULL);
""";
            await command.ExecuteNonQueryAsync();
        }

        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var status = await db.GetSchemaStatusAsync();

        // schema 版本随迁移演进（当前 20）：断言跟随实现常量而非硬编码快照
        Assert.Equal(db.CurrentSchemaVersion, status.CurrentVersion);
        Assert.True(status.History.Where(x => x.Version <= 3).All(x => x.Baselined));
        Assert.All(status.History.Where(x => x.Version > 3), x => Assert.False(x.Baselined));
        Assert.Equal(db.CurrentSchemaVersion, status.History.Max(x => x.Version));
    }

    [Fact]
    public async Task BackupAndRestore_RoundTripDatabaseAndPreviewArtifacts()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var env = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS backup_roundtrip(value TEXT NOT NULL); DELETE FROM backup_roundtrip; INSERT INTO backup_roundtrip(value) VALUES('before');";
            await command.ExecuteNonQueryAsync();
        }
        var artifact = Path.Combine(env.ContentRootPath, "data", "artifacts", "test", "preview.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "before-artifact");
        // 系统级资产（媒体库/设备配置）也必须随备份往返：空白机恢复依赖这些运行资产。
        var mediaFile = Path.Combine(env.ContentRootPath, "data", "media", "roundtrip-sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaFile)!);
        await File.WriteAllTextAsync(mediaFile, "before-media");
        var deviceFile = Path.Combine(env.ContentRootPath, "data", "devices", "roundtrip-device.json");
        Directory.CreateDirectory(Path.GetDirectoryName(deviceFile)!);
        await File.WriteAllTextAsync(deviceFile, "before-device");
        // R06：托管插件包（含活动指针）与发布者信任库、站点配置也必须随备份往返——空白机恢复依赖它们。
        var pointerFile = Path.Combine(env.ContentRootPath, "data", "plugin-packages", "active-pointers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pointerFile)!);
        await File.WriteAllTextAsync(pointerFile, "{\"active\":{\"demo\":\"1.0.0\"}}");
        var trustFile = Path.Combine(env.ContentRootPath, "data", "plugin-trust", "trusted-publishers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(trustFile)!);
        await File.WriteAllTextAsync(trustFile, "[{\"thumbprint\":\"AA11\"}]");
        var siteConfig = Path.Combine(env.ContentRootPath, "data", VisionStudioSiteConfig.FolderName, VisionStudioSiteConfig.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(siteConfig)!);
        await File.WriteAllTextAsync(siteConfig, "{\"station\":\"S1\"}");

        var backup = await backups.CreateAsync(true, default);
        Assert.True(backup.IncludesArtifacts);
        Assert.True(backup.IncludesSystemAssets);

        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backup_roundtrip SET value='after';";
            await command.ExecuteNonQueryAsync();
        }
        await File.WriteAllTextAsync(artifact, "after-artifact");
        await File.WriteAllTextAsync(mediaFile, "after-media");
        await File.WriteAllTextAsync(deviceFile, "after-device");
        await File.WriteAllTextAsync(pointerFile, "{\"active\":{}}");
        await File.WriteAllTextAsync(trustFile, "[]");
        await File.WriteAllTextAsync(siteConfig, "{\"station\":\"S2\"}");

        var restored = await backups.RestoreAsync(new StorageRestoreRequest(backup.BackupId, true), default);
        Assert.True(restored.ArtifactsRestored);
        Assert.True(restored.SystemAssetsRestored);

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM backup_roundtrip LIMIT 1;";
            Assert.Equal("before", Convert.ToString(await command.ExecuteScalarAsync()));
        }
        Assert.Equal("before-artifact", await File.ReadAllTextAsync(artifact));
        Assert.Equal("before-media", await File.ReadAllTextAsync(mediaFile));
        Assert.Equal("before-device", await File.ReadAllTextAsync(deviceFile));
        Assert.Equal("{\"active\":{\"demo\":\"1.0.0\"}}", await File.ReadAllTextAsync(pointerFile));
        Assert.Equal("[{\"thumbprint\":\"AA11\"}]", await File.ReadAllTextAsync(trustFile));
        Assert.Equal("{\"station\":\"S1\"}", await File.ReadAllTextAsync(siteConfig));
    }

    [Fact]
    public async Task RestoreFailure_RollsBackAndKeepsOriginalAssets()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var env = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        var artifact = Path.Combine(env.ContentRootPath, "data", "artifacts", "test", "rollback.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "v1-artifact");
        var mediaFile = Path.Combine(env.ContentRootPath, "data", "media", "rollback-sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaFile)!);
        await File.WriteAllTextAsync(mediaFile, "v1-media");
        // provenance 目录放一个文件 → 备份必含 system/provenance 段（用它注入切换失败）
        var provenanceRoot = Path.Combine(env.ContentRootPath, "data", "provenance");
        Directory.CreateDirectory(provenanceRoot);
        await File.WriteAllTextAsync(Path.Combine(provenanceRoot, "marker.txt"), "v1-provenance");

        var backup = await backups.CreateAsync(true, default);
        Assert.True(backup.IncludesSystemAssets);

        // 故障注入：把 provenance 目录替换为同名文件 → 系统资产切换（Move 到已存在路径）必然失败，
        // 恢复必须回滚到原状态（附件/系统资产/数据库），不得破坏现有数据。
        Directory.Delete(provenanceRoot, true);
        await File.WriteAllTextAsync(provenanceRoot, "injected blocker");
        try
        {
            await File.WriteAllTextAsync(artifact, "v2-artifact");
            await File.WriteAllTextAsync(mediaFile, "v2-media");

            await Assert.ThrowsAnyAsync<Exception>(() =>
                backups.RestoreAsync(new StorageRestoreRequest(backup.BackupId, true), default));

            // 回滚成功的验证：全部资源回到恢复前（v2）状态，注入物完好
            Assert.Equal("v2-artifact", await File.ReadAllTextAsync(artifact));
            Assert.Equal("v2-media", await File.ReadAllTextAsync(mediaFile));
            Assert.Equal("injected blocker", await File.ReadAllTextAsync(provenanceRoot));
        }
        finally
        {
            try { File.Delete(provenanceRoot); } catch { }
        }
    }


    [Fact]
    public async Task RestoreFailure_WithIncompleteRollback_KeepsMaintenanceLockEngaged()
    {
        // R04：恢复在提交前失败、且回滚**无法完成**时，主机必须被持久锁定，直到人工清除。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var coordinator = factory.Services.GetRequiredService<StorageMaintenanceCoordinator>();
        var env = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        var artifactDir = Path.Combine(env.ContentRootPath, "data", "artifacts", "test");
        var artifact = Path.Combine(artifactDir, "fault.jpg");
        Directory.CreateDirectory(artifactDir);
        await File.WriteAllTextAsync(artifact, "baseline-artifact");
        var backup = await backups.CreateAsync(true, default);

        Assert.False(coordinator.IsFailureLocked);

        // 注入点在安装完成、提交之前：此时 artifacts 已是备份内容。独占锁定其中一个已安装文件，
        // 使回滚的 `Directory.Delete(_artifactRoot, true)` 必然失败 → rollbackFailed 分支（确定性，无竞态）。
        FileStream? blocker = null;
        StorageBackupService.RestoreCommitFaultInjector = () =>
        {
            blocker = new FileStream(artifact, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            throw new IOException("injected commit fault");
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                backups.RestoreAsync(new StorageRestoreRequest(backup.BackupId, true), default));

            // 回滚未完成 → 持久失败锁生效，且与排他 lease 生命周期无关。
            Assert.True(coordinator.IsFailureLocked);
            Assert.True(coordinator.IsMaintenanceActive);
            Assert.Null(coordinator.TryEnterRequest());
            await Assert.ThrowsAsync<ApiConflictException>(() => coordinator.BeginExclusiveAsync("retry restore", default));
        }
        finally
        {
            StorageBackupService.RestoreCommitFaultInjector = null;
            blocker?.Dispose();
        }

        // 人工核对后显式清锁 → 主机重新接受常规写入。
        coordinator.ClearFailureLock();
        Assert.False(coordinator.IsFailureLocked);
        Assert.False(coordinator.IsMaintenanceActive);
        using var after = coordinator.TryEnterRequest();
        Assert.NotNull(after);
    }

    [Fact]
    public void FailureLock_PersistsAcrossRestart_AndClearsThroughExplicitReset()
    {
        // F03：失败锁必须跨重启保留（文件级），且只通过显式核对路径解除——旧实现纯内存，重启即丢。
        using var env = new TempWebHostEnvironment();
        var path = Path.Combine(env.ContentRootPath, "data", ".storage-maintenance", "failure-lock.json");

        var first = new StorageMaintenanceCoordinator(path);
        first.EnterFailureLock("rollback incomplete: db/artifacts may be inconsistent");
        Assert.True(first.IsFailureLocked);
        Assert.True(File.Exists(path));

        // "重启"：新实例从磁盘恢复锁，普通请求与新维护操作继续被拒。
        var restarted = new StorageMaintenanceCoordinator(path);
        Assert.True(restarted.IsFailureLocked);
        Assert.True(restarted.IsMaintenanceActive);
        Assert.Null(restarted.TryEnterRequest());

        // 显式清除：文件删除，再"重启"不再锁定。
        restarted.ClearFailureLock();
        Assert.False(File.Exists(path));
        var afterClear = new StorageMaintenanceCoordinator(path);
        Assert.False(afterClear.IsFailureLocked);

        // 文件损坏：保守锁定（绝不能当作"无锁"放行写入）。
        File.WriteAllText(path, "{not-json");
        var unreadable = new StorageMaintenanceCoordinator(path);
        Assert.True(unreadable.IsFailureLocked);
        unreadable.ClearFailureLock();
    }

    [Fact]
    public async Task ControlledRecoveryEndpoint_ClearsFailureLock_AfterChecks()
    {
        // F03 验收：注入恢复失败（回滚不可完成）→ 失败锁激活且落盘；受控恢复端点在锁定期间
        // 可达（中间件豁免），完成状态检查后清除锁——不绕过认证、不直接修改内存。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var coordinator = factory.Services.GetRequiredService<StorageMaintenanceCoordinator>();
        var env = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        var artifactDir = Path.Combine(env.ContentRootPath, "data", "artifacts", "test");
        Directory.CreateDirectory(artifactDir);
        var artifact = Path.Combine(artifactDir, "recover.jpg");
        await File.WriteAllTextAsync(artifact, "baseline");
        var backup = await backups.CreateAsync(true, default);

        FileStream? blocker = null;
        StorageBackupService.RestoreCommitFaultInjector = () =>
        {
            blocker = new FileStream(artifact, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            throw new IOException("injected commit fault");
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                backups.RestoreAsync(new StorageRestoreRequest(backup.BackupId, true), default));
            Assert.True(coordinator.IsFailureLocked);
        }
        finally
        {
            StorageBackupService.RestoreCommitFaultInjector = null;
            blocker?.Dispose();
        }

        var lockPath = Path.Combine(env.ContentRootPath, "data", ".storage-maintenance", "failure-lock.json");
        Assert.True(File.Exists(lockPath), "the failure lock must be persisted to disk");

        // 锁定期间常规请求被拒（503），但受控恢复入口可达。
        var blocked = await client.GetAsync("/api/storage/status");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, blocked.StatusCode);

        var recover = await client.PostAsync("/api/storage/maintenance/recover", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, recover.StatusCode);
        Assert.False(coordinator.IsFailureLocked);
        Assert.False(coordinator.IsMaintenanceActive);
        Assert.False(File.Exists(lockPath));

        // 解锁后常规请求恢复。
        var after = await client.GetAsync("/api/storage/status");
        Assert.Equal(System.Net.HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ArtifactSnapshotGate_BlocksWritersDuringFreeze_AndWaitsForActiveWriters()
    {
        // R07 共享快照协议（确定性单元覆盖）：
        // - 冻结窗口（EnterArtifactSnapshotAsync 排他）进行中 → 新附件写租约返回 null（写入方放弃落盘）。
        // - 冻结窗口需等待在途写租约释放后才返回（保证快照看到的是冻结后的稳定集合）。
        var coordinator = new StorageMaintenanceCoordinator();

        var writer = coordinator.TryEnterArtifactWrite();
        Assert.NotNull(writer);

        var snapshotTask = coordinator.EnterArtifactSnapshotAsync(default);
        await Task.Delay(30);
        Assert.False(snapshotTask.IsCompleted); // 在途写入未释放 → 排他窗口不返回
        Assert.True(coordinator.IsArtifactSnapshotActive);

        writer!.Dispose(); // 在途写入结束
        await using var snapshot = await snapshotTask;
        Assert.True(coordinator.IsArtifactSnapshotActive);

        // 冻结窗口内：新写入一律被拒（返回 null），不阻塞、不排队。
        Assert.Null(coordinator.TryEnterArtifactWrite());

        await snapshot.DisposeAsync();
        Assert.False(coordinator.IsArtifactSnapshotActive);

        // 解冻后写入恢复。
        using var after = coordinator.TryEnterArtifactWrite();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task BackupSnapshot_HasNoDanglingArtifactReferences()
    {
        // R07 关键验收：备份包内每条 DB 引用（preview/replay）都必须能在包内找到对应文件，且内容一致。
        // N02 修复：固定 StartedAt → 验收与运行日期无关；引用从**归档内**数据库读取（而不是活库）；
        // 冻结窗口内安排写入与清理竞争（可控屏障）——写入被拒并记 note、清理被跳过；逐条校验归档字节内容。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var traces = factory.Services.GetRequiredService<TraceabilityStore>();
        var coordinator = factory.Services.GetRequiredService<StorageMaintenanceCoordinator>();
        var env = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        var startedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        const string dayPath = "traces/2026-01-02";

        // 预置一条带 preview + replay 引用的 trace（经 store 真实 API 落盘：先写文件、后写 DB 行）。
        var workflow = new WorkflowDefinition("snap-job", "Snap Job",
            [new NodeDefinition("n1", "image.synthetic", "Synthetic", null,
                new Dictionary<string, System.Text.Json.JsonElement> { ["width"] = System.Text.Json.JsonSerializer.SerializeToElement(64) })], []);
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 9, 9, 9 };
        var result = new WorkflowRunResult(
            "seeded", Success: true, TotalDurationMs: 1.0, PreviewAvailable: true, PreviewWidth: 1, PreviewHeight: 1,
            NodeReports: [], Overlays: [], PreviewJpeg: jpeg, QualityDisposition: "OK")
        {
            ReplayInput = new ReplayInputArtifact(png, "n1", 1, 1),
            StartedAt = startedAt
        };
        await traces.RecordAsync(result, workflow, new RunTraceContext("SnapTest", WorkflowHash: "snap-hash"), default);

        // 可控竞争屏障：冻结附件窗口期间，新写入被拒（记 note、无引用），过期清理被跳过
        // （不得删除快照可能引用的文件）——保证快照看到的 DB 引用与附件集合一致。
        string skippedNote;
        await using (var snapshot = await coordinator.EnterArtifactSnapshotAsync(default))
        {
            // disposition 取非 OK：OK 走每 20 条采样，无法保证该 run 一定进入附件路径；
            // 非 OK 恒保留 → 必然尝试落盘并进入冻结跳过分支（用于计数断言）。
            var late = result with { RunId = "late-run", QualityDisposition = "NG" };
            var lateRecord = await traces.RecordAsync(late, workflow, new RunTraceContext("SnapTest"), default);
            Assert.False(lateRecord.HasPreview);
            Assert.False(lateRecord.HasReplayInput);
            skippedNote = lateRecord.Note ?? string.Empty;

            Assert.Equal(0, await traces.CleanupAsync(default));
        }
        Assert.Contains("skipped", skippedNote);

        // F05：预览与回放的跳过数量必须可见（现场验收需要知道备份窗口丢了多少张图）。
        Assert.True(traces.SnapshotSkippedPreviews >= 1);
        Assert.True(traces.SnapshotSkippedReplays >= 1);

        var backup = await backups.CreateAsync(true, default);
        Assert.True(backup.IncludesArtifacts);

        // 打开备份包：从**归档内**数据库读取引用（而不是运行中的活库），并校验归档文件内容。
        var archive = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "backups", backup.BackupId + ".vsbackup");
        Assert.True(File.Exists(archive));
        using var stream = File.OpenRead(archive);
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

        var archiveDb = Path.Combine(Path.GetTempPath(), $"vs-archive-{Guid.NewGuid():N}.db");
        try
        {
            await using (var source = zip.GetEntry("database/visionstudio.db")!.Open())
            await using (var target = File.Create(archiveDb))
                await source.CopyToAsync(target);

            var referenced = new List<string>();
            var archiveConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = archiveDb,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using (var connection = new SqliteConnection(archiveConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
SELECT preview_relative_path FROM run_traces WHERE has_preview=1 AND preview_relative_path IS NOT NULL
UNION ALL
SELECT replay_relative_path FROM run_traces WHERE has_replay_input=1 AND replay_relative_path IS NOT NULL;
""";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) referenced.Add(reader.GetString(0).Replace('\\', '/'));
            }

            Assert.Contains($"{dayPath}/seeded/preview.jpg", referenced);
            Assert.Contains($"{dayPath}/seeded/replay-input.png", referenced);
            Assert.DoesNotContain(referenced, p => p.Contains("late-run", StringComparison.OrdinalIgnoreCase));

            var artifactsRoot = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "artifacts");
            foreach (var relative in referenced)
            {
                var entry = zip.GetEntry("artifacts/" + relative);
                Assert.True(entry is not null, $"missing archive entry for reference '{relative}'");
                using var entryStream = entry!.Open();
                using var buffer = new MemoryStream();
                await entryStream.CopyToAsync(buffer);
                var disk = await File.ReadAllBytesAsync(Path.Combine(artifactsRoot, relative));
                Assert.Equal(disk, buffer.ToArray());
            }

            // 冻结期间被跳过的写入没有任何归档条目。
            Assert.Null(zip.GetEntry($"artifacts/{dayPath}/late-run/preview.jpg"));
            Assert.Null(zip.GetEntry($"artifacts/{dayPath}/late-run/replay-input.png"));
        }
        finally
        {
            try { File.Delete(archiveDb); } catch { }
        }

        // 解冻后同一份数据可以被清理（证明冻结窗内返回 0 是"跳过"而不是"无过期项"）。
        Assert.True(await traces.CleanupAsync(default) >= 1);
    }

    [Fact]
    public async Task MaintenanceFailureLock_OutlivesLease_AndBlocksAllNormalWorkUntilCleared()
    {
        // R04 契约（确定性单元覆盖）：恢复失败并回滚未完成时，失败锁必须**独立于排他 lease**存活——
        // 即使 BeginExclusiveAsync 的 await using 已 Dispose，IsMaintenanceActive 仍为 true，
        // 普通请求门与新的排他维护操作都被拒绝，直到人工显式 ClearFailureLock。
        var coordinator = new StorageMaintenanceCoordinator();

        await using (var lease = await coordinator.BeginExclusiveAsync("restore", default))
        {
            Assert.True(coordinator.IsMaintenanceActive);
            coordinator.EnterFailureLock("rollback incomplete: db/artifacts may be inconsistent");
            Assert.True(coordinator.IsFailureLocked);
        }

        // lease 已释放，但失败锁必须继续生效。
        Assert.True(coordinator.IsMaintenanceActive);
        Assert.True(coordinator.IsFailureLocked);
        Assert.Null(coordinator.TryEnterRequest());
        await Assert.ThrowsAsync<ApiConflictException>(() => coordinator.BeginExclusiveAsync("retry restore", default));

        // 人工核对完毕 → 显式清锁 → 主机重新接受常规写入。
        coordinator.ClearFailureLock();
        Assert.False(coordinator.IsFailureLocked);
        Assert.False(coordinator.IsMaintenanceActive);
        using var after = coordinator.TryEnterRequest();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task RestoreFailure_WithCompletedRollback_DoesNotEngageFailureLock()
    {
        // R04 反向对照：回滚**成功完成**的恢复失败不应锁定主机（否则正常故障恢复会永久卡死）。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var coordinator = factory.Services.GetRequiredService<StorageMaintenanceCoordinator>();
        var env = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        var artifact = Path.Combine(env.ContentRootPath, "data", "artifacts", "test", "neg.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "v1");
        var provenanceRoot = Path.Combine(env.ContentRootPath, "data", "provenance");
        Directory.CreateDirectory(provenanceRoot);
        await File.WriteAllTextAsync(Path.Combine(provenanceRoot, "marker.txt"), "v1-provenance");
        var backup = await backups.CreateAsync(true, default);

        // 注入：provenance 变同名文件 → 切换失败，但回滚可以完整复原 → 不应留下失败锁。
        Directory.Delete(provenanceRoot, true);
        await File.WriteAllTextAsync(provenanceRoot, "injected blocker");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                backups.RestoreAsync(new StorageRestoreRequest(backup.BackupId, true), default));
            Assert.False(coordinator.IsFailureLocked);
            Assert.False(coordinator.IsMaintenanceActive);
            using var after = coordinator.TryEnterRequest();
            Assert.NotNull(after);
        }
        finally
        {
            if (File.Exists(provenanceRoot)) File.Delete(provenanceRoot);
        }
    }

    [Fact]
    public async Task MaintenanceCoordinator_DrainsInFlightWork_AndRejectsNewWorkUntilReleased()
    {
        var coordinator = new StorageMaintenanceCoordinator();
        using var active = coordinator.TryEnterRequest();
        Assert.NotNull(active);

        var exclusiveTask = coordinator.BeginExclusiveAsync("test restore", default);
        await Task.Delay(20);
        Assert.False(exclusiveTask.IsCompleted);
        active!.Dispose();

        await using var exclusive = await exclusiveTask;
        Assert.True(coordinator.IsMaintenanceActive);
        Assert.Null(coordinator.TryEnterRequest());
        await exclusive.DisposeAsync();
        Assert.False(coordinator.IsMaintenanceActive);
        using var after = coordinator.TryEnterRequest();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task Backup_IsAllowedWhileProductionIsRunning_ThanksToSnapshotProtocol()
    {
        // R07（共享快照协议后）：运行中的生产**允许**一致性备份；只有过渡态被拒。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var production = factory.Services.GetRequiredService<ProductionRuntimeService>();
        var jobs = factory.Services.GetRequiredService<JobStore>();
        var dependencies = factory.Services.GetRequiredService<RuntimeDependencyManifestService>();

        Assert.Equal(ProductionRuntimeState.Stopped, production.Status.State);
        Assert.NotNull(await backups.CreateAsync(true, default));

        var workflow = new WorkflowDefinition("guard-job", "Guard Job",
            [new NodeDefinition("n1", "image.synthetic", "Synthetic", null,
                new Dictionary<string, System.Text.Json.JsonElement> { ["width"] = System.Text.Json.JsonSerializer.SerializeToElement(640) })], []);
        await jobs.CreateAsync(new CreateJobRequest("guard-job", "Guard Job", null, workflow, "v1"), default);
        var snapshot = await dependencies.CaptureAsync(workflow, default);
        await jobs.PublishAsync("guard-job", 1, "storage-guard", snapshot, default);
        await production.UpdateConfigAsync(new ProductionRuntimeConfig("guard-job", false, 2, 1000, 3, false, 1), default);
        await production.StartAsync(null, default);
        await WaitUntilAsync(() => production.Status.State == ProductionRuntimeState.Running, TimeSpan.FromSeconds(5));

        try
        {
            // 运行中也应能备份：附件由共享快照协议冻结，DB 用 online snapshot。
            var online = await backups.CreateAsync(true, default);
            Assert.NotNull(online);
            Assert.True(online.IncludesArtifacts);
        }
        finally
        {
            await production.StopAsync(default);
        }
    }

    [Fact]
    public void SiteConfig_FileIsLoadedAsLiveConfigurationSource()
    {
        // F06：site-config/appsettings.site.json 必须是**实际生效**的配置源（此前只被复制、从未加载）。
        using var factory = new StorageVisionStudioApiFactory(maxArtifactBytes: null, siteConfigJson: "{\"Site\":{\"Station\":\"Station-A\"}}");
        using var client = factory.CreateClient();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("Station-A", configuration["Site:Station"]);
    }

    [Fact]
    public void SiteConfig_OverridesEarlierSources()
    {
        // F06：站点文件覆盖更早的配置来源（内容根 appsettings 等），使"备份的目录"与"生效配置"同源。
        using var env = new TempWebHostEnvironment();
        var siteDir = Path.Combine(env.ContentRootPath, "data", VisionStudioSiteConfig.FolderName);
        Directory.CreateDirectory(siteDir);
        File.WriteAllText(Path.Combine(siteDir, VisionStudioSiteConfig.FileName),
            "{\"Site\":{\"Station\":\"S1\"},\"Storage\":{\"CriticalFreeBytes\":42}}");

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:Station"] = "default",
            ["Storage:CriticalFreeBytes"] = "1"
        });
        VisionStudioSiteConfig.AddSource(configuration, env.ContentRootPath);

        Assert.Equal("S1", configuration["Site:Station"]);
        Assert.Equal("42", configuration["Storage:CriticalFreeBytes"]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "condition not met before timeout");
    }

    [Fact]
    public async Task CapacityGuard_MeasuresArtifactsUnderDataRoot()
    {
        // R05：容量核算必须跟随数据根（contentRoot/data）。artifacts 放在数据根下必须被计入，
        // 放在打包目录其它位置则不参与核算。
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var capacities = Options.Create(new StorageMaintenanceOptions
        {
            WarningFreeBytes = 0,
            CriticalFreeBytes = 0,
            MaxArtifactBytes = long.MaxValue,
            MaxBackupBytes = long.MaxValue
        });
        var capacity = new StorageCapacityService(env, db, capacities);

        var dataArtifact = Path.Combine(env.ContentRootPath, "data", "artifacts", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dataArtifact)!);
        await File.WriteAllBytesAsync(dataArtifact, new byte[4096]);
        var strayArtifact = Path.Combine(env.ContentRootPath, "not-data", "b.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(strayArtifact)!);
        await File.WriteAllBytesAsync(strayArtifact, new byte[4096]);

        var status = await capacity.RefreshAsync();
        Assert.Equal(4096, status.ArtifactBytes);
        Assert.True(status.ProductionStartAllowed);
    }

    [Fact]
    public async Task CapacityGuard_TracksAndReleasesConcurrentArtifactReservations()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var options = Options.Create(new StorageMaintenanceOptions
        {
            WarningFreeBytes = 0,
            CriticalFreeBytes = 0,
            MaxArtifactBytes = 1500,
            MaxBackupBytes = long.MaxValue
        });
        var capacity = new StorageCapacityService(env, db, options);
        var status = await capacity.RefreshAsync();

        Assert.NotEqual(StorageCapacityLevel.Critical, status.Level);
        Assert.True(capacity.CanPersistArtifact(1000));
        Assert.False(capacity.CanPersistArtifact(600));
        capacity.ReleaseArtifactReservation(1000);
        Assert.True(capacity.CanPersistArtifact(600));
        capacity.ReleaseArtifactReservation(600);
    }

    [Fact]
    public async Task CapacityGuard_BlocksArtifactWrites_WhenCriticalReserveIsBreached()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var options = Options.Create(new StorageMaintenanceOptions
        {
            WarningFreeBytes = long.MaxValue,
            CriticalFreeBytes = long.MaxValue - 1,
            MaxArtifactBytes = long.MaxValue,
            MaxBackupBytes = long.MaxValue
        });
        var capacity = new StorageCapacityService(env, db, options);
        var status = await capacity.RefreshAsync();

        Assert.Equal(StorageCapacityLevel.Critical, status.Level);
        Assert.False(status.ProductionStartAllowed);
        Assert.False(capacity.CanPersistArtifact(1024));
    }

    [Fact]
    public async Task ReplaySkippedDuringArtifactSnapshot_ReleasesReservation_AndRecordsNote()
    {
        // N01 回归：附件冻结窗口内跳过回放落盘时，容量预留必须归还（否则跳过累积成虚假占用，
        // 后续图像被容量检查拒绝），且跳过原因必须写入数据库（与 preview 分支一致，可诊断）。
        using var factory = new StorageVisionStudioApiFactory(maxArtifactBytes: 1500);
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var traces = factory.Services.GetRequiredService<TraceabilityStore>();
        var coordinator = factory.Services.GetRequiredService<StorageMaintenanceCoordinator>();
        var capacity = factory.Services.GetRequiredService<StorageCapacityService>();
        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        await capacity.RefreshAsync();

        var workflow = new WorkflowDefinition("replay-skip-job", "Replay Skip Job",
            [new NodeDefinition("n1", "image.synthetic", "Synthetic", null,
                new Dictionary<string, System.Text.Json.JsonElement> { ["width"] = System.Text.Json.JsonSerializer.SerializeToElement(64) })], []);

        // 第一条（400 字节）：正常路径保存并归还预留——磁盘 400，预留 0。
        var png = new byte[400];
        var baseline = new WorkflowRunResult(
            "replay-baseline", Success: true, TotalDurationMs: 1.0, PreviewAvailable: false, PreviewWidth: 0, PreviewHeight: 0,
            NodeReports: [], Overlays: [], PreviewJpeg: null, QualityDisposition: "OK")
        {
            ReplayInput = new ReplayInputArtifact(png, "n1", 10, 10),
            StartedAt = DateTimeOffset.UtcNow
        };
        var baselineRecord = await traces.RecordAsync(baseline, workflow, new RunTraceContext("SnapTest"), default);
        Assert.True(baselineRecord.HasReplayInput);

        // 冻结附件快照 → 第二条（400 字节）通过容量检查但必须被跳过；修复后归还预留。
        // 泄漏时预留 400 会让解冻后的检查拒绝（400 磁盘 + 400 泄漏 + 1000 > 1500）。
        await using (var snapshot = await coordinator.EnterArtifactSnapshotAsync(default))
        {
            var skipped = baseline with { RunId = "replay-skipped" };
            var skippedRecord = await traces.RecordAsync(skipped, workflow, new RunTraceContext("SnapTest"), default);
            Assert.False(skippedRecord.HasReplayInput);
            Assert.Contains("Replay input skipped", skippedRecord.Note);
        }

        await capacity.RefreshAsync();
        Assert.True(capacity.CanPersistArtifact(1000), "reservation leaked during snapshot freeze");
        capacity.ReleaseArtifactReservation(1000);

        // F05：跳过数量必须可见（现场验收需要知道备份窗口丢了多少张图）。
        Assert.True(traces.SnapshotSkippedReplays >= 1, "skipped replay count must be visible");

        // 跳过原因已持久化到 run_traces（可诊断性）。
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT note FROM run_traces WHERE run_id='replay-skipped';";
        var note = Convert.ToString(await command.ExecuteScalarAsync());
        Assert.Contains("Replay input skipped", note);
    }
}


public sealed class StorageVisionStudioApiFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "visionstudio-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly long _maxArtifactBytes;

    public StorageVisionStudioApiFactory() : this(maxArtifactBytes: null, siteConfigJson: null) { }

    public StorageVisionStudioApiFactory(long? maxArtifactBytes) : this(maxArtifactBytes, siteConfigJson: null) { }

    public StorageVisionStudioApiFactory(long? maxArtifactBytes, string? siteConfigJson)
    {
        _maxArtifactBytes = maxArtifactBytes ?? 1073741824;
        Directory.CreateDirectory(_root);
        if (siteConfigJson is not null)
        {
            // F06：在宿主构建之前写入站点配置——验证其作为"实际生效的配置源"被加载。
            var siteDir = Path.Combine(_root, "data", VisionStudioSiteConfig.FolderName);
            Directory.CreateDirectory(siteDir);
            File.WriteAllText(Path.Combine(siteDir, VisionStudioSiteConfig.FileName), siteConfigJson);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(_root);
        builder.UseSetting("Security:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Port", "0");
        builder.UseSetting("Storage:WarningFreeBytes", "1");
        builder.UseSetting("Storage:CriticalFreeBytes", "1");
        builder.UseSetting("Storage:MaxArtifactBytes", _maxArtifactBytes.ToString());
        builder.UseSetting("Storage:MaxBackupBytes", "1073741824");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
