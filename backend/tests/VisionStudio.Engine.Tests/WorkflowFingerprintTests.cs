using System.Text.Json;

namespace VisionStudio.Engine.Tests;

public sealed class WorkflowFingerprintTests
{
    [Fact]
    public void DesignerOnlyChanges_DoNotChangeExecutionFingerprint()
    {
        var original = Workflow(
            name: "Designer A",
            nodeName: "Threshold A",
            position: new NodePosition(10, 20),
            edgeId: "edge-a",
            threshold: 128);
        var moved = Workflow(
            name: "Designer B",
            nodeName: "Renamed Node",
            position: new NodePosition(900, 700),
            edgeId: "edge-b",
            threshold: 128);

        Assert.Equal(WorkflowFingerprint.Compute(original), WorkflowFingerprint.Compute(moved));
    }

    [Fact]
    public void ExecutionParameterChange_ChangesExecutionFingerprint()
    {
        var a = Workflow("A", "Node", new NodePosition(10, 20), "e1", 128);
        var b = Workflow("A", "Node", new NodePosition(10, 20), "e1", 129);
        Assert.NotEqual(WorkflowFingerprint.Compute(a), WorkflowFingerprint.Compute(b));
    }

    [Fact]
    public void ParameterObjectPropertyOrder_DoesNotChangeExecutionFingerprint()
    {
        var a = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["x"] = 1, ["y"] = 2 });
        var b = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["y"] = 2, ["x"] = 1 });
        var wa = new WorkflowDefinition("id", "A", [new NodeDefinition("n", "test", "A", null, new() { ["roi"] = a })], []);
        var wb = new WorkflowDefinition("id", "B", [new NodeDefinition("n", "test", "B", new NodePosition(3, 4), new() { ["roi"] = b })], []);
        Assert.Equal(WorkflowFingerprint.Compute(wa), WorkflowFingerprint.Compute(wb));
    }

    private static WorkflowDefinition Workflow(string name, string nodeName, NodePosition position, string edgeId, int threshold)
        => new(
            "workflow-id",
            name,
            [
                new NodeDefinition("source", "source", "Source", new NodePosition(0, 0), null),
                new NodeDefinition("threshold", "threshold", nodeName, position,
                    new Dictionary<string, JsonElement> { ["threshold"] = JsonSerializer.SerializeToElement(threshold) })
            ],
            [new EdgeDefinition(edgeId, "source", "image", "threshold", "image", "data")]);
}
