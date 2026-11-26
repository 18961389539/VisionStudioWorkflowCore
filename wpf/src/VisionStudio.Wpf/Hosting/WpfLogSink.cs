using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace VisionStudio.Wpf.Hosting;

/// <summary>
/// 供 WPF 界面实时展示的日志条目。
/// </summary>
public sealed record WpfLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string? ExceptionText);

/// <summary>
/// Serilog 内存环形日志接收器：在写滚动文件的同时向桌面端诊断日志面板广播最新日志。
/// </summary>
public sealed class WpfLogSink : ILogEventSink
{
    private const int MaxEntries = 500;
    private readonly ConcurrentQueue<WpfLogEntry> _entries = new();

    public event Action<WpfLogEntry>? EntryReceived;

    public IReadOnlyList<WpfLogEntry> Snapshot() => _entries.ToArray();

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }

    public void Emit(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "跟踪",
            LogEventLevel.Debug => "调试",
            LogEventLevel.Information => "信息",
            LogEventLevel.Warning => "警告",
            LogEventLevel.Error => "错误",
            LogEventLevel.Fatal => "致命",
            _ => logEvent.Level.ToString()
        };

        var source = logEvent.Properties.TryGetValue("SourceContext", out var srcVal)
            ? srcVal.ToString().Trim('"')
            : "VisionStudio";
        var dot = source.LastIndexOf('.');
        if (dot >= 0 && dot < source.Length - 1)
        {
            source = source[(dot + 1)..];
        }

        var entry = new WpfLogEntry(
            logEvent.Timestamp,
            level,
            source,
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }

        EntryReceived?.Invoke(entry);
    }
}
