namespace VisionStudio.Api;

public sealed record PluginBenchmarkCiGateOptions(
    bool RequireRegressionPass = true,
    bool FailOnBenchmarkFailure = true);

public sealed record PluginBenchmarkCiSpec(
    int SchemaVersion,
    string Name,
    StartPluginBenchmarkRequest Benchmark,
    PluginBenchmarkCiGateOptions Gate)
{
    public const int CurrentSchemaVersion = 1;

    public static PluginBenchmarkCiSpec FromBaseline(PluginBenchmarkRun run)
    {
        var baseline = PluginBenchmarkBaselineSnapshot.FromRun(run);
        return new PluginBenchmarkCiSpec(
            CurrentSchemaVersion,
            $"{run.PluginId}-{run.PluginVersion}-performance-gate",
            new StartPluginBenchmarkRequest(
                run.DatasetId,
                run.ValidationRunId,
                run.PluginId,
                run.PoolSizes,
                run.RequestedCount,
                run.WarmupCount,
                run.WorkloadConcurrency,
                BaselineRunId: null,
                RegressionPolicy: run.RegressionPolicy,
                BaselineSnapshot: baseline),
            new PluginBenchmarkCiGateOptions());
    }
}
