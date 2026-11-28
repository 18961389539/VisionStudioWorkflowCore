using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api.Tests;

public sealed class ValueP0DurableCounterTests
{
    [Fact]
    public async Task DurableEventFingerprintRejectsChangedOperationsWithoutMutatingCounters()
    {
        using var environment = new TempWebHostEnvironment();
        var node = new ValueCounterNode(durableStore: new SqliteDurableRunCounterStore(new SqliteMetadataDatabase(environment)));

        var first = await Run(node, "event-1");
        Assert.Equal(1, Count(first));
        var retry = await Run(node, "event-1");
        Assert.Equal(1, Count(retry));
        Assert.True(retry.Outputs["reused"].Require<bool>("reused"));

        var fractional = await Run(node, "fractional-event", step: 1.1);
        Assert.Equal(2, Count(fractional));
        await AssertConflict(node, "fractional-event", step: 1.4);
        Assert.Equal(2, Count(await Run(node, "fractional-event", step: 1.1)));

        await AssertConflict(node, "event-1", step: 2);
        await AssertConflict(node, "event-1", pass: false);
        await AssertConflict(node, "event-1", reset: true);
        await AssertConflict(node, "event-1", resetOnPass: true);

        var next = await Run(node, "event-2");
        Assert.Equal(3, Count(next));
        Assert.Equal(3, next.Outputs["total"].Require<long>("total"));
    }

    private static async Task AssertConflict(ValueCounterNode node, string eventId, double step = 1, bool pass = true,
        bool reset = false, bool resetOnPass = false)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(node, eventId, step, pass, reset, resetOnPass));
        Assert.Contains("errorCode=eventConflict", error.Message);
        var retry = await Run(node, eventId, step: step == 1.4 ? 1.1 : 1);
        Assert.True(retry.Outputs["reused"].Require<bool>("reused"));
    }

    private static Task<NodeExecutorResult> Run(ValueCounterNode node, string eventId, double step = 1,
        bool pass = true, bool reset = false, bool resetOnPass = false)
    {
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["eventId"] = VisionValue.String(eventId),
            ["pass"] = VisionValue.Boolean(pass),
            ["reset"] = VisionValue.Boolean(reset)
        }, workflowScope: "plan") { WorkflowId = "workflow" };
        var definition = new NodeDefinition("counter", "value.counter", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["durability"] = "durable", ["step"] = step, ["resetOnPass"] = resetOnPass
            })));
        return node.ExecuteAsync(context, definition, CancellationToken.None).AsTask();
    }

    private static long Count(NodeExecutorResult result) => result.Outputs["count"].Require<long>("count");
}
