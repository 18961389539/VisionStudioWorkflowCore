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
            CancellationToken ct) =>
        {
            var context = new RunTraceContext("AdHoc");
            var runId = RunTraceRecorder.NewRunId();
            // 运行期硬件租约：覆盖整段执行（含计时收尾），完成/取消/异常时经 using 统一释放
            using var lease = await production.AcquireRunLeaseAsync(runId, workflow, $"Cannot run workflow '{workflow.Id}'", ct);
            await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, workflow, context);
            // 交互式运行：捕获逐节点图像以支持检查中间结果（生产/后台运行保持关闭）
            var result = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true), runId, ct);
            await recorder.FinalizeAsync(result, workflow, context);
            return EndpointResults.StoreAndMapRunResult(result, store);
        }).RequireEngineer("workflow.run.adhoc", "workflow");

        app.MapPost("/api/debug/run", async (
            DebugRunRequest request,
            IVisionWorkflowRunner runner,
            RunStore store,
            RunTraceRecorder recorder,
            ProductionRuntimeService production,
            CancellationToken ct) =>
        {
            // 交互式调试：捕获逐节点图像以支持按节点检查中间结果
            var options = (request.Options ?? new VisionRunOptions()) with { CaptureNodeImages = true };
            var context = new RunTraceContext("Debug", DebugMode: options.Mode.ToString());
            var runId = RunTraceRecorder.NewRunId();
            using var lease = await production.AcquireRunLeaseAsync(runId, request.Workflow, $"Cannot debug-run workflow '{request.Workflow.Id}'", ct);
            await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, request.Workflow, context);
            var result = await runner.RunAsync(request.Workflow, options, runId, ct);
            await recorder.FinalizeAsync(result, request.Workflow, context);
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
                        .Select(x => new { x.NodeId, x.PortName, x.Width, x.Height, bytes = x.Jpeg.Length })
                        .ToArray()
                })
                : Results.NotFound());

        app.MapGet("/api/runs/{runId}/nodes/{nodeId}/image", (string runId, string nodeId, RunStore store) =>
        {
            var image = store.TryGet(runId, out var artifact)
                ? artifact.NodeImages.FirstOrDefault(x => x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
                : null;
            return image is null ? Results.NotFound() : Results.File(image.Jpeg, "image/jpeg");
        });

        return app;
    }
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
