using VisionStudio.Api;
using Xunit;

namespace VisionStudio.Api.Tests;

public sealed class PluginPerformanceBenchmarkTests
{
    private static PluginWorkerLatencyDistribution Dist(double p95, double p99) => new(p95 * .7, p95 * .6, p95, p99, p99 * 1.05);
    private static PluginWorkerPerformanceProfile Perf(int pool, double p95) => new(
        "sample.math", pool, 512, 100, 100, 0, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow, 10, .5, .1,
        "Plugin execute", pool, true, "ok", Dist(1,1.2), Dist(1,1.2), Dist(1,1.2), Dist(1,1.2), Dist(1,1.2), Dist(p95,p95*1.1), Dist(1,1.2), Dist(1,1.2), Dist(p95,p95*1.1), []);
    private static PluginBenchmarkPoolResult Result(int pool, double throughput, double p95, double p99, long memory=100_000_000, int failures=0) =>
        new(pool,100,100-failures,failures,failures/100d,1000,throughput,memory,Dist(p95,p99),Perf(pool,p95));

    [Fact]
    public void Recommend_PrefersSmallestPoolWithinFivePercentOfMaximumThroughput()
    {
        var recommendation = PluginBenchmarkAnalysis.Recommend([Result(1,40,20,24), Result(2,78,12,15), Result(4,80,11,14)]);
        Assert.Equal(2, recommendation.RecommendedPoolSize);
    }

    [Fact]
    public void RegressionGate_FailsWhenP95BudgetIsExceeded()
    {
        var baselineResults = new[] { Result(2,80,10,12) };
        var baseline = new PluginBenchmarkRun("b","d","v","p","1",null,"h","Completed",[2],100,5,4,null,new(),DateTimeOffset.UtcNow,null,false,null,
            PluginBenchmarkAnalysis.Recommend(baselineResults),null,baselineResults);
        var gate = PluginBenchmarkAnalysis.EvaluateRegression(baseline,[Result(2,79,12,13)],new PluginBenchmarkRegressionPolicy(MaximumP95RegressionPercent:10));
        Assert.Equal("FAIL", gate.Status);
        Assert.Contains(gate.Reasons, x => x.Contains("p95", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegressionGate_PassesStableCandidate()
    {
        var baselineResults = new[] { Result(2,80,10,12,100_000_000) };
        var baseline = new PluginBenchmarkRun("b","d","v","p","1",null,"h","Completed",[2],100,5,4,null,new(),DateTimeOffset.UtcNow,null,false,null,
            PluginBenchmarkAnalysis.Recommend(baselineResults),null,baselineResults);
        var gate = PluginBenchmarkAnalysis.EvaluateRegression(baseline,[Result(2,82,10.3,12.5,105_000_000)],new());
        Assert.Equal("PASS", gate.Status);
    }

    [Fact]
    public void RegressionGate_UsesBaselineRecommendedPoolInsteadOfHidingRegressionWithLargerPool()
    {
        var baselineResults = new[] { Result(1,50,15,18), Result(2,90,10,12), Result(4,92,9.8,12) };
        var baseline = new PluginBenchmarkRun("b","d","v","p","1",null,"h","Completed",[1,2,4],100,5,4,null,new(),DateTimeOffset.UtcNow,null,false,null,
            PluginBenchmarkAnalysis.Recommend(baselineResults),null,baselineResults);
        var gate = PluginBenchmarkAnalysis.EvaluateRegression(baseline,[Result(1,48,16,19), Result(2,70,13,16), Result(4,110,9,11)],new());
        Assert.Equal(2, gate.ComparedPoolSize);
        Assert.Equal("FAIL", gate.Status);
    }

    [Fact]
    public void RegressionGate_AcceptsPortableBaselineSnapshot()
    {
        var baselineResults = new[] { Result(2,80,10,12,100_000_000) };
        var baseline = new PluginBenchmarkBaselineSnapshot("portable-baseline","d","sample.math","1.0.0","abc","h",
            PluginBenchmarkAnalysis.Recommend(baselineResults), baselineResults);
        var gate = PluginBenchmarkAnalysis.EvaluateRegression(baseline,[Result(2,82,10.2,12.4,104_000_000)],new());
        Assert.Equal("PASS", gate.Status);
        Assert.Equal("1.0.0", gate.BaselinePluginVersion);
        Assert.Equal("abc", gate.BaselinePluginAssemblySha256);
    }
}
