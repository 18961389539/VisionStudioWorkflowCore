using System.Collections.Concurrent;
using VisionStudio.Api.Infrastructure;

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
public sealed record DeviceLeaseConflict(string ResourceKind, string ResourceId, DeviceLeaseOwner Owner, TimeSpan? Remaining, bool Abandoned = false);

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
/// </summary>
public sealed class DeviceLeaseRegistry
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

    /// <param name="probe">
    /// R02：可选的活动探针。为 null 时 Abandoned 租约不允许被接管（最保守）。
    /// </param>
    public DeviceLeaseRegistry(TimeProvider? timeProvider = null, IDeviceActivityProbe? probe = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _probe = probe;
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
                // R02：接管规则——只有"全部冲突都是 Abandoned"且"活动探针确认每个资源空闲"时
                // 才回收陈旧条目并授予新代次（token 递增；旧句柄的 Release 因 token 不匹配而失效）。
                // 任何一项仍在忙碌（机器人 120 秒等待、忽略取消的驱动调用）都必须继续拒绝。
                if (_probe is null ||
                    conflicts.Any(x => !x.Abandoned) ||
                    conflicts.Any(x => _probe.IsResourceBusy(x.ResourceKind, x.ResourceId)))
                {
                    return null;
                }
                foreach (var conflict in conflicts)
                    _leases.TryRemove((conflict.ResourceKind, conflict.ResourceId), out _);
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
            var suffix = conflict.Abandoned
                ? "lease TTL elapsed but the holder never released it — takeover requires the device to report idle"
                : conflict.Remaining is { } remaining
                    ? $"auto-release in {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))}s"
                    : "until released";
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
/// </summary>
public sealed class DeviceLease : IDisposable
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

    public IReadOnlyList<DeviceLeaseResource> Resources { get; }

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    internal long Token { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _registry.Release(this);
    }
}