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

/// <summary>进程内实现：不落盘，重启即清零。需要跨重启保留时由宿主替换实现。</summary>
public sealed class InMemoryRunCounterStore : IRunCounterStore
{
    private readonly ConcurrentDictionary<string, CounterState> _states = new(StringComparer.Ordinal);

    public CounterState GetOrAdd(string scope, string counterId)
        => _states.GetOrAdd($"{scope}\u001f{counterId}", _ => new CounterState());

    /// <summary>清空全部计数器（宿主重启流程/切换配方时使用）。</summary>
    public void Clear() => _states.Clear();
}
