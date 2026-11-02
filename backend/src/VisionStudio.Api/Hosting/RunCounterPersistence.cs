using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Hosting;

/// <summary>计数器落盘的可观测状态（诊断端点用）。</summary>
/// <param name="SnapshotPath">快照文件路径。</param>
/// <param name="CounterCount">当前内存中的计数器条数。</param>
/// <param name="LastWriteAt">最近一次成功写盘时间；从未成功写过为 null。</param>
/// <param name="ConsecutiveFailures">连续写盘失败次数；成功一次即归零。</param>
/// <param name="LastError">最近一次失败的原因；恢复后清空。</param>
public sealed record RunCounterPersistenceStatus(
    string SnapshotPath,
    int CounterCount,
    DateTimeOffset? LastWriteAt,
    int ConsecutiveFailures,
    string? LastError)
{
    /// <summary>
    /// 连续写盘失败中：计数仍在内存里累加，但**磁盘上的版本已经落后**。
    /// 这正是"重启后良率对不上"的前兆——必须在界面上看见，而不是只躺在日志里。
    /// </summary>
    public bool Degraded => ConsecutiveFailures > 0;
}

/// <summary>
/// 计数器落盘的运行参数（2026-10-29）。集中在这里，避免构造函数里散落魔法数字。
/// </summary>
/// <param name="FlushInterval">
/// 快照采样间隔。默认 5s：value.counter 每轮都可能执行（几千次/小时），
/// 间隔太短会把 IO 抖动带到节拍上，太长则断电丢的增量变多。
/// </param>
/// <param name="Retention">
/// 计数器保留期。超过这个时间没被任何节点访问的条目，在启动时被回收——
/// 流程改名/删除后旧 scope 的计数器会永远留在快照里，跑一年就积成一本没人读的账。
/// </param>
public sealed record RunCounterPersistenceOptions(TimeSpan FlushInterval, TimeSpan Retention)
{
    /// <summary>默认参数：5s 采样、30 天保留。</summary>
    public static RunCounterPersistenceOptions Default { get; } =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromDays(30));

    /// <summary>
    /// 从配置读取（<c>RunCounters:FlushIntervalSeconds</c> / <c>RunCounters:RetentionDays</c>）。
    ///
    /// 缺省、非数字、越界一律回落到默认值并保持可启动：计数器是辅助信息，
    /// 不值得为一条写错的配置让整条产线起不来。上限同理——<see cref="PeriodicTimer"/>
    /// 对超大周期会抛 <see cref="ArgumentOutOfRangeException"/>，不能把配置错误变成启动崩溃。
    /// </summary>
    public static RunCounterPersistenceOptions FromConfiguration(IConfiguration? configuration)
    {
        if (configuration is null) return Default;
        return new RunCounterPersistenceOptions(
            TimeSpan.FromSeconds(ReadNumber(configuration["RunCounters:FlushIntervalSeconds"], 5, 1, 3600)),
            TimeSpan.FromDays(ReadNumber(configuration["RunCounters:RetentionDays"], 30, 1, 3650)));
    }

    private static double ReadNumber(string? raw, double fallback, double min, double max)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
           && value >= min && value <= max
            ? value
            : fallback;
}

