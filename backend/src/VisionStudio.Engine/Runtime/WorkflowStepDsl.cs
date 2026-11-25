using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine.Runtime;

internal sealed record WorkflowDslDocument(
    string Id,
    int Version,
    string DataType,
    IReadOnlyList<WorkflowDslStep> Steps);

internal sealed record WorkflowDslStep(
    string Id,
    string StepType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Inputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<IReadOnlyList<WorkflowDslStep>>? Do = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextStepId = null,
    string ErrorBehavior = WorkflowStepDsl.TerminateOnError);

internal static class WorkflowStepDsl
{
    public const string TerminateOnError = "Terminate";
    public const string IfStepType = "WorkflowCore.Primitives.If, WorkflowCore";
    public const string ForeachStepType = "WorkflowCore.Primitives.Foreach, WorkflowCore";
    public const string WhileStepType = "WorkflowCore.Primitives.While, WorkflowCore";
    public const string SequenceStepType = "WorkflowCore.Primitives.Sequence, WorkflowCore";

    public static WorkflowDslStep VisionStep(string stepType, string nodeId, string? nextStepId, string? loopNodeId = null)
    {
        var inputs = new Dictionary<string, string>
        {
            ["NodeId"] = JsonSerializer.Serialize(nodeId)
        };
        if (!string.IsNullOrWhiteSpace(loopNodeId))
            inputs["LoopNodeId"] = JsonSerializer.Serialize(loopNodeId);
        return new WorkflowDslStep(nodeId, stepType, Inputs: inputs, NextStepId: nextStepId);
    }

    public static WorkflowDslStep PipelineStep(string stepType, string segmentId, string? nextStepId)
        => new(
            segmentId,
            stepType,
            Inputs: new Dictionary<string, string> { ["SegmentId"] = JsonSerializer.Serialize(segmentId) },
            NextStepId: nextStepId);

    public static WorkflowDslStep ImplicitJoinStep(string stepType, string joinId, string? nextStepId)
        => new(
            joinId,
            stepType,
            Inputs: new Dictionary<string, string> { ["JoinNodeId"] = JsonSerializer.Serialize(joinId) },
            NextStepId: nextStepId);

    public static WorkflowDslStep LoopControlStep(
        string stepType,
        string stepId,
        string loopNodeId,
        string action,
        string kind,
        LoopConditionSource? source,
        string? nextStepId = null)
    {
        var inputs = new Dictionary<string, string>
        {
            ["LoopNodeId"] = JsonSerializer.Serialize(loopNodeId),
            ["Action"] = JsonSerializer.Serialize(action),
            ["Kind"] = JsonSerializer.Serialize(kind)
        };
        if (source is not null)
        {
            inputs["SourceNodeId"] = JsonSerializer.Serialize(source.NodeId);
            inputs["SourcePort"] = JsonSerializer.Serialize(source.Port);
        }
        return new WorkflowDslStep(stepId, stepType, Inputs: inputs, NextStepId: nextStepId);
    }

    public static WorkflowDslStep If(string id, string conditionExpression, IReadOnlyList<WorkflowDslStep> body, string? nextStepId = null)
        => new(
            id,
            IfStepType,
            Inputs: new Dictionary<string, string> { ["Condition"] = conditionExpression },
            Do: [body],
            NextStepId: nextStepId);

    public static WorkflowDslStep Foreach(string id, string collectionExpression, IReadOnlyList<WorkflowDslStep> body, string? nextStepId = null)
        => new(
            id,
            ForeachStepType,
            Inputs: new Dictionary<string, string>
            {
                ["Collection"] = collectionExpression,
                ["RunParallel"] = "false"
            },
            Do: [body],
            NextStepId: nextStepId);

    public static WorkflowDslStep While(string id, string conditionExpression, IReadOnlyList<WorkflowDslStep> body, string? nextStepId = null)
        => new(
            id,
            WhileStepType,
            Inputs: new Dictionary<string, string> { ["Condition"] = conditionExpression },
            Do: [body],
            NextStepId: nextStepId);

    public static WorkflowDslStep Sequence(string id, IReadOnlyList<WorkflowDslStep> branch1, IReadOnlyList<WorkflowDslStep> branch2, string? nextStepId = null)
        => new(
            id,
            SequenceStepType,
            Do: [branch1, branch2],
            NextStepId: nextStepId);
}
