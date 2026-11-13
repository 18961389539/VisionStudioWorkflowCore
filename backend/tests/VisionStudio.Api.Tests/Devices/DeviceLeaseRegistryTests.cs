using System.Collections.Concurrent;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

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
    public void TtlLease_ExpiresIntoAbandoned_RequiresExplicitAdminRelease()
    {
        // R02/N04：TTL 到期只证明"持有者可能失联"，不证明"底层操作已结束"。过期租约转为
        // Abandoned 后**不做自动接管**（瞬时设备空闲不足以证明旧调用不会再写设备，而下发路径
        // 没有代次校验）——恢复路径只有进程重启或管理员显式释放（TryForceRelease，要求空闲 + 审计）。
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

        // TTL 到期：租约不再被自动删除，也**不允许自动接管**（即使探针报告空闲）。
        clock.Advance(TimeSpan.FromSeconds(51));
        probe.Busy = false;
        Assert.Null(registry.TryAcquire(DeviceLeaseOwner.Production("job-1"), [Res("robot", "r1")], out var abandonedConflicts));
        Assert.True(Assert.Single(abandonedConflicts).Abandoned);

        // 管理员显式释放：设备仍在工作时拒绝。
        probe.Busy = true;
        Assert.False(registry.TryForceRelease("robot", "r1", out var busyReason));
        Assert.Contains("active work", busyReason);

        // 设备确认空闲后，管理员释放成功 → 新所有者可用（旧句柄 token 已失效）。
        probe.Busy = false;
        Assert.True(registry.TryForceRelease("robot", "r1", out _));
        using var recovered = registry.AcquireOrThrow(DeviceLeaseOwner.Production("job-1"), [Res("robot", "r1")], "start");
        Assert.Equal(1, registry.ActiveLeaseCount);

        // 陈旧句柄释放不得影响新持有者（token 换代）。
        manual.Dispose();
        var holder = Assert.Single(registry.DescribeConflicts([Res("robot", "r1")]));
        Assert.Equal("job-1", holder.Owner.Id);
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

    [Fact]
    public void CommandPermit_RejectsStaleOwner_AfterAdminForceReleaseAndTakeover()
    {
        // A02 回归（2026-10-11 复审）：管理员释放过期租约后，旧执行完成过第一步写也不能再写第二步——
        // 真实派发边界校验递增所有权令牌（探针反例：旧调用 OLD-OWNER-STEP2 写入成功）。
        var clock = new FakeTimeProvider();
        var probe = new ControllableProbe();
        var registry = new DeviceLeaseRegistry(clock, probe);
        var oldLease = registry.AcquireOrThrow(
            DeviceLeaseOwner.Manual("request-1"), [Res("device", "d1")], "first-step", ttl: TimeSpan.FromSeconds(60));
        using (DeviceCommandOwnership.Enter(oldLease))
        using (registry.AcquireCommandPermit("device", "d1"))
        {
            // 第一步写在租约有效期内完成。
        }

        // TTL 到期仍不自动接管；管理员释放成功后新执行取得租约。
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(registry.TryAcquire(DeviceLeaseOwner.Production("new-owner"), [Res("device", "d1")], out _));
        Assert.True(registry.TryForceRelease("device", "d1", out _));
        using var newLease = registry.AcquireOrThrow(DeviceLeaseOwner.Production("new-owner"), [Res("device", "d1")], "takeover");

        // 旧执行的第二步派发：必须被拒绝（旧实现：成功写入）。
        var stale = Assert.Throws<ApiConflictException>(() =>
        {
            using var staleScope = DeviceCommandOwnership.Enter(oldLease);
            registry.AcquireCommandPermit("device", "d1");
        });
        Assert.Contains("revoked", stale.Message, StringComparison.OrdinalIgnoreCase);

        // 新执行正常派发。
        using (DeviceCommandOwnership.Enter(newLease))
        using (registry.AcquireCommandPermit("device", "d1"))
        {
        }
    }

    [Fact]
    public void CommandPermit_BlocksAdminForceRelease_WhileCommandIsBeingDispatched()
    {
        // A02 竞争窗口：许可存续（真实派发进行中）期间管理员释放必须被拒绝——
        // 检查与派发之间不留"释放后旧写仍落盘"的窗口。
        var clock = new FakeTimeProvider();
        var probe = new ControllableProbe();
        var registry = new DeviceLeaseRegistry(clock, probe);
        var lease = registry.AcquireOrThrow(
            DeviceLeaseOwner.Manual("request-1"), [Res("device", "d1")], "operate", ttl: TimeSpan.FromSeconds(60));
        using var scope = DeviceCommandOwnership.Enter(lease);
        clock.Advance(TimeSpan.FromSeconds(61));

        var permit = registry.AcquireCommandPermit("device", "d1");
        Assert.False(registry.TryForceRelease("device", "d1", out var reason));
        Assert.Contains("still being dispatched", reason);

        permit.Dispose();
        Assert.True(registry.TryForceRelease("device", "d1", out _));
    }

    [Fact]
    public void CommandPermit_RejectsSupersededOwner_EvenWithoutForceRelease()
    {
        // 正常释放后被新所有者取得：旧令牌换代 ⇒ 旧执行派发被拒。
        var registry = new DeviceLeaseRegistry();
        var oldLease = registry.AcquireOrThrow(DeviceLeaseOwner.Manual("request-1"), [Res("device", "d1")], "operate");
        using (DeviceCommandOwnership.Enter(oldLease))
        using (registry.AcquireCommandPermit("device", "d1"))
        {
        }

        oldLease.Dispose();
        using var newLease = registry.AcquireOrThrow(DeviceLeaseOwner.Production("job-1"), [Res("device", "d1")], "start");

        Assert.Throws<ApiConflictException>(() =>
        {
            using var staleScope = DeviceCommandOwnership.Enter(oldLease);
            registry.AcquireCommandPermit("device", "d1");
        });
    }

    [Fact]
    public void CommandPermit_RejectsReleasedLease_WhenNoNewOwnerTookOver()
    {
        // R01 反例一（2026-10-11 复审）：普通释放（Dispose）后且无新持有者时，旧令牌仍能派发。
        // 现在：IsReleased 校验在同一临界区内完成 ⇒ 拒绝。
        var registry = new DeviceLeaseRegistry();
        var lease = registry.AcquireOrThrow(DeviceLeaseOwner.Manual("request-1"), [Res("device", "d1")], "operate");
        using (DeviceCommandOwnership.Enter(lease))
        using (registry.AcquireCommandPermit("device", "d1"))
        {
        }

        lease.Dispose();
        Assert.True(lease.IsReleased);
        Assert.False(registry.IsHeldBy(lease.Owner, lease.Resources));

        var rejected = Assert.Throws<ApiConflictException>(() =>
        {
            using var scope = DeviceCommandOwnership.Enter(lease);
            registry.AcquireCommandPermit("device", "d1");
        });
        Assert.Contains("released", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandPermit_RejectsResourceOutsideLeaseScope()
    {
        // R01 反例二：只租了 other-device，却对未租用的 d1 派发（范围外资源）。
        var registry = new DeviceLeaseRegistry();
        using var lease = registry.AcquireOrThrow(DeviceLeaseOwner.Manual("request-1"), [Res("device", "other-device")], "operate");

        var rejected = Assert.Throws<ApiConflictException>(() =>
        {
            using var scope = DeviceCommandOwnership.Enter(lease);
            registry.AcquireCommandPermit("device", "d1");
        });
        Assert.Contains("outside the lease scope", rejected.Message, StringComparison.OrdinalIgnoreCase);

        // 租约范围内的资源正常放行。
        using (DeviceCommandOwnership.Enter(lease))
        using (registry.AcquireCommandPermit("device", "other-device"))
        {
        }
    }

    [Fact]
    public void CommandPermit_RejectsMissingContext_AndForeignRegistry()
    {
        // R01：无上下文不再默认放行；异地注册表的令牌同样拒绝。
        var registry = new DeviceLeaseRegistry();
        var noContext = Assert.Throws<ApiConflictException>(() => registry.AcquireCommandPermit("device", "d1"));
        Assert.Contains("no device-command ownership context", noContext.Message, StringComparison.OrdinalIgnoreCase);

        using var lease = registry.AcquireOrThrow(DeviceLeaseOwner.Manual("request-1"), [Res("device", "d1")], "operate");
        var foreignRegistry = new DeviceLeaseRegistry();
        var rejected = Assert.Throws<ApiConflictException>(() =>
        {
            using var scope = DeviceCommandOwnership.Enter(lease);
            foreignRegistry.AcquireCommandPermit("device", "d1");
        });
        Assert.Contains("different lease registry", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandPermit_AllowsExplicitSystemContext()
    {
        // R01：启动/维护/验收路径必须显式进入系统上下文，而不是靠"无上下文默认放行"。
        var registry = new DeviceLeaseRegistry();
        using (DeviceCommandOwnership.EnterSystem("probe-maintenance", "unit test"))
        using (registry.AcquireCommandPermit("device", "d1"))
        {
        }

        // 系统上下文许可同样登记在途：进行中禁止管理员强制释放（避免派发窗口内被换代）。
        var clock = new FakeTimeProvider();
        var probe = new ControllableProbe();
        var registry2 = new DeviceLeaseRegistry(clock, probe);
        var lease = registry2.AcquireOrThrow(
            DeviceLeaseOwner.Manual("request-1"), [Res("device", "d1")], "operate", ttl: TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(61));
        using var systemScope = DeviceCommandOwnership.EnterSystem("probe-maintenance", "unit test");
        using var permit = registry2.AcquireCommandPermit("device", "d1");
        Assert.False(registry2.TryForceRelease("device", "d1", out var reason));
        Assert.Contains("still being dispatched", reason);
    }

    [Fact]
    public void TryAcquire_IsBlocked_WhileAnotherHoldersCommandIsInFlight()
    {
        // R01：真实派发中的旧命令是独立冲突来源——即使租约已被普通释放，旧命令未收尾前
        // 持有者不同的获取必须被拒绝（防"释放→接管→旧命令仍在设备上执行"的重叠窗口）。
        var registry = new DeviceLeaseRegistry();
        var oldLease = registry.AcquireOrThrow(DeviceLeaseOwner.Manual("request-1"), [Res("device", "d1")], "operate");
        using var scope = DeviceCommandOwnership.Enter(oldLease);
        var permit = registry.AcquireCommandPermit("device", "d1"); // 模拟派发进行中
        oldLease.Dispose(); // 普通释放（异常/提前释放路径）

        var acquired = registry.TryAcquire(DeviceLeaseOwner.Production("job-1"), [Res("device", "d1")], out var conflicts);
        Assert.Null(acquired);
        var conflict = Assert.Single(conflicts);
        Assert.Contains("still being dispatched", conflict.Reason);
        Assert.Contains("still being dispatched", DeviceLeaseRegistry.DescribeConflicts(conflicts));

        permit.Dispose();
        using var recovered = registry.AcquireOrThrow(DeviceLeaseOwner.Production("job-1"), [Res("device", "d1")], "start");
    }
}