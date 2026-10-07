using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api.Contracts;

public sealed record FileCameraRegistrationRequest(string Id, string? Name = null, string? Source = null, string? Path = null);

public sealed record VendorCameraRegistrationRequest(string Driver, string Id, string? Name = null, string? SerialNumber = null, string? UserDefinedName = null, string? DeviceKey = null, CameraSettings? Settings = null, int RingCapacity = 4);
public sealed record DeviceTagWriteRequest(object? Value);
public sealed record FrameResolveRequest(IReadOnlyList<FrameTransformDefinition> Transforms, string SourceFrame, string TargetFrame);
public sealed record RobotPanelTargetRequest(
    double X,
    double Y,
    double RDeg,
    string Frame = "RobotBase",
    string Unit = "mm",
    string Robot = "ABB",
    string GuidanceMode = "Manual",
    string Action = "Handshake",
    bool WaitForInPosition = true,
    int TimeoutMs = 5000,
    int MaxRetries = 1,
    int RetryDelayMs = 100,
    bool AutoAck = true);

public sealed record CameraFeatureValueRequest(string Value);
public sealed record CameraFeatureProfileCaptureRequest(string Id, string Name, string CameraId);
public sealed record CameraFeatureProfileImportRequest(string Id, string Name, string Driver, CameraCommissioningProfile Profile, string? SourceCameraId = null);
