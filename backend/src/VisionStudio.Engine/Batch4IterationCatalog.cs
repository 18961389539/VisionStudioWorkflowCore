using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

public static class Batch4IterationCatalog
{
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("flow.forEach","For Each Detection","Flow",[new("exec",VisionDataType.Control),new("repeat",VisionDataType.Control,Required:false),new("detections",VisionDataType.DetectionList)],[new("body",VisionDataType.Control),new("done",VisionDataType.Control),new("detections",VisionDataType.DetectionList),new("count",VisionDataType.Integer),new("index",VisionDataType.Integer,Required:false),new("center",VisionDataType.Point2D,Required:false),new("circle",VisionDataType.Circle,Required:false),new("rectangle",VisionDataType.Rectangle2D,Required:false),new("x",VisionDataType.Double,Required:false),new("y",VisionDataType.Double,Required:false),new("confidence",VisionDataType.Double,Required:false),new("classId",VisionDataType.Integer,Required:false)],[new("maxItems","Maximum Items","number",100,1,500,1)],"Sequentially execute the structured body once for each DetectionList item in input order. The body receives current index/center/circle/rectangle/x/y and optional confidence/classId; the loop fails when item count exceeds maxItems.",Capabilities:new VisionToolCapabilities(SupportsRunNode:false,SupportsParallel:false,ExecutionMode:VisionExecutionMode.WorkflowCore)),
        new("flow.loopEnd","Loop End","Flow",[new("exec",VisionDataType.Control)],[new("repeat",VisionDataType.Control)],[],"Marks the end of a forEach body and returns control to its paired flow.forEach node. It cannot be used as an arbitrary graph jump.",Capabilities:new VisionToolCapabilities(SupportsRunNode:false,SupportsParallel:false,ExecutionMode:VisionExecutionMode.WorkflowCore))
    ];
}
