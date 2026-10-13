using WorkflowCore.Interface;
using WorkflowCore.Models;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Workflow Core step for coarse orchestration nodes (camera/device/robot/control flow)
/// and conservative plugin nodes. Hot compute nodes are fused into VisionPipelineStep.
/// </summary>
public sealed class VisionNodeStep(VisionNodeRuntime nodeRuntime) : StepBodyAsync
{
    public string NodeId { get; set; } = string.Empty;
    /// <summary>Optional enclosing flow.forEach ID; body steps receive current DetectionList data via Workflow Core's item context.</summary>
    public string LoopNodeId { get; set; } = string.Empty;

    public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
    {
        if (context.Workflow.Data is not VisionWorkflowData data)
            throw new InvalidOperationException("Workflow data is not VisionWorkflowData.");

        if (!string.IsNullOrWhiteSpace(LoopNodeId) && context.Item is VisionDetection detection)
        {
            if (!data.OutputsByNode.TryGetValue(LoopNodeId, out var previous))
                throw new InvalidOperationException($"ForEach node '{LoopNodeId}' did not publish its bounded DetectionList before body execution.");
            var current = previous.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            AddCurrentDetection(current, detection);
            data.OutputsByNode[LoopNodeId] = current;
        }

        await nodeRuntime.ExecuteAsync(data, NodeId, context.CancellationToken);
        if (data.RequireNode(NodeId).Type.Equals("flow.forEach", StringComparison.OrdinalIgnoreCase))
        {
            if (!data.OutputsByNode.TryGetValue(NodeId, out var outputs) ||
                !outputs.TryGetValue("detections", out var listValue) ||
                listValue.Type != VisionDataType.DetectionList || listValue.Value is not IReadOnlyList<VisionDetection> detections)
                throw new InvalidOperationException($"ForEach node '{NodeId}' did not publish its validated DetectionList.");
            data.ForEachCollections[NodeId] = detections;
        }
        return ExecutionResult.Next();
    }

    private static void AddCurrentDetection(IDictionary<string, VisionValue> outputs, VisionDetection detection)
    {
        outputs["index"] = VisionValue.Integer(detection.Index);
        outputs["center"] = VisionValue.Point(new VisionPoint2D(detection.Circle.X, detection.Circle.Y));
        outputs["circle"] = VisionValue.Circle(detection.Circle);
        outputs["rectangle"] = VisionValue.Rectangle(detection.BoundingBox);
        outputs["x"] = VisionValue.Double(detection.Circle.X);
        outputs["y"] = VisionValue.Double(detection.Circle.Y);
        if (detection.Confidence is { } confidence)
            outputs["confidence"] = VisionValue.Double(confidence);
        else
            outputs.Remove("confidence");
        if (detection.ClassId is { } classId)
            outputs["classId"] = VisionValue.Integer(classId);
        else
            outputs.Remove("classId");
    }
}
