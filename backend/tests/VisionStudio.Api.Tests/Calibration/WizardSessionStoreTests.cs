using System.Text.Json;

namespace VisionStudio.Api.Tests;

public sealed class WizardSessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vs-wizard-store-{Guid.NewGuid():N}");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record TestSession(string SessionId, string FormatVersion, string UpdatedAt, int Sequence);

    private WizardSessionStore<TestSession> CreateStore(int maxRetainedSessions = 200, TimeSpan? retention = null, List<string>? warnings = null)
        => new(
            _root,
            "TestWizard",
            "1",
            s => s.SessionId,
            s => s.FormatVersion,
            s => s.UpdatedAt,
            (s, id) => s with { SessionId = id },
            Json,
            Json,
            warnings is null ? null : warnings.Add,
            maxRetainedSessions,
            retention);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ConcurrentPut_SameSession_IsAtomicAndLeavesNoTempFiles()
    {
        var warnings = new List<string>();
        var store = CreateStore(warnings: warnings);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(1, 24).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            store.Put(new TestSession("shared-session", "1", DateTimeOffset.UtcNow.ToString("O"), i));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Empty(warnings);
        var tmpFiles = Directory.GetFiles(_root, "*.tmp");
        Assert.Empty(tmpFiles);
        var loaded = store.Get("shared-session");
        Assert.InRange(loaded.Sequence, 1, 24);
    }

    [Fact]
    public void PutAndHydrate_PruneExpiredAndOverflowSessions()
    {
        var store = CreateStore(maxRetainedSessions: 2, retention: TimeSpan.FromDays(30));
        var now = DateTimeOffset.UtcNow;

        store.Put(new TestSession("expired", "1", now.AddDays(-45).ToString("O"), 1));
        store.Put(new TestSession("older", "1", now.AddDays(-2).ToString("O"), 2));
        store.Put(new TestSession("newer", "1", now.AddDays(-1).ToString("O"), 3));
        store.Put(new TestSession("newest", "1", now.ToString("O"), 4));

        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, x => x.SessionId == "expired");
        Assert.DoesNotContain(all, x => x.SessionId == "older");
        Assert.Contains(all, x => x.SessionId == "newer");
        Assert.Contains(all, x => x.SessionId == "newest");

        var rehydrated = CreateStore(maxRetainedSessions: 2, retention: TimeSpan.FromDays(30));
        Assert.Equal(2, rehydrated.All().Count);
        Assert.False(File.Exists(Path.Combine(_root, "expired.json")));
        Assert.False(File.Exists(Path.Combine(_root, "older.json")));
    }
}
