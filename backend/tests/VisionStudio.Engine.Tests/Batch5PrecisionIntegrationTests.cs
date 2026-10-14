using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 第五批（2026-10-14）验收 · 完整工作流集成：
/// 随源码交付的 "形状定位 → ROI 修正 → 卡尺测量 → 公差判定 → 导出结果" 模板
/// 通过真实 WorkflowCore 运行器执行；另以收紧公差验证 NG 判定路径。
/// </summary>
public sealed class Batch5PrecisionIntegrationTests
{
    [Fact]
    public async Task ShippedShapeLocateMeasureTemplate_RunsEndToEnd()
    {
        var workflow = await LoadSampleAsync("shape-locate-measure-workflow.json");
        var result = await RunWorkflowAsync(workflow);

        Assert.True(result.Success, result.Error);
        Assert.Equal(8, result.NodeReports.Count);

        var locate = Assert.Single(result.NodeReports, r => r.NodeId == "locate");
        Assert.True(Convert.ToBoolean(locate.Summary["found"]));
        Assert.True(Convert.ToDouble(locate.Summary["bestScore"]) > 0.8);

        var roi = Assert.Single(result.NodeReports, r => r.NodeId == "roi");
        Assert.Equal("Circle", roi.Summary["roiType"]);

        var measure = Assert.Single(result.NodeReports, r => r.NodeId == "measure");
        Assert.True(Convert.ToBoolean(measure.Summary["found"]));
        Assert.InRange(Convert.ToDouble(measure.Summary["fittedRadius"]), 78, 82);

        Assert.Equal("OK", Assert.Single(result.NodeReports, r => r.NodeId == "result").Summary["disposition"]);

        // 导出结果：JSON 文件真实落盘（artifacts 相对路径由宿主进程解析）。
        var export = Assert.Single(result.NodeReports, r => r.NodeId == "export");
        var path = Convert.ToString(export.Summary["path"])!;
        Assert.EndsWith(".json", path);
        Assert.True(File.Exists(path), $"export file missing: {path}");
        var contents = await File.ReadAllTextAsync(path);
        Assert.Contains("\"type\": \"Boolean\"", contents);
    }

    [Fact]
    public async Task TightTolerance_ProducesNgDisposition()
    {
        var workflow = await LoadSampleAsync("shape-locate-measure-workflow.json");
        // 把公差窗口收紧到 [85,95]：实测半径 ≈ 80 ⇒ NG。
        var nodes = workflow.Nodes.Select(node => node.Id == "tol"
            ? node with
            {
                Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"min":85,"max":95}""")
            }
            : node).ToArray();
        var result = await RunWorkflowAsync(workflow with { Nodes = nodes });

        Assert.True(result.Success, result.Error);
        Assert.Equal("NG", Assert.Single(result.NodeReports, r => r.NodeId == "result").Summary["disposition"]);
        Assert.False(Convert.ToBoolean(Assert.Single(result.NodeReports, r => r.NodeId == "tol").Summary["pass"]));
    }

    private static async Task<WorkflowDefinition> LoadSampleAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", fileName);
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static async Task<WorkflowRunResult> RunWorkflowAsync(WorkflowDefinition workflow)
    {
        var registry = new VisionNodeRegistry();
        IVisionNodeExecutor[] executors =
        [
            new SyntheticImageNode(), new ImageCropNode(), new ShapeMatchNode(), new RoiTransformNode(),
            new CaliperCircleNode(), new ValueRangeNode(), new ResultDispositionNode(), new ExportResultNode()
        ];
        foreach (var executor in executors)
            registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "builtin");

        var services = new ServiceCollection();
        services.AddLogging(); services.AddWorkflow(); services.AddWorkflowDSL();
        services.AddSingleton(registry); services.AddSingleton<VisionNodeDispatcher>(); services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>(); services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>(); services.AddTransient<VisionPipelineStep>();
        services.AddSingleton<WorkflowPlanCache>(); services.AddTransient<WorkflowCoreVisionRunner>();
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(workflow);
    }
}
