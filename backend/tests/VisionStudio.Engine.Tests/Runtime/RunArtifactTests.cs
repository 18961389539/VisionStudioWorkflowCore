using OpenCvSharp;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class RunArtifactTests
{
    [Fact]
    public void UnsampledOkRunsCaptureNoImagesWhileNgAlwaysCaptures()
    {
        using var data = ImageData();
        var runId = Enumerable.Range(0, 10000).Select(x => x.ToString()).First(x =>
            !RunArtifactSampling.Include(x, "OK", 20) && !RunArtifactSampling.Include(x, "OK", 20, replay: true));
        using var skipped = data.CaptureArtifacts(runId, "OK", new(DeferEncoding: true, PreviewSampleEvery: 20, ReplaySampleEvery: 20));
        Assert.Equal(0, skipped.RawBytes);
        var encoded = skipped.Encode(Result(runId));
        Assert.Null(encoded.PreviewJpeg);
        Assert.Null(encoded.ReplayInput);
        using var ng = data.CaptureArtifacts(runId, "NG", new(DeferEncoding: true, PreviewSampleEvery: 20, ReplaySampleEvery: 20));
        Assert.True(ng.RawBytes > 0);
        Assert.NotNull(ng.Encode(Result(runId)).PreviewJpeg);
    }

    [Fact]
    public void DeferredCopiesSurviveRunDisposalAndRespectRawMemoryLimit()
    {
        var data = ImageData();
        using var captured = data.CaptureArtifacts("owned", "OK", new(DeferEncoding: true));
        using var oversized = data.CaptureArtifacts("oversized", "NG", new(DeferEncoding: true, MaxRawBytes: 1));
        data.Dispose();
        var result = captured.Encode(Result("owned"));
        Assert.Equal(16, result.PreviewWidth);
        Assert.Equal(12, result.PreviewHeight);
        Assert.NotEmpty(result.PreviewJpeg!);
        Assert.Equal("image", result.ReplayInput!.SourceNodeId);
        Assert.NotEmpty(result.ReplayInput.Png);
        Assert.Null(result.DeferredArtifacts);
        Assert.Equal(0, oversized.RawBytes);
        Assert.Contains("limit", oversized.Warning!);
    }

    [Fact]
    public async Task DeferredRunnerReturnsRawArtifactsAndInlineRunnerKeepsImmediatePreview()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.synthetic"), new VisionStudio.Engine.Nodes.SyntheticImageNode(), "builtin");
        services.AddSingleton(registry);
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddSingleton<WorkflowPlanCache>();
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = new WorkflowDefinition("artifacts", "Artifacts", [new("n", "image.synthetic", null, null, null)], []);
        var deferred = await runner.RunAsync(workflow, new(Artifacts: new(DeferEncoding: true)));
        using var images = deferred.DeferredArtifacts;
        Assert.True(deferred.Success, deferred.Error);
        Assert.NotNull(images);
        Assert.Null(deferred.PreviewJpeg);
        Assert.Null(deferred.ReplayInput);
        Assert.NotNull(images.Encode(deferred).PreviewJpeg);
        var inline = await runner.RunAsync(workflow);
        Assert.True(inline.Success, inline.Error);
        Assert.NotNull(inline.PreviewJpeg);
        Assert.NotNull(inline.ReplayInput);
        Assert.Null(inline.DeferredArtifacts);
        Assert.Equal(1, provider.GetRequiredService<WorkflowPlanCache>().CompilationCount);
        var json = System.Text.Json.JsonSerializer.Serialize(new VisionRunOptions(Artifacts: new(DeferEncoding: true)));
        Assert.DoesNotContain("Artifacts", json);
        Assert.DoesNotContain("DeferredArtifacts", System.Text.Json.JsonSerializer.Serialize(deferred));
    }

    private static VisionWorkflowData ImageData()
    {
        var data = new VisionWorkflowData();
        var image = VisionImage.Own(new Mat(12, 16, MatType.CV_8UC1, Scalar.All(100)));
        var outputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) };
        data.OutputsByNode["image"] = outputs;
        data.TrackOutputs(outputs);
        data.AddReport(new("image", "test.image", true, 1, new Dictionary<string, object?>()));
        return data;
    }

    private static WorkflowRunResult Result(string id) => new(id, true, 1, false, 0, 0, [], [], null);
}
