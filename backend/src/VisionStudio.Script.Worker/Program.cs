using System.Diagnostics;
using System.IO.Pipes;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Nodes;

// Phase 1 script worker: hosts script.csharp behind the same named-pipe protocol as the plugin worker.
// The host still owns scheduling/timeouts; this process owns Roslyn execution and can be killed
// without taking down the API host.

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

var scriptCatalog = BuiltInNodeCatalog.Items.Single(item =>
    string.Equals(item.Type, "script.csharp", StringComparison.OrdinalIgnoreCase));
using var registry = new VisionNodeRegistry();
registry.Register(scriptCatalog, new CSharpScriptNode(), "builtin");

var supportedTransports = sharedMemory is null
    ? new[] { PluginWorkerImageTransport.Png }
    : new[] { PluginWorkerImageTransport.Png, PluginWorkerImageTransport.FileBackedSharedMemory };

var discovery = new PluginWorkerDiscovery(
    VisionPluginWorkerProtocol.Version,
    new VisionPluginDescriptor(
        "builtin.script",
        "C# Script Runtime",
        typeof(CSharpScriptNode).Assembly.GetName().Version?.ToString() ?? "0.63.0",
        "Trusted-code C# script host running in an isolated worker process."),
    VisionPluginCompatibility.Current,
    VisionPluginSdk.ApiVersion,
    [new PluginWorkerToolDescriptor(
        scriptCatalog,
        VisionExecutorLifetime.Singleton,
        VisionExecutorConcurrency.Serialized,
        null)],
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
            _ => Failure(request, $"Unknown script worker operation '{request.Operation}'.")
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
    if (!string.Equals(request.NodeType, "script.csharp", StringComparison.OrdinalIgnoreCase) || request.Node is null)
        return Failure(request, "Script worker only accepts nodeType 'script.csharp' with a node definition.");

    var registration = registry.Require(scriptCatalog.Type);
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
        var result = await registration.ExecuteAsync(
            new NodeExecutionContext(decodedInputs, request.WorkflowScope, request.RunId),
            request.Node,
            CancellationToken.None);
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
    string? SharedMemoryRoot,
    int SharedMemoryThresholdBytes)
{
    public static WorkerArguments Parse(string[] args)
    {
        string? pipe = null, sharedMemoryRoot = null;
        var threshold = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
                case "--shared-memory-root" when i + 1 < args.Length: sharedMemoryRoot = Path.GetFullPath(args[++i]); break;
                case "--shared-memory-threshold-bytes" when i + 1 < args.Length && int.TryParse(args[++i], out var parsed): threshold = Math.Max(0, parsed); break;
                case "--worker-kind" when i + 1 < args.Length:
                    if (!string.Equals(args[++i], "script", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("VisionStudio.Script.Worker only supports --worker-kind script.");
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(pipe))
            throw new ArgumentException("Usage: VisionStudio.Script.Worker --pipe <name> [--shared-memory-root <dir>] [--shared-memory-threshold-bytes <n>]");
        return new WorkerArguments(pipe, sharedMemoryRoot, threshold);
    }
}
