using System.Diagnostics;

namespace VisionStudio.Engine.Diagnostics;

/// <summary>
/// 一次节点执行的快照（2026-11-12）。
/// </summary>
/// <param name="Sequence">进程内自增序号，越大越新。环形缓冲覆盖后仍能判断先后。</param>
/// <param name="At">span 结束时刻（UTC）。</param>
/// <param name="NodeId">流程里的节点实例 id，用于对齐画布上的节点。</param>
/// <param name="NodeType">节点类型（<c>measure.caliper</c> 等）。</param>
/// <param name="RunId">运行 id；单节点试跑/校验时为 null。</param>
/// <param name="Scope">模块调用（<c>module.call</c>）作用域。</param>
/// <param name="DurationMs">耗时，与 <c>vision.node.duration_ms</c> 同一口径。</param>
/// <param name="TimedOut">是否由节点级 <c>__timeoutMs</c> 打穿。</param>
/// <param name="Status"><c>Ok</c> / <c>Error</c>（来自 <see cref="ActivityStatusCode"/>）。</param>
/// <param name="Error">失败描述（截断，避免长堆栈撑爆缓冲）。</param>
public sealed record NodeSpanRecord(
    long Sequence,
    DateTimeOffset At,
    string NodeId,
    string NodeType,
    string? RunId,
    string? Scope,
    double DurationMs,
    bool TimedOut,
    string Status,
    string? Error);

/// <summary>
/// <see cref="NodeSpanBuffer"/> 的一次读快照。
/// </summary>
/// <param name="CapturedAt">读取时刻。缓冲重启清零，时间戳让使用者能判断数据新鲜度。</param>
/// <param name="Capacity">环形容量。</param>
/// <param name="Captured">进程启动以来捕获的 span 总数。</param>
/// <param name="Overwritten">被环形覆盖掉的条数（= Captured - 当前条数），大于 0 说明 Recent 不是全量。</param>
/// <param name="Recent">最近若干条，**按时间倒序**（最新在前）。</param>
/// <param name="Failures">最近失败/超时的记录，同样倒序，最多 <see cref="NodeSpanBuffer.MaxFailures"/> 条。</param>
public sealed record NodeSpanSnapshot(
    DateTimeOffset CapturedAt,
    int Capacity,
    long Captured,
    long Overwritten,
    IReadOnlyList<NodeSpanRecord> Recent,
    IReadOnlyList<NodeSpanRecord> Failures);

/// <summary>
/// 要监听的 Source 的包装（2026-11-12）。
///
/// 为什么包一层而不是直接收 <see cref="ActivitySource"/>：DI 容器里**已经有一个**
/// `ActivitySource` 实例（名字是 `Microsoft.AspNetCore`，.NET 10 宿主内置注册）。
/// 构造函数一旦声明 `ActivitySource? source = null`，容器就会把它注入进来——
/// 于是"只收引擎节点 span"变成"只收 HTTP 请求 span"，而且 <c>ShouldListenTo</c> 的
/// 引用比较还会返回 true（因为监听的确实是那个 source），从代码上完全看不出错。
/// 用一个容器不认识的自有类型，让这件事在编译/解析期就不可能发生。
/// </summary>
public sealed record NodeSpanSource(ActivitySource Source);

/// <summary>
/// 进程内节点级 span 消费者（2026-11-12）。
///
/// 为什么需要它：<see cref="EngineTelemetry"/> 只负责**产生** <see cref="Activity"/>，
/// 而 <see cref="ActivitySource.StartActivity(string)"/> 在没有监听者时返回 null——
/// 也就是说，只有埋点、没有消费者时，这一层在产线上恒为空，日志、诊断包、界面都拿不到
/// "刚才哪一次执行慢了三倍"这类单次事实（<c>NodeDurationStatistics</c> 只有分位数，没有单次）。
/// 现场没有 Jaeger/OTLP，所以"接 APM"不是可选项；这个类就是那个消费者。
///
/// 设计取舍：
/// <list type="bullet">
/// <item>**内存环形**，默认 1024 条；一条记录约 200 B，常驻约 200 KB，不落盘（重启清零，
///       避免上一批工艺的耗时混进本次排障）。</item>
/// <item>只捕获 <see cref="EngineTelemetry.SourceName"/> 一个 Source，不干扰其它诊断监听者。</item>
/// <item>不采样、不做聚合裁剪：既保留"最近发生了什么"，也保留失败清单；
///       分位数仍归 <c>NodeDurationStatistics</c>，两者口径不重叠。</item>
/// <item>监听器在构造时注册、<see cref="Dispose"/> 时摘除——测试可以按实例隔离，
///       不会因为静态注册让整个测试进程都被监听。</item>
/// </list>
/// </summary>
public sealed class NodeSpanBuffer : IDisposable
{
    /// <summary>环形容量。按 1 s 节拍、每轮 10 个节点估算，约覆盖最近 100 秒。</summary>
    public const int DefaultCapacity = 1024;

    /// <summary>快照里失败清单的条数上限。</summary>
    public const int MaxFailures = 32;

