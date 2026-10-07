using System.Security.Cryptography;
using System.Text.Json;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Provenance;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Provenance;

public sealed record HardwareProvenanceSnapshot(
    string Kind,
    string Id,
    string Provider,
    HardwareProvenanceData Data,
    string Fingerprint,
    string Completeness,
    IReadOnlyList<string> MissingRecommendedFields,
    DateTimeOffset CapturedAt,
    bool HasManualDeclaration,
    bool LiveProbeConfigured = false,
    bool LiveProbeSucceeded = false,
    bool LiveProbeRequired = false,
    string? LiveProbeProvider = null,
    string? LiveProbeError = null);

public sealed class HardwareProvenanceService(
    HardwareProvenanceStore store,
    CameraManager cameras,
    DeviceManager devices,
    RobotManager robots,
    VendorProvenanceProbeRegistry vendorProbes)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HardwareProvenanceSnapshot>> ListAsync(CancellationToken ct = default)
    {
        var output = new List<HardwareProvenanceSnapshot>();
        foreach (var camera in cameras.List()) output.Add(await CaptureAsync("camera", camera.Id, ct));
        foreach (var device in devices.List()) output.Add(await CaptureAsync("device", device.Id, ct));
        foreach (var robot in robots.List()) output.Add(await CaptureAsync("robot", robot.Id, ct));
        return output.OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<HardwareProvenanceSnapshot> CaptureAsync(string kind, string id, CancellationToken ct = default)
    {
        kind = NormalizeKind(kind);
        var automaticResult = await AutomaticAsync(kind, id, ct);
        var automatic = automaticResult.Data;
        var provider = automaticResult.Provider;
        var probe = automaticResult.Probe;
        var declared = await store.GetAsync(kind, id, ct);
        var declaredData = declared?.Declaration.ToData();
        var merged = Merge(automatic, declaredData);
        var missing = MissingRecommended(kind, merged);
        var completeness = missing.Count == 0 ? "Complete" : IsEmpty(merged) ? "Unavailable" : "Partial";
        // Hash both channels independently. A manual declaration can improve the displayed merged record,
        // but it can never mask a changed serial/firmware value that an adapter reports automatically.
        var fingerprint = Fingerprint(kind, id, automatic, declaredData);
        return new HardwareProvenanceSnapshot(
            kind, id, declared is null ? provider : provider + "+declared", merged, fingerprint,
            completeness, missing, DateTimeOffset.UtcNow, declared is not null,
            probe is not null, probe?.Succeeded ?? false, probe?.RequiredForProduction ?? false,
            probe?.Provider, probe?.Error);
    }

    public async Task<HardwareProvenanceSnapshot> UpsertAsync(string kind, string id, HardwareProvenanceDeclaration declaration, CancellationToken ct = default)
    {
        kind = NormalizeKind(kind);
        EnsureExists(kind, id);
        await store.UpsertAsync(kind, id, declaration, ct);
        return await CaptureAsync(kind, id, ct);
    }

    public async Task<bool> DeleteDeclarationAsync(string kind, string id, CancellationToken ct = default)
    {
        kind = NormalizeKind(kind);
        EnsureExists(kind, id);
        return await store.DeleteAsync(kind, id, ct);
    }

    public async Task<HardwareProvenanceSnapshot> CaptureForProductionAsync(string kind, string id, CancellationToken ct = default)
    {
        var snapshot = await CaptureAsync(kind, id, ct);
        if (snapshot.LiveProbeConfigured && snapshot.LiveProbeRequired && !snapshot.LiveProbeSucceeded)
            throw new InvalidOperationException(
                $"Required live hardware provenance probe '{snapshot.LiveProbeProvider}' for {snapshot.Kind} '{snapshot.Id}' is unavailable: {snapshot.LiveProbeError ?? "unknown error"}.");
        return snapshot;
    }

    private async Task<(HardwareProvenanceData Data, string Provider, VendorProvenanceProbeResult? Probe)> AutomaticAsync(string kind, string id, CancellationToken ct)
    {
        HardwareProvenanceData automatic;
        string provider;
        switch (kind)
        {
            case "camera":
            {
                var adapter = cameras.Require(id);
                if (adapter is IAsyncHardwareProvenanceProvider asyncHardware)
                {
                    provider = "adapter-async:" + adapter.Driver;
                    automatic = await asyncHardware.GetHardwareProvenanceAsync(ct);
                }
                else if (adapter is IHardwareProvenanceProvider hardware)
                {
                    provider = "adapter:" + adapter.Driver;
                    automatic = hardware.GetHardwareProvenance();
                }
                else
                {
                    var descriptor = cameras.Get(id);
                    provider = "descriptor:" + descriptor.Driver;
                    automatic = new HardwareProvenanceData(ProductName: descriptor.Name, Model: descriptor.Driver);
                }
                break;
            }
            case "device":
            {
                var driver = devices.Require(id);
                if (driver is IAsyncHardwareProvenanceProvider asyncHardware)
                {
                    provider = "adapter-async:" + driver.Driver;
                    automatic = await asyncHardware.GetHardwareProvenanceAsync(ct);
                }
                else if (driver is IHardwareProvenanceProvider hardware)
                {
                    provider = "adapter:" + driver.Driver;
                    automatic = hardware.GetHardwareProvenance();
                }
                else
                {
                    var descriptor = devices.Get(id);
                    provider = "descriptor:" + descriptor.Driver;
                    automatic = new HardwareProvenanceData(
                        Manufacturer: NullIfGeneric(descriptor.Vendor), ProductName: descriptor.Name, Model: descriptor.Model,
                        Attributes: new Dictionary<string, string> { ["protocol"] = descriptor.Protocol, ["endpoint"] = descriptor.Endpoint });
                }
                break;
            }
            case "robot":
            {
                var adapter = robots.Require(id);
                if (adapter is IAsyncHardwareProvenanceProvider asyncHardware)
                {
                    provider = "adapter-async:" + adapter.Driver;
                    automatic = await asyncHardware.GetHardwareProvenanceAsync(ct);
                }
                else if (adapter is IHardwareProvenanceProvider hardware)
                {
                    provider = "adapter:" + adapter.Driver;
                    automatic = hardware.GetHardwareProvenance();
                }
                else
                {
                    var descriptor = robots.Get(id);
                    provider = "descriptor:" + descriptor.Driver;
                    automatic = new HardwareProvenanceData(
                        Manufacturer: NullIfGeneric(descriptor.Vendor), ProductName: descriptor.Name, Model: descriptor.Model,
                        Attributes: new Dictionary<string, string> { ["baseFrame"] = descriptor.BaseFrame, ["unit"] = descriptor.Unit });
                }
                break;
            }
            default: throw new InvalidOperationException($"Unknown hardware provenance kind '{kind}'.");
        }

        VendorProvenanceProbeResult? probeResult = null;
        if (vendorProbes.TryGet(kind, id, out var probe))
        {
            probeResult = await probe.CaptureAsync(ct);
            provider += "+live:" + probe.Provider;
            if (probeResult.Succeeded) automatic = Merge(automatic, probeResult.Data);
        }
        return (Normalize(automatic), provider, probeResult);
    }

    private void EnsureExists(string kind, string id)
    {
        _ = kind switch
        {
            "camera" => cameras.Get(id).Id,
            "device" => devices.Get(id).Id,
            "robot" => robots.Get(id).Id,
            _ => throw new InvalidOperationException($"Unknown hardware provenance kind '{kind}'.")
        };
    }

    private static HardwareProvenanceData Merge(HardwareProvenanceData automatic, HardwareProvenanceData? declared)
    {
        if (declared is null) return Normalize(automatic);
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (automatic.Attributes is not null) foreach (var x in automatic.Attributes) attributes[x.Key] = x.Value;
        if (declared.Attributes is not null) foreach (var x in declared.Attributes) attributes[x.Key] = x.Value;
        return Normalize(new HardwareProvenanceData(
            Pick(declared.Manufacturer, automatic.Manufacturer), Pick(declared.ProductName, automatic.ProductName), Pick(declared.Model, automatic.Model),
            Pick(declared.SerialNumber, automatic.SerialNumber), Pick(declared.HardwareRevision, automatic.HardwareRevision), Pick(declared.FirmwareVersion, automatic.FirmwareVersion),
            Pick(declared.SoftwareVersion, automatic.SoftwareVersion), Pick(declared.ControllerVersion, automatic.ControllerVersion), Pick(declared.ProgramName, automatic.ProgramName),
            Pick(declared.ProgramHash, automatic.ProgramHash), attributes.Count == 0 ? null : attributes));
    }

    private static HardwareProvenanceData Normalize(HardwareProvenanceData value)
    {
        IReadOnlyDictionary<string, string>? attrs = null;
        if (value.Attributes is { Count: > 0 })
            attrs = value.Attributes.Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key.Trim(), x => x.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        return value with
        {
            Manufacturer = Clean(value.Manufacturer), ProductName = Clean(value.ProductName), Model = Clean(value.Model), SerialNumber = Clean(value.SerialNumber),
            HardwareRevision = Clean(value.HardwareRevision), FirmwareVersion = Clean(value.FirmwareVersion), SoftwareVersion = Clean(value.SoftwareVersion),
            ControllerVersion = Clean(value.ControllerVersion), ProgramName = Clean(value.ProgramName), ProgramHash = Clean(value.ProgramHash), Attributes = attrs
        };
    }

    private static IReadOnlyList<string> MissingRecommended(string kind, HardwareProvenanceData d)
    {
        var missing = new List<string>();
        void Need(string name, string? value) { if (string.IsNullOrWhiteSpace(value)) missing.Add(name); }
        Need("manufacturer", d.Manufacturer);
        Need("model", d.Model);
        Need("serialNumber", d.SerialNumber);
        if (kind == "camera") Need("firmwareVersion", d.FirmwareVersion);
        if (kind == "device") Need("firmwareVersion", d.FirmwareVersion);
        if (kind == "robot")
        {
            Need("controllerVersion", d.ControllerVersion);
            Need("programName", d.ProgramName);
            Need("programHash", d.ProgramHash);
        }
        return missing;
    }

    private static bool IsEmpty(HardwareProvenanceData d)
        => new[] { d.Manufacturer, d.ProductName, d.Model, d.SerialNumber, d.HardwareRevision, d.FirmwareVersion, d.SoftwareVersion, d.ControllerVersion, d.ProgramName, d.ProgramHash }
            .All(string.IsNullOrWhiteSpace) && (d.Attributes is null || d.Attributes.Count == 0);

    private static string Fingerprint(string kind, string id, HardwareProvenanceData automatic, HardwareProvenanceData? declared)
    {
        static object Channel(HardwareProvenanceData d) => new
        {
            d.Manufacturer, d.ProductName, d.Model, d.SerialNumber, d.HardwareRevision, d.FirmwareVersion,
            d.SoftwareVersion, d.ControllerVersion, d.ProgramName, d.ProgramHash,
            attributes = d.Attributes?.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        var canonical = new
        {
            kind, id,
            adapter = Channel(Normalize(automatic)),
            declaration = declared is null ? null : Channel(Normalize(declared))
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeKind(string kind)
    {
        var normalized = kind.Trim().ToLowerInvariant();
        return normalized is "camera" or "device" or "robot" ? normalized : throw new InvalidOperationException("Hardware provenance kind must be camera, device or robot.");
    }
    private static string? Pick(string? preferred, string? fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NullIfGeneric(string? value) => string.Equals(value, "Virtual", StringComparison.OrdinalIgnoreCase) ? null : Clean(value);
}
