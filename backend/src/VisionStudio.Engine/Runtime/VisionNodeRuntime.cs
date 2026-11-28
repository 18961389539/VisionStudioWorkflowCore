using System.Diagnostics;
using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

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
        CancellationToken cancellationToken,
        string? enclosingLoopId = null,
        int? loopIteration = null)
    {
        var node = data.RequireNode(nodeId);

        var debugDecision = data.BeforeNode(node.Id);
        if (debugDecision == StepDebugDecision.Skip)
            return VisionNodeRuntimeOutcome.Skipped;
        if (debugDecision == StepDebugDecision.HaltBefore)
            return VisionNodeRuntimeOutcome.HaltedBefore;

        var phase = data.CurrentPhase;
        var observation = data.BeginNodeObservation(node.Id);
        var sw = Stopwatch.StartNew();
        IReadOnlyDictionary<string, VisionValue> inputs = new Dictionary<string, VisionValue>();
        CaliperCircleDiagnostic? caliperDiagnostic = null;
        CaliperEdgeDiagnostic? caliperEdgeDiagnostic = null;
        var captureCaliperDiagnostic = data.CaptureNodeImages && node.Type.Equals("measure.caliperCircle", StringComparison.OrdinalIgnoreCase)
            && (data.DebugMode != DebugRunMode.RunNode || node.Id.Equals(data.TargetNodeId, StringComparison.OrdinalIgnoreCase));
        var captureCaliperEdgeDiagnostic = data.CaptureNodeImages && (node.Type.Equals("measure.caliper", StringComparison.OrdinalIgnoreCase)
            || node.Type.Equals("feature.edgeSubpixel", StringComparison.OrdinalIgnoreCase))
            && (data.DebugMode != DebugRunMode.RunNode || node.Id.Equals(data.TargetNodeId, StringComparison.OrdinalIgnoreCase));
        if (captureCaliperDiagnostic || captureCaliperEdgeDiagnostic) data.ClearCaliperDiagnostic(node.Id);
        try
        {
            // A02：节点执行前重入所有权环境——真实派发（写设备/机器人指令/相机触发）据此校验租约代次。
            using var ownership = DeviceCommandOwnership.Enter(data.Ownership);
            inputs = dispatcher.ResolveInputs(data, node);
            NodeExecutorResult result;
            using (captureCaliperDiagnostic ? CaliperCircleDiagnosticCapture.Begin(diagnostic => caliperDiagnostic = diagnostic) : null)
            using (captureCaliperEdgeDiagnostic ? CaliperEdgeDiagnosticCapture.Begin(diagnostic => caliperEdgeDiagnostic = diagnostic) : null)
                result = await dispatcher.ExecuteAsync(node, inputs, data.WorkflowScope, cancellationToken, data.RunId, data.ModelAssets,
                    data.Definition.Id, data.DebugMode != DebugRunMode.Full, enclosingLoopId, loopIteration);
            if (FlowSemantics.IsTry(node.Type))
                result = await ExecuteTryBodyAsync(data, node, result, cancellationToken);
            sw.Stop();

            data.OutputsByNode[node.Id] = result.Outputs;
            data.RecordNodeExecution(node.Id);
            data.TrackOutputs(result.Outputs);
            data.AddOverlays(node.Id, result.Overlays);
            if (data.CaptureNodeImages) data.CaptureNodeImage(node.Id, result.Outputs);

            // Prefer the executor's own algorithmBackend (e.g. managed chamfer, opencv fisheye, hybrid).
            // Only fill from BackendFor when the node left it unset. Also stamp domain backend keys when missing.
            var reportSummary = result.Summary;
            if (!reportSummary.ContainsKey(NodeSummaryKeys.AlgorithmBackend) &&
                Algorithms.VisionAlgorithmRuntime.BackendFor(node.Type) is { } algorithmBackend)
            {
                reportSummary = new Dictionary<string, object?>(reportSummary) { [NodeSummaryKeys.AlgorithmBackend] = algorithmBackend };
            }
            reportSummary = Algorithms.VisionAlgorithmRuntime.EnsureDomainBackendLabels(node.Type, reportSummary);
            if (caliperDiagnostic is not null)
                reportSummary = new Dictionary<string, object?>(reportSummary)
                {
                    [NodeSummaryKeys.CaliperDiagnosticsAvailable] = data.CaptureCaliperDiagnostic(node.Id, observation.Sequence, caliperDiagnostic)
                };
            if (caliperEdgeDiagnostic is not null)
                reportSummary = new Dictionary<string, object?>(reportSummary)
                {
                    [NodeSummaryKeys.CaliperEdgeDiagnosticsAvailable] = data.CaptureCaliperEdgeDiagnostic(node.Id, observation.Sequence, caliperEdgeDiagnostic)
                };

            if (FlowSemantics.IsConditional(node.Type) &&
                result.Outputs.TryGetValue("condition", out var condition) &&
                condition.Type == VisionDataType.Boolean && condition.Value is bool branchCondition)
            {
                data.SetBranchCondition(node.Id, branchCondition, node.Type);
            }

            if (FlowSemantics.IsSwitch(node.Type) &&
                result.Outputs.TryGetValue("selected", out var selected) &&
                selected.Type == VisionDataType.String && selected.Value is string selectedBranch)
                data.SetSwitchSelection(node.Id, selectedBranch);

            if (FlowSemantics.IsParallel(node.Type))
            {
                var (branchLabels, branchTimeoutMs) = ParallelMarkerNode.ReadConfiguration(node);
                data.ConfigureParallelBranches(node.Id, branchLabels, branchTimeoutMs);
                data.MarkParallelBranches(node.Id);
            }
            else if (data.IsParallelRegionEntry(node.Id))
            {
                // 隐式并行没有 flow.parallel 标记节点：扇出节点自己开启新一轮区域运行。
                data.BeginParallelRegion(node.Id);
            }

            // 通用循环：循环节点自身作为入口标记执行一次，在这里初始化跨迭代状态。
            // while 的预算是参数；repeat 的总次数由执行器解析（端口优先）后随 total 输出回传。
            if (FlowSemantics.IsWhile(node.Type))
            {
                data.BeginLoop(node.Id, "while", node.GetInt("maxIterations", 100));
            }
            else if (FlowSemantics.IsRepeat(node.Type))
            {
                if (!result.Outputs.TryGetValue("total", out var totalValue) || totalValue.Value is not long total || total is < 1)
                    throw new InvalidOperationException($"flow.repeat '{node.Id}' did not publish a positive iteration total.");
                data.BeginLoop(node.Id, "repeat", (int)total);
            }

            if (FlowSemantics.IsResult(node.Type))
            {
                var resetRequested = result.Outputs.TryGetValue("resetScope", out var resetValue) && resetValue.Value is true;
                if (resetRequested) data.ResetDisposition(node.Id);
                if (result.Outputs.TryGetValue("disposition", out var disposition) &&
                    disposition.Type == VisionDataType.String && disposition.Value is string qualityDisposition)
                {
                    // Inside a Parallel branch the disposition is scoped to that branch and merged at the
                    // paired Join (any-NG), so branch completion order can never mask an NG with a later OK.
                    var sticky = result.Outputs.TryGetValue("sticky", out var stickyValue) && stickyValue.Value is true;
                    var contribution = data.RecordDisposition(node.Id, qualityDisposition, sticky);
                    var audit = new Dictionary<string, object?>(reportSummary)
                    {
                        [NodeSummaryKeys.DispositionScope] = contribution.ScopeId ?? "run",
                        [NodeSummaryKeys.DispositionSetBy] = node.Id,
                        [NodeSummaryKeys.DispositionPrevious] = contribution.PreviousDisposition
                    };
                    if (contribution.ScopedToBranch) audit[NodeSummaryKeys.BranchDisposition] = contribution.BranchDisposition;
                    if (resetRequested) audit[NodeSummaryKeys.DispositionReset] = true;
                    reportSummary = audit;
                }
            }

            if (FlowSemantics.IsJoin(node.Type))
            {
                var mergePolicy = ResultFileWriter.Choice(node, "mergePolicy", ParallelMergePolicies.AnyNg, ParallelMergePolicies.All);
                var quorum = node.GetInt("quorum", 0);
                if (quorum is < 0 or > 64)
                    throw new InvalidOperationException($"flow.join '{node.Id}' quorum must be within 0..64 (0 = all branches).");
                var aggregation = data.ApplyParallelJoin(node.Id, mergePolicy, quorum);
                if (aggregation is not null)
                {
                    var completed = aggregation.BranchNames(ParallelBranchStatus.Completed);
                    var failed = aggregation.BranchNames(ParallelBranchStatus.Failed);
                    var cancelled = aggregation.BranchNames(ParallelBranchStatus.Cancelled);
                    var missing = aggregation.BranchNames(ParallelBranchStatus.Missing);
                    var branchElapsed = aggregation.BranchExecutions.Values.Select(x => x.ElapsedMs).DefaultIfEmpty(0).Max();

                    // 分支状态必须能被下游看到（不只是报告）：合并策略与缺失分支会改变流程走向。
                    var merged = new Dictionary<string, VisionValue>(result.Outputs, StringComparer.OrdinalIgnoreCase)
                    {
                        ["branchCount"] = VisionValue.Integer(aggregation.BranchExecutions.Count),
                        ["completedBranches"] = VisionValue.StringList(completed),
                        ["failedBranches"] = VisionValue.StringList(failed),
                        ["cancelledBranches"] = VisionValue.StringList(cancelled),
                        ["missingBranches"] = VisionValue.StringList(missing),
                        ["completedCount"] = VisionValue.Integer(completed.Count),
                        ["failedCount"] = VisionValue.Integer(failed.Count),
                        ["cancelledCount"] = VisionValue.Integer(cancelled.Count),
                        ["missingCount"] = VisionValue.Integer(missing.Count),
                        ["mergePolicy"] = VisionValue.String(aggregation.Policy),
                        ["policySatisfied"] = VisionValue.Boolean(aggregation.PolicySatisfied),
                        ["duplicateToken"] = VisionValue.Boolean(aggregation.Duplicate),
                        ["branchDispositionsJson"] = VisionValue.String(JsonSerializer.Serialize(aggregation.BranchDispositions)),
                        ["branchStatesJson"] = VisionValue.String(JsonSerializer.Serialize(
                            aggregation.BranchExecutions.Values
                                .OrderBy(x => x.BranchName, StringComparer.OrdinalIgnoreCase)
                                .Select(x => new
                                {
                                    branch = x.BranchName, x.Label, x.ExecutionId, status = x.Status.ToString().ToLowerInvariant(),
                                    x.ElapsedMs, x.Error, cancellationReason = x.CancellationReason
                                }))),
                        ["parallelElapsedMs"] = VisionValue.Double(branchElapsed)
                    };
                    if (aggregation.Aggregate is { } parallelDisposition)
                        merged["parallelDisposition"] = VisionValue.String(parallelDisposition);
                    result = result with { Outputs = merged };
                    data.OutputsByNode[node.Id] = merged;

                    var joinSummary = new Dictionary<string, object?>(reportSummary)
                    {
                        [NodeSummaryKeys.BranchDispositions] = aggregation.BranchDispositions,
                        [NodeSummaryKeys.BranchCompleted] = completed,
                        [NodeSummaryKeys.BranchFailed] = failed,
                        [NodeSummaryKeys.BranchCancelled] = cancelled,
                        [NodeSummaryKeys.BranchMissing] = missing,
                        [NodeSummaryKeys.MergePolicy] = aggregation.Policy,
                        [NodeSummaryKeys.MergeSatisfied] = aggregation.PolicySatisfied,
                        [NodeSummaryKeys.ParallelElapsedMs] = branchElapsed,
                        [NodeSummaryKeys.JoinDuplicate] = aggregation.Duplicate
                    };
                    // 无分支处置时不要写 parallelDisposition：null 键与"没有处置"是两件事。
                    if (aggregation.Aggregate is { } aggregate) joinSummary[NodeSummaryKeys.ParallelDisposition] = aggregate;
                    reportSummary = joinSummary;
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
            // 并行调度层主动取消（兄弟分支失败/本分支超时）不是程序缺陷：根因已由抛出方记录，
            // 这里再记一次会抢走错误码（超时分支会退化成 node_failure）。
            var schedulerCancellation = ex is OperationCanceledException ? data.CancellationReasonForNode(node.Id) : null;
            // 先停兄弟分支，再对外报故障：否则观察者一收到失败回调，兄弟分支可能已经
            // 抢在取消之前又发出一条设备命令。
            if (schedulerCancellation is null && ex is not OperationCanceledException)
                data.CancelSiblingBranches(node.Id, ParallelCancellationReasons.SiblingFailed);
            if (schedulerCancellation is null) data.RecordFault(node.Id, ex);
            var failureSummary = FailureSummary(data, ex, cancellationToken);
            if (schedulerCancellation is not null)
                failureSummary = new Dictionary<string, object?>(failureSummary)
                {
                    [NodeSummaryKeys.SchedulerCancelled] = true,
                    [NodeSummaryKeys.CancellationReason] = schedulerCancellation
                };
            // R10/F08：保留原始异常供统一结束阶段向观察者报告——本异常会被 WorkflowCore 捕获
            // 并转为结果返回，运行器的 catch 边界不会经过，不能只依赖那里记录异常链。
            data.AddReport(new NodeRunReport(
                node.Id,
                node.Type,
                false,
                sw.Elapsed.TotalMilliseconds,
                failureSummary,
                schedulerCancellation is null ? ex.Message : $"cancelled by {schedulerCancellation}",
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
        var started = Stopwatch.GetTimestamp();
        var failurePolicy = Nodes.ResultFileWriter.Choice(node, "failurePolicy", "route", "route", "rethrow");
        var captureStack = node.GetBool("captureStack", false);
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
            var attemptedNodeId = node.Id;
            try
            {
                foreach (var bodyNodeId in body)
                {
                    attemptedNodeId = bodyNodeId;
                    cancellationToken.ThrowIfCancellationRequested();
                    var outcome = await ExecuteAsync(data, bodyNodeId, cancellationToken).ConfigureAwait(false);
                    if (outcome != VisionNodeRuntimeOutcome.Executed || data.Halted) break;
                    if (node.GetBool("failOnWarning", false) && data.OutputsByNode.TryGetValue(bodyNodeId, out var bodyOutputs)
                        && bodyOutputs.TryGetValue("warningCount", out var warning) && Convert.ToDouble(warning.Value) > 0)
                        throw new InvalidOperationException($"Try body node '{bodyNodeId}' produced warnings.");
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
                data.ClearHandledTryError(attemptedNodeId, ex);
                if (ex is OperationCanceledException || attempt > maxRetries) break;
                if (retryDelayMs > 0)
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            finally { data.EndTryAttempt(); }
        }

        if (!succeeded) data.ClearTryBodyOutputs(body);
        var errorText = lastError?.Message ?? string.Empty;
        data.SetTryOutcome(node.Id, succeeded, errorText, attempts);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var stackTrace = captureStack && lastError is not null ? lastError.ToString() : null;
        if (stackTrace?.Length > 65536) stackTrace = stackTrace[..65536];
        var diagnostics = CoordinateValidation.Diagnostics(node.Type, ("succeeded", succeeded), ("error", errorText),
            ("attempts", attempts), ("retried", attempts > 1), ("elapsedMs", elapsedMs), ("failurePolicy", failurePolicy), ("stackTrace", stackTrace));
        if (!succeeded && failurePolicy == "rethrow" && lastError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastError).Throw();
        var outputs = new Dictionary<string, VisionValue>(marker.Outputs)
        {
            ["succeeded"] = VisionValue.Boolean(succeeded),
            ["errorMessage"] = VisionValue.String(errorText),
            ["attempts"] = VisionValue.Integer(attempts), ["retried"] = VisionValue.Boolean(attempts > 1),
            ["elapsedMs"] = VisionValue.Double(elapsedMs), ["diagnostics"] = VisionValue.String(diagnostics)
        };
        return new NodeExecutorResult(outputs, new Dictionary<string, object?>(marker.Summary)
        {
            ["succeeded"] = succeeded,
            ["error"] = errorText,
            ["attempts"] = attempts,
            ["handled"] = hadHandledFailures, ["elapsedMs"] = elapsedMs, ["retried"] = attempts > 1, ["diagnostics"] = diagnostics
        });
    }

    private static IReadOnlyDictionary<string, object?> FailureSummary(VisionWorkflowData data, Exception exception, CancellationToken cancellationToken)
    {
        var summary = new Dictionary<string, object?>();
        // 主动失败必须与运行时异常可区分：监控按 intentional/failureCode 分组，不会把计划内停机当缺陷。
        if (FindIntentionalFailure(exception) is { } intentional)
        {
            summary["intentional"] = true;
            summary["failureCode"] = intentional.Code;
            if (intentional.DetailsJson is { } details) summary["details"] = details;
        }
        if (data.CurrentTryAttempt is { } current)
        {
            summary["handledByTry"] = !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
            summary["tryNodeId"] = current.NodeId;
            summary["tryAttempt"] = current.Attempt;
        }
        return summary;
    }

    private static IntentionalWorkflowFailureException? FindIntentionalFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is IntentionalWorkflowFailureException intentional) return intentional;
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                if (inner is IntentionalWorkflowFailureException intentional) return intentional;
        return null;
    }

    private static IReadOnlyDictionary<string, VisionValueSnapshot> SnapshotValues(IReadOnlyDictionary<string, VisionValue> values)
        => values.ToDictionary(x => x.Key, x => SnapshotValue(x.Value), StringComparer.OrdinalIgnoreCase);

    private static VisionValueSnapshot SnapshotValue(VisionValue value)
    {
        if (value.Value is null) return new VisionValueSnapshot(value.Type.ToString(), "null");
        if (value.Type == VisionDataType.Roi && value.Value is VisionRoi roi)
            return SnapshotRoi(roi);
        if (value.Value is IVisionDomainImage scoped)
            return new VisionValueSnapshot(value.Type.ToString(), $"DomainImage {scoped.Width}x{scoped.Height}", new { scoped.Width, scoped.Height, scoped.Domain });
        if (value.Value is IVisionImage image)
            return new VisionValueSnapshot(value.Type.ToString(), $"Image {image.Width}x{image.Height}", new { image.Width, image.Height });
        if (value.Value is IReadOnlyList<VisionContour> contours)
            return new VisionValueSnapshot(value.Type.ToString(), $"Contours {contours.Count}",
                contours.Select(contour => contour with { NativeSnapshot = null }).ToArray());
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
            VisionDataType.Contour or VisionDataType.ContourProperties or VisionDataType.Region or VisionDataType.MetrologyResults or
            VisionDataType.StringList or VisionDataType.Table or VisionDataType.Json => value.Value,
            _ => value.Value.ToString()
        };
        return new VisionValueSnapshot(value.Type.ToString(), value.Value.ToString() ?? value.Type.ToString(), payload);
    }

    private static VisionValueSnapshot SnapshotRoi(VisionRoi roi)
    {
        const int maxPolygonSamples = 32;
        const int maxFullPolygonPoints = 256;
        const int maxBrushStrokeSamples = 16;
        const int maxBrushPointsPerStroke = 8;
        const int maxFullBrushPoints = 256;
        var type = roi.Type.ToString();
        object? payload;
        string display;
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
            {
                var x = roi.X ?? 0;
                var y = roi.Y ?? 0;
                var width = roi.Width ?? 0;
                var height = roi.Height ?? 0;
                payload = roi;
                display = FormattableString.Invariant($"ROI Rectangle · x={x:0.##}, y={y:0.##}, {width:0.##}×{height:0.##}");
                break;
            }
            case VisionRoiType.Circle:
            {
                var x = roi.X ?? 0;
                var y = roi.Y ?? 0;
                var radius = roi.Radius ?? 0;
                payload = roi;
                display = FormattableString.Invariant($"ROI Circle · center=({x:0.##}, {y:0.##}), r={radius:0.##}");
                break;
            }
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                var bounds = GetRoiBounds(points);
                payload = points.Count <= maxFullPolygonPoints
                    ? roi
                    : new { type, vertexCount = points.Count, bounds, samplePoints = SampleRoiPoints(points, maxPolygonSamples) };
                display = $"ROI Polygon · {points.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} vertices"
                    + (bounds is { } box ? FormattableString.Invariant($" · bounds=({box.MinX:0.##}, {box.MinY:0.##})–({box.MaxX:0.##}, {box.MaxY:0.##})") : string.Empty);
                break;
            }
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                long pointCount = 0;
                foreach (var stroke in strokes) pointCount += stroke?.Points?.Count ?? 0;
                var addStrokeCount = strokes.Count(stroke => stroke?.Mode == VisionRoiStrokeMode.Add);
                var eraseStrokeCount = strokes.Count(stroke => stroke?.Mode == VisionRoiStrokeMode.Erase);
                var bounds = GetRoiBounds(strokes.Where(stroke => stroke is not null).SelectMany(stroke => stroke.Points ?? []));
                var strokeSamples = strokes.Take(maxBrushStrokeSamples).Select(stroke => stroke is null ? null : new
                {
                    mode = stroke.Mode.ToString(),
                    stroke.Width,
                    pointCount = stroke.Points?.Count ?? 0,
                    samplePoints = SampleRoiPoints(stroke.Points ?? [], maxBrushPointsPerStroke)
                }).ToArray();
                payload = pointCount <= maxFullBrushPoints && strokes.Count <= maxBrushStrokeSamples
                    ? roi
                    : new { type, strokeCount = strokes.Count, addStrokeCount, eraseStrokeCount, pointCount, bounds, sampleStrokes = strokeSamples };
                display = $"ROI Brush · {strokes.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} strokes"
                    + FormattableString.Invariant($" ({addStrokeCount:N0} add, {eraseStrokeCount:N0} erase) · {pointCount:N0} points");
                break;
            }
            default:
                payload = new { type };
                display = $"ROI {type}";
                break;
        }
        return new VisionValueSnapshot(VisionDataType.Roi.ToString(), display, payload);
    }

    private readonly record struct RoiBoundsSnapshot(double MinX, double MinY, double MaxX, double MaxY);

    private static RoiBoundsSnapshot? GetRoiBounds(IEnumerable<VisionRoiPoint?> points)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var point in points)
        {
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        return double.IsFinite(minX) ? new RoiBoundsSnapshot(minX, minY, maxX, maxY) : null;
    }

    private static object?[] SampleRoiPoints(IReadOnlyList<VisionRoiPoint> points, int maximum)
    {
        if (points.Count == 0) return [];
        var count = Math.Min(points.Count, maximum);
        return Enumerable.Range(0, count).Select(index =>
        {
            var pointIndex = count == points.Count ? index : (int)Math.Round(index * (points.Count - 1d) / (count - 1));
            var point = points[pointIndex];
            return point is null ? null : (object)new { point.X, point.Y };
        }).ToArray();
    }
}
