using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public enum AlarmSeverity { Info, Warning, Error, Critical }

/// <summary>报警分页结果；Total 是裁剪到 take 之前的条数。</summary>
public sealed record AlarmPage(IReadOnlyList<AlarmRecord> Items, int Total);

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
    /// <summary>
    /// 报警历史的保留上限（2026-10-26）。此前 alarms.json 只增不减——按每班 50 条估算，
    /// 半年就是数万条，读全量进内存会让接口和 GC 一起变慢。
    /// 规则：**活动报警永不裁剪**（未恢复的报警是现场依据），其余按 RaisedAt 保留最近的 N 条。
    /// </summary>
    public const int MaxRetainedRecords = 2000;

    /// <summary>列表接口的默认返回条数（未传 take 时）。</summary>
    public const int DefaultPageSize = 200;

    /// <summary>列表接口允许的最大 take，避免调用方一次拉全量。</summary>
    public const int MaxPageSize = 1000;

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

    /// <summary>
    /// 分页读取报警。返回分页结果与**裁剪前**的总数（供 X-Total-Count），
    /// take 会被夹在 [1, MaxPageSize]，skip 负数按 0 处理。
    /// </summary>
    public async Task<AlarmPage> ListAsync(bool activeOnly, int skip, int take, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadUnsafeAsync(ct);
            var filtered = items.Where(x => !activeOnly || x.Active).OrderByDescending(x => x.RaisedAt).ToArray();
            var effectiveSkip = Math.Max(0, skip);
            var effectiveTake = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);
            return new AlarmPage(filtered.Skip(effectiveSkip).Take(effectiveTake).ToArray(), filtered.Length);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 保留策略：活动报警全留，其余按时间倒序保留到上限。
    /// 抽成纯函数是为了能在单测里直接覆盖边界（不需要写 2000 次文件）。
    /// </summary>
    public static List<AlarmRecord> TrimToRetention(IEnumerable<AlarmRecord> items, int maxRetained = MaxRetainedRecords)
    {
        var all = items.ToList();
        if (all.Count <= maxRetained) return all;
        var active = all.Where(x => x.Active).ToList();
        var rest = all.Where(x => !x.Active).OrderByDescending(x => x.RaisedAt).ToList();
        var keepInactive = Math.Max(0, maxRetained - active.Count);
        return active.Concat(rest.Take(keepInactive)).ToList();
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
            await WriteUnsafeAsync(TrimToRetention(items), ct);
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
