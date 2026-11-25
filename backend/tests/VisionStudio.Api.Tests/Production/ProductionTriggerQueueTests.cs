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

    [Fact]
    public void TriggerQueue_JournalsIncrementalTransitions_AndCompactsAtThreshold()
    {
        using var env = new TempWebHostEnvironment();
        var path = Path.Combine(env.ContentRootPath, "data", "production", "triggers.json");
        var journalPath = path + ".journal";
        var queue = new ProductionTriggerQueue(path);

        // 130 enqueue + 130 begin + 130 complete = 390 journal writes (> compaction threshold 256).
        for (var i = 0; i < 130; i++)
        {
            var id = $"evt-{i:D3}";
            Assert.True(queue.Enqueue(id).IsNew);
            var started = queue.TryBeginNext();
            Assert.NotNull(started);
            queue.Complete(started!.EventId, $"run-{i:D3}");
        }

        // Compaction occurred at 256 entries, leaving fewer than 256 entries in the active journal.
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(journalPath));
        Assert.True(File.ReadAllLines(journalPath).Length < 256);

        // Reopening replays the remaining journal entries cleanly.
        var reopened = new ProductionTriggerQueue(path);
        var events = reopened.List();
        Assert.Equal(130, events.Count);
        Assert.All(events, item => Assert.Equal(ProductionTriggerState.Completed, item.State));
    }
}
