using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 节点级执行超时。
///
/// 此前只有整轮 <see cref="VisionRunOptions.TimeoutMs"/> 一个预算：一个慢节点吃掉全部预算，
/// 超时只报"整个流程超时"，既定位不到节点，也无法给相机采集（500 ms）、HTTP（2 s）、
/// 机器人运动（30 s）这类耗时差异极大的节点分别设限。
///
/// 超时值来自节点参数 <c>__timeoutMs</c>，与 <c>__replayInputPath</c> 等宿主内部参数同一约定：
/// 不进目录、不显示在参数面板，只在流程文档里携带。**未配置即不设限**，因此对既有流程零行为变化。
/// 不按节点类型给默认值是有意的——猜错的默认超时会把正常的慢节点误杀，比没有超时更糟。
///
/// 语义边界：取消是协作式的（<see cref="CancellationToken"/>），只能中断会在等待点检查取消的节点。
/// 卡在原生库内部且不轮询取消的调用不会被强行打断；这类硬超时若靠放弃等待实现，会让后台任务
/// 继续持有设备写权限，风险高于收益，故不提供。
/// </summary>
public static class NodeTimeoutPolicy
{
    /// <summary>节点参数名。双下划线前缀与 <c>__replay*</c> 一致，表示宿主内部参数。</summary>
    public const string ParameterName = "__timeoutMs";

    /// <summary>
    /// 默认上限：与整轮预算同级，避免单节点超时掩盖整轮看门狗。
    ///
    /// 保留为 <c>const</c>（而不是直接改造成配置项）有两个原因：测试的 <c>[InlineData]</c>
    /// 需要编译期常量；而且绝大多数流程都该用默认值，只有确实存在长周期节点
    /// （烘烤、长时间 TCP 握手、多帧堆叠）的现场才需要抬高——
    /// 那种情况走 <see cref="TryConfigureCeiling"/> 显式声明，别让它成为悄悄存在的第二套默认。
    /// </summary>
    public const int MaxTimeoutMs = 600_000;

    /// <summary>
    /// 配置可达的绝对上限：24 小时。
    ///
    /// 上限本身也必须封顶，否则"把上限调到 10 年"等于关掉看门狗——
    /// 而关掉看门狗的现场表现是"卡住的节点永远不报错、节拍慢慢烂掉"，比报错难查得多。
    /// </summary>
    public const int AbsoluteMaxTimeoutMs = 24 * 60 * 60 * 1000;

    /// <summary>上限的最小可设值：低于 1s 几乎必然是"毫秒写成秒"的单位笔误。</summary>
    public const int MinCeilingMs = 1_000;

    private static int _ceilingMs = MaxTimeoutMs;

    /// <summary>
    /// 当前生效的节点超时上限。默认 <see cref="MaxTimeoutMs"/>，可由宿主启动时抬高/降低。
    ///
    /// 注意它**不改变整轮看门狗**：<see cref="VisionRunOptions.TimeoutMs"/> 仍然先到期。
    /// 给单节点配 30 分钟预算时，整轮预算必须一起抬，否则报出来的永远是 <c>timeout</c> 而不是
    /// <c>node_timeout</c>——这正是本策略要区分的那件事。
    /// </summary>
    public static int CeilingMs => Volatile.Read(ref _ceilingMs);

    /// <summary>
    /// 设置上限（**仅限启动阶段**，如宿主绑定 <c>Pipeline:NodeTimeoutMaxMs</c> 时）。
    ///
    /// 参数线程安全（<see cref="Volatile"/>），但不保证"改完对正在跑的流程原子生效"——
    /// 这是启动期一次性配置，不是运行期调参入口。非法值返回 false 而不抛异常，
    /// 让调用方决定是记录并沿用默认值还是直接拒绝启动。
    /// </summary>
    public static bool TryConfigureCeiling(int maxTimeoutMs, out string? error)
    {
        if (maxTimeoutMs < MinCeilingMs || maxTimeoutMs > AbsoluteMaxTimeoutMs)
        {
            error = $"Node timeout ceiling {maxTimeoutMs} ms is outside the supported range [{MinCeilingMs}, {AbsoluteMaxTimeoutMs}] ms.";
            return false;
        }

        Volatile.Write(ref _ceilingMs, maxTimeoutMs);
        error = null;
        return true;
    }

    /// <summary>把上限恢复为默认值（测试用，避免静态状态在用例之间互相污染）。</summary>
    public static void ResetCeiling() => Volatile.Write(ref _ceilingMs, MaxTimeoutMs);

    /// <summary>解析节点超时；返回 0 表示该节点不设节点级超时。</summary>
    public static int Resolve(NodeDefinition node) => Normalize(node.GetInt(ParameterName, 0));

    /// <summary>负数与 0 都归为"不设限"，正数钳到当前上限（<see cref="CeilingMs"/>）。</summary>
    public static int Normalize(int timeoutMs) => timeoutMs <= 0 ? 0 : Math.Min(timeoutMs, CeilingMs);
}

/// <summary>
/// 节点超出自身 <c>__timeoutMs</c> 时抛出。
///
/// 刻意**不继承** <see cref="OperationCanceledException"/>：取消表示"调用方不要这个结果了"，
/// 超时表示"这次执行失败了"，两者在 trace、重试策略和 Try/Catch 路由上的归属完全不同。
/// 若复用取消异常，超时会被 <c>catch (OperationCanceledException) when (token.IsCancellationRequested)</c>
/// 误判为整轮取消而中断运行。
/// </summary>
public sealed class NodeTimeoutException : Exception
{
    public NodeTimeoutException(string nodeType, int timeoutMs, TimeSpan elapsed, Exception? innerException = null)
        : base($"Node type '{nodeType}' exceeded its {timeoutMs} ms node timeout (elapsed {elapsed.TotalMilliseconds:0} ms). " +
               $"Raise '{NodeTimeoutPolicy.ParameterName}' on this node, or remove it to inherit the run budget.", innerException)
    {
        NodeType = nodeType;
        TimeoutMs = timeoutMs;
        ElapsedMs = elapsed.TotalMilliseconds;
    }

    public string NodeType { get; }

    public int TimeoutMs { get; }

    public double ElapsedMs { get; }
}
