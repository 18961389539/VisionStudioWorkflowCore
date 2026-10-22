using System.Text.Json;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Api.Provenance;

public sealed record HardwareProvenanceDeclaration(
    string? Manufacturer = null,
    string? ProductName = null,
    string? Model = null,
    string? SerialNumber = null,
    string? HardwareRevision = null,
    string? FirmwareVersion = null,
    string? SoftwareVersion = null,
    string? ControllerVersion = null,
    string? ProgramName = null,
    string? ProgramHash = null,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    public HardwareProvenanceData ToData() => new(
        Clean(Manufacturer), Clean(ProductName), Clean(Model), Clean(SerialNumber), Clean(HardwareRevision),
        Clean(FirmwareVersion), Clean(SoftwareVersion), Clean(ControllerVersion), Clean(ProgramName), Clean(ProgramHash),
        NormalizeAttributes(Attributes));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyDictionary<string, string>? NormalizeAttributes(IReadOnlyDictionary<string, string>? input)
    {
        if (input is null || input.Count == 0) return null;
        return input
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key.Trim(), x => x.Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record HardwareProvenanceDeclarationRecord(
    string Kind,
    string Id,
    HardwareProvenanceDeclaration Declaration,
    DateTimeOffset UpdatedAt);

public sealed class HardwareProvenanceStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public HardwareProvenanceStore(string root)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "hardware-provenance.json");
    }

    public async Task<IReadOnlyList<HardwareProvenanceDeclarationRecord>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadNoLockAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<HardwareProvenanceDeclarationRecord?> GetAsync(string kind, string id, CancellationToken ct = default)
        => (await ListAsync(ct)).FirstOrDefault(x => Same(x.Kind, kind) && Same(x.Id, id));

    public async Task<HardwareProvenanceDeclarationRecord> UpsertAsync(string kind, string id, HardwareProvenanceDeclaration declaration, CancellationToken ct = default)
    {
        ValidateKey(kind, id);
        ArgumentNullException.ThrowIfNull(declaration);
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await ReadNoLockAsync(ct)).ToList();
            items.RemoveAll(x => Same(x.Kind, kind) && Same(x.Id, id));
            var record = new HardwareProvenanceDeclarationRecord(kind.ToLowerInvariant(), id.Trim(), declaration, DateTimeOffset.UtcNow);
            items.Add(record);
            await WriteNoLockAsync(items.OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray(), ct);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string kind, string id, CancellationToken ct = default)
    {
        ValidateKey(kind, id);
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await ReadNoLockAsync(ct)).ToList();
            var removed = items.RemoveAll(x => Same(x.Kind, kind) && Same(x.Id, id)) > 0;
            if (removed) await WriteNoLockAsync(items, ct);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<HardwareProvenanceDeclarationRecord>> ReadNoLockAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<HardwareProvenanceDeclarationRecord>>(stream, _json, ct) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Hardware provenance store '{_path}' is invalid JSON.", ex);
        }
    }

    private async Task WriteNoLockAsync(IReadOnlyList<HardwareProvenanceDeclarationRecord> items, CancellationToken ct)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, items, _json, ct);
        File.Move(temp, _path, overwrite: true);
    }

    private static void ValidateKey(string kind, string id)
    {
        if (kind is not ("camera" or "device" or "robot")) throw new InvalidOperationException("Hardware provenance kind must be camera, device or robot.");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Hardware provenance asset id is required.");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