/// <summary>
/// 跨轮计数器的落盘（2026-10-29）。<see cref="InMemoryRunCounterStore"/> 本身只是进程内状态，
/// 停机就丢——"本班次良率""连续 3 件 NG"这类数字一旦跨班次清零，现场会直接怀疑视觉算错。
///
/// 策略（刻意保守）：
/// - 启动时读 <c>data/runtime/counters.json</c> 并恢复，文件损坏只记日志不阻断启动；
/// - 启动时按 <see cref="RunCounterPersistenceOptions.Retention"/> 回收长期没被访问的孤儿计数器；
/// - 运行期每 <see cref="RunCounterPersistenceOptions.FlushInterval"/>（默认 5s）取一次快照，
///   内容有变化才写；写临时文件、刷盘后再原子替换；
/// - 停机时补写一次最终快照（StopAsync），用独立预算避免被 shutdown 令牌掐断；
/// - 任何 IO 异常都不影响主流程（计数是辅助信息，不能让采集停线），但会**节流**记日志，
///   并通过 <see cref="Status"/> 暴露"连续失败/最后成功时间"，供诊断端点读取。
///
/// 为什么不让节点自己写盘：value.counter 每轮都可能执行（几千次/小时），节点内同步写盘
/// 会把 IO 抖动直接带到节拍上。快照+节流把写盘次数压到每个采样窗口最多一次。
/// </summary>
public sealed class RunCounterPersistence : BackgroundService
{
    /// <summary>
    /// 停机补写的独立预算。刻意不复用宿主的 shutdown 令牌：那个令牌在 ShutdownTimeout 到期
    /// 时取消，而"关得慢"往往正是因为 IO 慢——用同一个令牌补写，恰好会在最需要写成功的
    /// 时刻抛取消：既丢增量，又让 StopAsync 冒泡取消异常、在宿主日志里记成一次关闭失败。
    /// </summary>
    private static readonly TimeSpan FinalFlushTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 持续失败时的日志节流间隔。首次失败立即记；之后每分钟最多一条。
    /// 用时间而不是"每 N 次"是因为写盘间隔已可配置（<c>RunCounters:FlushIntervalSeconds</c>），
    /// 按次数节流在长间隔下会变成几乎不记日志。磁盘满这类故障会持续数小时，不节流会冲掉有用日志。
    /// </summary>
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>只依赖抽象：换存储实现（SQLite/Redis）时本类不需要改签名。</summary>
    private readonly IRunCounterStore _store;
    private readonly ILogger<RunCounterPersistence> _logger;
    private readonly AlarmStore? _alarms;
    private readonly string _path;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _retention;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();

    /// <summary>落盘持续失败时上报的告警码；恢复时用它做 Recover。</summary>
    public const string DegradedAlarmCode = "RUN_COUNTER_PERSIST_DEGRADED";

    private DateTimeOffset _lastFailureLogAt;

    /// <summary>上次成功写盘的内容；相同则跳过写入（计数器不动时零 IO）。</summary>
    private string _lastWritten = string.Empty;

    private DateTimeOffset? _lastWriteAt;
    private int _consecutiveFailures;
    private string? _lastError;

    public RunCounterPersistence(
        IRunCounterStore store,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<RunCounterPersistence> logger,
        AlarmStore alarms)
        : this(
            store,
            ResolveSnapshotPath(environment),
            logger,
            alarms,
            RunCounterPersistenceOptions.FromConfiguration(configuration))
    {
    }

