using System.Collections.Concurrent;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 跨轮计数存储（value.counter 的宿主侧状态）。
///
/// 为什么需要宿主状态：流程节点本身是无状态执行器，每轮都是新的一次 ExecuteAsync。
/// "连续 3 件 NG 停机""本班次良率"这类判断需要跨轮记忆，只能落在比节点更长的生命周期里。
///
/// 作用域语义：
/// - <c>workflow</c>：按编译后工作流身份隔离，跨 run 保留（班次统计、连续 NG）；
/// - <c>run</c>：按 runId 隔离，run 一换就重新开始（单件内的多次采样）。
///
/// 线程安全：并行分支可能同时触发同一个计数器，所有读写都在实例锁内完成。
/// </summary>
public interface IRunCounterStore
{
    /// <summary>取出（或创建）指定作用域下的计数器状态；返回的对象只在锁内可变。</summary>
    CounterState GetOrAdd(string scope, string counterId);

    /// <summary>
    /// 取全部计数器的稳定快照，顺序固定（Scope/CounterId 升序），便于持久化时按内容比较去抖。
    ///
    /// 快照能力放在接口上而不是只留在 <see cref="InMemoryRunCounterStore"/>：宿主的落盘逻辑
    /// 只应依赖抽象，否则换成 SQLite/Redis 实现时 <c>RunCounterPersistence</c> 必须跟着改签名，
    /// 而这正是"注册了接口却绕过它"的典型漏点。
    /// </summary>
    IReadOnlyList<RunCounterSnapshot> Snapshot();

    /// <summary>
    /// 恢复快照。**只应在启动阶段调用**：它会覆盖同名计数器的当前值，
    /// 运行期调用会把正在累加的班次统计冲掉。
    /// </summary>
    void Restore(IEnumerable<RunCounterSnapshot> snapshots);
}

/// <summary>一个计数器的可变状态。字段读写必须由调用方在 <c>lock (state)</c> 内完成。</summary>
public sealed class CounterState
{
    public long Count { get; set; }
    public long Total { get; set; }
    public long Pass { get; set; }
    public long Fail { get; set; }

    /// <summary>
    /// 最近一次被节点访问的时间（UTC），由 <see cref="IRunCounterStore.GetOrAdd"/> 刷新。
    ///
    /// 存在的理由：流程改名或删除后，旧 scope 的计数器会永远留在快照里，每次启动全量恢复。
    /// 现场跑上一年，<c>counters.json</c> 会积下大量没人读的条目，还会让"某个计数器到底属于
    /// 哪个流程"变得不可判断。有了这个时间戳，宿主才能在启动时丢掉长期没动过的孤儿。
    /// </summary>
    public DateTimeOffset LastTouchedAt { get; set; }

    public void Reset()
    {
        Count = 0;
        Total = 0;
        Pass = 0;
        Fail = 0;
    }
}

/// <summary>一个计数器的可持久化快照（作用域 + 计数器 ID + 四个计数值 + 最后访问时间）。</summary>
/// <param name="LastTouchedAt">
/// 最后访问时间。旧版快照没有这个字段，反序列化后为 <c>default</c>——宿主据此判断
/// "无法判定年龄"，一律保留，不做静默丢弃。
/// </param>
public sealed record RunCounterSnapshot(
    string Scope,
    string CounterId,
    long Count,
    long Total,
    long Pass,
    long Fail,
    DateTimeOffset LastTouchedAt = default);

/// <summary>进程内实现：不落盘，重启即清零。需要跨重启保留时由宿主替换实现。</summary>
public sealed class InMemoryRunCounterStore : IRunCounterStore
{
    private sealed record Entry(string Scope, string CounterId, CounterState State);

    /// <summary>
    /// 键是 (Scope, CounterId) 元组，**不是**拼接字符串。
    ///
    /// 旧实现用 <c>$"{scope}\u001f{counterId}"</c> 做键：只要 scope 或 counterId 里出现
    /// U+001F（或任何被选作分隔符的字符），不同的两对就会算出同一个键——
    /// <c>("A", "B\u001FC")</c> 与 <c>("A\u001FB", "C")</c> 静默合并成同一个计数器，
    /// 表现为"两个工位的良率串在一起"，且没有任何异常可查。元组键让碰撞在结构上不可能，
    /// 比"校验并拒绝某些字符"更稳：后者要穷举所有危险字符，而前者不需要枚举。
    /// 元组键还省掉每次访问的一次字符串分配（value.counter 是每轮都跑的热路径）。
    /// </summary>
    private readonly ConcurrentDictionary<(string Scope, string CounterId), Entry> _states = new();

    public CounterState GetOrAdd(string scope, string counterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(counterId);

        var state = _states.GetOrAdd((scope, counterId), static key => new Entry(key.Scope, key.CounterId, new CounterState())).State;
        // 刷新最后访问时间，供宿主丢弃长期没人读的孤儿计数器。锁粒度与调用方随后的读写一致，
        // 不额外引入竞争（value.counter 本来就要在 lock(state) 内累加）。
        lock (state)
        {
            state.LastTouchedAt = DateTimeOffset.UtcNow;
        }
        return state;
    }

    /// <summary>清空全部计数器（宿主重启流程/切换配方时使用）。</summary>
    public void Clear() => _states.Clear();

    /// <summary>
    /// 取全部计数器的稳定快照（2026-10-29）。每个状态在各自的锁内读取，保证
    /// "Count 与 Pass/Fail 来自同一次累加"，不会读到半更新的一行。
    /// 顺序按 Scope/CounterId 排序，方便持久化时做内容比较去抖。
    /// </summary>
    public IReadOnlyList<RunCounterSnapshot> Snapshot()
    {
        var snapshots = new List<RunCounterSnapshot>(_states.Count);
        foreach (var entry in _states.Values)
        {
            lock (entry.State)
            {
                snapshots.Add(new RunCounterSnapshot(entry.Scope, entry.CounterId,
                    entry.State.Count, entry.State.Total, entry.State.Pass, entry.State.Fail,
                    entry.State.LastTouchedAt));
            }
        }
        snapshots.Sort(static (left, right) =>
        {
            var scope = string.CompareOrdinal(left.Scope, right.Scope);
            return scope != 0 ? scope : string.CompareOrdinal(left.CounterId, right.CounterId);
        });
        return snapshots;
    }

    /// <summary>
    /// 恢复快照。**只应在启动阶段调用**：它会覆盖同名计数器的当前值，
    /// 运行期调用会把正在累加的班次统计冲掉。
    /// </summary>
    public void Restore(IEnumerable<RunCounterSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        foreach (var snapshot in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Scope) || string.IsNullOrWhiteSpace(snapshot.CounterId)) continue;
            var state = GetOrAdd(snapshot.Scope, snapshot.CounterId);
            lock (state)
            {
                state.Count = snapshot.Count;
                state.Total = snapshot.Total;
                state.Pass = snapshot.Pass;
                state.Fail = snapshot.Fail;
                state.LastTouchedAt = snapshot.LastTouchedAt;
            }
        }
    }
}
