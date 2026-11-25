using VisionStudio.Abstractions;

namespace VisionStudio.Api;

public sealed record PluginPackageManifest
{
    public int SchemaVersion { get; init; } = VisionPluginSdk.PackageManifestSchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string EntryAssembly { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int MinimumSdkApiVersion { get; init; } = VisionPluginSdk.ApiVersion;
    public int MaximumSdkApiVersion { get; init; } = VisionPluginSdk.ApiVersion;
    public string? Vendor { get; init; }
    public string? Description { get; init; }
    public string? AssemblySha256 { get; init; }
    public VisionPluginIsolationMode IsolationMode { get; init; } = VisionPluginIsolationMode.InProcess;
    public int? WorkerPoolSize { get; init; }
    public int? WorkerSharedMemoryThresholdBytes { get; init; }
}

public sealed record PluginToolLoadInfo(
    string Type,
    string DisplayName,
    string? ToolVersion,
    VisionExecutorLifetime Lifetime,
    VisionExecutorConcurrency Concurrency,
    bool Experimental);

public sealed record PluginLoadInfo(
    string Id,
    string Name,
    string Version,
    string AssemblyPath,
    int NodeCount,
    bool Loaded,
    string? Error = null,
    string? AssemblySha256 = null,
    string PackageFormat = "legacy-v1",
    string? PackageManifestSha256 = null,
    int SdkApiVersion = 1,
    int MinimumSdkApiVersion = 1,
    int MaximumSdkApiVersion = 1,
    string? Vendor = null,
    bool Enabled = true,
    bool RestartRequired = false,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<PluginToolLoadInfo>? Tools = null,
    VisionPluginIsolationMode IsolationMode = VisionPluginIsolationMode.InProcess,
    int? WorkerProcessId = null,
    string? WorkerState = null,
    int? WorkerPoolSize = null,
    string? WorkerImageTransport = null,
    int? WorkerSharedMemoryThresholdBytes = null,
    int? WorkerProtocolVersion = null);

public sealed record PluginSdkInfo(
    int ApiVersion,
    int PackageManifestSchemaVersion,
    IReadOnlyList<string> SupportedLifetimes,
    IReadOnlyList<string> SupportedConcurrencyModes,
    IReadOnlyList<string> SupportedIsolationModes,
    bool RuntimeDiscovery,
    bool RuntimeReplacement,
    string IsolationBoundary);

public sealed record PluginRescanResult(
    int LoadedNow,
    int Failed,
    int RestartRequired,
    IReadOnlyList<PluginLoadInfo> Plugins);
