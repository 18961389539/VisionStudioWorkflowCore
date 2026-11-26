using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Core.Analysis;
using Xunit;

namespace ImageViewer.Core.Tests;

public sealed class AnalysisPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_RunsStagesInOrder()
    {
        var order = new List<int>();
        var pipeline = new AnalysisPipeline<List<int>>(
        [
            new DelegateAnalysisStage<List<int>>((context, _) => { context.Add(1); return ValueTask.CompletedTask; }),
            new DelegateAnalysisStage<List<int>>((context, _) => { context.Add(2); return ValueTask.CompletedTask; })
        ]);

        await pipeline.ExecuteAsync(order);

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task ExecuteAsync_StopsBeforeNextStageWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executed = false;
        var pipeline = new AnalysisPipeline<List<int>>
        ([new DelegateAnalysisStage<List<int>>((_, _) =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        })]);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pipeline.ExecuteAsync([], cancellation.Token));
        Assert.False(executed);
    }
}
