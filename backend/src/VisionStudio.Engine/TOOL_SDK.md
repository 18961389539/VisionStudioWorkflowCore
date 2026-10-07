# Vision Tool SDK V0.22

V0.22 makes the plugin boundary explicit. Third-party plugins reference only `VisionStudio.Abstractions`; they no longer reference `VisionStudio.Engine`, Workflow Core, OpenCV integration code, PLC/robot runtimes or ASP.NET host types.

## Project reference

```xml
<ProjectReference Include="..\\VisionStudio.Abstractions\\VisionStudio.Abstractions.csproj" />
```

A packaged SDK can replace this project reference with a NuGet package later without changing the contract shape.

## Minimal tool

```csharp
using VisionStudio.Abstractions;

public sealed class MyTool : VisionToolBase
{
    public static NodeCatalogItem ToolDescriptor { get; } = new(
        "vendor.myTool",
        "My Tool",
        "Vendor",
        [new("exec", VisionDataType.Control), new("value", VisionDataType.Double)],
        [new("result", VisionDataType.Double), new("next", VisionDataType.Control)],
        [new("gain", "Gain", "number", 1.0, 0, 100, 0.1)],
        "Example plugin tool",
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.Pipeline));

    public override NodeCatalogItem Descriptor => ToolDescriptor;

    public override ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        var value = context.RequireNumber("value");
        var gain = node.GetDouble("gain", 1.0);
        var result = value * gain;
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["result"] = VisionValue.Double(result) },
            new Dictionary<string, object?> { ["result"] = result }));
    }
}
```

Expose it through a host-managed factory:

```csharp
public IEnumerable<VisionPluginNode> CreateNodes()
{
    yield return VisionPluginNode.Transient<MyTool>(
        MyTool.ToolDescriptor,
        VisionExecutorConcurrency.ThreadSafe);
}
```

## Lifetime model

- `Singleton`: one executor instance is created when the plugin node is registered and reused.
- `Transient`: the host factory creates an executor per execution and disposes it immediately afterward when it implements `IDisposable`/`IAsyncDisposable`.

Use `Singleton` for expensive, thread-safe model/session objects. Use `Transient` for cheap stateful tools or wrappers where instance isolation is preferable.

## Concurrency model

- `ThreadSafe`: concurrent `ExecuteAsync` calls are allowed.
- `Serialized`: the host uses a per-node-type semaphore and guarantees one-at-a-time execution.

`SupportsParallel=false` is authoritative. The registry forces `Serialized` even if the plugin requests `ThreadSafe`, so the compiler and runtime both defend non-parallel tools.

## Image boundary

The SDK exposes `IVisionImage` rather than `VisionStudio.Engine.Camera.VisionImage`. A plugin can carry its native object through `IVisionImage.NativeImage`. The built-in OpenCV runtime accepts any `IVisionImage` whose `NativeImage` is an `OpenCvSharp.Mat`; a plugin therefore does not need an Engine reference to interoperate with built-in image nodes.

## SDK rules

1. Ports use `VisionDataType`; prefer typed geometry over loose number bundles.
2. ROI and Overlay types are serializable Abstractions contracts.
3. Parameters declare units/groups so the Web UI can generate a consistent property editor.
4. Executors return only outputs declared by their descriptor; runtime validation remains active.
5. Plugins should default external-I/O/wait nodes to Workflow Core execution; Pipeline is for short compute nodes.
6. Choose lifetime and concurrency deliberately; vendor SDK wrappers should default to `Serialized` unless re-entrancy is proven.
7. Plugin factories must return an executor whose `Type` exactly matches the catalog type.
