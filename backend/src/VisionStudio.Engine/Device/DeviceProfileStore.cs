using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine.Device;

/// <summary>
/// Persists protocol/device mapping separately from Workflow JSON. The workflow contract is the
/// stable Tag ID; protocol addresses can change by replacing a device profile.
/// </summary>
public sealed record DeviceProfile(
    string Id,
    string Kind,
    string Name,
    string Host,
    int Port,
    int TimeoutMs,
    byte UnitId = 1,
    Modbus32BitOrder RegisterOrder = Modbus32BitOrder.ABCD,
    string CpuType = "S71500",
    short Rack = 0,
    short Slot = 0,
    IReadOnlyList<DeviceTagDefinition>? Tags = null,
    DeviceRuntimeSettings? Settings = null,
    DateTimeOffset? UpdatedAt = null);

public sealed class DeviceProfileStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public DeviceProfileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<IReadOnlyList<DeviceProfile>> ListAsync(CancellationToken ct = default)
    {
        var output = new List<DeviceProfile>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var profile = await JsonSerializer.DeserializeAsync<DeviceProfile>(stream, _json, ct);
                if (profile is not null) output.Add(profile);
            }
            catch (JsonException)
            {
                // A corrupt profile is skipped rather than preventing the API from starting.
            }
        }
        return output;
    }

    public async Task SaveAsync(DeviceProfile profile, CancellationToken ct = default)
    {
        ValidateProfile(profile);
        var normalized = profile with
        {
            Tags = profile.Tags ?? Array.Empty<DeviceTagDefinition>(),
            Settings = (profile.Settings ?? new DeviceRuntimeSettings()).Normalize(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var path = ProfilePath(profile.Id);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, normalized, _json, ct);
        File.Move(temp, path, overwrite: true);
    }

    public async Task UpdateSettingsAsync(string id, DeviceRuntimeSettings settings, CancellationToken ct = default)
    {
        var profile = (await ListAsync(ct)).FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;
        await SaveAsync(profile with { Settings = settings.Normalize() }, ct);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = ProfilePath(id);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    private string ProfilePath(string id)
    {
        var safe = new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new InvalidOperationException("Device profile ID is invalid.");
        return Path.Combine(_root, safe + ".json");
    }

    private static void ValidateProfile(DeviceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id)) throw new InvalidOperationException("Device profile ID is required.");
        if (string.IsNullOrWhiteSpace(profile.Host)) throw new InvalidOperationException("Device host/IP is required.");
        if (profile.Port is < 1 or > 65535) throw new InvalidOperationException("Device port must be between 1 and 65535.");
        if (profile.Kind is not ("modbus-tcp" or "s7")) throw new InvalidOperationException($"Unknown device profile kind '{profile.Kind}'.");
    }
}
