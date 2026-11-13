using VisionStudio.Engine.Camera;

namespace VisionStudio.Api;

public sealed class CameraAdapterOptions
{
    public string? BaslerAssemblyPath { get; set; }
    public string? HikrobotAssemblyPath { get; set; }
    public List<ConfiguredVendorCamera> Cameras { get; set; } = [];
}

public sealed class ConfiguredVendorCamera
{
    public bool Enabled { get; set; } = true;
    public string Driver { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? SerialNumber { get; set; }
    public string? UserDefinedName { get; set; }
    public string? DeviceKey { get; set; }
    public int RingCapacity { get; set; } = 4;
    public string? FeatureProfileId { get; set; }
    public CameraSettings Settings { get; set; } = new();
}
