using McMaster.NETCore.Plugins;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

var options = WorkerArguments.Parse(args);
using var registry = new VisionNodeRegistry();
using var loader = PluginLoader.CreateFromAssemblyFile(
    options.AssemblyPath,
    isUnloadable: false,
    sharedTypes: [typeof(IVisionPlugin), typeof(IVisionPluginV2), typeof(IVisionNodeExecutor), typeof(NodeCatalogItem), typeof(VisionPluginNode)]);

var assembly = loader.LoadDefaultAssembly();
var pluginTypes = assembly.GetTypes().Where(t => typeof(IVisionPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToArray();
if (pluginTypes.Length != 1) throw new InvalidOperationException($"Worker packages require exactly one IVisionPlugin implementation; found {pluginTypes.Length}.");
var plugin = (IVisionPlugin?)Activator.CreateInstance(pluginTypes[0]) ?? throw new InvalidOperationException("Could not instantiate plugin. A parameterless constructor is required.");
if (!string.Equals(plugin.Descriptor.Id, options.PluginId, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Worker plugin id '{plugin.Descriptor.Id}' does not match requested package id '{options.PluginId}'.");
var compatibility = plugin is IVisionPluginV2 v2 ? v2.Compatibility : VisionPluginCompatibility.LegacyV1;
var sdkApiVersion = plugin is IVisionPluginV2 ? VisionPluginSdk.ApiVersion : 1;
var nodes = plugin.CreateNodes().ToArray();
if (nodes.Length == 0) throw new InvalidOperationException("Worker plugin contains no nodes.");
foreach (var node in nodes)
{
    var catalog = node.Catalog with { PluginId = plugin.Descriptor.Id };
    registry.Register(catalog, node, plugin.Descriptor.Id);
}

await PluginWorkerHost.RunAsync(
    options.PipeName,
    options.SharedMemoryRoot,
    options.SharedMemoryThresholdBytes,
    registry,
    supportedTransports => new PluginWorkerDiscovery(
        VisionPluginWorkerProtocol.Version,
        plugin.Descriptor,
        compatibility,
        sdkApiVersion,
        nodes.Select(node => new PluginWorkerToolDescriptor(
            node.Catalog with { PluginId = plugin.Descriptor.Id },
            node.Lifetime,
            node.Catalog.Capabilities?.SupportsParallel == false ? VisionExecutorConcurrency.Serialized : node.Concurrency,
            node.ToolIdentity)).ToArray(),
        supportedTransports));

sealed record WorkerArguments(
    string PipeName,
    string AssemblyPath,
    string PluginId,
    string? SharedMemoryRoot,
    int SharedMemoryThresholdBytes)
{
    public static WorkerArguments Parse(string[] args)
    {
        string? pipe = null, assembly = null, id = null, sharedMemoryRoot = null;
        var threshold = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
                case "--assembly" when i + 1 < args.Length: assembly = args[++i]; break;
                case "--plugin-id" when i + 1 < args.Length: id = args[++i]; break;
                case "--shared-memory-root" when i + 1 < args.Length: sharedMemoryRoot = Path.GetFullPath(args[++i]); break;
                case "--shared-memory-threshold-bytes" when i + 1 < args.Length && int.TryParse(args[++i], out var parsed): threshold = Math.Max(0, parsed); break;
            }
        }
        if (string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Usage: VisionStudio.Plugin.Worker --pipe <name> --assembly <plugin.dll> --plugin-id <id> [--shared-memory-root <path>] [--shared-memory-threshold-bytes <n>]");
        return new WorkerArguments(pipe, Path.GetFullPath(assembly), id, sharedMemoryRoot, threshold);
    }
}
