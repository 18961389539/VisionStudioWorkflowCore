using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Per-node image capture for interactive inspection: with CaptureNodeImages enabled every image
/// output is encoded once as JPEG and survives the run (live Mats are disposed with the run data).
/// </summary>
public sealed class RunNodeImageCaptureTests
{
    [Fact]
    public async Task CaptureNodeImages_StoresJpegsForEveryImageNode()
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(
            ImageWorkflow("capture-on"),
            new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.NodeImages);
        Assert.Equal(2, result.NodeImages!.Count);

        var source = Assert.Single(result.NodeImages, x => x.NodeId == "source");
        var threshold = Assert.Single(result.NodeImages, x => x.NodeId == "threshold");
        Assert.Equal("image", source.PortName);
        Assert.Equal(320, source.Width);
        Assert.Equal(240, source.Height);
        Assert.True(source.Jpeg.Length > 0);
        Assert.True(threshold.Jpeg.Length > 0);
        Assert.Equal(0xFF, source.Jpeg[0]);
        Assert.Equal(0xD8, source.Jpeg[1]);
    }

    [Fact]
    public async Task CaptureNodeImages_IsOffByDefault()
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(ImageWorkflow("capture-off"), new VisionRunOptions(DebugRunMode.Full));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.NodeImages);
        Assert.Empty(result.NodeImages!);
    }

    [Fact]
    public void CaptureNodeImage_RejectsSingleImageBeyondTotalByteBudget()
    {
        using var data = new VisionWorkflowData();
        data.Configure(new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true));

        data.CaptureNodeImage("huge", ImageOutput(NoiseMat(4096, 4096)));

        // 随机噪声 4096² 的 JPEG 远超 16MB 单 run 预算：整张放弃，而不是把内存撑爆
        Assert.Empty(data.SnapshotNodeImages());
    }

    [Fact]
    public void CaptureNodeImage_StopsCapturing_WhenByteBudgetExhausted()
    {
        using var data = new VisionWorkflowData();
        data.Configure(new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true));

        data.CaptureNodeImage("small", ImageOutput(NoiseMat(160, 160)));
        data.CaptureNodeImage("huge", ImageOutput(NoiseMat(4096, 4096)));

        var snapshots = data.SnapshotNodeImages();
        var first = Assert.Single(snapshots);
        Assert.Equal("small", first.NodeId); // 预算用尽：大图被放弃，先捕获的小图保留
    }

    private static Mat NoiseMat(int width, int height)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3);
        Cv2.Randu(mat, 0, 256); // 随机噪声几乎不可压缩，JPEG 体积接近原始大小
        return mat;
    }

    private static Dictionary<string, VisionValue> ImageOutput(Mat mat)
        => new() { ["image"] = new VisionValue(VisionDataType.Image, VisionImage.Own(mat)) };

    private static WorkflowDefinition ImageWorkflow(string id) => new(
        id,
        "Node Images",
        [
            new NodeDefinition("source", "image.synthetic", "Synthetic", null, new Dictionary<string, JsonElement>
            {
                ["width"] = JsonSerializer.SerializeToElement(320),
                ["height"] = JsonSerializer.SerializeToElement(240),
                ["centerX"] = JsonSerializer.SerializeToElement(160),
                ["centerY"] = JsonSerializer.SerializeToElement(120),
                ["radius"] = JsonSerializer.SerializeToElement(45)
            }),
            new NodeDefinition("threshold", "image.threshold", "Threshold", null, new Dictionary<string, JsonElement>
            {
                ["threshold"] = JsonSerializer.SerializeToElement(100)
            })
        ],
        [
            new EdgeDefinition("c1", "source", "next", "threshold", "exec", "control"),
            new EdgeDefinition("d1", "source", "image", "threshold", "image")
        ]);

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();

        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new SyntheticImageNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("image.threshold"), new ThresholdNode(), "builtin");
        services.AddSingleton(registry);
        services.AddSingleton<WorkflowPlanCache>();
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        return services.BuildServiceProvider();
    }
}
