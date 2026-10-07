using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine.Camera;

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

public sealed class CameraFeatureProfileStore(SqliteMetadataDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<CameraFeatureProfileRecord>> ListAsync(string? driver = null, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(driver)
            ? "SELECT id,name,driver,source_camera_id,profile_hash,profile_json,created_at,updated_at FROM camera_feature_profiles ORDER BY driver,name,id;"
            : "SELECT id,name,driver,source_camera_id,profile_hash,profile_json,created_at,updated_at FROM camera_feature_profiles WHERE driver=$driver ORDER BY name,id;";
        if (!string.IsNullOrWhiteSpace(driver)) command.Parameters.AddWithValue("$driver", driver.Trim());
        var output = new List<CameraFeatureProfileRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) output.Add(Read(reader));
        return output;
    }

    public async Task<CameraFeatureProfileRecord> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,driver,source_camera_id,profile_hash,profile_json,created_at,updated_at FROM camera_feature_profiles WHERE id=$id;";
        command.Parameters.AddWithValue("$id", NormalizeId(id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException($"Camera feature profile '{id}' was not found.");
        return Read(reader);
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
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO camera_feature_profiles(id,name,driver,source_camera_id,profile_hash,profile_json,created_at,updated_at)
VALUES($id,$name,$driver,$source,$hash,$json,$at,$at)
ON CONFLICT(id) DO UPDATE SET
  name=excluded.name,
  driver=excluded.driver,
  source_camera_id=excluded.source_camera_id,
  profile_hash=excluded.profile_hash,
  profile_json=excluded.profile_json,
  updated_at=excluded.updated_at;
""";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name.Trim());
            command.Parameters.AddWithValue("$driver", driver.Trim());
            command.Parameters.AddWithValue("$source", (object?)sourceCameraId?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$at", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM camera_feature_profiles WHERE id=$id;";
        command.Parameters.AddWithValue("$id", NormalizeId(id));
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"Camera feature profile '{id}' was not found.");
    }

    private static CameraFeatureProfileRecord Read(SqliteDataReader reader)
    {
        var profile = JsonSerializer.Deserialize<CameraCommissioningProfile>(reader.GetString(5), Json)
            ?? throw new InvalidDataException($"Camera feature profile '{reader.GetString(0)}' contains invalid JSON.");
        profile = CameraFeatureProfiles.Normalize(profile);
        var storedHash = reader.GetString(4);
        var actualHash = CameraFeatureProfiles.Hash(profile);
        if (!string.Equals(storedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Camera feature profile '{reader.GetString(0)}' failed SHA-256 integrity validation.");
        return new CameraFeatureProfileRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            storedHash, profile, DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7)));
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
