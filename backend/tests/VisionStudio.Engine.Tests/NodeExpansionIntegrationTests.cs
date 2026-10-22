using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class NodeExpansionIntegrationTests
{
    [Theory]
    [InlineData("expanded-inspection-workflow.json")]
    [InlineData("foreach-detections-workflow.json")]
    public async Task ShippedExamples_ExecuteThroughTheRealRuntime(string fileName)
    {
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", fileName)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var registry = new VisionNodeRegistry();
        IVisionNodeExecutor[] executors = [new SyntheticImageNode(), new GrayscaleNode(), new GaussianBlurNode(),
            new OtsuThresholdNode(), new MorphologyNode(), new MultiBlobNode(), new ValueRangeNode(), new ResultDispositionNode(),
            new ThresholdNode(), new ValueNumberNode(), new ValueArithmeticNode(), new DetectionForEachNode(),
            new LoopEndNode(), new DetectionCountNode()];
        foreach (var executor in executors) registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "builtin");
        var services = new ServiceCollection();
        services.AddLogging(); services.AddWorkflow(); services.AddWorkflowDSL();
        services.AddSingleton(registry); services.AddSingleton<VisionNodeDispatcher>(); services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>(); services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>(); services.AddTransient<VisionPipelineStep>();
        services.AddSingleton<WorkflowPlanCache>(); services.AddTransient<WorkflowCoreVisionRunner>();
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(workflow);
        Assert.True(result.Success, result.Error);
        if (fileName.StartsWith("foreach", StringComparison.Ordinal))
        {
            var count = Convert.ToInt32(Assert.Single(result.NodeReports, report => report.NodeId == "count").Summary["count"]);
            Assert.True(count > 1);
            Assert.Equal(count, result.NodeReports.Count(report => report.NodeId == "position"));
        }
        else Assert.Equal("OK", Assert.Single(result.NodeReports, report => report.NodeId == "result").Summary["disposition"]);
    }

    [Fact]
    public async Task ZeroDegreeExpandedRotation_PreservesEveryPixelAndInputOwnership()
    {
        using var mat = new Mat(3, 4, MatType.CV_8UC1, Scalar.All(0));
        mat.Set(0, 0, (byte)255); mat.Set(2, 3, (byte)173);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new ImageRotateNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) }),
            new("rotate", "image.rotate", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"angleDeg\":0,\"expand\":true}")), default);
        using var output = (VisionImage)result.Outputs["image"].Value!;
        Assert.Equal(0, Cv2.Norm(input.Mat, output.Mat));
        Assert.Equal(255, input.Mat.At<byte>(0, 0));
    }

    [Theory]
    [InlineData(1, 5, "OK")]
    [InlineData(100, 200, "NG")]
    public async Task PreprocessingAndCounting_ProduceTraceableDisposition(int min, int max, string expected)
    {
        var registry = new VisionNodeRegistry();
        IVisionNodeExecutor[] executors = [new SyntheticImageNode(), new GrayscaleNode(), new GaussianBlurNode(),
            new OtsuThresholdNode(), new MorphologyNode(), new MultiBlobNode(), new ValueRangeNode(), new ResultDispositionNode()];
        foreach (var executor in executors) registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "builtin");
        var services = new ServiceCollection();
        services.AddLogging(); services.AddWorkflow(); services.AddWorkflowDSL();
        services.AddSingleton(registry); services.AddSingleton<VisionNodeDispatcher>(); services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>(); services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>(); services.AddTransient<VisionPipelineStep>();
        services.AddSingleton<WorkflowPlanCache>(); services.AddTransient<WorkflowCoreVisionRunner>();
        await using var provider = services.BuildServiceProvider();
        var nodes = executors.Select((executor, i) => new NodeDefinition($"n{i}", executor.Type, null, null,
            i == 6 ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { min, max })) : null)).ToArray();
        var edges = new List<EdgeDefinition>();
        for (var i = 0; i < nodes.Length - 1; i++)
        {
            edges.Add(new($"c{i}", $"n{i}", "next", $"n{i + 1}", "exec", "control"));
            edges.Add(new($"d{i}", $"n{i}", i < 5 ? "image" : i == 5 ? "count" : "pass", $"n{i + 1}", i < 5 ? "image" : i == 5 ? "value" : "pass"));
        }
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(new("expanded-inspection", "Expanded Inspection", nodes, edges));
        Assert.True(result.Success, result.Error);
        Assert.Equal(8, result.NodeReports.Count);
        Assert.Equal(expected, Assert.Single(result.NodeReports, report => report.NodeId == "n7").Summary["disposition"]);
        var detections = Assert.Single(result.NodeReports, report => report.NodeId == "n5");
        Assert.InRange(Convert.ToInt32(detections.Summary["count"]), 1, 5);
    }
}
