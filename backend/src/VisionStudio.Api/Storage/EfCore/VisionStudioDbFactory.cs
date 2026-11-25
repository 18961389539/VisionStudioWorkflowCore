using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace VisionStudio.Api.Storage.EfCore;

public interface IVisionStudioDbFactory
{
    SqliteMetadataDatabase Database { get; }
    Task<VisionStudioDbContext> CreateDbContextAsync(CancellationToken ct = default, bool strongDurability = false);
    VisionStudioDbContext CreateDbContext();
    Task<ImmediateDbTransaction> BeginImmediateTransactionAsync(VisionStudioDbContext db, CancellationToken ct = default);
}

public sealed class VisionStudioDbFactory : IVisionStudioDbFactory
{
    private static readonly DbContextOptions<VisionStudioDbContext> SharedOptions =
        new DbContextOptionsBuilder<VisionStudioDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

    private readonly SqliteMetadataDatabase _database;

    public VisionStudioDbFactory(SqliteMetadataDatabase database)
    {
        _database = database;
    }

    public SqliteMetadataDatabase Database => _database;

    public async Task<VisionStudioDbContext> CreateDbContextAsync(CancellationToken ct = default, bool strongDurability = false)
    {
        var connection = await _database.OpenConnectionAsync(ct, strongDurability).ConfigureAwait(false);
        try
        {
            return CreateContextForConnection(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public VisionStudioDbContext CreateDbContext()
    {
        var connection = _database.OpenConnection();
        try
        {
            return CreateContextForConnection(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Task<ImmediateDbTransaction> BeginImmediateTransactionAsync(VisionStudioDbContext db, CancellationToken ct = default)
        => db.BeginImmediateTransactionAsync(ct);

    private static VisionStudioDbContext CreateContextForConnection(SqliteConnection connection)
    {
        var db = new VisionStudioDbContext(SharedOptions);
        db.AttachOwnedConnection(connection);
        db.Database.SetDbConnection(connection);
        return db;
    }
}

public sealed class ImmediateDbTransaction(IDbContextTransaction efTransaction, SqliteTransaction sqliteTransaction)
    : IAsyncDisposable, IDisposable
{
    public SqliteTransaction SqliteTransaction => sqliteTransaction;

    public Task CommitAsync(CancellationToken ct = default) => efTransaction.CommitAsync(ct);

    public Task RollbackAsync(CancellationToken ct = default) => efTransaction.RollbackAsync(ct);

    public void Dispose()
    {
        efTransaction.Dispose();
        sqliteTransaction.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await efTransaction.DisposeAsync().ConfigureAwait(false);
        await sqliteTransaction.DisposeAsync().ConfigureAwait(false);
    }
}

public static class VisionStudioDbContextExtensions
{
    public static async Task<ImmediateDbTransaction> BeginImmediateTransactionAsync(
        this VisionStudioDbContext db,
        CancellationToken ct = default)
    {
        var sqliteConnection = (SqliteConnection)db.Database.GetDbConnection();
        var sqliteTx = sqliteConnection.BeginTransaction(deferred: false);
        try
        {
            var efTx = await db.Database.UseTransactionAsync(sqliteTx, ct).ConfigureAwait(false);
            if (efTx is null)
            {
                await sqliteTx.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Failed to attach immediate SQLite transaction to EF Core DbContext.");
            }
            return new ImmediateDbTransaction(efTx, sqliteTx);
        }
        catch
        {
            await sqliteTx.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
