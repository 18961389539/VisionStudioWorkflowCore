using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VisionStudio.Api.Storage.EfCore;

public partial class VisionStudioDbContext : DbContext
{
    private SqliteConnection? _ownedConnection;

    public VisionStudioDbContext(DbContextOptions<VisionStudioDbContext> options)
        : base(options)
    {
    }

    internal void AttachOwnedConnection(SqliteConnection connection)
    {
        _ownedConnection = connection;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // The hand-written migrations own indexes; map them explicitly rather than adding EF-only indexes.
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(VisionStudioDbContext).Assembly,
            type => type.Namespace is not null &&
                    type.Namespace.StartsWith("VisionStudio.Api.Storage.EfCore", StringComparison.Ordinal));
    }

    public override void Dispose()
    {
        base.Dispose();
        _ownedConnection?.Dispose();
        _ownedConnection = null;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (_ownedConnection is not null)
        {
            await _ownedConnection.DisposeAsync().ConfigureAwait(false);
            _ownedConnection = null;
        }
    }

    private sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, string>(
        v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
