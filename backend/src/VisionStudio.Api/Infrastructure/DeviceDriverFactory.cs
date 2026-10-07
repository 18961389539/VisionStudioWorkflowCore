using VisionStudio.Engine.Device;

namespace VisionStudio.Api.Infrastructure;

public static class DeviceDriverFactory
{
    public static IDeviceDriver Create(DeviceProfile profile)
        => profile.Kind switch
        {
            "modbus-tcp" => new NModbusTcpDeviceDriver(new NModbusTcpDeviceOptions(
                profile.Id, profile.Name, profile.Host, profile.Port, profile.UnitId, profile.TimeoutMs,
                profile.RegisterOrder, profile.Tags ?? Array.Empty<DeviceTagDefinition>())),
            "s7" => new S7NetPlusDeviceDriver(new S7NetPlusDeviceOptions(
                profile.Id, profile.Name, profile.Host, profile.CpuType, profile.Port, profile.Rack, profile.Slot,
                profile.TimeoutMs, profile.Tags ?? Array.Empty<DeviceTagDefinition>())),
            _ => throw new ApiValidationException($"Unknown persisted device profile kind '{profile.Kind}'.")
        };
}
