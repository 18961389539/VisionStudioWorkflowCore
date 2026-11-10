using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 执行模式契约（2026-11-10 · 设计审计 P0-1）：模式由目录能力位单一决定，
/// 内置节点必须显式声明，插件未声明时保守回退 WorkflowCore。
/// </summary>
public sealed class NodeExecutionModeTests
{
    [Fact]
    public void EveryBuiltInNodeDeclaresExplicitExecutionMode()
    {
        var undeclared = BuiltInNodeCatalog.Items
            .Where(item => (item.Capabilities?.ExecutionMode ?? VisionExecutionMode.Auto) == VisionExecutionMode.Auto)
            .Select(item => item.Type)
            .ToArray();
        Assert.Empty(undeclared);
    }

    [Theory]
    [InlineData("image.threshold", VisionExecutionMode.Pipeline)]
    [InlineData("measure.caliper", VisionExecutionMode.Pipeline)]
    [InlineData("match.shape", VisionExecutionMode.Pipeline)]
    [InlineData("value.range", VisionExecutionMode.Pipeline)]
    [InlineData("robot.guidance2d", VisionExecutionMode.Pipeline)]
    [InlineData("flow.if", VisionExecutionMode.WorkflowCore)]
    [InlineData("device.writeTag", VisionExecutionMode.WorkflowCore)]
    [InlineData("recognition.barcode", VisionExecutionMode.WorkflowCore)]
    [InlineData("inference.detection", VisionExecutionMode.WorkflowCore)]
    [InlineData("image.acquire", VisionExecutionMode.WorkflowCore)]
    [InlineData("script.csharp", VisionExecutionMode.WorkflowCore)]
    public void KnownNodesResolveToExpectedMode(string type, VisionExecutionMode expected)
    {
        Assert.Equal(expected, VisionExecutionPolicy.Resolve(BuiltInNodeCatalog.Require(type)));
    }

    [Fact]
    public void PluginWithoutDeclarationFallsBackToWorkflowCore()
    {
        var plugin = new NodeCatalogItem("acme.custom", "Custom", "Plugin", [], [], [], PluginId: "acme");
        Assert.Equal(VisionExecutionMode.WorkflowCore, VisionExecutionPolicy.Resolve(plugin));
    }

    [Fact]
    public void IterativeControlDetectionCoversWhileRepeatAndTry()
    {
        static NodeDefinition Node(string type) => new("n", type, null, null, null);
        Assert.True(VisionExecutionPolicy.HasIterativeOrRetryControl([Node("flow.while")]));
        Assert.True(VisionExecutionPolicy.HasIterativeOrRetryControl([Node("flow.repeat")]));
        Assert.True(VisionExecutionPolicy.HasIterativeOrRetryControl([Node("flow.try")]));
        Assert.True(VisionExecutionPolicy.HasIterativeOrRetryControl([Node("flow.forEach")]));
        Assert.False(VisionExecutionPolicy.HasIterativeOrRetryControl([Node("flow.if"), Node("image.threshold")]));
    }
}
