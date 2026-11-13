using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 流程级金样回归（2026-11-12）。
///
/// 为什么需要它：引擎测试有 1300+ 条，但输入大多是测试里现画的 <c>new Mat(...)</c>——
/// 图跟着代码一起变，等于没有固定基准。升级 OpenCvSharp（当前 5.0.0）、换 JLVision 库、
/// 改一次预处理插值，单测仍然可能全绿，而产线结果已经整体漂移。
///
/// 这里的夹具是**提交进仓库的固定 PNG**（生成脚本 scripts/generate-golden-fixtures.py，
/// 重新生成必须同步更新期望值），断言走真实工作流运行器：image.load → 算法节点 → 汇总。
/// 容差按算法口径给，不用哈希：浮点结果逐位比对必然 flaky，卡太死则会逼人改期望而不是查回归。
/// </summary>
public sealed class GoldenWorkflowRegressionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ScratchFixture_ThroughThresholdAndBlobs_MatchesGoldenSnapshot()
    {
        // 拉丝背景 + 三条划痕：阈值 60 只保留划痕，斑点面积/数量是缺陷检出的基线。
        var result = await RunAsync(
            [
                Node("load", "image.load", new { path = Fixture("scratch-640x480.png"), pathMode = "file", colorMode = "gray" }),
                Node("threshold", "image.threshold", new { threshold = 60, invert = true }),
                Node("blobs", "measure.blobs", new { mode = "all", minArea = 40, maxArea = 0, maxCount = 20 })
            ],
            ("load", "image", "threshold", "image"),
            ("threshold", "image", "blobs", "image"));

        Assert.True(result.Success, result.Error);
        var blobs = Assert.Single(result.NodeReports, report => report.NodeId == "blobs");

        // 数量是离散口径：多一条划痕被切碎或漏检都应该在这里变红。
        Assert.Equal(3, Convert.ToInt32(blobs.Summary["count"]));
        // 面积按 ±8% 容差：只拦"整体尺度变了"，不拦亚像素级的边界抖动。
        Assert.InRange(Convert.ToDouble(blobs.Summary["totalArea"]), 1454 * 0.92, 1454 * 1.08);
    }

    [Fact]
    public async Task EdgeFixture_ThroughCaliper_KeepsSubpixelEdgePosition()
    {
        // 0.7 px 过渡宽度的竖直台阶，真实边缘在 x=312.4：抛物线插值应稳定落在 ±0.35 px。
        var result = await RunAsync(
            [
                Node("load", "image.load", new { path = Fixture("edge-640x480.png"), pathMode = "file", colorMode = "gray" }),
                Node("caliper", "measure.caliper", new
                {
                    startX = 180,
                    startY = 240,
                    endX = 460,
                    endY = 240,
                    sampleWidth = 9,
                    edgeThreshold = 20,
                    polarity = "rising",
                    refine = "parabolic"
                })
            ],
            ("load", "image", "caliper", "image"));

        Assert.True(result.Success, result.Error);
        var caliper = Assert.Single(result.NodeReports, report => report.NodeId == "caliper");
        Assert.True(Convert.ToBoolean(caliper.Summary["found"]), Convert.ToString(caliper.Summary["reason"]));
        Assert.InRange(Convert.ToDouble(caliper.Summary["x"]), 312.4 - 0.35, 312.4 + 0.35);
        Assert.InRange(Convert.ToDouble(caliper.Summary["y"]), 240 - 0.35, 240 + 0.35);
    }

    [Fact]
    public async Task RingFixture_ThroughCaliperCircle_KeepsFittedRadius()
    {
        // 圆心 (319.6, 240.3)、半径 120.5、环宽约 1.15 px：24 个卡尺点的拟合半径是圆测量的基线。
        var result = await RunAsync(
            [
                Node("load", "image.load", new { path = Fixture("ring-640x480.png"), pathMode = "file", colorMode = "gray" }),
                Node("circle", "measure.caliperCircle", new
                {
                    centerX = 320,
                    centerY = 240,
                    radius = 120,
                    angleStartDeg = 0,
                    angleEndDeg = 360,
                    caliperCount = 24,
                    radialBand = 6,
                    minContrast = 20,
                    sampleWidth = 5,
                    refine = "parabolic"
                })
            ],
            ("load", "image", "circle", "image"));

        Assert.True(result.Success, result.Error);
        var circle = Assert.Single(result.NodeReports, report => report.NodeId == "circle");
        Assert.True(Convert.ToBoolean(circle.Summary["found"]), Convert.ToString(circle.Summary["reason"]));
        Assert.InRange(Convert.ToDouble(circle.Summary["fittedRadius"]), 120.5 - 0.4, 120.5 + 0.4);
        Assert.InRange(Convert.ToDouble(circle.Summary["centerX"]), 319.6 - 0.5, 319.6 + 0.5);
        Assert.InRange(Convert.ToDouble(circle.Summary["centerY"]), 240.3 - 0.5, 240.3 + 0.5);
    }

    private static string Fixture(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static NodeDefinition Node(string id, string type, object parameters)
        => new(
            id,
            type,
            null,
            new NodePosition(0, 0),
            JsonSerializer.SerializeToElement(parameters, Json).EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase));

    private static async Task<WorkflowRunResult> RunAsync(
        NodeDefinition[] nodes,
        params (string From, string Port, string To, string TargetPort)[] dataEdges)
    {
        var edges = new List<EdgeDefinition>();
        for (var index = 0; index < nodes.Length - 1; index++)
            edges.Add(new EdgeDefinition($"c{index}", nodes[index].Id, "next", nodes[index + 1].Id, "exec", "control"));
        foreach (var (from, port, to, targetPort) in dataEdges)
            edges.Add(new EdgeDefinition($"d{edges.Count}", from, port, to, targetPort, "data"));

        var workflow = new WorkflowDefinition("golden-regression", "Golden regression", nodes, edges);

        var registry = new VisionNodeRegistry();
        IVisionNodeExecutor[] executors =
        [
            new ImageLoadNode(new FixtureResolver()),
            new ThresholdNode(),
            new MultiBlobNode(),
            new CaliperNode(),
            new CaliperCircleNode()
        ];
        foreach (var executor in executors)
            registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "builtin");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        services.AddSingleton(registry);
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddSingleton<WorkflowPlanCache>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(workflow);
    }

    /// <summary>只授权测试输出目录下的 Fixtures：金样用例也要走真实的读盘授权路径。</summary>
    private sealed class FixtureResolver : IReplayInputResolver
    {
        private static readonly string Root =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures")) + Path.DirectorySeparatorChar;

        public string? Resolve(string requestedPath)
        {
            var full = Path.GetFullPath(requestedPath);
            return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
    }
}
