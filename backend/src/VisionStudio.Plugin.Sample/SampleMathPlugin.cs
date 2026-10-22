using VisionStudio.Abstractions;

namespace VisionStudio.Plugin.Sample;

public sealed class SampleMathPlugin : IVisionPluginV2
{
    public VisionPluginDescriptor Descriptor => new(
        "sample.math",
        "Sample Math Plugin",
        "0.59.0",
        "Demonstrates the V0.59 SDK 2.0 benchmark-ready WorkerProcess mode, pooled execution, shared-memory image transport, signed-package lifecycle and PerNode lifetime.");

    public VisionPluginCompatibility Compatibility => VisionPluginCompatibility.Current;

    public IEnumerable<VisionPluginNode> CreateNodes()
    {
        yield return VisionPluginNode.PerNode<AddOffsetTool>(
                AddOffsetTool.ToolDescriptor,
                VisionExecutorConcurrency.ThreadSafe)
            .WithToolIdentity(
                version: "2.0.0",
                vendor: "VisionStudio SDK Sample",
                documentationUrl: "PLUGIN_SDK_2.md");
    }
}

public sealed class AddOffsetTool : VisionToolBase
{
    public static NodeCatalogItem ToolDescriptor { get; } = new(
        "math.offset",
        "Add Offset",
        "Plugin / Math",
        [new("exec", VisionDataType.Control), new("value", VisionDataType.Double)],
        [new("value", VisionDataType.Double), new("next", VisionDataType.Control)],
        [new("offset", "Offset", "number", 10d, -100000, 100000, 0.1, Unit: "unit", Group: "Math")],
        "Add a configurable offset to a Double value.",
        Capabilities: new VisionToolCapabilities(EmitsOverlay: false));

    public override NodeCatalogItem Descriptor => ToolDescriptor;

    public override ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        var value = context.RequireNumber("value");
        var offset = node.GetDouble("offset", 10);
        var result = value + offset;
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(result) },
            new Dictionary<string, object?> { ["input"] = value, ["offset"] = offset, ["result"] = result }));
    }
}
