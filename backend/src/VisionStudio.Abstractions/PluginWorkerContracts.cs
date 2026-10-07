using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Abstractions;

/// <summary>
/// Where a plugin package executes. InProcess preserves the SDK 2.0 behavior; WorkerProcess executes the
/// plugin inside VisionStudio.Plugin.Worker and communicates with the host over a local named pipe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisionPluginIsolationMode
{
    InProcess = 0,
    WorkerProcess = 1
}

/// <summary>Image transport negotiated by the V0.57 worker protocol.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginWorkerImageTransport
{
    Png = 0,
    FileBackedSharedMemory = 1
}

public static class VisionPluginWorkerProtocol
{
    public const int Version = 3;
}

/// <summary>
/// Descriptor for one raw OpenCV image stored in a file-backed memory mapped file. FileName is deliberately
/// relative; both processes resolve it against the host-provided shared-memory root and reject path traversal.
/// </summary>
public sealed record PluginWorkerSharedImage(
    string FileName,
    int Rows,
    int Cols,
    string PixelFormat,
    long PayloadBytes,
    PluginWorkerImageTransport Transport = PluginWorkerImageTransport.FileBackedSharedMemory);

public sealed record PluginWorkerValue(
    VisionDataType Type,
    JsonElement? Json = null,
    byte[]? ImagePng = null,
    PluginWorkerSharedImage? SharedImage = null);

public sealed record PluginWorkerToolDescriptor(
    NodeCatalogItem Catalog,
    VisionExecutorLifetime Lifetime,
    VisionExecutorConcurrency Concurrency,
    VisionPluginToolIdentity? ToolIdentity);

public sealed record PluginWorkerDiscovery(
    int ProtocolVersion,
    VisionPluginDescriptor Descriptor,
    VisionPluginCompatibility Compatibility,
    int SdkApiVersion,
    IReadOnlyList<PluginWorkerToolDescriptor> Tools,
    IReadOnlyList<PluginWorkerImageTransport>? SupportedImageTransports = null);


public sealed record PluginWorkerTiming(
    double InputDecodeMs,
    double PluginExecuteMs,
    double OutputEncodeMs);

public sealed record PluginWorkerExecutionResult(
    IReadOnlyDictionary<string, PluginWorkerValue> Outputs,
    IReadOnlyDictionary<string, JsonElement> Summary,
    IReadOnlyList<VisionOverlay>? Overlays);

public sealed record PluginWorkerRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Operation { get; init; } = "ping";
    public string? NodeType { get; init; }
    public NodeDefinition? Node { get; init; }
    public IReadOnlyDictionary<string, PluginWorkerValue>? Inputs { get; init; }

    /// <summary>Workflow plan identity of the execution; worker-side PerNode caches key by it like the host does.</summary>
    public string? WorkflowScope { get; init; }
}

public sealed record PluginWorkerResponse
{
    public string RequestId { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public PluginWorkerDiscovery? Discovery { get; init; }
    public PluginWorkerExecutionResult? Result { get; init; }
    public PluginWorkerTiming? Timing { get; init; }
    public long WorkingSetBytes { get; init; }
}
