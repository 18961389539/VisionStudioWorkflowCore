using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class WorkflowExecutionEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/compile", async (WorkflowDefinition workflow, VisionWorkflowCompiler compiler, WorkflowModuleExpander modules, CancellationToken ct) =>
        {
            try
            {
                var expanded = await modules.ExpandAsync(workflow, ct);
                var compiled = compiler.Compile(expanded.Workflow);
                return Results.Ok(new
                {
                    compiled.WorkflowCoreId,
                    compiled.Version,
                    compiled.OrderedNodeIds,
                    compiled.PipelineSegments,
                    pipelineSegmentCount = compiled.PipelineSegments.Count,
                    compiled.ControlRegions,
                    structuredRegionCount = compiled.ControlRegions.Count,
                    moduleDependencies = expanded.Dependencies,
                    expandedNodeCount = expanded.Workflow.Nodes.Count,
                    compiled.DslJson
                });
            }
            catch (InvalidOperationException ex)
            {
                throw new ApiValidationException(ex.Message, ex);
            }
        }).RequireAuthorization(SecurityPolicies.Engineer);

        // Validation is intentionally a result-style endpoint rather than ProblemDetails because
        // the designer consumes valid=false as normal interactive feedback.
        app.MapPost("/api/validate", async (WorkflowDefinition workflow, VisionWorkflowCompiler compiler, WorkflowModuleExpander modules, CancellationToken ct) =>
        {
            try
            {
                var expanded = await modules.ExpandAsync(workflow, ct);
                var compiled = compiler.Compile(expanded.Workflow);
                return Results.Ok(new
                {
                    valid = true,
                    compiled.WorkflowCoreId,
                    compiled.OrderedNodeIds,
                    compiled.PipelineSegments,
                    pipelineSegmentCount = compiled.PipelineSegments.Count,
                    compiled.ControlRegions,
                    structuredRegionCount = compiled.ControlRegions.Count,
                    moduleDependencies = expanded.Dependencies,
                    expandedNodeCount = expanded.Workflow.Nodes.Count,
                    registeredNodeTypes = expanded.Workflow.Nodes.Select(x => x.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { valid = false, error = ex.Message });
            }
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/run", async (
            WorkflowDefinition workflow,
            IVisionWorkflowRunner runner,
            RunStore store,
            RunTraceRecorder recorder,
            ProductionRuntimeService production,
            DeviceActionAuthorizationService auth,
            WorkflowModuleExpander modules,
            VisionNodeRegistry nodes,
            CancellationToken ct) =>
        {
            var context = new RunTraceContext("AdHoc");
            var runId = RunTraceRecorder.NewRunId();
            // 运行期硬件租约：覆盖整段执行（含计时收尾），完成/取消/异常时经 using 统一释放
            using var lease = await production.AcquireRunLeaseAsync(runId, workflow, $"Cannot run workflow '{workflow.Id}'", ct);
            // A02：进入所有权环境——节点真实派发边界据此校验租约代次（失去所有权的旧执行被拒绝）。
            using var commandScope = DeviceCommandOwnership.Enter(lease);
            // N01/N06：动作分类、完成判定与意图登记必须消费**展开后的执行计划**——
            // 模块调用在运行期展开（报告 ID 带 `module-xxx::` 前缀），用未展开定义判定会漏检；
            // 纯计算流程不登记动作意图（失败不应阻断设备动作）。
            var effectiveWorkflow = (await modules.ExpandAsync(workflow, ct)).Workflow;
            var hasSideEffects = DeviceSideEffectClassifier.HasDeviceSideEffects(effectiveWorkflow, nodes);
            using var actionIntent = hasSideEffects
                ? await auth.BeginWorkflowIntentAsync("workflow.run.adhoc", effectiveWorkflow, runId, ct)
                : null;
            if (hasSideEffects)
                await recorder.BeginStrictAsync(runId, DateTimeOffset.UtcNow, workflow, context);
            else
                await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, workflow, context);
            // 交互式运行：捕获逐节点图像以支持检查中间结果（生产/后台运行保持关闭）
            var result = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true), runId, ct);
            await recorder.FinalizeAsync(RedactTrainingModelAssets(result), workflow, context);
            // C01/F02：成功 + 追溯证据已提交 + **设备动作确认完成** 三者齐全才解除意图。
            // 仅"流程返回成功"不够——异步机器人命令（SendTarget/未等待到位）只是"已受理"。
            if (actionIntent is not null)
            {
                if (result.Success && !DeviceSideEffectClassifier.RequiresManualReconciliation(effectiveWorkflow, result, nodes))
                    actionIntent.Complete();
                else if (result.Success)
                    actionIntent.MarkUnknown("the run returned success but an accepted device action still awaits completion confirmation");
            }
            return EndpointResults.StoreAndMapRunResult(result, store);
        }).RequireEngineer("workflow.run.adhoc", "workflow");

        app.MapPost("/api/debug/run", async (
            DebugRunRequest request,
            IVisionWorkflowRunner runner,
            RunStore store,
            RunTraceRecorder recorder,
            ProductionRuntimeService production,
            DeviceActionAuthorizationService auth,
            WorkflowModuleExpander modules,
            VisionNodeRegistry nodes,
            CancellationToken ct) =>
        {
            // 交互式调试：捕获逐节点图像以支持按节点检查中间结果
            var options = (request.Options ?? new VisionRunOptions()) with { CaptureNodeImages = true };
            var context = new RunTraceContext("Debug", DebugMode: options.Mode.ToString());
            var runId = RunTraceRecorder.NewRunId();
            using var lease = await production.AcquireRunLeaseAsync(runId, request.Workflow, $"Cannot debug-run workflow '{request.Workflow.Id}'", ct);
            // A02：进入所有权环境——节点真实派发边界据此校验租约代次（失去所有权的旧执行被拒绝）。
            using var commandScope = DeviceCommandOwnership.Enter(lease);
            // N01/N06：展开执行计划后判定副作用；纯计算调试不登记动作意图。
            var effectiveWorkflow = (await modules.ExpandAsync(request.Workflow, ct)).Workflow;
            var hasSideEffects = DeviceSideEffectClassifier.HasDeviceSideEffects(effectiveWorkflow, nodes);
            using var actionIntent = hasSideEffects
                ? await auth.BeginWorkflowIntentAsync("workflow.run.debug", effectiveWorkflow, runId, ct)
                : null;
            if (hasSideEffects)
                await recorder.BeginStrictAsync(runId, DateTimeOffset.UtcNow, request.Workflow, context);
            else
                await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, request.Workflow, context);
            var result = await runner.RunAsync(request.Workflow, options, runId, ct);
            await recorder.FinalizeAsync(RedactTrainingModelAssets(result), request.Workflow, context);
            if (actionIntent is not null)
            {
                if (result.Success && !DeviceSideEffectClassifier.RequiresManualReconciliation(effectiveWorkflow, result, nodes))
                    actionIntent.Complete();
                else if (result.Success)
                    actionIntent.MarkUnknown("the debug run returned success but an accepted device action still awaits completion confirmation");
            }
            return EndpointResults.StoreAndMapRunResult(result, store);
        }).RequireEngineer("workflow.run.debug", "workflow");

        app.MapGet("/api/runs/{runId}/preview", (string runId, RunStore store) =>
            store.TryGet(runId, out var artifact) && artifact.PreviewJpeg is { Length: > 0 } jpeg
                ? Results.File(jpeg, "image/jpeg")
                : Results.NotFound());

        app.MapGet("/api/runs/{runId}/overlays", (string runId, RunStore store) =>
            store.TryGet(runId, out var artifact)
                ? Results.Ok(new { artifact.Width, artifact.Height, artifact.Overlays })
                : Results.NotFound());

        // 交互式调试：按节点查看中间图像（运行期捕获，随 RunStore 保留 32 条运行）
        app.MapGet("/api/runs/{runId}/images", (string runId, RunStore store) =>
            store.TryGet(runId, out var artifact)
                ? Results.Ok(new
                {
                    runId,
                    images = artifact.NodeImages
                        .Select(x => new { x.NodeId, x.PortName, x.Width, x.Height, x.ContentType, bytes = x.Bytes.Length })
                        .ToArray()
                })
                : Results.NotFound());

        app.MapGet("/api/runs/{runId}/nodes/{nodeId}/image", (string runId, string nodeId, string? portName, RunStore store) =>
        {
            var nodeImages = store.TryGet(runId, out var artifact)
                ? artifact.NodeImages.Where(x => x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            var image = portName is null
                ? nodeImages.FirstOrDefault(x => x.PortName.Equals("image", StringComparison.OrdinalIgnoreCase)) ?? nodeImages.FirstOrDefault()
                : nodeImages.FirstOrDefault(x => x.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            return image is null ? Results.NotFound() : Results.File(image.Bytes, image.ContentType);
        });

        app.MapGet("/api/runs/{runId}/nodes/{nodeId}/caliper-edge-diagnostics", (
            string runId, string nodeId, RunStore store) =>
        {
            var capture = store.TryGet(runId, out var artifact)
                ? artifact.CaliperEdgeDiagnostics?.FirstOrDefault(x => x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)) : null;
            if (capture is null) return Results.NotFound(new { error = "此运行没有单边缘卡尺诊断数据，或数据已被清理。请重新运行节点。" });
            var data = capture.Data;
            return Results.Ok(new
            {
                runId, nodeId, capture.ExecutionSequence,
                data.ImageWidth, data.ImageHeight, data.CoordinateFrame, data.Start, data.End,
                data.ProfileStart, data.ProfileEnd, data.Clipped, data.StartOffset, data.Spacing,
                data.MinContrast, data.SampleWidth, data.Polarity, data.Direction, data.Refine,
                data.Found, data.Reason, data.EdgePosition, data.EdgePoint, data.EdgeStrength, data.FitError,
                data.SelectedCandidateIndex, data.Candidates,
                profile = data.Profile.Select(value => double.IsFinite(value) ? (double?)value : null).ToArray(),
                sampleCount = data.Profile.Length, validSampleCount = data.Profile.Count(double.IsFinite)
            });
        }).RequireAuthorization(SecurityPolicies.Engineer);

        // Read captured samples only; selecting a spoke must never execute a workflow or a device.
        app.MapGet("/api/runs/{runId}/nodes/{nodeId}/caliper-diagnostics", (
            string runId, string nodeId, int? spokeIndex, RunStore store) =>
        {
            var capture = store.TryGet(runId, out var artifact)
                ? artifact.CaliperDiagnostics?.FirstOrDefault(x => x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)) : null;
            if (capture is null) return Results.NotFound(new { error = "此运行没有卡尺诊断数据，或数据已被清理。请重新运行节点。" });
            var data = capture.Data;
            if (spokeIndex is { } index)
            {
                if (index < 0 || index >= data.Spokes.Count)
                    return Results.BadRequest(new { error = "卡尺序号超出本次运行的采样范围。" });
                var spoke = data.Spokes[index];
                return Results.Ok(new
                {
                    runId, nodeId, capture.ExecutionSequence,
                    spoke.Index, spoke.AngleDeg, spoke.Status, spoke.StartRadius, spoke.Spacing,
                    spoke.ProfileStart, spoke.ProfileEnd, spoke.Clipped, spoke.EdgeRadius, spoke.EdgeStrength, spoke.EdgePoint,
                    profile = spoke.Profile.Select(value => double.IsFinite(value) ? (double?)value : null).ToArray(),
                    spoke.Candidates
                });
            }
            return Results.Ok(new
            {
                runId, nodeId, capture.ExecutionSequence,
                data.ImageWidth, data.ImageHeight, data.CoordinateFrame, data.Center, data.NominalRadius, data.RadialBand,
                data.MinContrast, data.SampleWidth, data.Polarity, data.SearchDirection, data.Found, data.Reason,
                spokes = data.Spokes.Select(spoke => new
                {
                    spoke.Index, spoke.AngleDeg, spoke.Start, spoke.End, spoke.Status,
                    spoke.ProfileStart, spoke.ProfileEnd, spoke.Clipped, spoke.EdgePoint,
                    spoke.EdgeRadius, spoke.EdgeStrength, sampleCount = spoke.Profile.Length,
                    validSampleCount = spoke.Profile.Count(double.IsFinite), candidateCount = spoke.Candidates.Count
                }).ToArray()
            });
        }).RequireAuthorization(SecurityPolicies.Engineer);

        return app;
    }

    private static WorkflowRunResult RedactTrainingModelAssets(WorkflowRunResult result) => result with
    {
        NodeReports = result.NodeReports.Select(report =>
            report.Summary.ContainsKey("trainingModelData") || report.Summary.ContainsKey("trainingImageData") || report.Summary.ContainsKey("referenceImage")
                ? report with { Summary = report.Summary.Where(pair => pair.Key is not ("trainingModelData" or "trainingPreviewImage" or "trainingImageData" or "referenceImage")).ToDictionary(pair => pair.Key, pair => pair.Value) }
                : report).ToArray()
    };

}

internal static class EndpointResults
{
    public static IResult StoreAndMapRunResult(WorkflowRunResult result, RunStore store)
    {
        store.Put(result);
        var response = new
        {
            result.RunId,
            result.Success,
            result.TotalDurationMs,
            result.PreviewAvailable,
            result.PreviewWidth,
            result.PreviewHeight,
            result.Overlays,
            result.NodeReports,
            result.Error,
            result.DebugState,
            result.HaltNodeId,
            result.HaltReason,
            result.QualityDisposition,
            result.ControlFlowDecisions,
            result.ErrorCode,
            engine = "Workflow Core + Recursive Structured IR + VisionPipelineExecutor"
        };
        return result.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
}
