using VisionStudio.Api;
using Xunit;

namespace VisionStudio.Api.Tests;

public sealed class PluginBenchmarkCiTests
{
    private static PluginWorkerLatencyDistribution Dist(double p95, double p99) => new(p95 * .7, p95 * .6, p95, p99, p99 * 1.05);
    private static PluginWorkerPerformanceProfile Perf(int pool) => new(
        "sample.math", pool, 512, 100, 100, 0, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow, 10, .5, .1,
        "Plugin execute", pool, true, "ok", Dist(1,1.2), Dist(1,1.2), Dist(1,1.2), Dist(1,1.2), Dist(1,1.2), Dist(10,12), Dist(1,1.2), Dist(1,1.2), Dist(10,12), []);

    [Fact]
    public void CiSpec_EmbedsPortableBaselineAndRemovesDatabaseRunDependency()
    {
        var results = new[] { new PluginBenchmarkPoolResult(2,100,100,0,0,1000,100,120_000_000,Dist(10,12),Perf(2)) };
        var run = new PluginBenchmarkRun("bench-1","dataset-1","validation-1","sample.math","1.2.3","deadbeef","workflow-hash","Completed",[2],100,5,4,null,new(),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,false,null,
            PluginBenchmarkAnalysis.Recommend(results),null,results);

        var spec = PluginBenchmarkCiSpec.FromBaseline(run);

        Assert.Equal(PluginBenchmarkCiSpec.CurrentSchemaVersion, spec.SchemaVersion);
        Assert.Null(spec.Benchmark.BaselineRunId);
        Assert.NotNull(spec.Benchmark.BaselineSnapshot);
        Assert.Equal("bench-1", spec.Benchmark.BaselineSnapshot!.SourceRunId);
        Assert.Equal("1.2.3", spec.Benchmark.BaselineSnapshot.PluginVersion);
        Assert.True(spec.Gate.RequireRegressionPass);
    }
}
