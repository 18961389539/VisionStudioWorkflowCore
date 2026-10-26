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
                result = await dispatcher.ExecuteAsync(node, inputs, data.WorkflowScope, cancellationToken, data.RunId);
            if (node.Type.Equals("flow.try", StringComparison.OrdinalIgnoreCase))
                result = await ExecuteTryBodyAsync(data, node, result, cancellationToken);
            sw.Stop();

            data.OutputsByNode[node.Id] = result.Outputs;
            data.TrackOutputs(result.Outputs);
            data.AddOverlays(node.Id, result.Overlays);
            if (data.CaptureNodeImages) data.CaptureNodeImage(node.Id, result.Outputs);

            // Prefer the executor's own algorithmBackend (e.g. managed chamfer, opencv fisheye, hybrid).
            // Only fill from BackendFor when the node left it unset. Also stamp domain backend keys when missing.
            var reportSummary = result.Summary;
            if (!reportSummary.ContainsKey("algorithmBackend") &&
                Algorithms.VisionAlgorithmRuntime.BackendFor(node.Type) is { } algorithmBackend)
            {
                reportSummary = new Dictionary<string, object?>(reportSummary) { ["algorithmBackend"] = algorithmBackend };
            }
            reportSummary = Algorithms.VisionAlgorithmRuntime.EnsureDomainBackendLabels(node.Type, reportSummary);
            if (caliperDiagnostic is not null)
                reportSummary = new Dictionary<string, object?>(reportSummary)
                {
                    ["caliperDiagnosticsAvailable"] = data.CaptureCaliperDiagnostic(node.Id, observation.Sequence, caliperDiagnostic)
                };
            if (caliperEdgeDiagnostic is not null)
                reportSummary = new Dictionary<string, object?>(reportSummary)
                {
                    ["caliperEdgeDiagnosticsAvailable"] = data.CaptureCaliperEdgeDiagnostic(node.Id, observation.Sequence, caliperEdgeDiagnostic)
                };

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

            // 通用循环：循环节点自身作为入口标记执行一次，在这里初始化跨迭代状态。
            // while 的预算是参数；repeat 的总次数由执行器解析（端口优先）后随 total 输出回传。
            if (node.Type.Equals("flow.while", StringComparison.OrdinalIgnoreCase))
            {
                data.BeginLoop(node.Id, "while", node.GetInt("maxIterations", 100));
            }
            else if (node.Type.Equals("flow.repeat", StringComparison.OrdinalIgnoreCase))
            {
                if (!result.Outputs.TryGetValue("total", out var totalValue) || totalValue.Value is not long total || total is < 1)
                    throw new InvalidOperationException($"flow.repeat '{node.Id}' did not publish a positive iteration total.");
                data.BeginLoop(node.Id, "repeat", (int)total);
            }

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
            try
            {
                foreach (var bodyNodeId in body)
                {
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
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var stackTrace = captureStack && lastError is not null ? lastError.ToString() : null;
        if (stackTrace?.Length > 65536) stackTrace = stackTrace[..65536];
        var diagnostics = Nodes.CoordinateValidation.Diagnostics(node.Type, ("succeeded", succeeded), ("error", errorText),
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
            VisionDataType.Contour or VisionDataType.ContourProperties or VisionDataType.Region or VisionDataType.MetrologyResults => value.Value,
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
