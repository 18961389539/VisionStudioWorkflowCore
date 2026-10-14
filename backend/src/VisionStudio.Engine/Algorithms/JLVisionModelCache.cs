namespace VisionStudio.Engine.Algorithms;

/// <summary>Bounded native-model cache. An active model is never disposed while a matcher uses it.</summary>
internal static class JLVisionModelCache
{
    private const int Capacity = 16;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new();
    private sealed class Entry(IDisposable model)
    {
        public readonly IDisposable Model = model;
        public int Users;
        public long LastUse;
        public bool Cached;
    }
    internal sealed class Lease : IDisposable
    {
        private Action? _release;
        public IDisposable Model { get; }
        public bool Hit { get; }
        public Lease(IDisposable model, bool hit, Action release) { Model = model; Hit = hit; _release = release; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    public static Lease Acquire(string key, Func<IDisposable> create)
    {
        lock (Gate)
        {
            var hit = Entries.TryGetValue(key, out var entry);
            if (!hit)
            {
                if (Entries.Count >= Capacity)
                {
                    var idle = Entries.Where(e => e.Value.Users == 0).OrderBy(e => e.Value.LastUse).FirstOrDefault();
                    if (idle.Value is not null) { Entries.Remove(idle.Key); idle.Value.Cached = false; idle.Value.Model.Dispose(); }
                }
                entry = new(create()) { Cached = Entries.Count < Capacity };
                if (entry.Cached) Entries.Add(key, entry);
            }
            entry!.Users++; entry.LastUse = Environment.TickCount64;
            return new(entry.Model, hit, () => Release(entry));
        }
    }
    private static void Release(Entry entry)
    {
        lock (Gate) { entry.Users--; if (entry.Users == 0 && !entry.Cached) entry.Model.Dispose(); }
    }
    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var entry in Entries.Values) { entry.Cached = false; if (entry.Users == 0) entry.Model.Dispose(); }
            Entries.Clear();
        }
    }
}
