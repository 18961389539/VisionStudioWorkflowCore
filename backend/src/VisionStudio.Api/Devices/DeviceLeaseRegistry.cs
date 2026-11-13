using System.Collections.Concurrent;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>
/// Resource kinds arbitrated by <see cref="DeviceLeaseRegistry"/>. Values match the kind strings
/// already used by dependency-mutation and runtime-operation guards.
/// </summary>
public static class DeviceLeaseResourceKinds
{
    public const string Camera = "camera";
    public const string Device = "device";
    public const string Robot = "robot";
}

/// <summary>
/// Logical actor holding hardware assets. Kind separates the arbitration classes; Id distinguishes
/// concurrent owners inside one class (job id, session id, run id, request id).
/// </summary>
public sealed record DeviceLeaseOwner(string Kind, string Id, string? Description = null)
{
    public static DeviceLeaseOwner Production(string jobId) => new("Production", jobId, "Production Runtime");

    public static DeviceLeaseOwner DebugSession(string sessionId) => new("DebugSession", sessionId, "Debug Session");

    public static DeviceLeaseOwner Run(string runId) => new("Run", runId, "Workflow Run");

    public static DeviceLeaseOwner Manual(string requestId) => new("Manual", requestId, "Manual Operation");
}

/// <summary>One arbitrated hardware asset.</summary>
public sealed record DeviceLeaseResource(string Kind, string Id)
{
    public static IReadOnlyList<DeviceLeaseResource> FromReferences(WorkflowRuntimeReferences references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return references.Cameras.Select(x => new DeviceLeaseResource(DeviceLeaseResourceKinds.Camera, x))
            .Concat(references.Devices.Select(x => new DeviceLeaseResource(DeviceLeaseResourceKinds.Device, x)))
            .Concat(references.Robots.Select(x => new DeviceLeaseResource(DeviceLeaseResourceKinds.Robot, x)))
            .ToArray();
    }
}

/// <summary>One asset already held by another owner, with the remaining hold time for TTL leases.</summary>
/// <param name="Abandoned">
/// R02：该租约的 TTL 已过但未被释放——持有者可能仍在执行操作。Abandoned 不是"设备可用"的证据；
/// 只有在设备活动探针确认资源空闲时才允许接管。
/// </param>
/// <param name="Reason">
/// R01：当冲突不是"租约占用"而是"命令仍在真实派发中"时给出的确定性描述（优先于 TTL/持有文本）。
/// </param>
public sealed record DeviceLeaseConflict(
    string ResourceKind,
    string ResourceId,
    DeviceLeaseOwner Owner,
    TimeSpan? Remaining,
    bool Abandoned = false,
    string? Reason = null);

/// <summary>
/// R02：设备活动探针——租约接管前的"资源当前是否仍在执行"判定。
/// TTL 到期只说明持有者可能失联；底层调用是否结束必须直接问设备层。
/// </summary>
public interface IDeviceActivityProbe
{
    bool IsResourceBusy(string resourceKind, string resourceId);
}

/// <summary>
/// Process-wide hardware arbitration for Production Runtime, debug sessions, workflow runs and manual
/// operations. Every actor must hold a lease over each camera/device/robot it touches; conflicting
/// acquisitions fail fast with a 409 description naming the asset and the current holder.
/// Leases without TTL are held until their owner releases them; a TTL exists only as a last-resort
/// auto-release for request-scoped manual operations.
///
/// A02：本注册表同时是<b>命令派发边界的所有权闸门</b>（<see cref="IDeviceCommandOwnershipGate"/>）——
/// 管理员强制释放（<see cref="TryForceRelease"/>）会撤销旧令牌；此后旧执行携带旧令牌的任何真实
/// 派发在 <see cref="AcquireCommandPermit"/> 被拒绝，即使它此前完成过第一步写入。
/// 检查与派发之间用在途许可计数封闭竞争窗口：派发期间管理员释放被拒绝。
/// </summary>
public sealed class DeviceLeaseRegistry : IDeviceCommandOwnershipGate
{
    private sealed record LeaseEntry(DeviceLeaseOwner Owner, long Token, DateTimeOffset? ExpiresAt)
    {
        /// <summary>R02：TTL 已过但未释放——保留条目，等待探针确认空闲后才允许接管。</summary>
        public bool Abandoned { get; init; }
    }

