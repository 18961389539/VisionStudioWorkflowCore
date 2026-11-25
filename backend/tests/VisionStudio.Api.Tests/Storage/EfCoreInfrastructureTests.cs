using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using Xunit;

namespace VisionStudio.Api.Tests.Storage;

public sealed class EfCoreInfrastructureTests
{
    [Fact]
    public async Task Factory_AppliesWalAndDurabilityPragmas_AndDisposesOwnedConnection()
    {
        using var env = new TempWebHostEnvironment();
        var database = new SqliteMetadataDatabase(env);
        var factory = new VisionStudioDbFactory(database);

        await using (var normalDb = await factory.CreateDbContextAsync())
        {
            var conn = (SqliteConnection)normalDb.Database.GetDbConnection();
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous;";
            Assert.Equal(1L, Convert.ToInt64(await pragma.ExecuteScalarAsync()));
        }

        await using (var fullDb = await factory.CreateDbContextAsync(strongDurability: true))
        {
            var conn = (SqliteConnection)fullDb.Database.GetDbConnection();
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous;";
            Assert.Equal(2L, Convert.ToInt64(await pragma.ExecuteScalarAsync()));

            await using var tx = await factory.BeginImmediateTransactionAsync(fullDb);
            await tx.CommitAsync();
        }

        using (var syncDb = factory.CreateDbContext())
        {
            var conn = (SqliteConnection)syncDb.Database.GetDbConnection();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, Convert.ToInt64(pragma.ExecuteScalar()));
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task EnsureCreated_MatchesAllBusinessTablesAndColumns_AndTranslatesDateTimeOffsetQueries()
    {
        using var env = new TempWebHostEnvironment();
        var migratedDb = new SqliteMetadataDatabase(env);
        await migratedDb.EnsureInitializedAsync();

        var efDbPath = Path.Combine(env.ContentRootPath, "ef-only.db");
        var efOptions = new DbContextOptionsBuilder<VisionStudioDbContext>()
            .UseSqlite($"Data Source={efDbPath};Pooling=False")
            .Options;

        await using (var efDb = new VisionStudioDbContext(efOptions))
        {
            await efDb.Database.EnsureCreatedAsync();

            var t1 = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(8));
            var t2 = t1.AddMinutes(5);
            efDb.Alarms.AddRange(
                new AlarmEntity { Id = "a1", Code = "C1", Severity = "Warning", Source = "S1", Message = "m1", Active = true, RaisedAt = t1 },
                new AlarmEntity { Id = "a2", Code = "C2", Severity = "Error", Source = "S2", Message = "m2", Active = false, RaisedAt = t2 });
            await efDb.SaveChangesAsync();

            var ordered = await efDb.Alarms
                .AsNoTracking()
                .Where(x => x.RaisedAt >= t1)
                .OrderByDescending(x => x.RaisedAt)
                .Select(x => x.Id)
                .ToListAsync();
            Assert.Equal(["a2", "a1"], ordered);
        }

        await using var legacyConn = await migratedDb.OpenConnectionAsync();
        await using var efConn = new SqliteConnection($"Data Source={efDbPath};Pooling=False");
        await efConn.OpenAsync();

        var legacySchema = await ReadSchemaAsync(legacyConn);
        var efSchema = await ReadSchemaAsync(efConn);

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "schema_info",
            "schema_migration_history",
            "legacy_alarm_import",
            "sqlite_sequence"
        };

        var expectedTables = legacySchema.Keys.Where(k => !excluded.Contains(k)).OrderBy(k => k).ToList();
        var actualTables = efSchema.Keys.Where(k => !excluded.Contains(k)).OrderBy(k => k).ToList();
        Assert.Equal(expectedTables, actualTables);

        foreach (var table in expectedTables)
        {
            Assert.Equal(legacySchema[table], efSchema[table]);
        }

        SqliteConnection.ClearAllPools();
    }

    private static async Task<Dictionary<string, List<string>>> ReadSchemaAsync(SqliteConnection connection)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tables = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            var cols = new List<string>();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) cols.Add(reader.GetString(1));
            cols.Sort(StringComparer.OrdinalIgnoreCase);
            result[table] = cols;
        }
        return result;
    }
}

