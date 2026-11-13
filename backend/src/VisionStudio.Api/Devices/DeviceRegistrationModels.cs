using VisionStudio.Engine.Device;

namespace VisionStudio.Api;

public sealed record ModbusTcpRegistrationRequest(
    string Id,
    string Name,
    string Host,
    int Port = 502,
    byte UnitId = 1,
    int TimeoutMs = 3000,
    Modbus32BitOrder RegisterOrder = Modbus32BitOrder.ABCD,
    IReadOnlyList<DeviceTagDefinition>? Tags = null);

public sealed record S7NetPlusRegistrationRequest(
    string Id,
    string Name,
    string Host,
    string CpuType = "S71500",
    int Port = 102,
    short Rack = 0,
    short Slot = 0,
    int TimeoutMs = 3000,
    IReadOnlyList<DeviceTagDefinition>? Tags = null);

public sealed record DeviceBatchWriteRequest(IReadOnlyDictionary<string, object?> Values);

public sealed record DeviceAddressTemplate(
    string Driver,
    string Protocol,
    IReadOnlyList<string> Examples,
    string Notes);