    private sealed class ResourceKeyComparer : IEqualityComparer<(string Kind, string Id)>
    {
        public bool Equals((string Kind, string Id) x, (string Kind, string Id) y)
            => string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Kind, string Id) x)
            => HashCode.Combine(x.Kind.ToLowerInvariant(), x.Id.ToLowerInvariant());
    }

    private static readonly ResourceKeyComparer KeyComparer = new();

    private readonly ConcurrentDictionary<(string Kind, string Id), LeaseEntry> _leases = new(KeyComparer);
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly IDeviceActivityProbe? _probe;
    private long _nextToken;
    /// <summary>A02：被管理员强制释放（换代）的令牌——旧执行携带这些令牌的派发一律拒绝。</summary>
    private readonly HashSet<string> _revokedTokens = new(StringComparer.OrdinalIgnoreCase);

    private static string RevocationKey((string Kind, string Id) key, long token)
        => $"{key.Kind.ToLowerInvariant()}|{key.Id.ToLowerInvariant()}|{token}";

    /// <summary>
    /// R01：正在真实派发中的命令——记录计数与持有者。在途命令未结束时管理员释放被拒绝
    /// （封闭检查与派发的竞争窗口）；持有者不同的获取同样被拒绝（旧命令未收尾前不得接管）。
    /// </summary>
    private sealed record InFlightCommands(DeviceLeaseOwner Owner, int Count);

    private readonly Dictionary<(string Kind, string Id), InFlightCommands> _commandsInFlight = new(KeyComparer);

    /// <param name="probe">
    /// R02：可选的活动探针。为 null 时 Abandoned 租约不允许被接管（最保守）。
    /// </param>
    public DeviceLeaseRegistry(TimeProvider? timeProvider = null, IDeviceActivityProbe? probe = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _probe = probe;
    }

    /// <summary>
    /// N04：管理员显式释放一个**已放弃**的租约（TTL 已过且持有者从未释放）。要求设备活动探针
    /// 确认空闲——瞬时空闲不足以自动接管，但可以支撑"人工确认 + 审计"的受控释放。
    /// A02：释放同时<b>撤销旧令牌</b>——旧执行此后的真实派发会被 <see cref="AcquireCommandPermit"/>
    /// 拒绝；仍在派发中的命令（许可计数 &gt; 0）会阻止释放，避免检查与派发之间的竞争窗口。
    /// </summary>
    public bool TryForceRelease(string resourceKind, string resourceId, out string reason)
    {
        lock (_gate)
        {
            // A02：先清扫 TTL 过期条目——"是否已放弃"不应依赖上一次其它操作的清扫时机。
            SweepExpiredLocked(_time.GetUtcNow());
            var key = (resourceKind, resourceId);
            if (!_leases.TryGetValue(key, out var entry))
            {
                reason = "no lease exists for this resource";
                return false;
            }
            if (!entry.Abandoned)
            {
                reason = "the lease is still active (its holder has not released it and the TTL has not elapsed)";
                return false;
            }
            if (_commandsInFlight.TryGetValue(key, out var inFlight) && inFlight.Count > 0)
            {
                reason = "a command is still being dispatched to this resource; wait for it to finish before releasing";
                return false;
            }
            if (_probe is not null && _probe.IsResourceBusy(resourceKind, resourceId))
            {
                reason = "the device still reports active work; stop it or wait for it to finish before releasing";
                return false;
            }
            _leases.TryRemove(key, out _);
            _revokedTokens.Add(RevocationKey(key, entry.Token));
            reason = $"released the abandoned lease previously held by {entry.Owner.Kind} '{entry.Owner.Id}'";
            return true;
        }
    }

    /// <summary>
    /// R01/A02：真实命令派发前的所有权许可——同一临界区内完成全部校验：
    /// · 来源：调用流必须携带本注册表签发的租约令牌，或显式的系统上下文令牌；
    ///   无上下文、异地注册表令牌、无法识别的令牌一律拒绝（生产闸门不再默认放行）；
    /// · 未释放：普通释放（Dispose）后的旧令牌不能再派发；
    /// · 资源范围：目标必须属于该租约声明的资源（租 A 写 B 被拒绝）；
    /// · 目标条目：必须仍由该令牌持有（条目缺失/换代/所有者变化都拒绝）。
    /// 返回的许可必须覆盖整个底层调用；许可存续期间管理员强制释放被拒绝，
    /// 且持有者不同的租约获取被拒绝（旧命令未收尾前不得接管）。
    /// 安全停止、断开与只读诊断不经过本闸门。
    /// </summary>
    public IDisposable AcquireCommandPermit(string resourceKind, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceKind) || string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("resource kind and id are required.", nameof(resourceId));
        var key = (Kind: resourceKind.Trim().ToLowerInvariant(), Id: resourceId.Trim());
        lock (_gate)
        {
            var token = DeviceCommandOwnership.Current;
            DeviceLeaseOwner holder;
            switch (token)
            {
                case null:
                    throw new ApiConflictException(
                        $"Refusing to command {key.Kind} '{key.Id}': no device-command ownership context is attached to this call. " +
                        "Device commands must run inside a lease context (production/debug/run/manual operation) or an explicit system context.");
                case DeviceLease lease when lease.IsIssuedBy(this):
                    if (_revokedTokens.Contains(RevocationKey(key, lease.Token)))
                        throw new ApiConflictException(
                            $"Refusing to command {key.Kind} '{key.Id}': the ownership lease held by {lease.OwnerDescription} " +
                            "was revoked by an administrator force-release; the stale command was rejected. " +
                            "Re-acquire the lease through a new run/operation before commanding this resource again.");
                    if (lease.IsReleased)
                        throw new ApiConflictException(
                            $"Refusing to command {key.Kind} '{key.Id}': the ownership lease held by {lease.OwnerDescription} " +
                            "was released; the stale command was rejected. Re-acquire the lease before commanding this resource again.");
                    if (!lease.CoversResource(key))
                        throw new ApiConflictException(
                            $"Refusing to command {key.Kind} '{key.Id}': the target is outside the lease scope held by {lease.OwnerDescription} " +
                            $"(leased: {lease.DescribeResources()}). Declare the resource for the operation before commanding it.");
                    if (!_leases.TryGetValue(key, out var entry))
                        throw new ApiConflictException(
                            $"Refusing to command {key.Kind} '{key.Id}': the resource is no longer leased by {lease.OwnerDescription} " +
                            "(the lease entry was released or replaced); the stale command was rejected.");
                    if (entry.Token != lease.Token || !IsSameOwner(entry.Owner, lease.Owner))
                        throw new ApiConflictException(
                            $"Refusing to command {key.Kind} '{key.Id}': it is now owned by {entry.Owner.Kind} '{entry.Owner.Id}' " +
                            $"(ownership generation changed); the stale command from {lease.OwnerDescription} was rejected.");
                    holder = lease.Owner;
                    break;
                case DeviceLease foreign:
                    throw new ApiConflictException(
                        $"Refusing to command {key.Kind} '{key.Id}': the lease held by {foreign.OwnerDescription} " +
                        "was issued by a different lease registry; cross-registry ownership cannot authorize this device command.");
                case SystemDeviceCommandOwnershipToken system:
                    holder = new DeviceLeaseOwner("System", system.Actor, "System Context");
                    break;
                default:
                    throw new ApiConflictException(
                        $"Refusing to command {key.Kind} '{key.Id}': the ownership token '{token.OwnerDescription}' is not recognized by this lease registry.");
            }

            _commandsInFlight[key] = _commandsInFlight.TryGetValue(key, out var tracked)
                ? tracked with { Count = tracked.Count + 1 }
                : new InFlightCommands(holder, 1);
            return new CommandPermit(this, key);
        }
    }

    private sealed class CommandPermit(DeviceLeaseRegistry registry, (string Kind, string Id) key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (registry._gate)
            {
                if (!registry._commandsInFlight.TryGetValue(key, out var tracked)) return;
                if (tracked.Count <= 1) registry._commandsInFlight.Remove(key);
                else registry._commandsInFlight[key] = tracked with { Count = tracked.Count - 1 };
            }
        }
    }

    /// <summary>
    /// R02（2026-10-11 复审）：该资源当前是否有未释放的命令派发许可（触发/写入/运动仍在设备上执行）。
    /// 这是核对路径"无已知在途动作"检查的事实源——许可由 AcquireCommandPermit 登记、覆盖整个底层调用。
    /// </summary>
    public bool IsCommandInFlight(string resourceKind, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceKind) || string.IsNullOrWhiteSpace(resourceId)) return false;
        var key = (Kind: resourceKind.Trim().ToLowerInvariant(), Id: resourceId.Trim());
        lock (_gate) { return _commandsInFlight.ContainsKey(key); }
    }

    /// <summary>Number of assets currently leased (expired TTL entries are swept first).</summary>
    public int ActiveLeaseCount
    {
        get
        {
            lock (_gate)
            {
                SweepExpiredLocked(_time.GetUtcNow());
                return _leases.Count;
            }
        }
    }

    /// <summary>
    /// Non-blocking all-or-nothing acquisition. On success every resource is leased to
    /// <paramref name="owner"/> until the returned handle is disposed (or the optional TTL elapses).
    /// On failure no resource is leased and <paramref name="conflicts"/> names every blocker.
    /// Re-acquiring a resource already held by the same owner is allowed and refreshes ownership.
    /// </summary>
    public DeviceLease? TryAcquire(
        DeviceLeaseOwner owner,
        IReadOnlyList<DeviceLeaseResource> resources,
        out IReadOnlyList<DeviceLeaseConflict> conflicts,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resources);
        if (ttl is { } lifetime && lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive when specified.");

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            SweepExpiredLocked(now);
            var normalized = Normalize(resources);
            conflicts = CollectConflictsLocked(normalized, owner, now);
            if (conflicts.Count > 0)
            {
                // N04：**不做自动接管**。TTL 到期只证明"持有者可能失联"，不能证明"旧调用已终止"——
                // 瞬时设备空闲（命令门空闲/连接态）不足以作为证据，而且设备下发路径没有代次校验，
                // 旧请求句柄在被替换后仍能继续写设备（探针：takeover=True 后旧所有者写入成功）。
                // 恢复路径只有两条：进程重启（内存租约消失），或管理员显式释放（TryForceRelease，
                // 要求条目已 Abandoned 且设备确认空闲，并写入审计）。
                return null;
            }

            var token = ++_nextToken;
            foreach (var resource in normalized)
            {
                _leases[(resource.Kind, resource.Id)] = new LeaseEntry(
                    owner,
                    token,
                    ttl is { } window ? now + window : null);
            }
            return new DeviceLease(this, owner, normalized, token);
        }
    }

    /// <summary>Acquires a lease or throws a 409 naming the conflicting assets and their current holders.</summary>
    public DeviceLease AcquireOrThrow(
        DeviceLeaseOwner owner,
        IReadOnlyList<DeviceLeaseResource> resources,
        string action,
        TimeSpan? ttl = null)
    {
        var lease = TryAcquire(owner, resources, out var conflicts, ttl);
        if (lease is not null) return lease;
        throw new ApiConflictException($"{action}: {DescribeConflicts(conflicts)}.");
    }

    /// <summary>TTL fallback for request-scoped manual operations whose release path was lost.</summary>
    public static readonly TimeSpan ManualLeaseTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Request-scoped lease for one manual hardware operation (tag write, robot command, camera
    /// trigger): held while the request executes and auto-released by the TTL if the request never
    /// reaches its release path.
    /// </summary>
    public DeviceLease AcquireManualOrThrow(string resourceKind, string resourceId, string action, TimeSpan? ttl = null)
        => AcquireManualOrThrow([new DeviceLeaseResource(resourceKind, resourceId)], action, ttl);

    /// <summary>Multi-asset variant of <see cref="AcquireManualOrThrow(string, string, string, TimeSpan?)"/> (e.g. a camera group).</summary>
    public DeviceLease AcquireManualOrThrow(IReadOnlyList<DeviceLeaseResource> resources, string action, TimeSpan? ttl = null)
        => AcquireOrThrow(
            DeviceLeaseOwner.Manual(Guid.NewGuid().ToString("N")[..8]),
            resources,
            action,
            ttl ?? ManualLeaseTtl);

    /// <summary>Assets of <paramref name="resources"/> currently held by any owner, ordered deterministically.</summary>
    public IReadOnlyList<DeviceLeaseConflict> DescribeConflicts(IReadOnlyList<DeviceLeaseResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            SweepExpiredLocked(now);
            return CollectConflictsLocked(Normalize(resources), owner: null, now);
        }
    }

    /// <summary>
    /// True while <paramref name="owner"/> still holds every resource (vacuously true for empty sets);
    /// used by long-lived sessions to assert ownership before touching hardware again.
    /// </summary>
    public bool IsHeldBy(DeviceLeaseOwner owner, IReadOnlyList<DeviceLeaseResource> resources)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resources);
        lock (_gate)
        {
            SweepExpiredLocked(_time.GetUtcNow());
            foreach (var resource in Normalize(resources))
            {
                if (!_leases.TryGetValue((resource.Kind, resource.Id), out var entry) || !IsSameOwner(entry.Owner, owner))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Pure check for dependency mutations: throws a 409 while any owner (production, debug session,
    /// run or manual operation) still holds the asset.
    /// </summary>
    public void EnsureNotHeld(string resourceKind, string resourceId, string action)
    {
        var conflicts = DescribeConflicts([new DeviceLeaseResource(resourceKind, resourceId)]);
        if (conflicts.Count == 0) return;
        throw new ApiConflictException($"{action}: {DescribeConflicts(conflicts)}.");
    }

    /// <summary>Renders conflicts as "kind 'id' is held by holder 'owner' (until released|auto-release in Ns)".</summary>
    public static string DescribeConflicts(IReadOnlyList<DeviceLeaseConflict> conflicts)
        => string.Join("; ", conflicts.Select(conflict =>
        {
            var holder = string.IsNullOrWhiteSpace(conflict.Owner.Description) ? conflict.Owner.Kind : conflict.Owner.Description;
            var suffix = conflict.Reason
                ?? (conflict.Abandoned
                    ? "lease TTL elapsed but the holder never released it — takeover requires the device to report idle"
                    : conflict.Remaining is { } remaining
                        ? $"auto-release in {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))}s"
                        : "until released");
            return $"{conflict.ResourceKind} '{conflict.ResourceId}' is held by {holder} '{conflict.Owner.Id}' ({suffix})";
        }));

    internal void Release(DeviceLease lease)
    {
        lock (_gate)
        {
            foreach (var resource in lease.Resources)
            {
                var key = (resource.Kind, resource.Id);
                // A stale handle must not release resources re-acquired under a newer token.
                if (_leases.TryGetValue(key, out var entry) && entry.Token == lease.Token)
                    _leases.TryRemove(key, out _);
            }
        }
    }

    private List<DeviceLeaseConflict> CollectConflictsLocked(
        IReadOnlyList<DeviceLeaseResource> normalized,
        DeviceLeaseOwner? owner,
        DateTimeOffset now)
    {
        var conflicts = new List<DeviceLeaseConflict>();
        foreach (var resource in normalized)
        {
            // R01：真实派发中的命令是独立的冲突来源——即使租约条目已不存在（例如持有者已释放），
            // 未收尾的旧命令仍在设备上执行，持有者不同的获取必须被拒绝，直到该命令返回。
            if (_commandsInFlight.TryGetValue((resource.Kind, resource.Id), out var inFlight) &&
                (owner is null || !IsSameOwner(inFlight.Owner, owner)))
            {
                conflicts.Add(new DeviceLeaseConflict(
                    resource.Kind,
                    resource.Id,
                    inFlight.Owner,
                    Remaining: null,
                    Abandoned: false,
                    Reason: "a command from this holder is still being dispatched"));
                continue;
            }

            if (!_leases.TryGetValue((resource.Kind, resource.Id), out var entry)) continue;
            if (owner is not null && IsSameOwner(entry.Owner, owner)) continue;
            conflicts.Add(new DeviceLeaseConflict(
                resource.Kind,
                resource.Id,
                entry.Owner,
                entry.ExpiresAt is { } expires ? expires - now : null,
                entry.Abandoned));
        }
        return conflicts;
    }

    private void SweepExpiredLocked(DateTimeOffset now)
    {
        // R02：TTL 到期不再直接删除租约——那只证明"持有者可能失联"，不证明"底层操作已结束"。
        // 过期的条目转为 Abandoned：仍算冲突，只有获取路径结合活动探针确认资源空闲后才接管。
        foreach (var entry in _leases)
        {
            if (entry.Value.ExpiresAt is { } expires && expires <= now)
                _leases[entry.Key] = entry.Value with { ExpiresAt = null, Abandoned = true };
        }
    }

    private static bool IsSameOwner(DeviceLeaseOwner left, DeviceLeaseOwner right)
        => string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);

    private static List<DeviceLeaseResource> Normalize(IReadOnlyList<DeviceLeaseResource> resources)
    {
        var seen = new HashSet<(string Kind, string Id)>(KeyComparer);
        var normalized = new List<DeviceLeaseResource>(resources.Count);
        foreach (var resource in resources)
        {
            if (string.IsNullOrWhiteSpace(resource.Kind) || string.IsNullOrWhiteSpace(resource.Id)) continue;
            var key = (Kind: resource.Kind.Trim().ToLowerInvariant(), Id: resource.Id.Trim());
            if (seen.Add(key)) normalized.Add(new DeviceLeaseResource(key.Kind, key.Id));
        }
        normalized.Sort(static (left, right) =>
        {
            var byKind = string.Compare(left.Kind, right.Kind, StringComparison.Ordinal);
            return byKind != 0 ? byKind : string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
        });
        return normalized;
    }
}

/// <summary>
/// Handle over one successful acquisition. Disposing releases every leased resource; disposal is
/// idempotent and a superseded handle (same owner re-acquired) releases nothing.
/// A02：租约句柄同时是<b>命令派发的所有权令牌</b>——执行流进入 <see cref="DeviceCommandOwnership"/>
/// 环境后，真实派发会校验本令牌的代次；被换代/撤销后旧句柄不能再命令任何资源。
/// </summary>
public sealed class DeviceLease : IDisposable, IDeviceCommandOwnershipToken
{
    private readonly DeviceLeaseRegistry _registry;
    private int _released;

    internal DeviceLease(
        DeviceLeaseRegistry registry,
        DeviceLeaseOwner owner,
        IReadOnlyList<DeviceLeaseResource> resources,
        long token)
    {
        _registry = registry;
        Owner = owner;
        Resources = resources;
        Token = token;
    }

    public DeviceLeaseOwner Owner { get; }

    /// <summary>A02：所有权令牌的持有者描述（拒绝消息/审计用）。</summary>
    public string OwnerDescription
        => string.IsNullOrWhiteSpace(Owner.Description) ? $"{Owner.Kind} '{Owner.Id}'" : $"{Owner.Description} '{Owner.Id}'";

    /// <summary>A02：该令牌是否由指定注册表签发（跨注册表的令牌不做代次比较）。</summary>
    internal bool IsIssuedBy(DeviceLeaseRegistry registry) => ReferenceEquals(_registry, registry);

    /// <summary>R01：目标资源是否属于本租约声明的范围（大小写不敏感，与仲裁键一致）。</summary>
    internal bool CoversResource((string Kind, string Id) key)
        => Resources.Any(resource =>
            string.Equals(resource.Kind, key.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(resource.Id, key.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>R01：租约范围的确定性描述（拒绝消息中展示"租了什么"）。</summary>
    internal string DescribeResources()
        => Resources.Count == 0 ? "<none: offline/noop lease>" : string.Join(", ", Resources.Select(x => $"{x.Kind}:{x.Id}"));

    public IReadOnlyList<DeviceLeaseResource> Resources { get; }

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    internal long Token { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _registry.Release(this);
    }
}