namespace VisionStudio.Engine;

/// <summary>
/// A02（2026-10-11 复审）：真实命令派发边界的所有权令牌。
///
/// 反例：管理员释放过期租约后，旧执行句柄仍能继续向同一设备下发命令——租约此前只保护
/// "获取/释放"，真正写设备/发机器人指令的路径没有验证所有权代次。
/// 本契约把"递增所有权令牌"的校验放到每次真实派发前；检查与派发之间用许可计数封闭
/// 竞争窗口（在途命令未结束时管理员释放被拒绝）。
/// </summary>
public interface IDeviceCommandOwnershipToken
{
    /// <summary>持有者描述（用于拒绝消息与审计）。</summary>
    string OwnerDescription { get; }
}

/// <summary>
/// 派发边界的许可闸门：返回的许可必须覆盖整个底层调用；所有权已丢失（令牌被换代/撤销）时
/// 抛出异常拒绝派发。安全停止、断开、只读诊断等路径不经过本闸门。
/// </summary>
public interface IDeviceCommandOwnershipGate
{
    IDisposable? AcquireCommandPermit(string resourceKind, string resourceId);
}

/// <summary>
/// R01：**显式系统上下文**——启动、维护、验收（如相机同步标定耐久测试）等不绑定租约、
/// 但必须显式建模的派发权限。生产闸门不再对"无上下文调用"默认放行：这些路径必须显式
/// 进入系统上下文（可审计的 actor/reason），其余无上下文调用一律拒绝。
/// </summary>
public sealed record SystemDeviceCommandOwnershipToken(string Actor, string Reason) : IDeviceCommandOwnershipToken
{
    public string OwnerDescription => $"system '{Actor}'";
}

/// <summary>默认闸门：不做所有权校验（未接入租约注册表的独立构建与单元测试）。</summary>
public sealed class AllowAllDeviceCommandOwnershipGate : IDeviceCommandOwnershipGate
{
    public static readonly AllowAllDeviceCommandOwnershipGate Instance = new();

    private AllowAllDeviceCommandOwnershipGate() { }

    public IDisposable? AcquireCommandPermit(string resourceKind, string resourceId) => null;
}

/// <summary>可安全重复释放的空操作许可（闸门返回 null 时由调用方包装使用）。</summary>
public sealed class NoopDeviceCommandPermit : IDisposable
{
    public static readonly NoopDeviceCommandPermit Instance = new();

    private NoopDeviceCommandPermit() { }

    public void Dispose() { }
}

/// <summary>
/// 惰性解析的闸门包装——打破"设备/机器人/相机管理器 → 闸门 → 租约注册表 → 活动探针 → 管理器"
/// 的构造环：包装在构造时不解析任何依赖，首次派发时才解析真实闸门。
/// </summary>
public sealed class LazyDeviceCommandOwnershipGate(Func<IDeviceCommandOwnershipGate> resolve) : IDeviceCommandOwnershipGate
{
    private readonly Lazy<IDeviceCommandOwnershipGate> _inner =
        new(resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public IDisposable? AcquireCommandPermit(string resourceKind, string resourceId)
        => _inner.Value.AcquireCommandPermit(resourceKind, resourceId);
}

/// <summary>命令派发闸门使用的资源类别（与设备租约注册表的类别字符串一致）。</summary>
public static class DeviceCommandResourceKinds
{
    public const string Camera = "camera";
    public const string Device = "device";
    public const string Robot = "robot";
}

/// <summary>
/// 当前执行流的所有权环境（AsyncLocal）。租约持有入口（手动端点 / 运行 / 调试会话 / 生产循环）
/// 进入环境后，节点运行时再按工作流数据重入——即使 Workflow Core 的调度边界丢失执行上下文，
/// 节点内的真实派发仍能携带正确的所有权身份。
/// </summary>
public static class DeviceCommandOwnership
{
    private static readonly AsyncLocal<IDeviceCommandOwnershipToken?> CurrentToken = new();

    public static IDeviceCommandOwnershipToken? Current => CurrentToken.Value;

    /// <summary>进入所有权环境；token 为 null 时返回 null（不做任何事）。</summary>
    public static IDisposable? Enter(IDeviceCommandOwnershipToken? token)
        => token is null ? null : new Scope(token, CurrentToken.Value);

    /// <summary>R01：显式进入系统上下文（启动/维护/验收路径）——替代旧的"无上下文默认放行"。</summary>
    public static IDisposable EnterSystem(string actor, string reason)
        => new Scope(new SystemDeviceCommandOwnershipToken(actor, reason), CurrentToken.Value);

    private sealed class Scope : IDisposable
    {
        private readonly IDeviceCommandOwnershipToken? _previous;
        private int _disposed;

        public Scope(IDeviceCommandOwnershipToken token, IDeviceCommandOwnershipToken? previous)
        {
            _previous = previous;
            CurrentToken.Value = token;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            CurrentToken.Value = _previous;
        }
    }
}
