using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed record EngineRuntimeDependency(
    string Name,
    string Version,
    string InformationalVersion,
    string BinaryHash);

public sealed record PluginToolRuntimeDependency(string Type, string Version);

public sealed record PluginRuntimeDependency(
    string Id,
    string Name,
    string Version,
    string AssemblyHash,
    int? SdkApiVersion = null,
    string? PackageManifestHash = null,
    IReadOnlyList<PluginToolRuntimeDependency>? Tools = null,
    string? IsolationMode = null,
    int? WorkerProtocolVersion = null,
    int? WorkerPoolSize = null,
    string? WorkerImageTransport = null,
    int? WorkerSharedMemoryThresholdBytes = null);

public sealed record CameraRuntimeDependency(
    string Id,
    string Driver,
    string? Source,
    string ConfigurationHash,
    CameraSettings Settings,
    string? SourceFingerprint = null,
    HardwareProvenanceSnapshot? Hardware = null,
    IReadOnlyList<string>? SynchronizationGroupHashes = null);

public sealed record DeviceRuntimeDependency(
    string Id,
    string Driver,
    string Protocol,
    string Endpoint,
    string ConfigurationHash,
    string? ProfileRevision,
    HardwareProvenanceSnapshot? Hardware = null);

public sealed record RobotRuntimeDependency(
    string Id,
    string Driver,
    string Vendor,
    string Model,
    string BaseFrame,
    string Unit,
    string ConfigurationHash,
    HardwareProvenanceSnapshot? Hardware = null);

public sealed record CalibrationRuntimeDependency(
    string AssetId,
    int Version,
    string SnapshotHash);

public sealed record WorkflowModuleRuntimeDependency(
    string ModuleId,
    int Version,
    string ModuleHash);

/// <summary>
/// 形状模型资产锁定项（P1）：发布时固定 (assetId, version, sha256)，生产运行前逐项校验。
/// 版本永远落成具体数字——模型资产的"最新版"不会在发布后悄悄漂移。
/// </summary>
public sealed record ModelAssetRuntimeDependency(
    string AssetId,
    int Version,
    string Sha256);

public sealed record RuntimeDependencyManifest(
    int SchemaVersion,
    string ManifestHash,
    DateTimeOffset CapturedAt,
    EngineRuntimeDependency Engine,
    IReadOnlyList<PluginRuntimeDependency> Plugins,
    IReadOnlyList<CameraRuntimeDependency> Cameras,
    IReadOnlyList<DeviceRuntimeDependency> Devices,
    IReadOnlyList<RobotRuntimeDependency> Robots,
    IReadOnlyList<CalibrationRuntimeDependency> Calibrations,
    IReadOnlyList<WorkflowModuleRuntimeDependency>? Modules = null,
    IReadOnlyList<ModelAssetRuntimeDependency>? ModelAssets = null);

public sealed record RuntimeDependencyDrift(
    string Kind,
    string Id,
    string Expected,
    string? Actual,
    string Message);

public sealed record RuntimeDependencyValidation(
    bool Compatible,
    string ExpectedManifestHash,
    string CurrentManifestHash,
    IReadOnlyList<RuntimeDependencyDrift> Drifts,
    RuntimeDependencyManifest CurrentManifest);

/// <summary>
/// Asset ids referenced by one workflow, resolved without capturing a full manifest.
/// Used to arbitrate ad-hoc/debug runs against a running Production Runtime.
/// </summary>
public sealed record WorkflowRuntimeReferences(
    IReadOnlyList<string> Cameras,
    IReadOnlyList<string> Devices,
    IReadOnlyList<string> Robots);

/// <summary>恢复前设备状态核对结果；不通过时必须禁止自动恢复新周期。</summary>
public sealed record DeviceStateVerification(bool Ok, IReadOnlyList<string> Issues)
{
    public string Summary => Issues.Count == 0 ? "all devices verified" : string.Join("; ", Issues);
}

