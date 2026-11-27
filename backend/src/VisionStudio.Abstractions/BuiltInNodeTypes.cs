namespace VisionStudio.Abstractions;

/// <summary>
/// 跨引擎、编排编译器与宿主 API 共享的内置节点类型标识。
/// 集中定义控制流、采集、标定、匹配、设备与模块节点类型，避免各层散落硬编码字符串比较。
/// </summary>
public static class BuiltInNodeTypes
{
    public const string FlowIf = "flow.if";
    public const string FlowIfBoolean = "flow.ifBoolean";
    public const string FlowForEach = "flow.forEach";
    public const string FlowWhile = "flow.while";
    public const string FlowRepeat = "flow.repeat";
    public const string FlowLoopEnd = "flow.loopEnd";
    public const string FlowBreak = "flow.break";
    public const string FlowSwitch = "flow.switch";
    public const string FlowTry = "flow.try";
    public const string FlowParallel = "flow.parallel";
    public const string FlowJoin = "flow.join";
    public const string FlowMerge = "flow.merge";
    public const string FlowResult = "flow.result";

    public const string ImageAcquire = "image.acquire";
    public const string CameraSyncCapture = "camera.syncCapture";
    public const string ImageUndistort = "image.undistort";
    public const string ImageWarpAffine = "image.warpAffine";

    public const string MatchShape = "match.shape";
    public const string MatchFeature = "match.feature";
    public const string MatchTemplate = "match.template";

    public const string CalibrationPlanar = "calibration.planar";
    public const string CalibrationHandEye2D = "calibration.handEye2D";
    public const string CalibrationHandEye3D = "calibration.handEye3D";

    public const string MeasureCaliperCircle = "measure.caliperCircle";
    public const string RoiTransform = "roi.transform";
    public const string GeometryIntersections = "geometry.intersections";

    public const string JlVisionWorldPlane = "jlvision.worldPlane";
    public const string JlVisionDxf = "jlvision.dxf";
    public const string JlVisionImageOp = "jlvision.imageOp";

    public const string DeviceWriteTag = "device.writeTag";
    public const string DeviceWriteVisionResult = "device.writeVisionResult";
    public const string RobotExecuteTarget = "robot.executeTarget";

    public const string ScriptCSharp = "script.csharp";
    public const string ModuleCall = "module.call";

    public static bool Is(string? actualType, string expectedType)
        => string.Equals(actualType, expectedType, StringComparison.OrdinalIgnoreCase);

    public static bool Is(NodeDefinition? node, string expectedType)
        => node is not null && Is(node.Type, expectedType);
}
