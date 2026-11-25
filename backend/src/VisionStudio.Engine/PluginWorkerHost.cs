using System.Diagnostics;
using System.IO.Pipes;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>
/// Shared named-pipe host loop for isolated worker processes (Plugin.Worker &amp; Script.Worker).
/// Owns pipe connection, shared-memory transport setup, discovery/ping/shutdown/execute dispatch,
/// value codec encode/decode, IDisposable cleanup, and per-request timeout cancellation.
/// </summary>
public static class PluginWorkerHost
{
    public sealed record CommonArguments(
        string PipeName,
        string? SharedMemoryRoot,
        int SharedMemoryThresholdBytes);

    public static async Task RunAsync(
        string pipeName,
        string? sharedMemoryRoot,
        int sharedMemoryThresholdBytes,
        VisionNodeRegistry registry,
        Func<IReadOnlyList<PluginWorkerImageTransport>, PluginWorkerDiscovery> buildDiscovery,
        Func<PluginWorkerRequest, string?>? validateExecuteRequest = null,
        CancellationToken cancellationToken = default)
    {
        PluginWorkerSharedMemoryStore? sharedMemory = null;
        if (!string.IsNullOrWhiteSpace(sharedMemoryRoot) && sharedMemoryThresholdBytes > 0)
        {
            sharedMemory = new PluginWorkerSharedMemoryStore(sharedMemoryRoot, sharedMemoryThresholdBytes);
            sharedMemory.CleanupStaleFiles(TimeSpan.FromMinutes(30));
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

        var supportedTransports = sharedMemory is null
            ? new[] { PluginWorkerImageTransport.Png }
            : new[] { PluginWorkerImageTransport.Png, PluginWorkerImageTransport.FileBackedSharedMemory };
        var discovery = buildDiscovery(supportedTransports);

        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            PluginWorkerRequest request;
            try { request = await PluginWorkerWire.ReadAsync<PluginWorkerRequest>(pipe, cancellationToken).ConfigureAwait(false); }
            catch (EndOfStreamException) { break; }

            PluginWorkerResponse response;
            try
            {
                response = request.Operation.ToLowerInvariant() switch
                {
                    "discover" => Success(request, discovery: discovery),
                    "ping" => Success(request),
                    "shutdown" => Success(request),
                    "execute" => await ExecuteAsync(request, registry, sharedMemory, validateExecuteRequest, cancellationToken).ConfigureAwait(false),
                    _ => Failure(request, $"Unknown worker operation '{request.Operation}'.")
                };
            }
            catch (Exception ex)
            {
                response = Failure(request, ex.ToString());
            }

            await PluginWorkerWire.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, "shutdown", StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static async ValueTask<PluginWorkerResponse> ExecuteAsync(
        PluginWorkerRequest request,
        VisionNodeRegistry registry,
        PluginWorkerSharedMemoryStore? sharedMemory,
        Func<PluginWorkerRequest, string?>? validateExecuteRequest,
        CancellationToken cancellationToken)
    {
        if (validateExecuteRequest?.Invoke(request) is { } validationError)
            return Failure(request, validationError);
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

            using var requestCts = request.TimeoutMs is > 0 and var timeoutMs
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (requestCts is not null && request.TimeoutMs is > 0 and var ms)
                requestCts.CancelAfter(ms);
            var effectiveToken = requestCts?.Token ?? cancellationToken;

            var executeTimer = Stopwatch.StartNew();
            var result = await registration.ExecuteAsync(
                new NodeExecutionContext(decodedInputs, request.WorkflowScope, request.RunId),
                request.Node,
                effectiveToken).ConfigureAwait(false);
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
                    else if (value.Value is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (var disposable in disposables) disposable.Dispose();
        }
    }

    private static PluginWorkerResponse Success(
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

    private static PluginWorkerResponse Failure(PluginWorkerRequest request, string error) =>
        new()
        {
            RequestId = request.RequestId,
            Success = false,
            Error = error,
            WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64
        };
}
