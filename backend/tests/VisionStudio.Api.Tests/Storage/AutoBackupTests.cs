using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

public sealed class AutoBackupTests
{
    [Fact]
    public void DueAndStaleJudgments_FollowLatestBackupAge()
    {
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromHours(24);
        Assert.True(AutoBackupService.IsDue(now, null, interval));
        Assert.True(AutoBackupService.IsDue(now, now - TimeSpan.FromHours(24), interval));
        Assert.False(AutoBackupService.IsDue(now, now - TimeSpan.FromHours(23), interval));

        var staleAfter = TimeSpan.FromHours(48);
        Assert.True(AutoBackupService.IsStale(now, null, staleAfter));
        Assert.True(AutoBackupService.IsStale(now, now - TimeSpan.FromHours(49), staleAfter));
        Assert.False(AutoBackupService.IsStale(now, now - TimeSpan.FromHours(47), staleAfter));
    }

    [Fact]
    public async Task Health_ReportsStaleBeforeFirstBackup_AndFreshAfterAutomaticBackup()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/health");
        var beforeBackup = before.GetProperty("backup");
        Assert.True(beforeBackup.GetProperty("autoEnabled").GetBoolean());
        Assert.Equal(24, beforeBackup.GetProperty("intervalHours").GetInt32());
        Assert.Equal(48, beforeBackup.GetProperty("staleAfterHours").GetInt32());
        Assert.Equal(JsonValueKind.Null, beforeBackup.GetProperty("lastBackupAt").ValueKind);
        Assert.True(beforeBackup.GetProperty("stale").GetBoolean());

        var auto = factory.Services.GetRequiredService<AutoBackupService>();
        var created = await auto.RunOnceAsync(force: true, default);
        Assert.NotNull(created);
        Assert.False(created!.IncludesArtifacts);

        var status = await auto.GetStatusAsync(default);
        Assert.Equal(created.BackupId, status.LatestBackupId);
        Assert.Null(status.LastError);
        Assert.Null(status.ListError);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/health");
        var backup = after.GetProperty("backup");
        Assert.Equal(created.BackupId, backup.GetProperty("lastBackupId").GetString());
        Assert.NotEqual(JsonValueKind.Null, backup.GetProperty("lastBackupAt").ValueKind);
        Assert.False(backup.GetProperty("stale").GetBoolean());
        Assert.False(backup.GetProperty("lastBackupIncludesArtifacts").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, backup.GetProperty("lastAttemptAt").ValueKind);

        // 健康披露是信息性的：备份新鲜度绝不改变就绪/生产就绪语义。
        Assert.Equal("ok", after.GetProperty("status").GetString());
        Assert.True(after.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task AutomaticBackup_IsRestorable_Drill()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var auto = factory.Services.GetRequiredService<AutoBackupService>();

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS auto_backup_drill(value TEXT NOT NULL); DELETE FROM auto_backup_drill; INSERT INTO auto_backup_drill(value) VALUES('before');";
            await command.ExecuteNonQueryAsync();
        }

