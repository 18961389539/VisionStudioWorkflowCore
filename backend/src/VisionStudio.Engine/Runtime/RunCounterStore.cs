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
}

/// <summary>一个计数器的可变状态。字段读写必须由调用方在 <c>lock (state)</c> 内完成。</summary>
public sealed class CounterState
{
    public long Count { get; set; }
    public long Total { get; set; }
    public long Pass { get; set; }
    public long Fail { get; set; }

    public void Reset()
    {
        Count = 0;
        Total = 0;
        Pass = 0;
        Fail = 0;
    }
}

/// <summary>一个计数器的可持久化快照（作用域 + 计数器 ID + 四个计数值）。</summary>
public sealed record RunCounterSnapshot(
    string Scope,
    string CounterId,
    long Count,
    long Total,
    long Pass,
    long Fail);

/// <summary>进程内实现：不落盘，重启即清零。需要跨重启保留时由宿主替换实现。</summary>
public sealed class InMemoryRunCounterStore : IRunCounterStore
{
    private sealed record Entry(string Scope, string CounterId, CounterState State);

    private readonly ConcurrentDictionary<string, Entry> _states = new(StringComparer.Ordinal);

    public CounterState GetOrAdd(string scope, string counterId)
        => _states.GetOrAdd($"{scope}\u001f{counterId}", _ => new Entry(scope, counterId, new CounterState())).State;

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
                    entry.State.Count, entry.State.Total, entry.State.Pass, entry.State.Fail));
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
            }
        }
    }
}
