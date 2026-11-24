using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api.Tests;

public sealed class DurableRunCounterTests
{
    [Fact]
    public async Task DurableEventsAreIdempotentAcrossConcurrencyAndRestart()
    {
        using var environment = new TempWebHostEnvironment();
        var original = new ValueCounterNode(durableStore: new SqliteDurableRunCounterStore(new SqliteMetadataDatabase(environment)));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Run(original, "inspection-1"))));
        Assert.Single(results, result => !Reused(result));
        Assert.All(results, result => Assert.Equal(1, Count(result)));

        var restarted = new ValueCounterNode(durableStore: new SqliteDurableRunCounterStore(new SqliteMetadataDatabase(environment)));
        Assert.True(Reused(await Run(restarted, "inspection-1", plan: "rebuilt-plan")));
        Assert.Equal(2, Count(await Run(restarted, "inspection-preview", debug: true)));
        var second = await Run(restarted, "inspection-2");
        Assert.Equal(2, Count(second));
        Assert.False(Reused(second));

        var reset = await Run(restarted, "reset-event", reset: true);
        Assert.Equal(1, Count(reset));
        Assert.Equal(1, reset.Outputs["total"].Require<long>("total"));
        Assert.Equal(2, Count(await Run(restarted, "inspection-3")));
        Assert.True(Reused(await Run(restarted, "reset-event", reset: true)));
        Assert.Equal(3, Count(await Run(restarted, "inspection-4")));
        Assert.Equal(1, Count(await Run(restarted, "inspection-1", workflow: "separate-workflow")));
    }

    private static Task<NodeExecutorResult> Run(ValueCounterNode node, string eventId,
        bool reset = false, bool debug = false, string workflow = "production-workflow", string plan = "initial-plan")
    {
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["eventId"] = VisionValue.String(eventId),
            ["pass"] = VisionValue.Boolean(true),
            ["reset"] = VisionValue.Boolean(reset)
        }, workflowScope: plan) { WorkflowId = workflow, IsDebugRun = debug };
        var definition = new NodeDefinition("counter", "value.counter", null, null,
            new Dictionary<string, JsonElement> { ["durability"] = JsonSerializer.SerializeToElement("durable") });
        return node.ExecuteAsync(context, definition, CancellationToken.None).AsTask();
    }

    private static long Count(NodeExecutorResult result) => result.Outputs["count"].Require<long>("count");
    private static bool Reused(NodeExecutorResult result) => result.Outputs["reused"].Require<bool>("reused");
}
