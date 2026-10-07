using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VisionStudio.Api;

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

        Assert.Equal(15, status.CurrentVersion);
        Assert.Equal(15, status.TargetVersion);
        Assert.True(status.UpToDate);
        Assert.Equal(15, status.History.Count);
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

        Assert.Equal(15, status.CurrentVersion);
        Assert.True(status.History.Where(x => x.Version <= 3).All(x => x.Baselined));
        Assert.All(status.History.Where(x => x.Version > 3), x => Assert.False(x.Baselined));
        Assert.Equal(15, status.History.Max(x => x.Version));
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

        var backup = await backups.CreateAsync(true, default);
        Assert.True(backup.IncludesArtifacts);

        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backup_roundtrip SET value='after';";
            await command.ExecuteNonQueryAsync();
        }
        await File.WriteAllTextAsync(artifact, "after-artifact");

        var restored = await backups.RestoreAsync(new StorageRestoreRequest(backup.BackupId, true), default);
        Assert.True(restored.ArtifactsRestored);

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM backup_roundtrip LIMIT 1;";
            Assert.Equal("before", Convert.ToString(await command.ExecuteScalarAsync()));
        }
        Assert.Equal("before-artifact", await File.ReadAllTextAsync(artifact));
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
}


public sealed class StorageVisionStudioApiFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "visionstudio-storage-tests", Guid.NewGuid().ToString("N"));

    public StorageVisionStudioApiFactory() => Directory.CreateDirectory(_root);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(_root);
        builder.UseSetting("Security:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Port", "0");
        builder.UseSetting("Storage:WarningFreeBytes", "1");
        builder.UseSetting("Storage:CriticalFreeBytes", "1");
        builder.UseSetting("Storage:MaxArtifactBytes", "1073741824");
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
