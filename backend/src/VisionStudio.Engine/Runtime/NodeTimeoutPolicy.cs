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

    /// <summary>与整轮预算同级的上限，避免单节点超时掩盖整轮看门狗。</summary>
    public const int MaxTimeoutMs = 600_000;

    /// <summary>解析节点超时；返回 0 表示该节点不设节点级超时。</summary>
    public static int Resolve(NodeDefinition node) => Normalize(node.GetInt(ParameterName, 0));

    /// <summary>负数与 0 都归为"不设限"，正数钳到 <see cref="MaxTimeoutMs"/>。</summary>
    public static int Normalize(int timeoutMs) => timeoutMs <= 0 ? 0 : Math.Min(timeoutMs, MaxTimeoutMs);
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
