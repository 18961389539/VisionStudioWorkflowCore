using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Api;

public enum ProductionTriggerState
{
    Pending,
    Processing,
    Completed,
    Failed,
    NeedsReview
}

public sealed record ProductionTriggerEvent(
    string EventId,
    DateTimeOffset AcceptedAt,
    ProductionTriggerState State,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? RunId = null,
    string? Detail = null);

public sealed record ProductionTriggerEnqueueResult(ProductionTriggerEvent Event, bool IsNew);

public sealed class ProductionTriggerQueue
{
    private const int MaxRetainedEvents = 10_000;
    private const int MaxPendingEvents = 1_000;
    private const int JournalCompactionThreshold = 256;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly string _journalPath;
    private readonly object _sync = new();
    private readonly List<ProductionTriggerEvent> _events;
    private int _journalEntriesSinceCompaction;

    public ProductionTriggerQueue(string path)
    {
        _path = Path.GetFullPath(path);
        _journalPath = _path + ".journal";
        _events = Load(out var replayedJournal);
        var recovered = false;
        for (var index = 0; index < _events.Count; index++)
        {
            if (_events[index].State != ProductionTriggerState.Processing) continue;
            _events[index] = _events[index] with
            {
                State = ProductionTriggerState.NeedsReview,
                CompletedAt = DateTimeOffset.UtcNow,
                Detail = "The host stopped before this accepted trigger was durably completed. It was not replayed automatically."
            };
            recovered = true;
        }
        if (recovered || replayedJournal) PersistSnapshot();
    }

    public string StatePath => _path;

    public IReadOnlyList<ProductionTriggerEvent> List()
    {
        lock (_sync) return _events.OrderByDescending(item => item.AcceptedAt).ToArray();
    }

    public ProductionTriggerEnqueueResult Enqueue(string? eventId = null)
    {
        lock (_sync)
        {
            var normalizedId = string.IsNullOrWhiteSpace(eventId) ? Guid.NewGuid().ToString("N") : eventId.Trim();
            if (normalizedId.Length > 128 || normalizedId.Any(char.IsControl))
                throw new Infrastructure.ApiValidationException("Trigger event ID must be at most 128 characters and contain no control characters.");
            var existing = _events.FirstOrDefault(item => string.Equals(item.EventId, normalizedId, StringComparison.Ordinal));
            if (existing is not null) return new ProductionTriggerEnqueueResult(existing, false);

            var pruned = PruneExpired();
            if (_events.Count(x => x.State is ProductionTriggerState.Pending or ProductionTriggerState.Processing) >= MaxPendingEvents)
                throw new Infrastructure.ApiConflictException($"The production trigger queue is full (limit {MaxPendingEvents}).");
            if (_events.Count >= MaxRetainedEvents)
                throw new Infrastructure.ApiConflictException($"The production trigger history is full (limit {MaxRetainedEvents}); archive it before accepting more triggers.");

            var accepted = new ProductionTriggerEvent(normalizedId, DateTimeOffset.UtcNow, ProductionTriggerState.Pending);
            _events.Add(accepted);
            if (pruned) PersistSnapshot();
            else AppendJournalEntry(accepted);
            return new ProductionTriggerEnqueueResult(accepted, true);
        }
    }

    public ProductionTriggerEvent? TryBeginNext()
    {
        lock (_sync)
        {
            var index = _events.FindIndex(item => item.State == ProductionTriggerState.Pending);
            if (index < 0) return null;
            var processing = _events[index] with { State = ProductionTriggerState.Processing, StartedAt = DateTimeOffset.UtcNow };
            _events[index] = processing;
            AppendJournalEntry(processing);
            return processing;
        }
    }

    public void Complete(string eventId, string runId)
        => Transition(eventId, ProductionTriggerState.Completed, runId, null);

    public void Fail(string eventId, string runId, string detail)
        => Transition(eventId, ProductionTriggerState.Failed, runId, detail);

    public void MarkNeedsReview(string eventId, string? runId, string detail)
        => Transition(eventId, ProductionTriggerState.NeedsReview, runId, detail);

    private void Transition(string eventId, ProductionTriggerState next, string? runId, string? detail)
    {
        lock (_sync)
        {
            var index = _events.FindIndex(item => string.Equals(item.EventId, eventId, StringComparison.Ordinal));
            if (index < 0) throw new InvalidOperationException($"Production trigger '{eventId}' was not found.");
            if (_events[index].State != ProductionTriggerState.Processing)
                throw new InvalidOperationException($"Production trigger '{eventId}' cannot transition from {_events[index].State} to {next}.");
            var updated = _events[index] with
            {
                State = next,
                CompletedAt = DateTimeOffset.UtcNow,
                RunId = runId,
                Detail = detail
            };
            _events[index] = updated;
            AppendJournalEntry(updated);
        }
    }

    private List<ProductionTriggerEvent> Load(out bool replayedJournal)
    {
        replayedJournal = false;
        try
        {
            List<ProductionTriggerEvent> events = [];
            if (File.Exists(_path))
            {
                events = JsonSerializer.Deserialize<List<ProductionTriggerEvent>>(File.ReadAllText(_path), Json)
                         ?? throw new InvalidDataException("Trigger queue JSON deserialized to null.");
                if (events.Any(item => string.IsNullOrWhiteSpace(item.EventId) || item.EventId.Length > 128) ||
                    events.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != events.Count)
                    throw new InvalidDataException("Trigger queue contains invalid or duplicate event IDs.");
            }

            if (File.Exists(_journalPath))
            {
                var byId = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < events.Count; i++) byId[events[i].EventId] = i;

                var lines = File.ReadAllLines(_journalPath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize<ProductionTriggerEvent>(line, Json)
                                ?? throw new InvalidDataException("Trigger queue journal entry deserialized to null.");
                    if (string.IsNullOrWhiteSpace(entry.EventId) || entry.EventId.Length > 128)
                        throw new InvalidDataException("Trigger queue journal contains an invalid event ID.");
                    if (byId.TryGetValue(entry.EventId, out var existingIndex))
                        events[existingIndex] = entry;
                    else
                    {
                        byId[entry.EventId] = events.Count;
                        events.Add(entry);
                    }
                    replayedJournal = true;
                }
            }
            return events;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Production trigger queue at '{_path}' could not be read; accepting triggers is blocked to avoid losing accepted events.", ex);
        }
    }

    private bool PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        return _events.RemoveAll(item =>
            item.State is ProductionTriggerState.Completed or ProductionTriggerState.Failed or ProductionTriggerState.NeedsReview &&
            item.CompletedAt is { } completedAt && completedAt < cutoff) > 0;
    }

    private void AppendJournalEntry(ProductionTriggerEvent entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        using (var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        _journalEntriesSinceCompaction++;
        if (_journalEntriesSinceCompaction >= JournalCompactionThreshold)
            PersistSnapshot();
    }

    private void PersistSnapshot()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, _events, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, true);
        if (File.Exists(_journalPath)) File.Delete(_journalPath);
        _journalEntriesSinceCompaction = 0;
    }
}
