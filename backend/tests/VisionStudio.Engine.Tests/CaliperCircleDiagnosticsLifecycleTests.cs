using System.Text.Json;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class CaliperCircleDiagnosticsLifecycleTests
{
    [Fact]
    public async Task ReexecutingTheNodeReplacesItsCaptureAndAdvancesExecutionIdentity()
    {
        using var scenario = new Scenario();
        await scenario.Execute();
        var previous = Assert.Single(scenario.Data.SnapshotCaliperDiagnostics());
        await scenario.Runtime.ExecuteAsync(scenario.Data, "circle", CancellationToken.None);
        var current = Assert.Single(scenario.Data.SnapshotCaliperDiagnostics());
        Assert.True(current.ExecutionSequence > previous.ExecutionSequence);
        Assert.NotSame(current.Data, previous.Data);
        Assert.Equal(scenario.Data.SnapshotReports().Last().ExecutionSequence, current.ExecutionSequence);
        Assert.Equal(72, current.Data.Spokes.Count);
    }

    [Fact]
    public async Task FailedReexecutionRemovesThePreviouslySuccessfulCapture()
    {
        using var scenario = new Scenario();
        await scenario.Execute();
        Assert.Single(scenario.Data.SnapshotCaliperDiagnostics());
        var node = scenario.Data.Definition.Nodes.Single(x => x.Id == "circle");
        scenario.Data.Definition = scenario.Data.Definition with { Nodes = scenario.Data.Definition.Nodes
            .Select(x => x.Id == "circle" ? node with { Parameters = new(node.Parameters!) { ["radius"] = JsonSerializer.SerializeToElement(0) } } : x).ToArray() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Runtime.ExecuteAsync(scenario.Data, "circle", CancellationToken.None).AsTask());
        Assert.Empty(scenario.Data.SnapshotCaliperDiagnostics());
        Assert.False(scenario.Data.SnapshotReports().Last().Success);
        Assert.DoesNotContain("caliperDiagnosticsAvailable", scenario.Data.SnapshotReports().Last().Summary.Keys);
    }

    [Fact]
    public async Task ProductionExecutionDoesNotRetainAnyDiagnosticSamples()
    {
        using var scenario = new Scenario(capture: false);
        await scenario.Execute();
        Assert.Empty(scenario.Data.SnapshotCaliperDiagnostics());
        Assert.DoesNotContain("caliperDiagnosticsAvailable", scenario.Data.SnapshotReports().Last().Summary.Keys);
    }

    [Fact]
    public async Task ConcurrentDispatchesCannotPublishProfilesIntoAnotherRunsCaptureScope()
    {
        var radii = new[] { 40, 60, 80, 100 };
        var results = await Task.WhenAll(radii.Select(radius => Task.Run(async () =>
        {
            using var scenario = new Scenario(radius: radius);
            await scenario.Execute();
            return Assert.Single(scenario.Data.SnapshotCaliperDiagnostics());
        })));
        for (var index = 0; index < radii.Length; index++)
        {
            Assert.Equal(radii[index], results[index].Data.NominalRadius);
            Assert.True(results[index].Data.Found);
            Assert.All(results[index].Data.Spokes.Where(spoke => spoke.EdgeRadius.HasValue),
                spoke => Assert.InRange(spoke.EdgeRadius!.Value, radii[index] - 2, radii[index] + 2));
        }
    }

    [Fact]
    public void OverBudgetReplacementDropsTheOldCaptureInsteadOfReturningStaleSamples()
    {
        using var data = new VisionWorkflowData();
        data.Configure(new VisionRunOptions(CaptureNodeImages: true));
        var small = Diagnostic([1, 2, 3]);
        Assert.True(data.CaptureCaliperDiagnostic("circle", 1, small));
        var huge = Diagnostic(new double[16 * 1024 * 1024 / sizeof(double)]);
        Assert.False(data.CaptureCaliperDiagnostic("circle", 2, huge));
        Assert.Empty(data.SnapshotCaliperDiagnostics());
        Assert.True(data.CaptureCaliperDiagnostic("circle", 3, small));
        Assert.Equal(3, Assert.Single(data.SnapshotCaliperDiagnostics()).ExecutionSequence);
    }

    private static CaliperCircleDiagnostic Diagnostic(double[] profile) => new(100, 100, "CurrentImage", new(50, 50), 20, 4,
        8, 1, "any", "strongest", true, "ok", [new(0, 0, new(66, 50), new(74, 50)) { Profile = profile }]);

    private sealed class Scenario : IDisposable
    {
        public VisionWorkflowData Data { get; } = new();
        public VisionNodeRuntime Runtime { get; }
        public Scenario(bool capture = true, int radius = 100)
        {
            var registry = new VisionNodeRegistry();
            registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new SyntheticImageNode(), "test");
            registry.Register(BuiltInNodeCatalog.Require("measure.caliperCircle"), new CaliperCircleNode(), "test");
            Runtime = new(new VisionNodeDispatcher(registry));
            Data.Definition = new("diagnostics", "Diagnostic lifecycle", [
                Node("source", "image.synthetic", new { width = 400, height = 400, centerX = 200, centerY = 200, radius }),
                Node("circle", "measure.caliperCircle", new { centerX = 200, centerY = 200, radius, radialBand = 10, samplingMode = "count", caliperCount = 72 })
            ], [new("image", "source", "image", "circle", "image")]);
            Data.Configure(new VisionRunOptions(CaptureNodeImages: capture));
        }
        public async Task Execute()
        {
            await Runtime.ExecuteAsync(Data, "source", CancellationToken.None);
            await Runtime.ExecuteAsync(Data, "circle", CancellationToken.None);
        }
        public void Dispose() => Data.Dispose();
        private static NodeDefinition Node(string id, string type, object parameters)
            => new(id, type, id, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters)));
    }
}
