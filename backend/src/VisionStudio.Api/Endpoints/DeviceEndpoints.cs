using VisionStudio.Api.Contracts;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
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
            CancellationToken ct) =>
        {
            var profile = new DeviceProfile(
                request.Id, "modbus-tcp", request.Name, request.Host, request.Port, request.TimeoutMs,
                request.UnitId, request.RegisterOrder, Tags: request.Tags ?? Array.Empty<DeviceTagDefinition>());
            devices.Register(DeviceDriverFactory.Create(profile));
            await profiles.SaveAsync(profile, ct);
            return Results.Ok(devices.Get(request.Id));
        }).RequireEngineer("device.register.modbus", "device");

        app.MapPost("/api/devices/register/s7", async (
            S7NetPlusRegistrationRequest request,
            DeviceManager devices,
            DeviceProfileStore profiles,
            CancellationToken ct) =>
        {
            var profile = new DeviceProfile(
                request.Id, "s7", request.Name, request.Host, request.Port, request.TimeoutMs,
                CpuType: request.CpuType, Rack: request.Rack, Slot: request.Slot,
                Tags: request.Tags ?? Array.Empty<DeviceTagDefinition>());
            devices.Register(DeviceDriverFactory.Create(profile));
            await profiles.SaveAsync(profile, ct);
            return Results.Ok(devices.Get(request.Id));
        }).RequireEngineer("device.register.s7", "device");

        app.MapGet("/api/device-profiles", async (DeviceProfileStore profiles, CancellationToken ct) =>
            Results.Ok(await profiles.ListAsync(ct)));

        app.MapDelete("/api/devices/{id}", async (string id, DeviceManager devices, DeviceProfileStore profiles, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureDependencyMutationAllowed("device", id);
            if (string.Equals(id, "virtual-modbus-1", StringComparison.OrdinalIgnoreCase))
                throw new ApiConflictException("The bundled virtual device is retained as the offline test baseline.");
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
            // R01：统一动作授权——未闭合的安全意图阻断所有设备动作入口，并在动作前耐久登记本次意图。
            using var intent = auth.BeginManualIntent("device.writeTags", [id], []);
            await devices.WriteManyAsync(id, request.Values, autoConnect: true, cancellationToken: ct);
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
            using var intent = auth.BeginManualIntent("device.writeTag", [id], []);
            await devices.WriteTagAsync(id, tagId, request.Value, autoConnect: true, ct);
            return Results.Ok(await devices.ReadTagAsync(id, tagId, fresh: true, autoConnect: true, cancellationToken: ct));
        }).RequireEngineer("device.tag.write", "device");

        return app;
    }
}
