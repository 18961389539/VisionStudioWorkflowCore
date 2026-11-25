using TesseractOCR;
using TesseractOCR.Enums;

namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Caches Tesseract engines per tessdata directory, language list and engine mode. Building an
/// engine loads the .traineddata model, which dominates short inspection cycles; the pool keeps
/// a few warm engines and hands out a private instance when all are busy. Tesseract instances are
/// independent, so one engine per lease is safe across parallel branches. Every lease clears the
/// adaptive classifier and resets the character whitelist/blacklist/DPI before processing, so a result
/// never depends on the previous image or node configuration.
/// </summary>
internal static class OcrEnginePool
{
    private const int MaxKeys = 8;
    private const int MaxIdlePerKey = 2;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<TesseractOCR.Engine>> Idle = new(StringComparer.Ordinal);

    internal sealed class Lease : IDisposable
    {
        private readonly string _key;
        private bool _released;
        private bool _discarded;

        public TesseractOCR.Engine Engine { get; }
        public bool Reused { get; }

        internal Lease(string key, TesseractOCR.Engine engine, bool reused)
        {
            _key = key;
            Engine = engine;
            Reused = reused;
        }

        /// <summary>Keeps a failed engine out of the pool; its internal state is untrusted.</summary>
        public void Discard() => _discarded = true;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (_discarded) { Engine.Dispose(); return; }
            OcrEnginePool.Return(_key, Engine);
        }
    }

    public static Lease Rent(string dataPath, string language, EngineMode mode)
    {
        var key = $"{dataPath}\u001f{language}\u001f{mode}";
        lock (Gate)
        {
            if (Idle.TryGetValue(key, out var engines) && engines.Count > 0)
            {
                var pooled = engines[^1];
                engines.RemoveAt(engines.Count - 1);
                if (engines.Count == 0) Idle.Remove(key);
                return new Lease(key, pooled, reused: true);
            }
        }

        // Create outside the lock: model loading is the slow part and must not serialize
        // unrelated branches. A concurrent creator simply ends up as an idle spare.
        return new Lease(key, new TesseractOCR.Engine(dataPath, language, mode), reused: false);
    }

    /// <summary>Clears per-request overrides so a pooled engine behaves like a fresh one.</summary>
    public static void ResetForRequest(TesseractOCR.Engine engine, string whitelist, string blacklist, int dpi)
    {
        engine.ClearAdaptiveClassifier();
        engine.SetVariable("tessedit_char_whitelist", whitelist);
        engine.SetVariable("tessedit_char_blacklist", blacklist);
        engine.SetVariable("user_defined_dpi", dpi);
    }

    private static void Return(string key, TesseractOCR.Engine engine)
    {
        lock (Gate)
        {
            if (!Idle.TryGetValue(key, out var engines))
            {
                if (Idle.Count >= MaxKeys)
                {
                    // Admit a new warm configuration only after evicting an idle key at
                    // capacity. A cache miss alone must not evict another language/model.
                    var victim = Idle.First();
                    Idle.Remove(victim.Key);
                    foreach (var idleEngine in victim.Value) idleEngine.Dispose();
                }
                Idle[key] = engines = new List<TesseractOCR.Engine>(MaxIdlePerKey);
            }
            if (engines.Count >= MaxIdlePerKey) { engine.Dispose(); return; }
            engine.ClearAdaptiveClassifier();
            engines.Add(engine);
        }
    }
}
