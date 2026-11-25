using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Nodes;

// Phase 1 script worker: hosts script.csharp behind the shared PluginWorkerHost named-pipe runtime.
var options = WorkerArguments.Parse(args);
var scriptCatalog = BuiltInNodeCatalog.Items.Single(item =>
    string.Equals(item.Type, "script.csharp", StringComparison.OrdinalIgnoreCase));
using var registry = new VisionNodeRegistry();
registry.Register(scriptCatalog, new CSharpScriptNode(), "builtin");

await PluginWorkerHost.RunAsync(
    options.PipeName,
    options.SharedMemoryRoot,
    options.SharedMemoryThresholdBytes,
    registry,
    supportedTransports => new PluginWorkerDiscovery(
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
        supportedTransports),
    validateExecuteRequest: request =>
        !string.Equals(request.NodeType, "script.csharp", StringComparison.OrdinalIgnoreCase) || request.Node is null
            ? "Script worker only accepts nodeType 'script.csharp' with a node definition."
            : null);

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