    /// <summary>错误描述的截断长度。堆栈不该进内存缓冲，日志里有完整版本。</summary>
    private const int MaxErrorLength = 200;

    private readonly int _capacity;
    private readonly NodeSpanRecord?[] _ring;
    private readonly object _gate = new();
    private readonly ActivityListener _listener;
    private int _next;
    private long _captured;
    private long _sequence;
    private bool _disposed;

    /// <param name="capacity">环形容量。</param>
    /// <param name="source">
    /// 要监听的 Source。默认 <see cref="EngineTelemetry.ActivitySource"/>；测试传入自己的实例，
    /// 避免与并行运行的其它测试共用静态 Source 而互相污染计数。
    /// </param>
    public NodeSpanBuffer(int capacity = DefaultCapacity, NodeSpanSource? source = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _ring = new NodeSpanRecord?[capacity];
        var activitySource = source?.Source ?? EngineTelemetry.ActivitySource;
        _listener = new ActivityListener
        {
            // 引用比较而不是名字比较：同名 Source 在测试里很常见，按名字会误收别人的 span。
            ShouldListenTo = candidate => ReferenceEquals(candidate, activitySource),
            // AllData 而非 AllDataAndRecorded：进程内消费不需要 W3C 传播记录，
            // 少一层开销，也不改变上游采样决策（引擎侧本来就不做采样）。
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            // 二次过滤（2026-11-12 真后端冒烟发现）：只靠 ShouldListenTo 时，进程里仍收到了
            // ASP.NET Core 的 HttpRequestIn span（nodeId 为空、nodeType 是请求名），
            // 混进来会把"最近节点执行"变成"最近 HTTP 请求"。两道关都留：
            // ShouldListenTo 决定要不要建 Activity（性能），这里决定要不要记账（正确性）。
            ActivityStopped = activity =>
            {
                // 必须在这里过滤，不能只靠 ShouldListenTo：ActivityStopped 是进程级回调，
                // 进程内**任何** ActivitySource 停下的 span 都会进来（实测收到
                // src='Microsoft.AspNetCore' / display='...HttpRequestIn'，nodeId 为空）。
                // 混进来会把"最近节点执行"变成"最近 HTTP 请求"，而且看不出是错的。
                if (!string.Equals(activity.Source?.Name, activitySource.Name, StringComparison.Ordinal)) return;
                Capture(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>捕获一条已结束的 span；任何异常都必须吞掉——监听器里的异常会打断执行链。</summary>
    private void Capture(Activity activity)
    {
        try
        {
            var record = new NodeSpanRecord(
                Sequence: Interlocked.Increment(ref _sequence),
                At: DateTimeOffset.UtcNow,
                NodeId: Tag(activity, "vision.node.id") ?? string.Empty,
                NodeType: Tag(activity, "vision.node.type") ?? activity.DisplayName,
                RunId: Tag(activity, "vision.run.id"),
                Scope: Tag(activity, "vision.scope"),
                DurationMs: activity.Duration.TotalMilliseconds,
                TimedOut: string.Equals(Tag(activity, "vision.node.timed_out"), "true", StringComparison.OrdinalIgnoreCase),
                Status: activity.Status == ActivityStatusCode.Error ? "Error" : "Ok",
                Error: Truncate(activity.Status == ActivityStatusCode.Error ? activity.StatusDescription : null));

            lock (_gate)
            {
                if (_disposed) return;
                _ring[_next] = record;
                _next = (_next + 1) % _capacity;
                _captured++;
            }
        }
        catch
        {
            // 遥测不得影响生产：这里连日志都不写——日志设施自身故障时会把问题放大成双重故障。
        }
    }

    /// <summary>
    /// 读取快照。<paramref name="recent"/> 只影响返回条数，不影响缓冲内容。
    /// </summary>
    public NodeSpanSnapshot Snapshot(int recent = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recent, 0);
        lock (_gate)
        {
            var records = new List<NodeSpanRecord>(Math.Min(_capacity, Math.Max(recent, MaxFailures)));
            for (var offset = 1; offset <= _capacity; offset++)
            {
                var index = (_next - offset + _capacity * 2) % _capacity;
                if (_ring[index] is not { } record) break;
                records.Add(record);
                if (records.Count >= Math.Max(recent, MaxFailures)) break;
            }

            var failures = records
                .Where(r => r.Status == "Error" || r.TimedOut)
                .Take(MaxFailures)
                .ToArray();
            var trimmed = records.Count > recent ? records.GetRange(0, recent) : records;
            return new NodeSpanSnapshot(
                CapturedAt: DateTimeOffset.UtcNow,
                Capacity: _capacity,
                Captured: _captured,
                Overwritten: Math.Max(0, _captured - records.Count),
                Recent: trimmed,
                Failures: failures);
        }
    }

    private static string? Tag(Activity activity, string name)
        => activity.GetTagItem(name) as string ?? activity.GetTagItem(name)?.ToString();

    private static string? Truncate(string? value)
        => string.IsNullOrEmpty(value) ? null
            : value.Length <= MaxErrorLength ? value
            : value[..MaxErrorLength] + "…";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _listener.Dispose();
    }
}
