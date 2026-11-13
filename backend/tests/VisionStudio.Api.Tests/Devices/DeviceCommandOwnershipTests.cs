using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Device;

namespace VisionStudio.Api.Tests;

/// <summary>
/// A02 回归（2026-10-11 复审）：管理员释放过期租约后，旧调用不能再写同一设备。
/// 全链路验证：租约 + 所有权环境（执行入口）→ DeviceManager 真实派发边界 → 租约注册表代次/撤销校验。
/// 探针反例：旧执行在接管后继续写入 OLD-OWNER-STEP2 成功（value=OLD-OWNER-STEP2）。
/// </summary>
public sealed class DeviceCommandOwnershipTests
{
    [Fact]
    public async Task StaleOwner_CannotWriteDevice_AfterAdminForceReleaseAndTakeover()
    {
        using var factory = new StorageVisionStudioApiFactory();
        var devices = factory.Services.GetRequiredService<DeviceManager>();
        var registry = factory.Services.GetRequiredService<DeviceLeaseRegistry>();

        // 旧执行：手动租约（TTL 兜底）+ 所有权环境，第一步写成功。
        var oldLease = registry.AcquireManualOrThrow("device", "virtual-modbus-1", "first-step", ttl: TimeSpan.FromMilliseconds(50));
        using (DeviceCommandOwnership.Enter(oldLease))
        {
            await devices.WriteTagAsync("virtual-modbus-1", "statusText", "step1", autoConnect: true);
        }

        // TTL 到期：不自动接管；管理员释放成功后新执行取得租约。
        await Task.Delay(200);
        Assert.Null(registry.TryAcquire(
            DeviceLeaseOwner.Production("new-owner"), [new DeviceLeaseResource("device", "virtual-modbus-1")], out _));
        Assert.True(registry.TryForceRelease("device", "virtual-modbus-1", out _));
        using var newLease = registry.AcquireOrThrow(
            DeviceLeaseOwner.Production("new-owner"), [new DeviceLeaseResource("device", "virtual-modbus-1")], "takeover");

        // 旧执行的第二步写：派发边界必须拒绝（旧实现：写成功）。
        var stale = await Assert.ThrowsAsync<ApiConflictException>(async () =>
        {
            using var staleScope = DeviceCommandOwnership.Enter(oldLease);
            await devices.WriteTagAsync("virtual-modbus-1", "statusText", "OLD-OWNER-STEP2", autoConnect: true);
        });
        Assert.Contains("revoked", stale.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("step1", (await devices.ReadTagAsync("virtual-modbus-1", "statusText", fresh: true)).Value);

        // 新所有者正常写入。
        using (DeviceCommandOwnership.Enter(newLease))
        {
            await devices.WriteTagAsync("virtual-modbus-1", "statusText", "NEW-OWNER", autoConnect: true);
        }
        Assert.Equal("NEW-OWNER", (await devices.ReadTagAsync("virtual-modbus-1", "statusText", fresh: true)).Value);
    }

    [Fact]
    public async Task DisposedLease_CannotWriteDevice_WhenNoNewOwnerTookOver()
    {
        // R01 反例一（复审探针：released=True stillHeld=False value=DISPOSED-OWNER）：
        // 普通释放后且无接管者时，旧令牌的后续写入必须在真实派发边界被拒绝。
        using var factory = new StorageVisionStudioApiFactory();
        var devices = factory.Services.GetRequiredService<DeviceManager>();
        var registry = factory.Services.GetRequiredService<DeviceLeaseRegistry>();

        var lease = registry.AcquireManualOrThrow("device", "virtual-modbus-1", "first-step");
        using (DeviceCommandOwnership.Enter(lease))
        {
            await devices.WriteTagAsync("virtual-modbus-1", "statusText", "STEP1", autoConnect: true);
        }

        lease.Dispose();

        var stale = await Assert.ThrowsAsync<ApiConflictException>(async () =>
        {
            using var staleScope = DeviceCommandOwnership.Enter(lease);
            await devices.WriteTagAsync("virtual-modbus-1", "statusText", "DISPOSED-OWNER", autoConnect: true);
        });
        Assert.Contains("released", stale.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("STEP1", (await devices.ReadTagAsync("virtual-modbus-1", "statusText", fresh: true)).Value);
    }

    [Fact]
    public async Task LeaseForAnotherResource_CannotWriteDevice()
    {
        // R01 反例二（复审探针：resources=other-device value=OUTSIDE-LEASE）：
        // 租 A 资源却写 B 资源，必须在真实派发边界被拒绝。
        using var factory = new StorageVisionStudioApiFactory();
        var devices = factory.Services.GetRequiredService<DeviceManager>();
        var registry = factory.Services.GetRequiredService<DeviceLeaseRegistry>();

        using var lease = registry.AcquireManualOrThrow("device", "other-device", "wrong-target");
        var rejected = await Assert.ThrowsAsync<ApiConflictException>(async () =>
        {
            using var scope = DeviceCommandOwnership.Enter(lease);
            await devices.WriteTagAsync("virtual-modbus-1", "statusText", "OUTSIDE-LEASE", autoConnect: true);
        });
        Assert.Contains("outside the lease scope", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitSystemContext_CanWriteDevice()
    {
        // R01：启动/维护/验收路径以显式系统上下文派发——保持可达但不再依赖"无上下文默认放行"。
        using var factory = new StorageVisionStudioApiFactory();
        var devices = factory.Services.GetRequiredService<DeviceManager>();

        using (DeviceCommandOwnership.EnterSystem("probe-maintenance", "unit test"))
        {
            await devices.WriteTagAsync("virtual-modbus-1", "statusText", "SYSTEM-WRITE", autoConnect: true);
        }
        Assert.Equal("SYSTEM-WRITE", (await devices.ReadTagAsync("virtual-modbus-1", "statusText", fresh: true)).Value);

        // 无任何上下文：拒绝（不再是兼容放行）。
        await Assert.ThrowsAsync<ApiConflictException>(() =>
            devices.WriteTagAsync("virtual-modbus-1", "statusText", "NO-CONTEXT", autoConnect: true));
    }
}
