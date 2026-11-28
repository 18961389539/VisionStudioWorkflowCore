using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using Xunit;
using Xunit.Abstractions;

namespace VisionStudio.Api.Tests.Storage;

public sealed class EfCoreSchemaParityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EnsureCreated_MatchesMigratedBusinessSchema()
    {
        await using var migrated = new SqliteConnection("Data Source=:memory:");
        await migrated.OpenAsync();
        var status = await new SqliteSchemaMigrationRunner().MigrateAsync(migrated);
        Assert.True(status.UpToDate);
        Assert.Equal(SqliteSchemaMigrationRunner.CurrentVersion, status.History.Count);

        await using var generated = new SqliteConnection("Data Source=:memory:");
        await generated.OpenAsync();
        await using var db = new VisionStudioDbContext(new DbContextOptionsBuilder<VisionStudioDbContext>()
            .UseSqlite(generated).Options);
        await db.Database.EnsureCreatedAsync();

        foreach (var table in TablesOutsideEfModel)
        {
            await using var command = migrated.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        var expected = await SqliteSchemaSnapshot.ReadAsync(migrated, TablesOutsideEfModel);
        var actual = await SqliteSchemaSnapshot.ReadAsync(generated, equivalentUniqueIndexes: EquivalentUniqueIndexes);
        var differences = SqliteSchemaSnapshot.Differences(expected, actual);
        var knownDifferences = LegacyNullablePrimaryKeys.Select(key => $"table:{key}/notnull: SQL=0; EF=1")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(differences.SequenceEqual(knownDifferences),
            "Unexpected schema differences (or stale legacy exceptions):" + Environment.NewLine +
            string.Join(Environment.NewLine, differences.Except(knownDifferences)
                .Concat(knownDifferences.Except(differences).Select(x => "No longer present: " + x))));
        output.WriteLine("Schema reconciled: {0} tables, {1} columns, {2} indexes; {3} explicit legacy key nullability differences.",
            expected.Keys.Count(key => key.StartsWith("table:", StringComparison.Ordinal) && !key.Contains('/')),
            expected.Keys.Count(key => key.EndsWith("/type", StringComparison.Ordinal)),
            expected.Keys.Count(key => key.Contains("/index:", StringComparison.Ordinal) && !key.EndsWith("/filter", StringComparison.Ordinal)
                && !key.EndsWith("/expression", StringComparison.Ordinal)),
            knownDifferences.Length);
    }

    [Theory]
    [InlineData("value TEXT", "value INTEGER", "/type")]
    [InlineData("value TEXT NOT NULL", "value TEXT", "/notnull")]
    [InlineData("value TEXT DEFAULT 'A b'", "value TEXT DEFAULT 'Ab'", "/default")]
    [InlineData("a TEXT, b TEXT, PRIMARY KEY(a,b)", "a TEXT, b TEXT, PRIMARY KEY(b,a)", "/pk")]
    [InlineData("value TEXT COLLATE NOCASE", "value TEXT COLLATE BINARY", "/collation")]
    [InlineData("id INTEGER PRIMARY KEY AUTOINCREMENT", "id INTEGER PRIMARY KEY", "/autoincrement")]
    [InlineData("value TEXT CHECK(value IN ('OK','NG') AND length(trim(value)) > 0)", "value TEXT", "/checks")]
    [InlineData("a INTEGER, b INTEGER AS (a+1) STORED", "a INTEGER, b INTEGER AS (a+2) STORED", "/generated")]
    public async Task Comparison_ReportsColumnAndConstraintDrift(string before, string after, string field)
    {
        var differences = await CompareDdlAsync($"CREATE TABLE t({before});", $"CREATE TABLE t({after});");
        Assert.Contains(differences, difference => difference.Contains(field + ":", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CREATE INDEX ix ON t(a,b DESC);", "", "index:ix")]
    [InlineData("CREATE INDEX ix ON t(a,b DESC);", "CREATE INDEX ix ON t(b,a DESC);", "index:ix")]
    [InlineData("CREATE INDEX ix ON t(a,b DESC);", "CREATE INDEX ix ON t(a,b);", "index:ix")]
    [InlineData("CREATE UNIQUE INDEX ix ON t(a);", "CREATE INDEX ix ON t(a);", "index:ix")]
    [InlineData("CREATE INDEX ix ON t(a COLLATE NOCASE);", "CREATE INDEX ix ON t(a);", "index:ix")]
    [InlineData("CREATE INDEX ix ON t(a) WHERE b='A b';", "CREATE INDEX ix ON t(a) WHERE b='Ab';", "/filter")]
    [InlineData("CREATE INDEX ix ON t(length(a));", "CREATE INDEX ix ON t(length(b));", "/expression")]
    public async Task Comparison_ReportsIndexDrift(string before, string after, string field)
    {
        const string table = "CREATE TABLE t(a TEXT, b TEXT);";
        var differences = await CompareDdlAsync(table + before, table + after);
        Assert.Contains(differences, difference => difference.Contains(field + ":", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FOREIGN KEY(a,b) REFERENCES parent(a,b) ON DELETE CASCADE",
        "FOREIGN KEY(a,b) REFERENCES parent(a,b) ON DELETE RESTRICT", "/foreignKeys")]
    [InlineData("FOREIGN KEY(a,b) REFERENCES parent(a,b) ON UPDATE CASCADE",
        "FOREIGN KEY(a,b) REFERENCES parent(a,b)", "/foreignKeys")]
    [InlineData("FOREIGN KEY(a,b) REFERENCES parent(a,b)",
        "FOREIGN KEY(a,b) REFERENCES parent(b,a)", "/foreignKeys")]
    [InlineData("FOREIGN KEY(a,b) REFERENCES parent(a,b) DEFERRABLE INITIALLY DEFERRED",
        "FOREIGN KEY(a,b) REFERENCES parent(a,b)", "/deferrability")]
    public async Task Comparison_ReportsForeignKeyDrift(string before, string after, string field)
    {
        const string parent = "CREATE TABLE parent(a TEXT, b TEXT, PRIMARY KEY(a,b));";
        var differences = await CompareDdlAsync(parent + $"CREATE TABLE t(a TEXT, b TEXT, {before});",
            parent + $"CREATE TABLE t(a TEXT, b TEXT, {after});");
        Assert.Contains(differences, difference => difference.Contains(field + ":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Comparison_IgnoresDdlFormattingButPreservesQuotedLiterals()
    {
        var differences = await CompareDdlAsync(
            "CREATE TABLE t(a TEXT NOT NULL CHECK(a IN ('A b', 'O''K'))); CREATE INDEX ix ON t(a) WHERE a='O''K';",
            """
            CREATE TABLE "t" ("a" TEXT NOT NULL, CONSTRAINT "ck" CHECK ("a" IN ('A b','O''K')));
            CREATE INDEX "ix" ON "t" ("a" ASC) WHERE "a" = 'O''K';
            """);
        Assert.Empty(differences);
    }

    [Fact]
    public async Task Comparison_RejectsUnmodeledConflictPolicies()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => CompareDdlAsync(
            "CREATE TABLE t(a TEXT UNIQUE);",
            "CREATE TABLE t(a TEXT UNIQUE ON CONFLICT IGNORE);"));
    }

    private static async Task<string[]> CompareDdlAsync(string before, string after)
    {
        await using var expected = new SqliteConnection("Data Source=:memory:");
        await using var actual = new SqliteConnection("Data Source=:memory:");
        await expected.OpenAsync();
        await actual.OpenAsync();
        await using (var command = expected.CreateCommand())
        {
            command.CommandText = before;
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = actual.CreateCommand())
        {
            command.CommandText = after;
            await command.ExecuteNonQueryAsync();
        }
        return SqliteSchemaSnapshot.Differences(
            await SqliteSchemaSnapshot.ReadAsync(expected), await SqliteSchemaSnapshot.ReadAsync(actual));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableCounters_KeepDatabaseGeneratedRowIds(bool migrate)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VisionStudioDbContext(new DbContextOptionsBuilder<VisionStudioDbContext>()
            .UseSqlite(connection).Options);
        if (migrate) await new SqliteSchemaMigrationRunner().MigrateAsync(connection);
        else await db.Database.EnsureCreatedAsync();

        var first = new DurableCounterEventEntity { WorkflowId = "workflow", CounterId = "counter", EventId = "first" };
        var second = new DurableCounterEventEntity { WorkflowId = "workflow", CounterId = "counter", EventId = "second" };
        db.DurableCounterEvents.Add(first);
        await db.SaveChangesAsync();
        db.DurableCounterEvents.Add(second);
        await db.SaveChangesAsync();
        Assert.True(first.Seq > 0);
        Assert.True(second.Seq > first.Seq);
        Assert.Equal(2, await db.DurableCounterEvents.CountAsync());
    }

    private static readonly HashSet<string> InfrastructureTables =
        ["schema_info", "schema_migration_history", "legacy_alarm_import"];

    // 媒体库索引表由 MediaLibraryIndex 的原生 SQL 独占管理（upsert/清理/分页查询都在 SQL 侧），
    // EF 模型不映射它：EnsureCreated 生成的库缺这张表，启动迁移一定会补上，因此对齐检查按“库外表”处理。
    private static readonly HashSet<string> SqlManagedTables = ["media_index_items"];

    private static readonly HashSet<string> TablesOutsideEfModel = [.. InfrastructureTables, .. SqlManagedTables];

    // Keep mutable values as unique indexes, not EF alternate keys (which cannot be changed while tracked).
    private static readonly HashSet<string> EquivalentUniqueIndexes =
    [
        "IX_security_users_normalized_username",
        "IX_security_service_tokens_name",
        "IX_security_service_tokens_token_hash",
        "IX_investigation_cases_case_key",
        "IX_validation_dataset_items_dataset_id_source_kind_source_ref",
        "IX_durable_counter_events_workflow_id_counter_id_event_id",
        "IX_recipe_delivery_receipts_job_id_local_version",
        "IX_recipe_delivery_receipts_station_id_job_id_source_version_workflow_hash_requires_validation"
    ];

    // Existing SQLite rowid tables omitted NOT NULL on these keys; EF requires non-null entity keys.
    // This is an exact, directional list: new mismatches and disappearing exceptions both fail.
    private static readonly string[] LegacyNullablePrimaryKeys =
    [
        "alarms/column:id", "asset_blobs/column:sha256", "assets/column:id",
        "audit_events/column:seq", "calibration_assets/column:id", "calibration_publication_history/column:seq",
        "camera_feature_profiles/column:id", "camera_intrinsic_assets/column:id",
        "camera_intrinsic_publication_history/column:seq", "camera_sync_commissioning_tests/column:test_id",
        "camera_sync_groups/column:id", "camera_sync_runs/column:run_id", "durable_counter_events/column:seq",
        "hand_eye3d_calibration_assets/column:id", "hand_eye3d_calibration_publication_history/column:seq",
        "hand_eye_calibration_assets/column:id", "hand_eye_calibration_publication_history/column:seq",
        "investigation_case_verifications/column:id", "investigation_cases/column:id",
        "job_publication_history/column:seq", "jobs/column:id", "migration_state/column:key",
        "plugin_benchmark_runs/column:run_id", "products/column:id", "recipe_delivery_receipts/column:id",
        "recipe_rollout_batches/column:id", "recipe_rollout_latest_station/column:station_id", "result_records/column:id",
        "run_traces/column:run_id", "runtime_dependency_manifests/column:manifest_hash",
        "security_service_tokens/column:id", "security_sessions/column:token_hash", "security_users/column:id",
        "shape_model_assets/column:id", "shape_model_blobs/column:sha256", "validation_datasets/column:id",
        "validation_runs/column:run_id", "workflow_modules/column:id"
    ];
}
