using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

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

/// <summary>
/// /api/compile 的响应契约（2026-11-10）。此前是匿名对象：OpenAPI 里只有 "200 OK"，
/// 生成不出类型；Warnings 也无法被下游契约看到。字段名由 Minimal API 的 camelCase 序列化保持原样。
/// </summary>
public sealed record WorkflowCompileResponse(
    string WorkflowCoreId,
    int Version,
    IReadOnlyList<string> OrderedNodeIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PipelineSegments,
    int PipelineSegmentCount,
    IReadOnlyList<StructuredControlRegion> ControlRegions,
    int StructuredRegionCount,
    IReadOnlyList<CompileWarning> Warnings,
    IReadOnlyList<WorkflowModuleDependency> ModuleDependencies,
    int ExpandedNodeCount,
    string DslJson);

/// <summary>/api/validate 的成功响应（valid=true）；失败响应见 <see cref="WorkflowValidationFailure"/>。</summary>
public sealed record WorkflowValidationSuccess(
    bool Valid,
    string WorkflowCoreId,
    IReadOnlyList<string> OrderedNodeIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PipelineSegments,
    int PipelineSegmentCount,
    IReadOnlyList<StructuredControlRegion> ControlRegions,
    int StructuredRegionCount,
    IReadOnlyList<CompileWarning> Warnings,
    IReadOnlyList<WorkflowModuleDependency> ModuleDependencies,
    int ExpandedNodeCount,
    IReadOnlyList<string> RegisteredNodeTypes);

/// <summary>/api/validate 的失败响应：valid=false 是交互式反馈，不是 ProblemDetails。</summary>
public sealed record WorkflowValidationFailure(bool Valid, string Error);
