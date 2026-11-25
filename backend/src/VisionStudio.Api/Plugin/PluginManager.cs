using System.Text.Json;
using McMaster.NETCore.Plugins;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>
/// Host-side plugin package loader. V0.59 retains SDK 2.0 packages and adds profiled pooled WorkerProcess execution with shared-memory image transport while retaining the
/// V0.22 folder/DLL layout as legacy API-v1 compatibility. AssemblyLoadContext isolation is dependency isolation,
/// not a security sandbox. InProcess plugins share the host identity; WorkerProcess plugins use a separate process with the same OS identity.
/// </summary>
public sealed partial class PluginManager : IDisposable
{
    private readonly VisionNodeRegistry registry;
    private readonly PluginWorkerSupervisor? workers;
    private readonly ILogger<PluginManager> logger;

    public PluginManager(VisionNodeRegistry registry, PluginWorkerSupervisor workers, ILogger<PluginManager> logger)
    {
        this.registry = registry;
        this.workers = workers;
        this.logger = logger;
    }

    // Compatibility constructor for unit tests and hosts that only exercise in-process/legacy plugins.
    public PluginManager(VisionNodeRegistry registry, ILogger<PluginManager> logger)
    {
        this.registry = registry;
        this.logger = logger;
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly List<PluginLoader> _loaders = [];
    private readonly Dictionary<string, List<PluginLoadInfo>> _activeByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private List<PluginLoadInfo> _inventory = [];
    private string? _pluginsRoot;

    public IReadOnlyList<PluginLoadInfo> Plugins
    {
        get { lock (_sync) return _inventory.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public PluginSdkInfo SdkInfo { get; } = new(
        VisionPluginSdk.ApiVersion,
        VisionPluginSdk.PackageManifestSchemaVersion,
        Enum.GetNames<VisionExecutorLifetime>(),
        Enum.GetNames<VisionExecutorConcurrency>(),
        Enum.GetNames<VisionPluginIsolationMode>(),
        RuntimeDiscovery: true,
        RuntimeReplacement: false,
        IsolationBoundary: "InProcess uses AssemblyLoadContext dependency isolation. WorkerProcess executes the plugin in a separate process over a local named pipe; it isolates crashes/timeouts but is not an OS privilege sandbox.");

    public void LoadAll(string pluginsRoot)
    {
        lock (_sync)
        {
            _pluginsRoot = Path.GetFullPath(pluginsRoot);
            Directory.CreateDirectory(_pluginsRoot);
            _inventory = RescanCore().Plugins.ToList();
        }
    }

    public PluginRescanResult Rescan()
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_pluginsRoot))
                throw new InvalidOperationException("Plugin root is not initialized yet.");
            var result = RescanCore();
            _inventory = result.Plugins.ToList();
            return result;
        }
    }

    private PluginRescanResult RescanCore()
    {
        var root = _pluginsRoot ?? throw new InvalidOperationException("Plugin root is not initialized.");
        Directory.CreateDirectory(root);
        var inventory = new List<PluginLoadInfo>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedNow = 0;
        var failed = 0;

        foreach (var directory in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var fullDirectory = Path.GetFullPath(directory);
            seenDirectories.Add(fullDirectory);
            PackageCandidate candidate;
            try
            {
                candidate = ReadPackageCandidate(fullDirectory);
            }
            catch (Exception ex)
            {
                var id = Path.GetFileName(fullDirectory);
                inventory.Add(new PluginLoadInfo(id, id, "unknown", fullDirectory, 0, false, ex.Message));
                failed++;
                continue;
            }

            if (_activeByDirectory.TryGetValue(fullDirectory, out var active))
            {
                var changed = HasActivePackageChanged(candidate, active, out var warning);
                foreach (var item in active)
                {
                    var warnings = (item.Warnings ?? []).ToList();
                    if (changed && !string.IsNullOrWhiteSpace(warning) && !warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                        warnings.Add(warning);
                    inventory.Add(item with
                    {
                        Enabled = candidate.Manifest?.Enabled ?? true,
                        RestartRequired = changed,
                        Warnings = warnings
                    });
                }
                continue;
            }

            if (candidate.Manifest is { Enabled: false } manifest)
            {
                inventory.Add(DisabledInfo(candidate, manifest));
                continue;
            }

            try
            {
                var loaded = LoadPackage(candidate);
                _activeByDirectory[fullDirectory] = loaded.ToList();
                inventory.AddRange(loaded);
                loadedNow += loaded.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load VisionStudio plugin package {PluginDirectory}", fullDirectory);
                inventory.Add(FailedInfo(candidate, ex.Message));
                failed++;
            }
        }

        foreach (var pair in _activeByDirectory)
        {
            if (seenDirectories.Contains(pair.Key)) continue;
            foreach (var item in pair.Value)
            {
                var warnings = (item.Warnings ?? []).Append("Package directory was removed after activation; restart the host to unload the active plugin.").ToArray();
                inventory.Add(item with { RestartRequired = true, Warnings = warnings });
            }
        }

        var restart = inventory.Count(x => x.RestartRequired);
        return new PluginRescanResult(loadedNow, failed, restart, inventory.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private IReadOnlyList<PluginLoadInfo> LoadPackage(PackageCandidate candidate)
    {
        ValidateCandidate(candidate);
        var assemblyHash = ComputeSha256(candidate.AssemblyPath);
        if (!string.IsNullOrWhiteSpace(candidate.Manifest?.AssemblySha256) &&
            !string.Equals(candidate.Manifest.AssemblySha256, assemblyHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Assembly SHA-256 mismatch for '{candidate.Manifest.Id}'. Expected {candidate.Manifest.AssemblySha256}, actual {assemblyHash}.");

        if (candidate.Manifest?.IsolationMode == VisionPluginIsolationMode.WorkerProcess)
            return LoadWorkerPackage(candidate, assemblyHash);

        PluginLoader? loader = null;
        var registeredSources = new List<string>();
        try
        {
            loader = PluginLoader.CreateFromAssemblyFile(
                assemblyFile: candidate.AssemblyPath,
                isUnloadable: false,
                sharedTypes: [typeof(IVisionPlugin), typeof(IVisionPluginV2), typeof(IVisionNodeExecutor), typeof(NodeCatalogItem), typeof(VisionPluginNode)]);

            var pluginTypes = loader.LoadDefaultAssembly().GetTypes()
                .Where(t => typeof(IVisionPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .ToArray();
            if (pluginTypes.Length == 0)
                throw new InvalidOperationException("Assembly contains no IVisionPlugin implementation.");
            if (candidate.Manifest is not null && pluginTypes.Length != 1)
                throw new InvalidOperationException("SDK 2.0 package manifests require exactly one IVisionPlugin implementation per package.");

            var plugins = pluginTypes.Select(type =>
            {
                if (Activator.CreateInstance(type) is not IVisionPlugin plugin)
                    throw new InvalidOperationException($"Could not instantiate plugin '{type.FullName}'. A parameterless plugin constructor is required.");
                return plugin;
            }).ToArray();

            var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allNodeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prepared = new List<PreparedPlugin>();
            foreach (var plugin in plugins)
            {
                ValidateDescriptor(plugin.Descriptor);
                if (!sourceIds.Add(plugin.Descriptor.Id))
                    throw new InvalidOperationException($"Package declares duplicate plugin id '{plugin.Descriptor.Id}'.");
                if (registry.IsSourceRegistered(plugin.Descriptor.Id))
                    throw new InvalidOperationException($"Plugin id '{plugin.Descriptor.Id}' is already active. Replacing active plugins requires a host restart in V0.53.");

                if (plugin is IVisionPluginV2 && candidate.Manifest is null)
                    throw new InvalidOperationException($"SDK 2.0 plugin '{plugin.Descriptor.Id}' requires plugin.json schema v2. Legacy folder-only packaging is reserved for API-v1 compatibility plugins.");
                var compatibility = plugin is IVisionPluginV2 v2 ? v2.Compatibility : VisionPluginCompatibility.LegacyV1;
                var sdkApiVersion = plugin is IVisionPluginV2 ? VisionPluginSdk.ApiVersion : 1;
                if (plugin is IVisionPluginV2 && !compatibility.Supports(VisionPluginSdk.ApiVersion))
                    throw new InvalidOperationException($"Plugin '{plugin.Descriptor.Id}' supports SDK API {compatibility.MinimumApiVersion}..{compatibility.MaximumApiVersion}; host API is {VisionPluginSdk.ApiVersion}.");
                ValidateManifestAgainstPlugin(candidate.Manifest, plugin, compatibility);

                var nodes = plugin.CreateNodes().ToArray();
                if (nodes.Length == 0)
                    throw new InvalidOperationException($"Plugin '{plugin.Descriptor.Id}' contains no nodes.");
                foreach (var node in nodes)
                {
                    ValidateNodeSchema(plugin.Descriptor.Id, node, requireToolIdentity: plugin is IVisionPluginV2);
                    if (!allNodeTypes.Add(node.Catalog.Type))
                        throw new InvalidOperationException($"Package declares duplicate node type '{node.Catalog.Type}'.");
                    if (registry.IsRegistered(node.Catalog.Type))
                        throw new InvalidOperationException($"Node type '{node.Catalog.Type}' is already registered by the host or another plugin.");
                }
                prepared.Add(new PreparedPlugin(plugin, compatibility, sdkApiVersion, nodes));
            }

            var infos = new List<PluginLoadInfo>();
            foreach (var entry in prepared)
            {
                var plugin = entry.Plugin;
                registeredSources.Add(plugin.Descriptor.Id);
                foreach (var node in entry.Nodes)
                {
                    var catalog = node.Catalog with { PluginId = plugin.Descriptor.Id };
                    registry.Register(catalog, node, plugin.Descriptor.Id);
                }

                var warnings = new List<string>();
                if (entry.SdkApiVersion == 1)
                    warnings.Add("Legacy API-v1 compatibility plugin: add plugin.json + IVisionPluginV2 metadata to opt into SDK 2.0 package validation and tool identity provenance.");

                infos.Add(new PluginLoadInfo(
                    plugin.Descriptor.Id,
                    plugin.Descriptor.Name,
                    plugin.Descriptor.Version,
                    candidate.AssemblyPath,
                    entry.Nodes.Count,
                    true,
                    AssemblySha256: assemblyHash,
                    PackageFormat: candidate.Manifest is null ? "legacy-v1" : "manifest-v2",
                    PackageManifestSha256: candidate.ManifestSha256,
                    SdkApiVersion: entry.SdkApiVersion,
                    MinimumSdkApiVersion: entry.Compatibility.MinimumApiVersion,
                    MaximumSdkApiVersion: entry.Compatibility.MaximumApiVersion,
                    Vendor: candidate.Manifest?.Vendor,
                    Enabled: true,
                    Warnings: warnings,
                    Tools: entry.Nodes.Select(n => new PluginToolLoadInfo(
                        n.Catalog.Type,
                        n.Catalog.DisplayName,
                        n.ToolIdentity?.Version,
                        n.Lifetime,
                        n.Catalog.Capabilities?.SupportsParallel == false ? VisionExecutorConcurrency.Serialized : n.Concurrency,
                        n.ToolIdentity?.Experimental ?? false)).OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase).ToArray()));
            }

            _loaders.Add(loader);
            loader = null;
            foreach (var info in infos)
                logger.LogInformation("Loaded VisionStudio plugin {PluginId} SDK API {SdkApiVersion} ({NodeCount} nodes) from {AssemblyPath}", info.Id, info.SdkApiVersion, info.NodeCount, info.AssemblyPath);
            return infos;
        }
        catch
        {
            foreach (var source in registeredSources) registry.UnregisterSource(source);
            loader?.Dispose();
            throw;
        }
    }

    private IReadOnlyList<PluginLoadInfo> LoadWorkerPackage(PackageCandidate candidate, string assemblyHash)
    {
        var manifest = candidate.Manifest ?? throw new InvalidOperationException("WorkerProcess isolation requires plugin.json.");
        if (registry.IsSourceRegistered(manifest.Id))
            throw new InvalidOperationException($"Plugin id '{manifest.Id}' is already active. Replacing active plugins requires a host restart.");

        var supervisor = workers ?? throw new InvalidOperationException("WorkerProcess plugin requested but PluginWorkerSupervisor is not configured.");
        var discovery = supervisor.RegisterAndDiscoverAsync(
            manifest.Id,
            candidate.AssemblyPath,
            manifest.WorkerPoolSize,
            manifest.WorkerSharedMemoryThresholdBytes).GetAwaiter().GetResult();
        if (discovery.ProtocolVersion != VisionPluginWorkerProtocol.Version)
            throw new InvalidOperationException($"Plugin worker protocol {discovery.ProtocolVersion} is incompatible with host protocol {VisionPluginWorkerProtocol.Version}.");
        ValidateDescriptor(discovery.Descriptor);
        if (!string.Equals(manifest.Id, discovery.Descriptor.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Name, discovery.Descriptor.Name, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, discovery.Descriptor.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker plugin descriptor does not match plugin.json identity.");
        if (!discovery.Compatibility.Supports(VisionPluginSdk.ApiVersion))
            throw new InvalidOperationException($"Worker plugin '{manifest.Id}' supports SDK API {discovery.Compatibility.MinimumApiVersion}..{discovery.Compatibility.MaximumApiVersion}; host API is {VisionPluginSdk.ApiVersion}.");
        if (manifest.MinimumSdkApiVersion != discovery.Compatibility.MinimumApiVersion || manifest.MaximumSdkApiVersion != discovery.Compatibility.MaximumApiVersion)
            throw new InvalidOperationException("plugin.json SDK API range must match worker plugin compatibility metadata.");
        if (discovery.Tools.Count == 0) throw new InvalidOperationException($"Worker plugin '{manifest.Id}' contains no nodes.");

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in discovery.Tools)
            {
                ValidateNodeSchema(manifest.Id, tool.Catalog, tool.ToolIdentity, requireToolIdentity: true);
                if (!seen.Add(tool.Catalog.Type)) throw new InvalidOperationException($"Package declares duplicate node type '{tool.Catalog.Type}'.");
                if (registry.IsRegistered(tool.Catalog.Type)) throw new InvalidOperationException($"Node type '{tool.Catalog.Type}' is already registered by the host or another plugin.");
            }

            foreach (var tool in discovery.Tools)
            {
                var catalog = tool.Catalog with { PluginId = manifest.Id };
                registry.RegisterFactory(
                    catalog,
                    () => new WorkerPluginNodeExecutor(manifest.Id, catalog.Type, supervisor),
                    manifest.Id,
                    tool.Lifetime,
                    tool.Concurrency,
                    tool.ToolIdentity);
            }
        }
        catch
        {
            registry.UnregisterSource(manifest.Id);
            throw;
        }

        var status = supervisor.Status.FirstOrDefault(x => string.Equals(x.PluginId, manifest.Id, StringComparison.OrdinalIgnoreCase));
        return [new PluginLoadInfo(
            discovery.Descriptor.Id,
            discovery.Descriptor.Name,
            discovery.Descriptor.Version,
            candidate.AssemblyPath,
            discovery.Tools.Count,
            true,
            AssemblySha256: assemblyHash,
            PackageFormat: "manifest-v2-worker",
            PackageManifestSha256: candidate.ManifestSha256,
            SdkApiVersion: discovery.SdkApiVersion,
            MinimumSdkApiVersion: discovery.Compatibility.MinimumApiVersion,
            MaximumSdkApiVersion: discovery.Compatibility.MaximumApiVersion,
            Vendor: manifest.Vendor,
            Enabled: true,
            Warnings: (status?.PoolSize ?? 1) > 1
                ? new[]
                {
                    $"WorkerProcess isolation: plugin crashes/timeouts are isolated from the API host. Worker protocol v{VisionPluginWorkerProtocol.Version} uses a pool of {status?.PoolSize ?? 1} process(es); large supported images use file-backed shared memory with PNG fallback. Unsupported graph payload types fail closed.",
                    "Worker pool members are process replicas. Singleton/PerNode mutable state is not global across the pool; use pool size 1 for plugins that require one process-global state instance."
                }
                : new[] { $"WorkerProcess isolation: plugin crashes/timeouts are isolated from the API host. Worker protocol v{VisionPluginWorkerProtocol.Version} uses one process; large supported images use file-backed shared memory with PNG fallback. Unsupported graph payload types fail closed." },
            Tools: discovery.Tools.Select(n => new PluginToolLoadInfo(n.Catalog.Type, n.Catalog.DisplayName, n.ToolIdentity?.Version, n.Lifetime, n.Concurrency, n.ToolIdentity?.Experimental ?? false)).OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase).ToArray(),
            IsolationMode: VisionPluginIsolationMode.WorkerProcess,
            WorkerProcessId: status?.ProcessId,
            WorkerState: status?.State,
            WorkerPoolSize: status?.PoolSize,
            WorkerImageTransport: status?.ImageTransport,
            WorkerSharedMemoryThresholdBytes: status?.SharedMemoryThresholdBytes,
            WorkerProtocolVersion: VisionPluginWorkerProtocol.Version)];
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var loader in _loaders) loader.Dispose();
            _loaders.Clear();
        }
    }

    private sealed record PackageCandidate(string Directory, string AssemblyPath, PluginPackageManifest? Manifest, string? ManifestSha256);
    private sealed record PreparedPlugin(IVisionPlugin Plugin, VisionPluginCompatibility Compatibility, int SdkApiVersion, IReadOnlyList<VisionPluginNode> Nodes);
}
