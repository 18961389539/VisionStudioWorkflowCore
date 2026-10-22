using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public enum AlarmSeverity { Info, Warning, Error, Critical }

public sealed record AlarmRecord(
    string Id,
    string Code,
    AlarmSeverity Severity,
    string Source,
    string Message,
    bool Active,
    bool Acknowledged,
    DateTimeOffset RaisedAt,
    DateTimeOffset? RecoveredAt = null,
    DateTimeOffset? AcknowledgedAt = null,
    string? RunId = null);

public sealed class AlarmStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AlarmStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "production");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "alarms.json");
    }

    public async Task<IReadOnlyList<AlarmRecord>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            return items.Where(x => !activeOnly || x.Active).OrderByDescending(x => x.RaisedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord> RaiseAsync(string code, AlarmSeverity severity, string source, string message, string? runId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await ReadUnsafeAsync(ct)).ToList();
            var existingIndex = items.FindIndex(x => x.Active && x.Code.Equals(code, StringComparison.OrdinalIgnoreCase) && x.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
            var alarm = new AlarmRecord(Guid.NewGuid().ToString("N"), code, severity, source, message, true, false, DateTimeOffset.UtcNow, null, null, runId);
            if (existingIndex >= 0) items[existingIndex] = alarm;
            else items.Add(alarm);
            await WriteUnsafeAsync(items, ct);
            return alarm;
        }
        finally { _gate.Release(); }
    }

    public async Task RecoverAsync(string code, string source, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var items = (await ReadUnsafeAsync(ct)).Select(x =>
                x.Active && x.Code.Equals(code, StringComparison.OrdinalIgnoreCase) && x.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
                    ? x with { Active = false, RecoveredAt = now }
                    : x).ToList();
            await WriteUnsafeAsync(items, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord> AcknowledgeAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await ReadUnsafeAsync(ct)).ToList();
            var index = items.FindIndex(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ApiNotFoundException($"Alarm '{id}' does not exist.");
            items[index] = items[index] with { Acknowledged = true, AcknowledgedAt = DateTimeOffset.UtcNow };
            await WriteUnsafeAsync(items, ct);
            return items[index];
        }
        finally { _gate.Release(); }
    }

    private async Task<List<AlarmRecord>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<AlarmRecord>>(stream, _json, ct) ?? [];
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private async Task WriteUnsafeAsync(List<AlarmRecord> items, CancellationToken ct)
    {
        // A fixed ".tmp" name collides when multiple AlarmStore instances (runtime service,
        // diagnostics API, tests) share one path: both write the same temp file and one
        // Move loses the race with UnauthorizedAccessException. Use a per-write temp file.
        var temp = Path.Combine(Path.GetDirectoryName(_path)!, $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, items, _json, ct);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, _path, true);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(10 * attempt, ct);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
