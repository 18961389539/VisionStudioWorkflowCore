using VisionStudio.Api.Infrastructure;
using Microsoft.Data.Sqlite;

namespace VisionStudio.Api;

/// <summary>
/// Shared SQLite metadata database. Large binary artifacts stay on the filesystem;
/// transactional/searchable metadata and immutable JSON snapshots live here.
/// Schema lifecycle is owned by <see cref="SqliteSchemaMigrationRunner"/>.
/// </summary>
public sealed class SqliteMetadataDatabase
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly SqliteSchemaMigrationRunner _migrations = new();
    private volatile bool _initialized;

    public SqliteMetadataDatabase(IWebHostEnvironment env)
    {
        var dataRoot = VisionStudioDataRoot.Resolve(env.ContentRootPath);
        Directory.CreateDirectory(dataRoot);
        _databasePath = Path.Combine(dataRoot, "visionstudio.db");
        _connectionString = BuildConnectionString(_databasePath, SqliteOpenMode.ReadWriteCreate, pooling: true);
    }

    public string DatabasePath => _databasePath;
    public int CurrentSchemaVersion => SqliteSchemaMigrationRunner.CurrentVersion;

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await ConfigureConnectionAsync(connection, ct);
        return connection;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await ConfigureConnectionAsync(connection, ct);
            await ConfigureWalAsync(connection, ct);
            await _migrations.MigrateAsync(connection, ct);
            _initialized = true;
        }
        finally { _initializeGate.Release(); }
    }

    public async Task<SchemaMigrationStatus> GetSchemaStatusAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await ConfigureConnectionAsync(connection, ct);
        return await _migrations.GetStatusAsync(connection, ct);
    }

    /// <summary>Create a transactionally consistent SQLite snapshot using the Online Backup API.</summary>
    public async Task CreateSnapshotAsync(string destinationPath, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _snapshotGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);

            await using var source = new SqliteConnection(_connectionString);
            await source.OpenAsync(ct);
            await ConfigureConnectionAsync(source, ct);
            await using (var checkpoint = source.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                await checkpoint.ExecuteNonQueryAsync(ct);
            }

            await using var destination = new SqliteConnection(BuildConnectionString(destinationPath, SqliteOpenMode.ReadWriteCreate, pooling: false));
            await destination.OpenAsync(ct);
            source.BackupDatabase(destination);
        }
        finally { _snapshotGate.Release(); }
    }

    /// <summary>
    /// Restore a verified SQLite snapshot into the live database using the Online Backup API,
    /// then migrate it forward to the current schema. Callers must establish maintenance mode first.
    /// </summary>
    public async Task RestoreSnapshotAsync(string snapshotPath, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _snapshotGate.WaitAsync(ct);
        try
        {
            await using var source = new SqliteConnection(BuildConnectionString(snapshotPath, SqliteOpenMode.ReadOnly, pooling: false));
            await source.OpenAsync(ct);
            await using var destination = new SqliteConnection(_connectionString);
            await destination.OpenAsync(ct);
            await ConfigureConnectionAsync(destination, ct);
            source.BackupDatabase(destination);
            await ConfigureWalAsync(destination, ct);
            await _migrations.MigrateAsync(destination, ct);
        }
        finally { _snapshotGate.Release(); }
    }

    /// <summary>Migrate an extracted backup in isolation before hashing or touching the live database.</summary>
    public async Task<(bool Ok, string Result, int SchemaVersion)> PrepareSnapshotForRestoreAsync(string snapshotPath, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(snapshotPath, SqliteOpenMode.ReadWrite, pooling: false));
        await connection.OpenAsync(ct);
        await ConfigureConnectionAsync(connection, ct);
        await _migrations.MigrateAsync(connection, ct);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await integrity.ExecuteScalarAsync(ct)) ?? "unknown";
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT version FROM schema_info WHERE id=1;";
        var version = Convert.ToInt32(await schema.ExecuteScalarAsync(ct));
        return (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), result, version);
    }

    public static async Task<(bool Ok, string Result, int SchemaVersion)> ValidateSnapshotAsync(string snapshotPath, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(snapshotPath, SqliteOpenMode.ReadOnly, pooling: false));
        await connection.OpenAsync(ct);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await integrity.ExecuteScalarAsync(ct)) ?? "unknown";
        var schemaVersion = 0;
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='schema_info') THEN (SELECT version FROM schema_info WHERE id=1) ELSE 0 END;";
        schemaVersion = Convert.ToInt32(await schema.ExecuteScalarAsync(ct));
        return (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), result, schemaVersion);
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync(ct);
    }

    private static async Task ConfigureWalAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        await wal.ExecuteScalarAsync(ct);
        wal.CommandText = "PRAGMA journal_size_limit=67108864;";
        await wal.ExecuteNonQueryAsync(ct);
    }

    private static string BuildConnectionString(string path, SqliteOpenMode mode, bool pooling)
        => new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = pooling,
            DefaultTimeout = 5
        }.ToString();
}
