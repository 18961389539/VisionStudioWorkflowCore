namespace VisionStudio.Api.Tests;

public sealed class ProductionTriggerQueueTests
{
    [Fact]
    public void TriggerQueue_IsDurableIdempotent_AndNeverReplaysInterruptedEvents()
    {
        using var env = new TempWebHostEnvironment();
        var path = Path.Combine(env.ContentRootPath, "data", "production", "triggers.json");
        var first = new ProductionTriggerQueue(path);
        var accepted = first.Enqueue("plc-event-42");
        var duplicate = first.Enqueue("plc-event-42");

        Assert.True(accepted.IsNew);
        Assert.False(duplicate.IsNew);
        Assert.Equal(accepted.Event.EventId, duplicate.Event.EventId);
        Assert.Equal(ProductionTriggerState.Processing, first.TryBeginNext()!.State);
        first.Complete("plc-event-42", "run-42");

        var reopened = new ProductionTriggerQueue(path);
        Assert.Equal(ProductionTriggerState.Completed, Assert.Single(reopened.List()).State);

        var interrupted = reopened.Enqueue("plc-event-43");
        Assert.True(interrupted.IsNew);
        Assert.Equal(ProductionTriggerState.Processing, reopened.TryBeginNext()!.State);
        var afterRestart = new ProductionTriggerQueue(path);
        var interruptedEvent = Assert.Single(afterRestart.List(), item => item.EventId == "plc-event-43");
        Assert.Equal(ProductionTriggerState.NeedsReview, interruptedEvent.State);
        Assert.DoesNotContain(afterRestart.List(), item => item.State == ProductionTriggerState.Pending);
        Assert.Null(afterRestart.TryBeginNext());
    }
}
