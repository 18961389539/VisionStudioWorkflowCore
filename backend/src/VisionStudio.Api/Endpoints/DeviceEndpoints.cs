using VisionStudio.Api.Contracts;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Device;

namespace VisionStudio.Api.Endpoints;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/device-address-templates", () => Results.Ok(new DeviceAddressTemplate[]
        {
            new("NModbusTcpDeviceDriver", "Modbus TCP", ["C:0", "DI:0", "HR:0", "IR:0"], "0-based addresses. Boolean uses C/DI; Integer and Double use two registers (Int32 / Float32). 32-bit order: ABCD/CDAB/BADC/DCBA."),
            new("S7NetPlusDeviceDriver", "Siemens S7", ["DB10.DBX0.0", "DB10.DBW2", "DB10.DBD4", "M10.0", "MW20", "MD24"], "S7.Net Plus absolute addresses. Double uses a DWord address and is decoded as IEEE754 float32.")
        }));

        app.MapPost("/api/devices/register/modbus-tcp", async (
            ModbusTcpRegistrationRequest request,
            DeviceManager devices,
            DeviceProfileStore profiles,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var profile = new DeviceProfile(
                request.Id, "modbus-tcp", request.Name, request.Host, request.Port, request.TimeoutMs,
                request.UnitId, request.RegisterOrder, Tags: request.Tags ?? Array.Empty<DeviceTagDefinition>());
            return await RegisterDeviceAsync(profile, devices, profiles, loggerFactory.CreateLogger("DeviceRegistration"), ct);
        }).RequireEngineer("device.register.modbus", "device");

        app.MapPost("/api/devices/register/s7", async (
            S7NetPlusRegistrationRequest request,
            DeviceManager devices,
            DeviceProfileStore profiles,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var profile = new DeviceProfile(
                request.Id, "s7", request.Name, request.Host, request.Port, request.TimeoutMs,
                CpuType: request.CpuType, Rack: request.Rack, Slot: request.Slot,
                Tags: request.Tags ?? Array.Empty<DeviceTagDefinition>());
            return await RegisterDeviceAsync(profile, devices, profiles, loggerFactory.CreateLogger("DeviceRegistration"), ct);
        }).RequireEngineer("device.register.s7", "device");

        app.MapGet("/api/device-profiles", async (DeviceProfileStore profiles, CancellationToken ct) =>
            Results.Ok(await profiles.ListAsync(ct)));

        app.MapDelete("/api/devices/{id}", async (string id, DeviceManager devices, DeviceProfileStore profiles, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureDependencyMutationAllowed("device", id);
            if (string.Equals(id, "virtual-modbus-1", StringComparison.OrdinalIgnoreCase))
                throw new ApiConflictException("The bundled virtual device is retained as the offline test baseline.");
            // R3-01：先核对持久配置归属（在任何副作用之前）——清洗后的路径可能命中另一台设备的
            // 遗留文件（如 audit.a 与 audit_a），删除绝不能误伤别人；归属不符即拒绝且不改变状态。
            DeviceProfile? persisted;
            try
            {
                persisted = await profiles.FindAsync(id, ct);
            }
            catch (Exception ex)
            {
                throw new ApiConflictException(
                    $"The persisted profile for '{id}' cannot be read ({ex.Message}). Repair or remove that file explicitly before deleting the device.", ex);
            }
            if (persisted is not null && !string.Equals(persisted.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiConflictException(
                    $"Refusing to delete device '{id}': its profile file holds device '{persisted.Id}'. " +
                    $"Delete '{persisted.Id}' instead, or repair the profile file after verification.");
            }
            if (!await devices.RemoveAsync(id, ct)) return Results.NotFound();
            await profiles.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequireEngineer("device.delete", "device");

        app.MapPost("/api/devices/{id}/read-all", async (string id, DeviceManager devices, CancellationToken ct) =>
            Results.Ok(await devices.ReadAllFreshAsync(id, autoConnect: true, cancellationToken: ct))).RequireAuthorization(SecurityPolicies.Operator);

        app.MapPut("/api/devices/{id}/tags", async (string id, DeviceBatchWriteRequest request, DeviceManager devices, DeviceLeaseRegistry leases, DeviceActionAuthorizationService auth, CancellationToken ct) =>
        {
            // 请求期硬件租约：写操作执行期间独占该设备（TTL 仅作为释放路径丢失时的兜底）
            using var lease = leases.AcquireManualOrThrow("device", id, $"Cannot write device '{id}'");
            // A02：进入所有权环境——真实派发边界据此校验租约代次（失去所有权的旧调用被拒绝）。
            using var commandScope = DeviceCommandOwnership.Enter(lease);
            // R01：统一动作授权——未闭合的安全意图阻断所有设备动作入口，并在动作前耐久登记本次意图。
            using var intent = auth.BeginManualIntent("device.writeTags", [id], []);
            await devices.WriteManyAsync(id, request.Values, autoConnect: true, cancellationToken: ct);
            // F02：写入确认成功才解除意图；抛异常/取消时由 Dispose 保留为 Unknown（阻断后续动作）。
            intent.Complete();
            return Results.Ok(devices.Get(id));
        }).RequireEngineer("device.tags.write", "device");

        app.MapGet("/api/devices", (DeviceManager devices) => Results.Ok(devices.List()));
        app.MapGet("/api/devices/{id}", (string id, DeviceManager devices) => Results.Ok(devices.Get(id)));

        app.MapPost("/api/devices/{id}/connect", async (string id, DeviceManager devices, CancellationToken ct) =>
        {
            await devices.ConnectAsync(id, ct);
            return Results.Ok(devices.Get(id));
        }).RequireOperator("device.connect", "device");

        app.MapPost("/api/devices/{id}/disconnect", async (string id, DeviceManager devices, CancellationToken ct) =>
        {
            await devices.DisconnectAsync(id, ct);
            return Results.Ok(devices.Get(id));
        }).RequireOperator("device.disconnect", "device");

        app.MapPut("/api/devices/{id}/settings", async (
            string id,
            DeviceRuntimeSettings request,
            DeviceManager devices,
            DeviceProfileStore profiles,
            ProductionRuntimeService production,
            CancellationToken ct) =>
        {
            production.EnsureDependencyMutationAllowed("device", id);
            devices.ApplySettings(id, request);
            await profiles.UpdateSettingsAsync(id, request, ct);
            return Results.Ok(devices.Get(id));
        }).RequireEngineer("device.settings.update", "device");

        app.MapGet("/api/devices/{id}/tags/{tagId}", async (
            string id,
            string tagId,
            bool? fresh,
            DeviceManager devices,
            CancellationToken ct) =>
            Results.Ok(await devices.ReadTagAsync(id, tagId, fresh ?? false, autoConnect: true, ct)));

        app.MapPut("/api/devices/{id}/tags/{tagId}", async (
            string id,
            string tagId,
            DeviceTagWriteRequest request,
            DeviceManager devices,
            DeviceLeaseRegistry leases,
            DeviceActionAuthorizationService auth,
            CancellationToken ct) =>
        {
            using var lease = leases.AcquireManualOrThrow("device", id, $"Cannot write device '{id}'");
            // A02：进入所有权环境——真实派发边界据此校验租约代次（失去所有权的旧调用被拒绝）。
            using var commandScope = DeviceCommandOwnership.Enter(lease);
            using var intent = auth.BeginManualIntent("device.writeTag", [id], []);
            await devices.WriteTagAsync(id, tagId, request.Value, autoConnect: true, ct);
            // F02：写入确认成功才解除意图（读回验证通过后再 Complete）。
            var readBack = await devices.ReadTagAsync(id, tagId, fresh: true, autoConnect: true, cancellationToken: ct);
            intent.Complete();
            return Results.Ok(readBack);
        }).RequireEngineer("device.tag.write", "device");

        return app;
    }

    /// <summary>
    /// R3-03（2026-10-13 R3 审查）：注册的成功/失败语义必须与状态一致。
    /// 旧实现先 <c>devices.Register(...)</c> 再持久化：驱动构造会把端口夹取到合法范围，
    /// 持久层随后拒绝原始非法端口——接口报 409 但设备已留在运行时列表（界面说失败、系统已变化），
    /// 且用修正参数重试会得到 "already registered"。现在：
    ///   1. 统一前置校验（<see cref="DeviceProfileStore.ValidateRegistration"/>）在任何副作用之前；
    ///   2. 内存注册后持久化失败（写盘拒绝 / ID 碰撞）⇒ **补偿回滚**内存注册（断开并释放驱动），
    ///      再如实报 409；失败后内存与文件均保持原状态，合法重试可直接成功。
    /// </summary>
    private static async Task<IResult> RegisterDeviceAsync(
        DeviceProfile profile, DeviceManager devices, DeviceProfileStore profiles, ILogger logger, CancellationToken ct)
    {
        DeviceProfileStore.ValidateRegistration(profile);   // 400（ID 契约）或 409（端口/kind 等结构校验）——无副作用
        var driver = DeviceDriverFactory.Create(profile);
        devices.Register(driver);                           // 重复 ID → 409（此前无任何持久化副作用）
        try
        {
            await profiles.SaveAsync(profile, ct);
        }
        catch (OperationCanceledException)
        {
            await RollBackRegistrationAsync(profile.Id, devices, logger);
            throw;
        }
        catch (Exception ex)
        {
            await RollBackRegistrationAsync(profile.Id, devices, logger);
            throw new ApiConflictException(
                $"Device '{profile.Id}' was not registered: its profile could not be persisted ({ex.Message}). " +
                "The runtime registration was rolled back; fix the cause and register again.", ex);
        }
        return Results.Ok(devices.Get(profile.Id));
    }

    /// <summary>R3-03：撤销一次内存注册（断开 + 释放驱动）；补偿自身失败必须留下日志而不是吞掉。</summary>
    private static async Task RollBackRegistrationAsync(string id, DeviceManager devices, ILogger logger)
    {
        try
        {
            await devices.RemoveAsync(id, CancellationToken.None);
        }
        catch (Exception cleanupError)
        {
            logger.LogError(cleanupError,
                "Failed to roll back device registration '{DeviceId}' after a persistence failure; the runtime entry may remain until restart.",
                id);
        }
    }
}
