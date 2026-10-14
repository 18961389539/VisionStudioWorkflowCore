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
            // A02：节点执行前重入所有权环境——真实派发（写设备/机器人指令/相机触发）据此校验租约代次。
            using var ownership = DeviceCommandOwnership.Enter(data.Ownership);
            inputs = dispatcher.ResolveInputs(data, node);
            var result = await dispatcher.ExecuteAsync(node, inputs, data.WorkflowScope, cancellationToken);
            if (node.Type.Equals("flow.try", StringComparison.OrdinalIgnoreCase))
                result = await ExecuteTryBodyAsync(data, node, result, cancellationToken);
            sw.Stop();

            data.OutputsByNode[node.Id] = result.Outputs;
            data.TrackOutputs(result.Outputs);
            data.AddOverlays(node.Id, result.Overlays);
            if (data.CaptureNodeImages) data.CaptureNodeImage(node.Id, result.Outputs);

            var reportSummary = result.Summary;

            if ((node.Type.Equals("flow.if", StringComparison.OrdinalIgnoreCase) || node.Type.Equals("flow.ifBoolean", StringComparison.OrdinalIgnoreCase)) &&
                result.Outputs.TryGetValue("condition", out var condition) &&
                condition.Type == VisionDataType.Boolean && condition.Value is bool branchCondition)
            {
                data.SetBranchCondition(node.Id, branchCondition, node.Type);
            }

            if (node.Type.Equals("flow.switch", StringComparison.OrdinalIgnoreCase) &&
                result.Outputs.TryGetValue("selected", out var selected) &&
                selected.Type == VisionDataType.String && selected.Value is string selectedBranch)
                data.SetSwitchSelection(node.Id, selectedBranch);

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
            data.LastFaultedNodeId = node.Id;
            // R10/F08：保留原始异常供统一结束阶段向观察者报告——本异常会被 WorkflowCore 捕获
            // 并转为结果返回，运行器的 catch 边界不会经过，不能只依赖那里记录异常链。
            data.FaultedException ??= ex;
            data.FaultedNodeId ??= node.Id;
            data.AddReport(new NodeRunReport(
                node.Id,
                node.Type,
                false,
                sw.Elapsed.TotalMilliseconds,
                FailureSummary(data, ex, cancellationToken),
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

    private async ValueTask<NodeExecutorResult> ExecuteTryBodyAsync(
        VisionWorkflowData data,
        NodeDefinition node,
        NodeExecutorResult marker,
        CancellationToken cancellationToken)
    {
        var body = data.GetTryBodyNodeIds(node.Id);
        if (body.Count == 0) throw new InvalidOperationException($"Try '{node.Id}' body must not be empty.");
        var maxRetries = node.GetInt("maxRetries", 0);
        var retryDelayMs = node.GetInt("retryDelayMs", 100);
        if (maxRetries is < 0 or > 3 || retryDelayMs is < 0 or > 5000)
            throw new InvalidOperationException($"Try '{node.Id}' retry settings exceed supported bounds (maxRetries 0-3, retryDelayMs 0-5000).");

        Exception? lastError = null;
        var attempts = 0;
        var succeeded = false;
        var hadHandledFailures = false;
        for (var attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            attempts = attempt;
            data.BeginTryAttempt(node.Id, attempt, body);
            try
            {
                foreach (var bodyNodeId in body)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outcome = await ExecuteAsync(data, bodyNodeId, cancellationToken).ConfigureAwait(false);
                    if (outcome != VisionNodeRuntimeOutcome.Executed || data.Halted) break;
                }
                succeeded = !data.Halted;
                lastError = null;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                data.ClearTryBodyOutputs(body);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                hadHandledFailures = true;
                data.ClearHandledTryError(data.LastFaultedNodeId ?? body.LastOrDefault() ?? node.Id);
                if (ex is OperationCanceledException || attempt > maxRetries) break;
                if (retryDelayMs > 0)
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            finally { data.EndTryAttempt(); }
        }

        if (!succeeded) data.ClearTryBodyOutputs(body);
        var errorText = lastError?.Message ?? string.Empty;
        data.SetTryOutcome(node.Id, succeeded, errorText, attempts);
        var outputs = new Dictionary<string, VisionValue>(marker.Outputs)
        {
            ["succeeded"] = VisionValue.Boolean(succeeded),
            ["errorMessage"] = VisionValue.String(errorText),
            ["attempts"] = VisionValue.Integer(attempts)
        };
        return new NodeExecutorResult(outputs, new Dictionary<string, object?>(marker.Summary)
        {
            ["succeeded"] = succeeded,
            ["error"] = errorText,
            ["attempts"] = attempts,
            ["handled"] = hadHandledFailures
        });
    }

    private static IReadOnlyDictionary<string, object?> FailureSummary(VisionWorkflowData data, Exception exception, CancellationToken cancellationToken)
    {
        if (data.CurrentTryAttempt is not { } current) return new Dictionary<string, object?>();
        return new Dictionary<string, object?>
        {
            ["handledByTry"] = !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested),
            ["tryNodeId"] = current.NodeId,
            ["tryAttempt"] = current.Attempt
        };
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
            VisionDataType.DeviceTagValue or VisionDataType.Measurement or VisionDataType.DetectionList or
            VisionDataType.Contour or VisionDataType.ContourProperties or VisionDataType.Roi => value.Value,
            _ => value.Value.ToString()
        };
        return new VisionValueSnapshot(value.Type.ToString(), value.Value.ToString() ?? value.Type.ToString(), payload);
    }
}