        var created = await auto.RunOnceAsync(force: true, default);
        Assert.NotNull(created);

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE auto_backup_drill SET value='after';";
            await command.ExecuteNonQueryAsync();
        }

        // 演练：自动备份产物必须能被受控恢复入口直接消费。
        var restored = await backups.RestoreAsync(new StorageRestoreRequest(created!.BackupId, false), default);
        Assert.Equal(created.BackupId, restored.BackupId);

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM auto_backup_drill LIMIT 1;";
            Assert.Equal("before", Convert.ToString(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task DisabledScheduler_StillReportsFreshness_ButCreatesNoBackups()
    {
        using var factory = new StorageVisionStudioApiFactory(
            maxArtifactBytes: null,
            siteConfigJson: "{\"Storage\":{\"AutoBackup\":{\"Enabled\":false}}}");
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
        var backup = health.GetProperty("backup");
        Assert.False(backup.GetProperty("autoEnabled").GetBoolean());
        Assert.True(backup.GetProperty("stale").GetBoolean());

        var auto = factory.Services.GetRequiredService<AutoBackupService>();
        Assert.False(auto.Enabled);
        Assert.Empty(await factory.Services.GetRequiredService<StorageBackupService>().ListAsync(default));
        Assert.Null(await auto.RunOnceAsync(force: true, default));
    }

    [Fact]
    public async Task SecondaryBackupCopy_IsVerifiedAndCanRestoreWhenPrimaryCopyIsMissing()
    {
        using var factory = new StorageVisionStudioApiFactory();
        var secondaryRoot = Path.Combine(factory.ContentRoot, "independent-backup-volume");
        var siteConfig = Path.Combine(factory.ContentRoot, "data", VisionStudioSiteConfig.FolderName);
        Directory.CreateDirectory(siteConfig);
        File.WriteAllText(Path.Combine(siteConfig, VisionStudioSiteConfig.FileName),
            JsonSerializer.Serialize(new { Storage = new { BackupSecondaryRoot = secondaryRoot } }), Encoding.UTF8);
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();

        var autoBackups = factory.Services.GetRequiredService<AutoBackupService>();
        var created = await autoBackups.RunOnceAsync(force: true, default)
                      ?? throw new InvalidOperationException("Forced automatic backup did not create an archive.");
        var primary = Path.Combine(factory.ContentRoot, "data", "backups", created.BackupId + ".vsbackup");
        var secondary = Path.Combine(secondaryRoot, created.BackupId + ".vsbackup");
        Assert.True(File.Exists(primary));
        Assert.True(File.Exists(secondary));
        Assert.Equal(created.ArchiveBytes, new FileInfo(secondary).Length);
        var freshness = await factory.Services.GetRequiredService<AutoBackupService>().GetStatusAsync(default);
        Assert.True(factory.Services.GetRequiredService<AutoBackupService>().SecondaryBackupConfigured);
        Assert.Equal(created.BackupId, freshness.LatestSecondaryBackupId);

        File.Delete(primary);
        Assert.Contains(await backups.ListAsync(default), backup => backup.BackupId == created.BackupId);
        var restored = await backups.RestoreAsync(new StorageRestoreRequest(created.BackupId, false), default);
        Assert.Equal(created.BackupId, restored.BackupId);
    }

    [Fact]
    public async Task RestoreFromOlderBackup_PreservesCurrentNamedStationSafetyAndTriggerFacts()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        _ = await client.GetAsync("/api/health");
        var backups = factory.Services.GetRequiredService<StorageBackupService>();
        var production = factory.Services.GetRequiredService<ProductionRuntimeService>();
        var oldBackup = await backups.CreateAsync(false, default);

        _ = production.GetStatus("line-a");
        var productionRoot = Path.Combine(factory.ContentRoot, "data", "production");
        var safetyPath = Path.Combine(productionRoot, "device-action-safety.line-a.json");
        new DeviceActionSafetyStore(safetyPath, NullLogger<DeviceActionSafetyStore>.Instance).Save(
            new DeviceActionSafetyState("manifest-a", ["plc-a"], [], DateTimeOffset.UtcNow, "unresolved", Phase: DeviceActionSafetyPhase.Unknown));
        var triggerPath = Path.Combine(productionRoot, "trigger-queue.line-a.json");
        _ = new ProductionTriggerQueue(triggerPath).Enqueue("event-a");

        await backups.RestoreAsync(new StorageRestoreRequest(oldBackup.BackupId, false), default);

        var restoredSafety = new DeviceActionSafetyStore(safetyPath, NullLogger<DeviceActionSafetyStore>.Instance).Load();
        Assert.NotNull(restoredSafety);
        Assert.Equal("manifest-a", restoredSafety.ManifestHash);
        var restoredTrigger = Assert.Single(new ProductionTriggerQueue(triggerPath).List());
        Assert.Equal("event-a", restoredTrigger.EventId);
        Assert.Equal(ProductionTriggerState.Pending, restoredTrigger.State);
    }
}