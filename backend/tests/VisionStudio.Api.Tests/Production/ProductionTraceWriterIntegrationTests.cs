using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

public sealed class ProductionTraceWriterIntegrationTests
{
    [Fact]
    public async Task HostSharesCacheAndBackgroundWriterUsesConfiguredRetention()
    {
        using var factory = new VisionStudioApiFactory();
        // Start hosted services so the built-in registry is populated.
        using var client = factory.CreateClient();
        var runner = factory.Services.GetRequiredService<IVisionWorkflowRunner>();
        var traces = factory.Services.GetRequiredService<TraceabilityStore>();
        var runs = factory.Services.GetRequiredService<RunStore>();
        var workflow = new WorkflowDefinition("trace-integration", "Integration",
            [new("source", "image.synthetic", null, null, new()
            {
                ["width"] = JsonSerializer.SerializeToElement(160),
                ["height"] = JsonSerializer.SerializeToElement(120),
                // 合成图默认圆心/半径按 1280x960 画布设计，小画布必须显式收到画布内，
                // 否则 image.synthetic 会按越界规则拒绝执行。
                ["centerX"] = JsonSerializer.SerializeToElement(80),
                ["centerY"] = JsonSerializer.SerializeToElement(60),
                ["radius"] = JsonSerializer.SerializeToElement(40)
            })], []);
        var options = traces.GetArtifactOptions(32L * 1024 * 1024);
        var result = await runner.RunAsync(workflow, new(Artifacts: options));
        Assert.True(result.Success, result.Error);
        Assert.Null(result.PreviewJpeg);
        Assert.Null(result.ReplayInput);
        var cache = factory.Services.GetRequiredService<WorkflowPlanCache>();
        var compiled = cache.CompilationCount;
        var repeat = await runner.RunAsync(workflow);
        Assert.True(repeat.Success, repeat.Error);
        Assert.Equal(compiled, cache.CompilationCount);
        await using (var writer = new ProductionTraceWriter(traces, runs, new(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance))
            await writer.EnqueueAsync(result, workflow, new("ProductionRuntime"));
        var trace = await traces.GetAsync(result.RunId, default);
        Assert.NotNull(trace);
        Assert.Equal(RunArtifactSampling.Include(result.RunId, "OK", options.PreviewSampleEvery), trace.HasPreview);
        Assert.Equal(RunArtifactSampling.Include(result.RunId, "OK", options.ReplaySampleEvery, replay: true), trace.HasReplayInput);
    }
}
