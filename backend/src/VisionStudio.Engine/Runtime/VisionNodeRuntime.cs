using System.Diagnostics;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Runtime;

public enum VisionNodeRuntimeOutcome
{
    Executed,
    Skipped,
    HaltedBefore
}

/// <summary>
/// Executes one visual node and owns the node-level semantics shared by both the
/// Workflow Core step and the fused VisionPipelineExecutor: debug gating, reports,
/// output/resource tracking, overlays and control-result side effects.
/// </summary>
public sealed class VisionNodeRuntime(VisionNodeDispatcher dispatcher)
{
    public async ValueTask<VisionNodeRuntimeOutcome> ExecuteAsync(
        VisionWorkflowData data,
        string nodeId,
        CancellationToken cancellationToken)
    {
        var node = data.RequireNode(nodeId);

        var debugDecision = data.BeforeNode(node.Id);
        if (debugDecision == StepDebugDecision.Skip)
            return VisionNodeRuntimeOutcome.Skipped;
        if (debugDecision == StepDebugDecision.HaltBefore)
            return VisionNodeRuntimeOutcome.HaltedBefore;

        var phase = data.CurrentPhase;
        var observation = data.BeginNodeObservation();
        var sw = Stopwatch.StartNew();
        IReadOnlyDictionary<string, VisionValue> inputs = new Dictionary<string, VisionValue>();
        try
        {
            inputs = dispatcher.ResolveInputs(data, node);
            var result = await dispatcher.ExecuteAsync(node, inputs, data.WorkflowScope, cancellationToken);
            sw.Stop();

            data.OutputsByNode[node.Id] = result.Outputs;
            data.TrackOutputs(result.Outputs);
            data.AddOverlays(node.Id, result.Overlays);
            if (data.CaptureNodeImages) data.CaptureNodeImage(node.Id, result.Outputs);

            var reportSummary = result.Summary;

            if (node.Type.Equals("flow.if", StringComparison.OrdinalIgnoreCase) &&
                result.Outputs.TryGetValue("condition", out var condition) &&
                condition.Type == VisionDataType.Boolean && condition.Value is bool branchCondition)
            {
                data.SetBranchCondition(node.Id, branchCondition);
            }

            if (node.Type.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase))
                data.MarkParallelBranches(node.Id);

            if (node.Type.Equals("flow.result", StringComparison.OrdinalIgnoreCase) &&
                result.Outputs.TryGetValue("disposition", out var disposition) &&
                disposition.Type == VisionDataType.String && disposition.Value is string qualityDisposition)
            {
                // Inside a Parallel branch the disposition is scoped to that branch and merged at the
                // paired Join (any-NG), so branch completion order can never mask an NG with a later OK.
                var contribution = data.RecordDisposition(node.Id, qualityDisposition);
                if (contribution.ScopedToBranch)
                {
                    reportSummary = new Dictionary<string, object?>(result.Summary)
                    {
                        ["dispositionScope"] = $"{contribution.RegionId}.{contribution.BranchName}",
                        ["branchDisposition"] = contribution.BranchDisposition
                    };
                }
            }

            if (node.Type.Equals("flow.join", StringComparison.OrdinalIgnoreCase))
            {
                var aggregation = data.ApplyParallelJoin(node.Id);
                if (aggregation?.Aggregate is { } parallelDisposition)
                {
                    reportSummary = new Dictionary<string, object?>(result.Summary)
                    {
                        ["parallelDisposition"] = parallelDisposition,
                        ["branchDispositions"] = aggregation.BranchDispositions
                    };
                }
            }

            data.AddReport(new NodeRunReport(
                node.Id,
                node.Type,
                true,
                sw.Elapsed.TotalMilliseconds,
                reportSummary,
                Phase: phase,
                Inputs: SnapshotValues(inputs),
                Outputs: SnapshotValues(result.Outputs),
                ExecutionSequence: observation.Sequence,
                StartOffsetMs: observation.StartOffsetMs,
                EndOffsetMs: observation.StartOffsetMs + sw.Elapsed.TotalMilliseconds));

            data.AfterNode(node.Id);
            return VisionNodeRuntimeOutcome.Executed;
        }
        catch (Exception ex)
        {
            sw.Stop();
            data.Error = $"{node.Id}: {ex.Message}";
            data.AddReport(new NodeRunReport(
                node.Id,
                node.Type,
                false,
                sw.Elapsed.TotalMilliseconds,
                new Dictionary<string, object?>(),
                ex.Message,
                phase,
                SnapshotValues(inputs),
                new Dictionary<string, VisionValueSnapshot>(),
                observation.Sequence,
                observation.StartOffsetMs,
                observation.StartOffsetMs + sw.Elapsed.TotalMilliseconds));
            throw;
        }
    }

    private static IReadOnlyDictionary<string, VisionValueSnapshot> SnapshotValues(IReadOnlyDictionary<string, VisionValue> values)
        => values.ToDictionary(x => x.Key, x => SnapshotValue(x.Value), StringComparer.OrdinalIgnoreCase);

    private static VisionValueSnapshot SnapshotValue(VisionValue value)
    {
        if (value.Value is null) return new VisionValueSnapshot(value.Type.ToString(), "null");
        if (value.Value is IVisionImage image)
            return new VisionValueSnapshot(value.Type.ToString(), $"Image {image.Width}x{image.Height}", new { image.Width, image.Height });
        if (value.Value is IVisionFrameSet frameSet)
            return new VisionValueSnapshot(value.Type.ToString(), $"FrameSet {frameSet.GroupId} · {frameSet.Images.Count} cameras · {frameSet.TriggerSkewUs:0.###} us", new
            {
                frameSet.GroupId,
                frameSet.TimestampBasis,
                frameSet.TriggerSkewUs,
                frameSet.WithinTolerance,
                cameraCount = frameSet.Images.Count,
                cameras = frameSet.Frames.Values.Select(x => new { x.CameraId, x.Sequence, x.DeviceTimestampNs, x.TriggerId }).ToArray()
            });

        object? payload = value.Type switch
        {
            VisionDataType.Double or VisionDataType.Integer or VisionDataType.Boolean or VisionDataType.String or
            VisionDataType.Point2D or VisionDataType.Line2D or VisionDataType.Pose2D or VisionDataType.Rectangle2D or
            VisionDataType.Circle or VisionDataType.Transform2D or VisionDataType.CoordinatePoint2D or
            VisionDataType.CoordinateLine2D or VisionDataType.CoordinatePose2D or VisionDataType.RobotTarget2D or
            VisionDataType.DeviceTagValue or VisionDataType.Measurement => value.Value,
            _ => value.Value.ToString()
        };
        return new VisionValueSnapshot(value.Type.ToString(), value.Value.ToString() ?? value.Type.ToString(), payload);
    }
}
