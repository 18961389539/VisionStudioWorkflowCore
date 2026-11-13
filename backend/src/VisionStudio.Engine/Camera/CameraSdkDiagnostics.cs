using System.Collections.Concurrent;

namespace VisionStudio.Engine.Camera;

/// <summary>厂商 SDK 静默失败台账的一项；按 (driver, operation) 聚合。</summary>
public sealed record CameraSdkDiagnostic(
    string Driver,
    string Operation,
    long Failures,
    string? LastError,
    DateTimeOffset? LastFailureAt);

/// <summary>
/// 相机适配层的失败计数（2026-11-13）。
///
/// 适配层的 `catch { }` 多发生在清理与能力探测路径：单次失败不值得中断采集，但完全静默会让
/// "设备没释放""可选能力读不到"只能靠现场猜。这里只记录**真实异常**——"可选成员不存在"这类
/// 正常探测分支仍直接返回——按 (driver, operation) 聚合，经 `/api/health` 的 cameraSdk 字段暴露。
/// </summary>
public static class CameraSdkDiagnostics
{
    private sealed class Entry
    {
        public long Failures;
        public string? LastError;
        public DateTimeOffset? LastFailureAt;
    }

    private static readonly ConcurrentDictionary<(string Driver, string Operation), Entry> Entries = new();

    public static void RecordFailure(string driver, string operation, Exception error)
    {
        if (string.IsNullOrWhiteSpace(driver) || string.IsNullOrWhiteSpace(operation)) return;
        var entry = Entries.GetOrAdd((driver, operation), static _ => new Entry());
        lock (entry)
        {
            entry.Failures++;
            entry.LastError = error.Message;
            entry.LastFailureAt = DateTimeOffset.UtcNow;
        }
    }

    public static IReadOnlyList<CameraSdkDiagnostic> Snapshot()
        => Entries
            .Select(pair =>
            {
                lock (pair.Value)
                    return new CameraSdkDiagnostic(pair.Key.Driver, pair.Key.Operation, pair.Value.Failures, pair.Value.LastError, pair.Value.LastFailureAt);
            })
            .OrderBy(x => x.Driver, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Operation, StringComparer.Ordinal)
            .ToArray();
}
