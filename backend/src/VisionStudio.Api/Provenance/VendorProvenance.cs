using System.Collections.Concurrent;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Api.Provenance;

public sealed record VendorProvenanceProbeResult(
    string Provider,
    HardwareProvenanceData Data,
    bool Succeeded,
    bool RequiredForProduction,
    string? Error = null);

public interface IVendorHardwareProvenanceProbe : IDisposable
{
    string Kind { get; }
    string AssetId { get; }
    string Provider { get; }
    bool RequiredForProduction { get; }
    ValueTask<VendorProvenanceProbeResult> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed class VendorProvenanceProbeRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, IVendorHardwareProvenanceProbe> _probes =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string kind, string assetId) => $"{kind.Trim().ToLowerInvariant()}:{assetId.Trim()}";

    public void Register(IVendorHardwareProvenanceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!_probes.TryAdd(Key(probe.Kind, probe.AssetId), probe))
            throw new InvalidOperationException($"Vendor provenance probe already registered for {probe.Kind} '{probe.AssetId}'.");
    }

    public bool TryGet(string kind, string assetId, out IVendorHardwareProvenanceProbe probe)
        => _probes.TryGetValue(Key(kind, assetId), out probe!);

    public IReadOnlyList<(string Kind, string AssetId, string Provider, bool RequiredForProduction)> List()
        => _probes.Values
            .Select(x => (x.Kind, x.AssetId, x.Provider, x.RequiredForProduction))
            .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AssetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Dispose()
    {
        foreach (var probe in _probes.Values)
        {
            try { probe.Dispose(); } catch { }
        }
        _probes.Clear();
    }
}


public sealed class UnavailableVendorProvenanceProbe(
    string kind, string assetId, string provider, bool requiredForProduction, string error) : IVendorHardwareProvenanceProbe
{
    public string Kind { get; } = kind;
    public string AssetId { get; } = assetId;
    public string Provider { get; } = provider;
    public bool RequiredForProduction { get; } = requiredForProduction;
    public ValueTask<VendorProvenanceProbeResult> CaptureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new VendorProvenanceProbeResult(
            Provider,
            new HardwareProvenanceData(Attributes: new Dictionary<string, string>
            {
                ["liveProbe"] = Provider,
                ["liveProbeState"] = "unavailable"
            }),
            false,
            RequiredForProduction,
            error));
    public void Dispose() { }
}

public sealed class VendorProvenanceOptions
{
    public int CacheSeconds { get; set; } = 30;
    public List<BaslerPylonProbeOptions> Basler { get; set; } = [];
    public List<HikrobotMvsProbeOptions> Hikrobot { get; set; } = [];
    public List<AbbRwsProbeOptions> AbbRws { get; set; } = [];
}

public abstract class VendorProbeOptionsBase
{
    public bool Enabled { get; set; } = true;
    public string AssetId { get; set; } = "";
    public bool RequiredForProduction { get; set; } = true;
}

public sealed class BaslerPylonProbeOptions : VendorProbeOptionsBase
{
    public string? SerialNumber { get; set; }
    public string? UserDefinedName { get; set; }
    public string? AssemblyPath { get; set; }
}

public sealed class HikrobotMvsProbeOptions : VendorProbeOptionsBase
{
    public string? SerialNumber { get; set; }
    public string? UserDefinedName { get; set; }
    public string? AssemblyPath { get; set; }
}

public sealed class AbbRwsProbeOptions : VendorProbeOptionsBase
{
    public string BaseUrl { get; set; } = "http://127.0.0.1";
    public string Username { get; set; } = "Default User";
    public string PasswordEnvironmentVariable { get; set; } = "VISIONSTUDIO_ABB_RWS_PASSWORD";
    public string Task { get; set; } = "T_ROB1";
    public bool HashRapidProgram { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 8;
    public long MaxRapidBytes { get; set; } = 4 * 1024 * 1024;
    public bool AllowInvalidTlsCertificate { get; set; } = false;
}
