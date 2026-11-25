using System.Collections.Immutable;
using System.Text.Json;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class BoundaryContractTests
{
    [Fact]
    public void VisionValue_RejectsMismatchedTagAndPayload()
    {
        Assert.Throws<ArgumentException>(() => new VisionValue(VisionDataType.Image, "not-an-image"));
        Assert.Throws<ArgumentException>(() => new VisionValue(VisionDataType.Integer, 3.14));
        Assert.Throws<ArgumentException>(() => new VisionValue(VisionDataType.String, 42));

        var nullableInt = new VisionValue(VisionDataType.Integer, null);
        Assert.Null(nullableInt.Value);

        var anyValue = new VisionValue(VisionDataType.Any, "flexible");
        Assert.Equal("flexible", anyValue.Require<string>("anyPort"));
    }

    [Fact]
    public void VisionTransform3D_IsImmutableAndUsesValueEquality()
    {
        ImmutableArray<double> matrix =
        [
            1, 0, 0, 10,
            0, 1, 0, 20,
            0, 0, 1, 30,
            0, 0, 0, 1
        ];
        var first = new VisionTransform3D("Camera", "RobotBase", "mm", matrix);
        var second = new VisionTransform3D("Camera", "RobotBase", "mm",
        [
            1, 0, 0, 10,
            0, 1, 0, 20,
            0, 0, 1, 30,
            0, 0, 0, 1
        ]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Throws<ArgumentNullException>(() => new VisionTransform3D("Camera", "RobotBase", "mm", default));
    }

    [Fact]
    public void JsonParameters_GetInt_RejectsFractionsAndAcceptsExactIntegers()
    {
        var node = new NodeDefinition(
            "n1",
            "test.node",
            null,
            null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"whole":2.0,"wholeText":"5.0","fraction":2.7,"fractionText":"3.25"}"""));

        Assert.Equal(2, node.GetInt("whole", 0));
        Assert.Equal(5, node.GetInt("wholeText", 0));
        Assert.Throws<InvalidOperationException>(() => node.GetInt("fraction", 0));
        Assert.Throws<InvalidOperationException>(() => node.GetInt("fractionText", 0));
    }

    [Fact]
    public void CustomControlPorts_AreRecognizedFromCatalogWithoutHardcodedNames()
    {
        var pluginCatalog = new NodeCatalogItem(
            "plugin.customRouter",
            "Custom Router",
            "Control",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("dispatched", VisionDataType.Control)],
            []);
        using var registry = new VisionNodeRegistry();
        var sinkCatalog = new NodeCatalogItem(
            "plugin.customSink",
            "Custom Sink",
            "Control",
            [new PortDescriptor("enter", VisionDataType.Control)],
            [new PortDescriptor("done", VisionDataType.Control)],
            []);
        registry.Register(pluginCatalog, new NoopControlNode(pluginCatalog.Type), "custom.plugin");
        registry.Register(sinkCatalog, new NoopControlNode(sinkCatalog.Type), "custom.plugin");

        var workflow = new WorkflowDefinition(
            "custom-control",
            "Custom Control",
            [
                new NodeDefinition("n1", pluginCatalog.Type, null, null, null),
                new NodeDefinition("n2", sinkCatalog.Type, null, null, null)
            ],
            [new EdgeDefinition("e1", "n1", "dispatched", "n2", "enter")]);

        var compiled = new VisionWorkflowCompiler(registry).Compile(workflow);
        Assert.Equal(["n1", "n2"], compiled.OrderedNodeIds);
    }

    [Fact]
    public void RoiParameterNames_ReadsCatalogRoiParametersForPluginNodes()
    {
        var pluginCatalog = new NodeCatalogItem(
            "plugin.inspect",
            "Plugin Inspect",
            "Vision",
            [],
            [],
            [
                new ParameterDescriptor("inspectRoi", "Inspect ROI", "roi", Internal: true),
                new ParameterDescriptor("threshold", "Threshold", "number", 128)
            ]);

        Assert.Equal(["inspectRoi"], BuiltInNodeCatalog.RoiParameterNames(pluginCatalog));
    }

    [Fact]
    public void CameraPtpMapping_NormalizesStatesAndTicksConsistently()
    {
        Assert.Equal(CameraPtpClockState.Disabled, CameraPtpMapping.MapState("Locked", enabled: false));
        Assert.Equal(CameraPtpClockState.Locked, CameraPtpMapping.MapState("LOCKED", enabled: true));
        Assert.Equal(CameraPtpClockState.Slave, CameraPtpMapping.MapState("8", enabled: true, includeGenICamNumericCodes: true));
        Assert.Equal(CameraPtpClockState.Master, CameraPtpMapping.MapState("5", enabled: true, includeGenICamNumericCodes: true));
        Assert.Equal(CameraPtpClockState.Initializing, CameraPtpMapping.MapState("Pre_Master", enabled: true));
        Assert.Equal(500_000_000L, CameraPtpMapping.TicksToNanoseconds(500_000, 1_000_000));
        Assert.Null(CameraPtpMapping.TicksToNanoseconds(500_000, null));
        Assert.Equal(500_000L, CameraPtpMapping.TicksToNanoseconds(500_000, null, fallbackRawWhenEnabled: true, enabled: true));
    }

    [Fact]
    public void NodeTypeMigration_ReplacementMapMatchesAliasExecutors()
    {
        var aliasTypes = typeof(NodeTypeMigration).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IVisionNodeExecutor).IsAssignableFrom(t)
                && t.Namespace == "VisionStudio.Engine.Nodes"
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => ((IVisionNodeExecutor)Activator.CreateInstance(t)!).Type)
            .Where(type => NodeTypeMigration.ReplacementMap.ContainsKey(type))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var legacyType in NodeTypeMigration.ReplacementMap.Keys)
        {
            Assert.Contains(legacyType, aliasTypes);
        }
    }

    private sealed class NoopControlNode(string type) : IVisionNodeExecutor
    {
        public string Type => type;
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