/// <summary>
/// Captures the exact runtime dependencies referenced by one Workflow. The resulting ManifestHash is
/// semantic: CapturedAt and display-only timestamps are excluded, so unchanged runtime dependencies
/// produce the same hash across repeated publications.
/// </summary>
public sealed partial class RuntimeDependencyManifestService(
    VisionNodeRegistry registry,
    PluginManager plugins,
    CameraManager cameras,
    DeviceManager devices,
    DeviceProfileStore deviceProfiles,
    RobotManager robots,
    CalibrationAssetStore calibrations,
    HardwareProvenanceService hardwareProvenance,
    WorkflowModuleExpander? moduleExpander = null,
    CameraSynchronizationGroupStore? synchronizationGroups = null,
    DeviceLeaseRegistry? commandGate = null,
    CameraIntrinsicAssetStore? cameraIntrinsicAssets = null,
    AssetStore? assets = null,
    HandEyeCalibrationAssetStore? handEyeAssets = null,
    PlanarHandEyeCalibrationNode? handEyeSolver = null,
    HandEye3DCalibrationAssetStore? handEye3DAssets = null,
    HandEye3DCalibrationNode? handEye3DSolver = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string FingerprintAssembly(Assembly assembly)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location))
            {
                using var stream = File.OpenRead(assembly.Location);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
        }
        catch (Exception) when (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location)) { }
        return "mvid:" + assembly.ManifestModule.ModuleVersionId.ToString("N");
    }

    private static string HashCanonical(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void CompareById<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> current,
        Func<T, string> id,
        Func<T, string> fingerprint,
        string kind,
        List<RuntimeDependencyDrift> output)
    {
        var expectedMap = expected.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        var currentMap = current.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in expectedMap)
        {
            if (!currentMap.TryGetValue(key, out var actual))
            {
                output.Add(new RuntimeDependencyDrift(kind, key, fingerprint(value), null, $"Expected {kind.ToLowerInvariant()} is no longer available."));
                continue;
            }
            var expectedHash = fingerprint(value);
            var actualHash = fingerprint(actual);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                output.Add(new RuntimeDependencyDrift(kind, key, expectedHash, actualHash, $"{kind} configuration/binary changed after publication."));
        }
        foreach (var (key, value) in currentMap)
        {
            if (!expectedMap.ContainsKey(key))
                output.Add(new RuntimeDependencyDrift(kind, key, "<not present>", fingerprint(value), $"Current Workflow resolves an unexpected {kind.ToLowerInvariant()} dependency."));
        }
    }

    private static void CompareHardware<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> current,
        Func<T, string> id,
        Func<T, HardwareProvenanceSnapshot?> hardware,
        string kind,
        List<RuntimeDependencyDrift> output)
    {
        var expectedMap = expected.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        var currentMap = current.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in expectedMap)
        {
            if (!currentMap.TryGetValue(key, out var actual)) continue;
            var expectedHardware = hardware(value);
            var currentHardware = hardware(actual);
            if (expectedHardware is null)
            {
                output.Add(new RuntimeDependencyDrift(kind, key, "<not captured>", currentHardware?.Fingerprint,
                    "Published Job has no hardware provenance for this asset. Re-publish after reviewing hardware identity."));
                continue;
            }
            if (currentHardware is null || !string.Equals(expectedHardware.Fingerprint, currentHardware.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                output.Add(new RuntimeDependencyDrift(kind, key, expectedHardware.Fingerprint, currentHardware?.Fingerprint,
                    "Physical/controller provenance changed after publication."));
            }
        }
    }

    private sealed class ModelAssetRefComparer : IEqualityComparer<(string AssetId, int Version)>
    {
        public bool Equals((string AssetId, int Version) x, (string AssetId, int Version) y)
            => x.Version == y.Version && string.Equals(x.AssetId, y.AssetId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string AssetId, int Version) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AssetId), obj.Version);
    }

    private sealed class CalibrationRefComparer : IEqualityComparer<(string AssetId, int Version)>
    {
        public bool Equals((string AssetId, int Version) x, (string AssetId, int Version) y)
            => x.Version == y.Version && string.Equals(x.AssetId, y.AssetId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string AssetId, int Version) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AssetId), obj.Version);
    }
}
