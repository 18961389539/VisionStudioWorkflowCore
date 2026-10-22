using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace VisionStudio.Api.Infrastructure;

/// <summary>
/// 轻量滚动文件日志：按天切分（visionstudio-yyyyMMdd.log），保留最近 N 天，UTF-8。
/// 现场故障后可直接从 data/logs 取日志导出；任何写入失败都被吞掉——日志绝不阻断主流程。
///
/// 健壮性（P2）：
/// - **运行期 prune**：启动后按天（惰性触发）重新清理过期文件，长时间运行的现场主机不会无限增长。
/// - **容量上限**：单条日志超长时截断（保留异常堆栈的头部），防止单条巨型日志撑爆磁盘。
/// - **有界异步写入**：`Channel` 有界队列 + 单个后台写线程；生产者（日志调用方）绝不阻塞主流程，
///   队列满时**丢弃**最旧/新条目而不是等待——日志永远不能反压业务线程。
/// - **异常链保留**：使用 `Exception.ToString()`（含类型、消息、完整堆栈与内部异常），而非仅 `Type: Message`。
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    /// <summary>单条格式化日志的最大字符数；超出部分截断（含明确截断标记）。</summary>
    internal const int MaxLineLength = 16 * 1024;

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly Channel<string> _queue;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _shutdown = new();
    private StreamWriter? _writer;
    private DateTime _lastPruneDate = DateTime.MinValue;

    // F10：日志可靠性计数——满队列丢弃、写入/刷盘失败、关闭排空超时都不得静默（经 /api/health 暴露）。
    private long _droppedEntries;
    private long _writeFailures;
    private long _flushFailures;
    private long _drainTimeouts;

    /// <summary>满队列被丢弃的日志条数（BoundedChannelFullMode.DropWrite）。</summary>
    public long DroppedEntries => Interlocked.Read(ref _droppedEntries);
    /// <summary>写入单条日志失败的次数（磁盘/句柄错误；写入循环不回退、不阻塞，但必须可观测）。</summary>
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    /// <summary>刷盘或释放写入句柄失败的次数。</summary>
    public long FlushFailures => Interlocked.Read(ref _flushFailures);
    /// <summary>关闭时 5 秒内未排空、触发兜底取消的次数。</summary>
    public long DrainTimeouts => Interlocked.Read(ref _drainTimeouts);

    public RollingFileLoggerProvider(string directory, int retentionDays = 14, int queueCapacity = 4096)
    {
        _directory = directory;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            // F10：Wait 模式下队列满时 TryWrite 返回 false（调用方立即丢弃并计数）。
            // 注意不能用 DropWrite——它会在内部静默丢弃且 TryWrite 仍返回 true，丢弃量无从感知。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        try
        {
            Directory.CreateDirectory(_directory);
            _lastPruneDate = DateTime.UtcNow.Date;
            Prune();
        }
        catch { /* logging setup must never break the host */ }
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(this, name));

    internal void Append(string line)
    {
        var payload = line.Length > MaxLineLength
            ? string.Concat(line.AsSpan(0, MaxLineLength), " …[truncated]")
            : line;
        // 非阻塞入队：队列满时立即丢弃并计数（日志不可反压业务线程），丢弃量经 /api/health 可见。
        if (!_queue.Writer.TryWrite(payload)) Interlocked.Increment(ref _droppedEntries);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            // 注意：关闭以 TryComplete() 收尾（正常路径会把已入队条目读干净后退出）；
            // 传入 token 仅作为 Dispose 超时后的**兜底**中断，正常运行绝不触发。
            while (await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var line))
                {
                    try { AppendCore(line); }
                    catch { Interlocked.Increment(ref _writeFailures); /* 单条写入失败不得终止整条写入循环，但必须可观测 */ }
                }
                // 队列排空后 flush 一次：既让现场 `tail -f` 能立刻看到日志，又避免每条都开关文件。
                FlushIfNeeded();
            }
        }
        catch (OperationCanceledException) { /* 兜底取消：正常关闭不走到这里 */ }
        finally { CloseWriter(); }
    }

    /// <summary>写入单条日志（内存缓冲，不落盘）；按天轮转文件。</summary>
    private void AppendCore(string line)
    {
        var today = DateTime.UtcNow.Date;
        if (today != _lastPruneDate)
        {
            // 跨天（或首次）：轮转到新文件并顺带清理过期日志。
            CloseWriter();
            _lastPruneDate = today;
            Prune();
        }
        EnsureWriter();
        _writer!.Write(line);
        _writer.Write(Environment.NewLine);
    }

    /// <summary>空闲/收尾时把缓冲刷到磁盘。</summary>
    private void FlushIfNeeded()
    {
        if (_writer is null) return;
        try { _writer.Flush(); }
        catch { Interlocked.Increment(ref _flushFailures); /* 刷盘失败不得影响写入循环，但必须可观测 */ }
    }

    private void EnsureWriter()
    {
        if (_writer is not null) return;
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"visionstudio-{_lastPruneDate:yyyyMMdd}.log");
        // 长驻句柄：现场 `tail -f` 通过 AutoFlush=true 立即可见；避免每条日志开关文件（吞吐差数量级）。
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    private void CloseWriter()
    {
        try { _writer?.Flush(); } catch { Interlocked.Increment(ref _flushFailures); }
        try { _writer?.Dispose(); } catch { Interlocked.Increment(ref _flushFailures); }
        _writer = null;
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "visionstudio-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["visionstudio-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day) && day < cutoff)
                File.Delete(file);
        }
    }

    public void Dispose()
    {
        // 关键：先 TryComplete()，写入线程会把队列读净后退出——保证关闭前的日志不丢。
        try { _queue.Writer.TryComplete(); } catch { }
        var drained = false;
        try { drained = _writerTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        // 兜底：若写入线程在异常路径卡死，用取消令牌强制退出，绝不让 Dispose 阻塞宿主关闭。
        if (!drained)
        {
            Interlocked.Increment(ref _drainTimeouts);
            try { _shutdown.Cancel(); } catch { }
            try { _writerTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
        // 双保险：无论写入线程是否正常退出，都确保缓冲落盘并释放文件句柄。
        CloseWriter();
        _shutdown.Dispose();
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Information 及以上（避免 Debug/Http 噪声撑爆现场日志）
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            // 保留完整异常链（类型 + 消息 + 堆栈 + InnerException），而非仅 Type: Message。
            if (exception is not null) message += $" | {exception}";
            provider.Append($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {category} - {message}");
        }
    }
}
