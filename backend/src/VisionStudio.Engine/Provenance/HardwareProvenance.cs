namespace VisionStudio.Engine.Provenance;

/// <summary>
/// Adapter-supplied hardware identity. Adapters should return only information they can actually
/// obtain from the device/controller. Manual plant declarations are merged by the API host.
/// </summary>
public sealed record HardwareProvenanceData(
    string? Manufacturer = null,
    string? ProductName = null,
    string? Model = null,
    string? SerialNumber = null,
    string? HardwareRevision = null,
    string? FirmwareVersion = null,
    string? SoftwareVersion = null,
    string? ControllerVersion = null,
    string? ProgramName = null,
    string? ProgramHash = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>
/// Optional adapter capability. Real camera/PLC/robot integrations should implement this interface
/// when their vendor SDK/protocol can query serial number, firmware, controller version or program identity.
/// </summary>
public interface IHardwareProvenanceProvider
{
    HardwareProvenanceData GetHardwareProvenance();
}
/// <summary>
/// Async variant for live vendor/controller interrogation. Use this when identity capture requires
/// SDK enumeration, network I/O, controller queries, or program hashing.
/// </summary>
public interface IAsyncHardwareProvenanceProvider
{
    ValueTask<HardwareProvenanceData> GetHardwareProvenanceAsync(CancellationToken cancellationToken = default);
}

