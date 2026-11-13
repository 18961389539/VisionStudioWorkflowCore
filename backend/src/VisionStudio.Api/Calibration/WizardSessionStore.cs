using System.Collections.Concurrent;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

/// <summary>
/// Wizard session storage: in-memory authority plus best-effort disk persistence.
/// </summary>
/// <remarks>
/// The planar and SE(3) hand-eye wizards used two copy-pasted implementations of this logic (CRUD,
/// name/path sanitising, hydration, persistence). Besides the duplication, both wrote through
/// <c>File.Copy(tmp, path, overwrite: true)</c> followed by <c>File.Delete(tmp)</c>, which is not an
/// atomic replace: a crash between the copy and the delete can expose a partially written document.
/// This store writes the temp file and then <c>File.Move(tmp, path, overwrite: true)</c>, which is a
/// single atomic rename on NTFS.
/// </remarks>
internal sealed class WizardSessionStore<TDocument> where TDocument : class
{
    private readonly ConcurrentDictionary<string, TDocument> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly string _label;
    private readonly string _currentFormatVersion;
    private readonly Func<TDocument, string> _sessionId;
    private readonly Func<TDocument, string> _formatVersion;
    private readonly Func<TDocument, string> _updatedAt;
    private readonly Func<TDocument, string, TDocument> _withSessionId;
    private readonly JsonSerializerOptions _readJson;
    private readonly JsonSerializerOptions _writeJson;
    private readonly Action<string>? _warn;

    public WizardSessionStore(
        string root,
        string label,
        string currentFormatVersion,
        Func<TDocument, string> sessionId,
        Func<TDocument, string> formatVersion,
        Func<TDocument, string> updatedAt,
        Func<TDocument, string, TDocument> withSessionId,
        JsonSerializerOptions readJson,
        JsonSerializerOptions writeJson,
        Action<string>? warn = null)
    {
        _root = root;
        _label = label;
        _currentFormatVersion = currentFormatVersion;
        _sessionId = sessionId;
        _formatVersion = formatVersion;
        _updatedAt = updatedAt;
        _withSessionId = withSessionId;
        _readJson = readJson;
        _writeJson = writeJson;
        _warn = warn;
        Directory.CreateDirectory(_root);
        Hydrate();
    }

    public IReadOnlyList<TDocument> All()
        => _sessions.Values.OrderByDescending(d => _updatedAt(d), StringComparer.Ordinal).ToList();

    public TDocument Get(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var live)) return live;
        var path = PathFor(sessionId);
        if (!File.Exists(path)) throw new KeyNotFoundException($"{_label} session '{sessionId}' was not found.");
        try
        {
            var document = JsonSerializer.Deserialize<TDocument>(File.ReadAllText(path), _readJson)
                ?? throw new JsonException("empty");
            document = _withSessionId(document, sessionId);
            _sessions[sessionId] = document;
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new KeyNotFoundException($"{_label} session '{sessionId}' could not be restored from disk.", ex);
        }
    }

    public TDocument Put(TDocument document)
    {
        _sessions[_sessionId(document)] = document;
        Persist(document);
        return document;
    }

    public bool Delete(string sessionId)
    {
        var removed = _sessions.TryRemove(sessionId, out _);
        try
        {
            var path = PathFor(sessionId);
            if (File.Exists(path)) { File.Delete(path); removed = true; }
        }
        catch (IOException)
        {
            // In-memory removal still counts; disk cleanup is best-effort.
        }
        return removed;
    }

    private void Persist(TDocument document)
    {
        try
        {
            var path = PathFor(_sessionId(document));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(document, _writeJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Persistence is best-effort; the in-memory session remains authoritative for this process.
            _warn?.Invoke($"{_label} session persistence failed: {ex.Message}");
        }
    }

    private void Hydrate()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            {
                try
                {
                    var document = JsonSerializer.Deserialize<TDocument>(File.ReadAllText(file), _readJson);
                    if (document is null || string.IsNullOrWhiteSpace(_sessionId(document))) continue;
                    if (!string.Equals(_formatVersion(document), _currentFormatVersion, StringComparison.Ordinal))
                    {
                        // Silently dropping these used to look like "my sessions vanished after an upgrade".
                        _warn?.Invoke($"Skipped {_label} session file '{Path.GetFileName(file)}' with format '{_formatVersion(document)}' (current '{_currentFormatVersion}').");
                        continue;
                    }
                    _sessions.TryAdd(_sessionId(document), document);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    _warn?.Invoke($"Skipped unreadable {_label} session file '{Path.GetFileName(file)}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Could not scan {_label} session directory '{_root}': {ex.Message}");
        }
    }

    private string PathFor(string sessionId)
    {
        var safe = string.Concat((sessionId ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safe)) throw new ApiValidationException("Invalid session id.");
        return Path.Combine(_root, safe + ".json");
    }
}