    /// <summary>
    /// 测试专用：显式指定快照文件路径与运行参数。
    ///
    /// 不能让测试复用生产构造函数——<see cref="VisionStudioDataRoot.Resolve"/> 会**优先采用**
    /// <c>VISIONSTUDIO_DATA_ROOT</c> 环境变量而非传入的 ContentRootPath。测试进程一旦带着该变量
    /// （质量门禁正是这样跑的），用例就会写进与并发宿主用例共享的数据根，互相污染计数与目录状态。
    /// </summary>
    internal RunCounterPersistence(
        IRunCounterStore store,
        string snapshotPath,
        ILogger<RunCounterPersistence> logger,
        AlarmStore? alarms = null,
        RunCounterPersistenceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        var effective = options ?? RunCounterPersistenceOptions.Default;
        _store = store;
        _logger = logger;
        _alarms = alarms;
        _path = Path.GetFullPath(snapshotPath);
        _flushInterval = effective.FlushInterval;
        _retention = effective.Retention;
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    private static string ResolveSnapshotPath(IWebHostEnvironment environment)
        => Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath), "runtime", "counters.json");

    /// <summary>当前落盘状态；供诊断端点读取，不参与写盘路径。</summary>
    public RunCounterPersistenceStatus Status()
    {
        // 先取快照、再进锁，顺序不能反：FlushAsync 是 CounterState 锁（Snapshot 内部）
        // → _statusLock（MarkWriteSucceeded/Failed），这里若先拿 _statusLock 再调 Snapshot()，
        // 两条路径就以相反顺序请求同一组锁，是标准的死锁配方。
        // 代价只是"条数"与"失败次数"可能相差一次采样，对诊断展示没有影响。
        var counterCount = _store.Snapshot().Count;
        lock (_statusLock)
        {
            return new RunCounterPersistenceStatus(
                _path,
                counterCount,
                _lastWriteAt,
                _consecutiveFailures,
                _lastError);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();

        using var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // 停机补写：最后一个采样窗口内的增量不能丢。用独立预算（见 FinalFlushTimeout），
        // 超时只放弃本次补写并留痕，不让"关得慢"演变成"关出异常"。
        using var budget = new CancellationTokenSource(FinalFlushTimeout);
        try
        {
            await FlushAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Final run counter flush did not finish within {Timeout}; up to one {Interval} window of counts may be lost.",
                FinalFlushTimeout, _flushInterval);
        }
    }

    /// <summary>
    /// 读取并恢复快照；文件不存在属正常首启，损坏则记日志后按空状态继续。
    /// 恢复前先按保留期裁掉孤儿条目，并把裁剪结果写回磁盘。
    /// </summary>
    public void Load()
    {
        try
        {
            // 先清掉上次被强杀时遗留的半成品临时文件。
            // 独立于下面的恢复逻辑：清理失败只是"少清一次"，不能被当成"恢复失败"。
            try
            {
                var swept = RunCounterSnapshotFile.DeleteAbandonedTemporaries(_path, TimeSpan.FromHours(1));
                if (swept > 0)
                {
                    _logger.LogInformation(
                        "Removed {Count} abandoned run counter snapshot temporary file(s) next to {Path}.",
                        swept, _path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not sweep abandoned run counter temporaries next to {Path}.", _path);
            }

            var snapshots = RunCounterSnapshotFile.Load(_path);
            if (snapshots.Count == 0) return;

            var (kept, reclaimed) = ApplyRetention(snapshots);
            _store.Restore(kept);

            // 记下"磁盘上现在是什么"：首轮 Flush 若发现内容没变就不必再写一次（每次重启省一次 IO）。
            // 注意这里不更新 _lastWriteAt——读盘不是写盘，硬说成"刚写过"会让降级判断失真。
            _lastWritten = RunCounterSnapshotFile.Serialize(kept);

            if (reclaimed > 0) PersistReclaimed(kept, reclaimed);

            _logger.LogInformation(
                "Restored {Count} run counters from {Path} ({Reclaimed} idle counter(s) reclaimed).",
                kept.Count, _path, reclaimed);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Run counter snapshot at {Path} could not be restored; starting from zero.", _path);
        }
    }

    /// <summary>
    /// 丢掉超过保留期没被访问的计数器。
    ///
    /// 为什么要在启动时做：这是唯一能安全删除条目的时刻。运行期删会和正在累加的
    /// 班次统计抢同一条记录，而"这个 scope 已经没人用了"这件事本身只有重启才可判定。
    /// </summary>
    private (IReadOnlyList<RunCounterSnapshot> Kept, int Reclaimed) ApplyRetention(
        IReadOnlyList<RunCounterSnapshot> snapshots)
    {
        if (_retention <= TimeSpan.Zero) return (snapshots, 0);

        var cutoff = DateTimeOffset.UtcNow - _retention;
        var kept = new List<RunCounterSnapshot>(snapshots.Count);
        var reclaimed = 0;
        foreach (var snapshot in snapshots)
        {
            // 旧版快照没有 LastTouchedAt（default）：无法判定年龄，一律保留，不做静默丢弃。
            // 这些条目在恢复后的首次 GetOrAdd 就会被盖上时间戳，下一轮启动才有机会被回收。
            if (snapshot.LastTouchedAt != default && snapshot.LastTouchedAt < cutoff)
            {
                reclaimed++;
                continue;
            }

            kept.Add(snapshot);
        }

        return reclaimed == 0 ? (snapshots, 0) : (kept, reclaimed);
    }

    /// <summary>
    /// 把裁剪后的集合写回磁盘。不写回的话孤儿每次重启都会被重新读到、再裁一遍——
    /// 内存里干净了，文件只增不减，现场翻 counters.json 依然看到一堆废条目。
    /// 独立 try/catch：写回失败只是一次多余的重算，不能把已经成功的恢复也算成失败。
    /// </summary>
    private void PersistReclaimed(IReadOnlyList<RunCounterSnapshot> kept, int reclaimed)
    {
        try
        {
            RunCounterSnapshotFile.Save(_path, kept);
            lock (_statusLock)
            {
                _lastWriteAt = DateTimeOffset.UtcNow;
            }

            _logger.LogInformation(
                "Reclaimed {Reclaimed} run counter(s) untouched for over {Days:0.#} day(s); {Remaining} remain in {Path}.",
                reclaimed, _retention.TotalDays, kept.Count, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Reclaimed {Reclaimed} idle run counter(s) in memory but could not rewrite {Path}; they will be re-evaluated on the next start.",
                reclaimed, _path);

            // 内存已裁剪、磁盘没裁剪，所以 _lastWritten 不能再声称"磁盘内容和内存一致"，
            // 否则下一次 Flush 会误判"内容没变"而永久跳过写盘，孤儿一直留在文件里。
            _lastWritten = string.Empty;
        }
    }

    /// <summary>内容有变化才写盘；原子替换由 <see cref="RunCounterSnapshotFile"/> 负责。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshots = _store.Snapshot();
            var json = RunCounterSnapshotFile.Serialize(snapshots);
            if (string.Equals(json, _lastWritten, StringComparison.Ordinal)) return;

            await Task.Run(() => RunCounterSnapshotFile.Save(_path, snapshots), cancellationToken)
                .ConfigureAwait(false);
            _lastWritten = json;
            MarkWriteSucceeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落盘失败不阻断采集：下一个采样窗口会重试。但失败必须留痕且**可见**——
            // 这条路径以前只记 warning，磁盘满时日志被刷屏、界面又看不到，等于静默失效。
            MarkWriteFailed(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MarkWriteSucceeded()
    {
        int previous;
        lock (_statusLock)
        {
            previous = _consecutiveFailures;
            _consecutiveFailures = 0;
            _lastError = null;
            _lastWriteAt = DateTimeOffset.UtcNow;
        }

        if (previous > 0)
        {
            _logger.LogInformation(
                "Run counter snapshot writes recovered after {Count} consecutive failure(s); {Path} is current again.",
                previous, _path);
            RecoverDegradedAlarm();
        }
    }

    private void MarkWriteFailed(Exception exception)
    {
        int failures;
        bool shouldLog;
        lock (_statusLock)
        {
            failures = ++_consecutiveFailures;
            _lastError = exception.Message;

            // 首次立即记；之后每个 FailureLogInterval 最多一条（见常量注释）。
            var now = DateTimeOffset.UtcNow;
            shouldLog = failures == 1 || now - _lastFailureLogAt >= FailureLogInterval;
            if (shouldLog) _lastFailureLogAt = now;
        }

        if (shouldLog)
        {
            _logger.LogWarning(
                exception,
                "Run counter snapshot could not be written to {Path} ({Count} consecutive failure(s)). Counts keep accumulating in memory but the on-disk copy is stale.",
                _path, failures);
        }

        // 只在"刚进入降级"时上报一次。每个采样窗口报一条会把告警列表刷满，
        // 而且 AlarmStore 自己也是文件写入——磁盘满的场景下它同样可能失败。
        if (failures == 1) RaiseDegradedAlarm(exception);
    }

    /// <summary>
    /// 上报"计数未落盘"。故意 fire-and-forget + 全捕获：这条路径的触发原因通常就是磁盘/权限，
    /// 告警存储大概率也会失败；告警失败绝不能反过来打断落盘重试或停机补写。
    /// 状态本身仍可通过 <c>/api/diagnostics/run-counters</c> 读取。
    /// </summary>
    private void RaiseDegradedAlarm(Exception exception)
    {
        if (_alarms is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _alarms.RaiseAsync(
                    DegradedAlarmCode,
                    AlarmSeverity.Warning,
                    "run-counters",
                    $"Run counter snapshot cannot be written to {_path}; the on-disk copy is stale and counts would regress after a restart. {exception.Message}",
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 见方法注释：告警失败不改变任何落盘行为。
            }
        });
    }

    private void RecoverDegradedAlarm()
    {
        if (_alarms is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _alarms.RecoverAsync(DegradedAlarmCode, "run-counters", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 恢复失败不影响下一次成功的写盘。
            }
        });
    }

    /// <summary>测试与诊断用：当前落盘路径。</summary>
    public string SnapshotPath => _path;

    /// <summary>测试与诊断用：当前生效的采样间隔。</summary>
    public TimeSpan FlushInterval => _flushInterval;

    /// <summary>测试与诊断用：当前生效的保留期。</summary>
    public TimeSpan Retention => _retention;
}
