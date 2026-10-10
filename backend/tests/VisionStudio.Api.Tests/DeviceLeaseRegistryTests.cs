using System.Collections.Concurrent;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Hardware lease registry: exclusive all-or-nothing acquisition, deterministic conflict reporting,
/// idempotent/token-guarded release, TTL auto-release and concurrency safety.
/// </summary>
public sealed class DeviceLeaseRegistryTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static DeviceLeaseResource Res(string kind, string id) => new(kind, id);

    [Fact]
    public void TryAcquire_IsExclusivePerResource_AndAllOrNothing()
    {
        var registry = new DeviceLeaseRegistry();
        using var production = registry.AcquireOrThrow(
            DeviceLeaseOwner.Production("job-1"), [Res("device", "d1")], "start");

        var debugLease = registry.TryAcquire(
            DeviceLeaseOwner.DebugSession("session-1"),
            [Res("device", "d1"), Res("device", "d2")],
            out var conflicts);

        Assert.Null(debugLease);
        var conflict = Assert.Single(conflicts);
        Assert.Equal("device", conflict.ResourceKind);
        Assert.Equal("d1", conflict.ResourceId);
        Assert.Equal("Production", conflict.Owner.Kind);
        Assert.Equal("job-1", conflict.Owner.Id);
        Assert.Null(conflict.Remaining);

        // The rejected acquisition must not have leaked its unrelated resource.
        using var manual = registry.AcquireOrThrow(DeviceLeaseOwner.Manual("request-1"), [Res("device", "d2")], "operate");
        Assert.Equal(2, registry.ActiveLeaseCount);
    }

    [Fact]
    public void AcquireOrThrow_ReportsAssetAndHolder()
    {
        var registry = new DeviceLeaseRegistry();
        using var lease = registry.AcquireOrThrow(DeviceLeaseOwner.Production("job-1"), [Res("device", "d1")], "start");

        var ex = Assert.Throws<ApiConflictException>(() => registry.AcquireOrThrow(
            DeviceLeaseOwner.Run("run-9"), [Res("device", "d1")], "Cannot run workflow 'demo'"));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("Cannot run workflow 'demo'", ex.Message);
        Assert.Contains("device 'd1'", ex.Message);
        Assert.Contains("Production Runtime 'job-1'", ex.Message);
        Assert.Contains("until released", ex.Message);
    }

    [Fact]
    public void Release_IsIdempotent_AndStaleHandlesCannotReleaseRefreshedLease()
    {
        var registry = new DeviceLeaseRegistry();
        var owner = DeviceLeaseOwner.Production("job-1");

        var first = registry.AcquireOrThrow(owner, [Res("device", "d1")], "start");
        var second = registry.AcquireOrThrow(owner, [Res("device", "d1")], "restart");
        Assert.Equal(1, registry.ActiveLeaseCount);

        first.Dispose();
        first.Dispose();
        Assert.True(first.IsReleased);
        Assert.Null(registry.TryAcquire(DeviceLeaseOwner.Run("run-1"), [Res("device", "d1")], out _));

        second.Dispose();
        second.Dispose();
        Assert.Equal(0, registry.ActiveLeaseCount);

        using var third = registry.AcquireOrThrow(DeviceLeaseOwner.Run("run-1"), [Res("device", "d1")], "run");
        Assert.Equal(1, registry.ActiveLeaseCount);
    }

    [Fact]
    public void TtlLease_ExpiresIntoAbandoned_TakeoverRequiresIdleProbe()
    {
        // R02：TTL 到期只证明"持有者可能失联"，不证明"底层操作已结束"（机器人等待上限 120 秒、
        // 驱动可能忽略取消）。过期租约转为 Abandoned：探针确认资源空闲前一律拒绝接管；
        // 确认空闲后接管换发新代次，陈旧句柄的释放不再影响新持有者（fencing）。
        var clock = new FakeTimeProvider();
        var probe = new ControllableProbe();
        var registry = new DeviceLeaseRegistry(clock, probe);
        using var manual = registry.AcquireOrThrow(
            DeviceLeaseOwner.Manual("request-1"), [Res("robot", "r1")], "operate", ttl: TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(10));
        var blocked = registry.TryAcquire(DeviceLeaseOwner.Production("job-1"), [Res("robot", "r1")], out var conflicts);
        Assert.Null(blocked);
        var conflict = Assert.Single(conflicts);
        Assert.Equal(TimeSpan.FromSeconds(50), conflict.Remaining);
        Assert.False(conflict.Abandoned);
        Assert.Contains("auto-release in 50s", DeviceLeaseRegistry.DescribeConflicts(conflicts));

        // TTL 到期：租约不再被自动删除——操作仍可能活跃。
        clock.Advance(TimeSpan.FromSeconds(51));
        probe.Busy = true;
        Assert.Null(registry.TryAcquire(DeviceLeaseOwner.Production("job-1"), [Res("robot", "r1")], out var busyConflicts));
        Assert.True(Assert.Single(busyConflicts).Abandoned);
        Assert.Contains("takeover requires the device to report idle", DeviceLeaseRegistry.DescribeConflicts(busyConflicts));

        // 探针确认空闲 → 允许接管。
        probe.Busy = false;
        using var recovered = registry.AcquireOrThrow(DeviceLeaseOwner.Production("job-1"), [Res("robot", "r1")], "start");
        Assert.Equal(1, registry.ActiveLeaseCount);

        // 陈旧句柄释放必须不影响新持有者（token 换代）。
        manual.Dispose();
        Assert.Equal(1, registry.ActiveLeaseCount);
        var holder = Assert.Single(registry.DescribeConflicts([Res("robot", "r1")]));
        Assert.Equal("job-1", holder.Owner.Id); // 仍由接管者持有，而不是被旧句柄清空
    }

    /// <summary>R02：可控活动探针——模拟"底层操作仍在执行/已结束"。</summary>
    private sealed class ControllableProbe : IDeviceActivityProbe
    {
        public bool Busy { get; set; }
        public bool IsResourceBusy(string resourceKind, string resourceId) => Busy;
    }

    [Fact]
    public void DescribeConflicts_IsDeterministic_AndDeduplicatesResources()
    {
        var registry = new DeviceLeaseRegistry();
        using var lease = registry.AcquireOrThrow(
            DeviceLeaseOwner.Production("job-1"),
            [Res("robot", "r2"), Res("camera", "c1"), Res("device", "d3")],
            "start");

        var first = registry.DescribeConflicts([Res("device", "d3"), Res("robot", "r2"), Res("camera", "c1"), Res("device", "d3")]);
        Assert.Equal(["camera:c1", "device:d3", "robot:r2"], first.Select(x => $"{x.ResourceKind}:{x.ResourceId}"));

        var second = registry.DescribeConflicts([Res("robot", "r2"), Res("camera", "c1"), Res("device", "d3")]);
        Assert.Equal(first, second);

        Assert.Empty(registry.DescribeConflicts([Res("camera", "other"), Res("device", "d4")]));
    }

    [Fact]
    public void ConcurrentTryAcquire_GrantsExactlyOneOwner()
    {
        var registry = new DeviceLeaseRegistry();
        var winners = new ConcurrentBag<DeviceLease>();
        var losers = 0;
        var losersWithoutConflicts = 0;

        Parallel.For(0, 64, i =>
        {
            var lease = registry.TryAcquire(DeviceLeaseOwner.Run($"run-{i}"), [Res("camera", "cam-1")], out var conflicts);
            if (lease is null)
            {
                Interlocked.Increment(ref losers);
                if (conflicts.Count == 0) Interlocked.Increment(ref losersWithoutConflicts);
                return;
            }
            winners.Add(lease);
        });

        Assert.Single(winners);
        Assert.Equal(63, losers);
        Assert.Equal(0, losersWithoutConflicts);
        Assert.Equal(1, registry.ActiveLeaseCount);

        using var winner = winners.Single();
        winner.Dispose();
        Assert.Equal(0, registry.ActiveLeaseCount);
        using var next = registry.AcquireOrThrow(DeviceLeaseOwner.Run("run-next"), [Res("camera", "cam-1")], "run");
    }

    [Fact]
    public void EnsureNotHeld_ThrowsWhileAnyOwnerHoldsAsset()
    {
        var registry = new DeviceLeaseRegistry();
        registry.EnsureNotHeld("device", "d1", "Cannot delete device 'd1'");

        using var lease = registry.AcquireOrThrow(DeviceLeaseOwner.DebugSession("session-1"), [Res("device", "d1")], "create");
        var ex = Assert.Throws<ApiConflictException>(() => registry.EnsureNotHeld("device", "d1", "Cannot delete device 'd1'"));
        Assert.Contains("Debug Session 'session-1'", ex.Message);
        Assert.Contains("device 'd1'", ex.Message);
    }

    [Fact]
    public void IsHeldBy_ReflectsCurrentOwnership()
    {
        var registry = new DeviceLeaseRegistry();
        var owner = DeviceLeaseOwner.DebugSession("session-1");
        IReadOnlyList<DeviceLeaseResource> resources = [Res("device", "d1"), Res("camera", "c1")];

        Assert.False(registry.IsHeldBy(owner, resources));
        var lease = registry.AcquireOrThrow(owner, resources, "create");
        Assert.True(registry.IsHeldBy(owner, resources));
        Assert.True(registry.IsHeldBy(owner, [Res("device", "d1")]));
        Assert.True(registry.IsHeldBy(owner, []));
        Assert.False(registry.IsHeldBy(DeviceLeaseOwner.Production("job-1"), resources));
        Assert.False(registry.IsHeldBy(owner, [Res("device", "d2")]));

        lease.Dispose();
        Assert.False(registry.IsHeldBy(owner, resources));
    }

    [Fact]
    public void OfflineWorkflow_AcquiresNoopLease()
    {
        var registry = new DeviceLeaseRegistry();
        using var lease = registry.AcquireOrThrow(DeviceLeaseOwner.Run("run-offline"), [], "run");
        Assert.Empty(lease.Resources);
        Assert.Equal(0, registry.ActiveLeaseCount);
    }

    [Fact]
    public void FromReferences_MapsEveryAssetKind()
    {
        var resources = DeviceLeaseResource.FromReferences(new WorkflowRuntimeReferences(
            Cameras: ["cam-1"],
            Devices: ["dev-1", "dev-2"],
            Robots: ["rob-1"]));

        Assert.Equal(
            ["camera:cam-1", "device:dev-1", "device:dev-2", "robot:rob-1"],
            resources.Select(x => $"{x.Kind}:{x.Id}").OrderBy(x => x, StringComparer.Ordinal));
    }
}