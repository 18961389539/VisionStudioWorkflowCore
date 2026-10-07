using System.Diagnostics;
using System.IO.Pipes;
using McMaster.NETCore.Plugins;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

var options = WorkerArguments.Parse(args);
PluginWorkerSharedMemoryStore? sharedMemory = null;
if (!string.IsNullOrWhiteSpace(options.SharedMemoryRoot) && options.SharedMemoryThresholdBytes > 0)
{
    sharedMemory = new PluginWorkerSharedMemoryStore(options.SharedMemoryRoot, options.SharedMemoryThresholdBytes);
    sharedMemory.CleanupStaleFiles(TimeSpan.FromMinutes(30));
}

using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
await pipe.ConnectAsync(connectCts.Token);

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

var supportedTransports = sharedMemory is null
    ? new[] { PluginWorkerImageTransport.Png }
    : new[] { PluginWorkerImageTransport.Png, PluginWorkerImageTransport.FileBackedSharedMemory };

var discovery = new PluginWorkerDiscovery(
    VisionPluginWorkerProtocol.Version,
    plugin.Descriptor,
    compatibility,
    sdkApiVersion,
    nodes.Select(node => new PluginWorkerToolDescriptor(
        node.Catalog with { PluginId = plugin.Descriptor.Id },
        node.Lifetime,
        node.Catalog.Capabilities?.SupportsParallel == false ? VisionExecutorConcurrency.Serialized : node.Concurrency,
        node.ToolIdentity)).ToArray(),
    supportedTransports);

while (pipe.IsConnected)
{
    PluginWorkerRequest request;
    try { request = await PluginWorkerWire.ReadAsync<PluginWorkerRequest>(pipe, CancellationToken.None); }
    catch (EndOfStreamException) { break; }

    PluginWorkerResponse response;
    try
    {
        response = request.Operation.ToLowerInvariant() switch
        {
            "discover" => Success(request, discovery: discovery),
            "ping" => Success(request),
            "shutdown" => Success(request),
            "execute" => await ExecuteAsync(request),
            _ => Failure(request, $"Unknown worker operation '{request.Operation}'.")
        };
    }
    catch (Exception ex)
    {
        response = Failure(request, ex.ToString());
    }

    await PluginWorkerWire.WriteAsync(pipe, response, CancellationToken.None);
    if (string.Equals(request.Operation, "shutdown", StringComparison.OrdinalIgnoreCase)) break;
}

async ValueTask<PluginWorkerResponse> ExecuteAsync(PluginWorkerRequest request)
{
    if (string.IsNullOrWhiteSpace(request.NodeType) || request.Node is null)
        return Failure(request, "Execute requires nodeType and node.");
    var registration = registry.Require(request.NodeType);
    var decodedInputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase);
    var disposables = new List<IDisposable>();
    var decodeTimer = Stopwatch.StartNew();
    try
    {
        foreach (var pair in request.Inputs ?? new Dictionary<string, PluginWorkerValue>())
        {
            var value = PluginWorkerValueCodec.Decode(pair.Value, sharedMemory);
            decodedInputs[pair.Key] = value;
            if (value.Value is IDisposable disposable) disposables.Add(disposable);
        }
        decodeTimer.Stop();

        var executeTimer = Stopwatch.StartNew();
        var result = await registration.ExecuteAsync(new NodeExecutionContext(decodedInputs, request.WorkflowScope), request.Node, CancellationToken.None);
        executeTimer.Stop();
        try
        {
            var encodeTimer = Stopwatch.StartNew();
            var outputs = result.Outputs.ToDictionary(
                pair => pair.Key,
                pair => PluginWorkerValueCodec.Encode(pair.Value, sharedMemory),
                StringComparer.OrdinalIgnoreCase);
            var summary = PluginWorkerValueCodec.EncodeSummary(result.Summary);
            encodeTimer.Stop();
            return Success(
                request,
                result: new PluginWorkerExecutionResult(outputs, summary, result.Overlays),
                timing: new PluginWorkerTiming(decodeTimer.Elapsed.TotalMilliseconds, executeTimer.Elapsed.TotalMilliseconds, encodeTimer.Elapsed.TotalMilliseconds));
        }
        finally
        {
            foreach (var value in result.Outputs.Values)
            {
                if (value.Value is IDisposable disposable) disposable.Dispose();
                else if (value.Value is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            }
        }
    }
    finally
    {
        foreach (var disposable in disposables) disposable.Dispose();
    }
}

static PluginWorkerResponse Success(
    PluginWorkerRequest request,
    PluginWorkerDiscovery? discovery = null,
    PluginWorkerExecutionResult? result = null,
    PluginWorkerTiming? timing = null) =>
    new()
    {
        RequestId = request.RequestId,
        Success = true,
        Discovery = discovery,
        Result = result,
        Timing = timing,
        WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64
    };

static PluginWorkerResponse Failure(PluginWorkerRequest request, string error) =>
    new()
    {
        RequestId = request.RequestId,
        Success = false,
        Error = error,
        WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64
    };

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
