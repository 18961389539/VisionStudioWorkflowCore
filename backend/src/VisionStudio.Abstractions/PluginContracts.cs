namespace VisionStudio.Abstractions;

/// <summary>Stable SDK constants consumed by package manifests and host compatibility checks.</summary>
public static class VisionPluginSdk
{
    public const int ApiVersion = 2;
    public const int PackageManifestSchemaVersion = 2;
    public const string PackageManifestFileName = "plugin.json";
}

public sealed record VisionPluginDescriptor(string Id, string Name, string Version, string? Description = null);

/// <summary>
/// API range implemented by one plugin. A plugin is compatible when the host SDK API version falls inside
/// this inclusive range. V0.22 IVisionPlugin binaries are treated as API v1 compatibility plugins.
/// </summary>
public sealed record VisionPluginCompatibility(int MinimumApiVersion, int MaximumApiVersion)
{
    public bool Supports(int apiVersion) => apiVersion >= MinimumApiVersion && apiVersion <= MaximumApiVersion;
    public static VisionPluginCompatibility LegacyV1 { get; } = new(1, 1);
    public static VisionPluginCompatibility Current { get; } = new(VisionPluginSdk.ApiVersion, VisionPluginSdk.ApiVersion);
}

public sealed record VisionPluginToolIdentity(
    string Version,
    string? Vendor = null,
    string? DocumentationUrl = null,
    bool Experimental = false);

public enum VisionExecutorLifetime
{
    Singleton = 0,
    Transient = 1,
    /// <summary>
    /// One executor instance is retained per (workflow plan identity, node id): the identity combines the workflow
    /// id, its content fingerprint and the catalog revision, so two workflows that happen to reuse a node id never
    /// share mutable state. This is the preferred lifetime for stateful tools that cache a template/model per
    /// configured node. Instances live while the workflow's compiled plan stays in the host plan cache and are
    /// disposed when that plan entry is evicted or the plugin registration is released.
    /// </summary>
    PerNode = 2
}

/// <summary>
/// ThreadSafe allows concurrent ExecuteAsync calls. Serialized forces one-at-a-time execution for the
/// relevant executor instance, which is the safe default for native/vendor SDK wrappers that are not re-entrant.
/// </summary>
public enum VisionExecutorConcurrency
{
    ThreadSafe,
    Serialized
}

public interface IVisionTool : IVisionNodeExecutor
{
    NodeCatalogItem Descriptor { get; }
}

public abstract class VisionToolBase : IVisionTool
{
    public abstract NodeCatalogItem Descriptor { get; }
    public string Type => Descriptor.Type;
    public abstract ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken);
}

/// <summary>
/// Plugin node factory metadata. The primary constructor is intentionally unchanged from the V0.22 SDK so
/// existing source/binary plugins keep the same construction contract; SDK 2.0 metadata is additive.
/// </summary>
public sealed record VisionPluginNode(
    NodeCatalogItem Catalog,
    Func<IVisionNodeExecutor> Factory,
    VisionExecutorLifetime Lifetime = VisionExecutorLifetime.Singleton,
    VisionExecutorConcurrency Concurrency = VisionExecutorConcurrency.Serialized)
{
    public VisionPluginToolIdentity? ToolIdentity { get; init; }

    public static VisionPluginNode Singleton<TTool>(
        NodeCatalogItem catalog,
        VisionExecutorConcurrency concurrency = VisionExecutorConcurrency.Serialized)
        where TTool : IVisionNodeExecutor, new() =>
        new(catalog, static () => new TTool(), VisionExecutorLifetime.Singleton, concurrency);

    public static VisionPluginNode Transient<TTool>(
        NodeCatalogItem catalog,
        VisionExecutorConcurrency concurrency = VisionExecutorConcurrency.Serialized)
        where TTool : IVisionNodeExecutor, new() =>
        new(catalog, static () => new TTool(), VisionExecutorLifetime.Transient, concurrency);

    public static VisionPluginNode PerNode<TTool>(
        NodeCatalogItem catalog,
        VisionExecutorConcurrency concurrency = VisionExecutorConcurrency.Serialized)
        where TTool : IVisionNodeExecutor, new() =>
        new(catalog, static () => new TTool(), VisionExecutorLifetime.PerNode, concurrency);

    public static VisionPluginNode FromFactory(
        NodeCatalogItem catalog,
        Func<IVisionNodeExecutor> factory,
        VisionExecutorLifetime lifetime = VisionExecutorLifetime.Singleton,
        VisionExecutorConcurrency concurrency = VisionExecutorConcurrency.Serialized) =>
        new(catalog, factory, lifetime, concurrency);

    public VisionPluginNode WithToolIdentity(
        string version,
        string? vendor = null,
        string? documentationUrl = null,
        bool experimental = false) =>
        this with { ToolIdentity = new VisionPluginToolIdentity(version, vendor, documentationUrl, experimental) };
}

/// <summary>Minimal V0.22-compatible plugin contract.</summary>
public interface IVisionPlugin
{
    VisionPluginDescriptor Descriptor { get; }
    IEnumerable<VisionPluginNode> CreateNodes();
}

/// <summary>
/// SDK 2.0 capability contract. It extends IVisionPlugin without changing the original interface ABI.
/// The host checks Compatibility before registering any node factories.
/// </summary>
public interface IVisionPluginV2 : IVisionPlugin
{
    VisionPluginCompatibility Compatibility { get; }
}
