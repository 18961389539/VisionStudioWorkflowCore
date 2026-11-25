using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Engine.Camera;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

public sealed record CameraFeatureProfileRecord(
    string Id,
    string Name,
    string Driver,
    string? SourceCameraId,
    string ProfileHash,
    CameraCommissioningProfile Profile,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class CameraFeatureProfileStore(IVisionStudioDbFactory dbFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public CameraFeatureProfileStore(SqliteMetadataDatabase database) : this(new VisionStudioDbFactory(database)) { }

    public async Task<IReadOnlyList<CameraFeatureProfileRecord>> ListAsync(string? driver = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<CameraFeatureProfileEntity> query = db.CameraFeatureProfiles;
        if (!string.IsNullOrWhiteSpace(driver))
        {
            var trimmed = driver.Trim();
            query = query.Where(x => x.Driver == trimmed);
        }
        var entities = await query
            .OrderBy(x => x.Driver)
            .ThenBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<CameraFeatureProfileRecord> GetAsync(string id, CancellationToken ct = default)
    {
        var normalizedId = NormalizeId(id);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.CameraFeatureProfiles.FirstOrDefaultAsync(x => x.Id == normalizedId, ct)
            ?? throw new KeyNotFoundException($"Camera feature profile '{id}' was not found.");
        return ToRecord(entity);
    }

    public async Task<CameraFeatureProfileRecord> UpsertAsync(
        string id,
        string name,
        string driver,
        CameraCommissioningProfile profile,
        string? sourceCameraId = null,
        CancellationToken ct = default)
    {
        id = NormalizeId(id);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Profile name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(driver)) throw new ArgumentException("Profile driver is required.", nameof(driver));
        profile = CameraFeatureProfiles.Normalize(profile);
        var hash = CameraFeatureProfiles.Hash(profile);
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(profile, Json);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            var entity = await db.CameraFeatureProfiles.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (entity is null)
            {
                entity = new CameraFeatureProfileEntity
                {
                    Id = id,
                    Name = name.Trim(),
                    Driver = driver.Trim(),
                    SourceCameraId = sourceCameraId?.Trim(),
                    ProfileHash = hash,
                    ProfileJson = json,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.CameraFeatureProfiles.Add(entity);
            }
            else
            {
                entity.Name = name.Trim();
                entity.Driver = driver.Trim();
                entity.SourceCameraId = sourceCameraId?.Trim();
                entity.ProfileHash = hash;
                entity.ProfileJson = json;
                entity.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToRecord(entity);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var normalizedId = NormalizeId(id);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.CameraFeatureProfiles.Where(x => x.Id == normalizedId).ExecuteDeleteAsync(ct);
        if (affected == 0) throw new KeyNotFoundException($"Camera feature profile '{id}' was not found.");
    }

    private static CameraFeatureProfileRecord ToRecord(CameraFeatureProfileEntity entity)
    {
        var profile = JsonSerializer.Deserialize<CameraCommissioningProfile>(entity.ProfileJson, Json)
            ?? throw new InvalidDataException($"Camera feature profile '{entity.Id}' contains invalid JSON.");
        profile = CameraFeatureProfiles.Normalize(profile);
        var actualHash = CameraFeatureProfiles.Hash(profile);
        if (!string.Equals(entity.ProfileHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Camera feature profile '{entity.Id}' failed SHA-256 integrity validation.");
        return new CameraFeatureProfileRecord(
            entity.Id, entity.Name, entity.Driver, entity.SourceCameraId,
            entity.ProfileHash, profile, entity.CreatedAt, entity.UpdatedAt);
    }

    private static string NormalizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Profile id is required.", nameof(id));
        var value = id.Trim();
        if (value.Length > 80 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("Profile id may contain only letters, digits, '-', '_' and '.', max 80 characters.", nameof(id));
        return value;
    }
}
