using System.Text.Json;
using Microsoft.Extensions.Logging;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Api;

internal sealed class IntrinsicsWizardSessionRepository
{
    private readonly CalibrationIntrinsicsWizard.SessionStore _store = new();
    private readonly string _persistRoot;
    private readonly JsonSerializerOptions _readJson;
    private readonly JsonSerializerOptions _persistJson;
    private readonly ILogger? _logger;

    public IntrinsicsWizardSessionRepository(
        string persistRoot,
        JsonSerializerOptions readJson,
        JsonSerializerOptions persistJson,
        ILogger? logger)
    {
        _persistRoot = persistRoot;
        _readJson = readJson;
        _persistJson = persistJson;
        _logger = logger;
        Directory.CreateDirectory(_persistRoot);
        TryHydrateFromDisk();
    }

    public CalibrationIntrinsicsWizard.Session Create(CalibrationIntrinsicsWizard.BoardConfig board)
        => _store.Create(board);

    public CalibrationIntrinsicsWizard.Session CreateFromDocument(CalibrationIntrinsicsWizard.SessionDocument document)
        => _store.CreateFromDocument(document);

    public CalibrationIntrinsicsWizard.Session Resolve(string sessionId)
    {
        if (_store.TryGet(sessionId, out var live) && live is not null)
            return live;
        var path = SessionPath(sessionId);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"Calibration wizard session '{sessionId}' was not found or has expired.");
        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<CalibrationIntrinsicsWizard.SessionDocument>(json, _readJson)
                ?? throw new JsonException("empty");
            document = document with { SessionId = sessionId };
            return _store.CreateFromDocument(document);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            throw new KeyNotFoundException($"Calibration wizard session '{sessionId}' could not be restored from disk.", ex);
        }
    }

    public bool Delete(string sessionId)
    {
        var removed = _store.Remove(sessionId);
        var path = SessionPath(sessionId);
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Could not delete persisted intrinsics wizard session file {Path}.", path);
            }
        }
        return removed || !File.Exists(path);
    }

    public void Persist(CalibrationIntrinsicsWizard.Session session)
    {
        try
        {
            var document = session.ExportDocument();
            var path = SessionPath(session.SessionId);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(document, _persistJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Persistence is best-effort; in-memory session remains authoritative for this process.
            _logger?.LogWarning(ex, "Failed to persist intrinsics wizard session {SessionId}.", session.SessionId);
        }
    }

    private void TryHydrateFromDisk()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_persistRoot, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var document = JsonSerializer.Deserialize<CalibrationIntrinsicsWizard.SessionDocument>(json, _readJson);
                    if (document is null || _store.TryGet(document.SessionId, out _)) continue;
                    _store.CreateFromDocument(document);
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
                {
                    _logger?.LogWarning(ex, "Skipping unreadable intrinsics wizard session file {Path}.", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not enumerate persisted intrinsics wizard sessions under {Root}.", _persistRoot);
        }
    }

    private string SessionPath(string sessionId)
    {
        var safe = string.Concat((sessionId ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safe)) throw new ApiValidationException("Invalid session id.");
        return Path.Combine(_persistRoot, safe + ".json");
    }
}
