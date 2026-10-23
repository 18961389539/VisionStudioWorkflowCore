using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", async (JobStore jobs, CancellationToken ct) => Results.Ok(await jobs.ListAsync(ct)))
            .Produces<IReadOnlyList<JobDescriptor>>();

        app.MapPost("/api/jobs", async (
            CreateJobRequest request,
            JobStore jobs,
            VisionWorkflowCompiler compiler,
            WorkflowModuleExpander modules,
            CancellationToken ct) =>
        {
            await ValidateWorkflowAsync(request.Workflow, compiler, modules, ct);
            return Results.Ok(await jobs.CreateAsync(request, ct));
        }).RequireEngineer("job.create", "job");

        app.MapGet("/api/jobs/{id}", async (string id, JobStore jobs, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        app.MapPost("/api/jobs/{id}/versions", async (
            string id,
            SaveJobVersionRequest request,
            JobStore jobs,
            VisionWorkflowCompiler compiler,
            WorkflowModuleExpander modules,
            CancellationToken ct) =>
        {
            await ValidateWorkflowAsync(request.Workflow, compiler, modules, ct);
            return Results.Ok(await jobs.AddVersionAsync(id, request, ct));
        }).RequireEngineer("job.version.create", "job");

        app.MapGet("/api/jobs/{id}/versions/{version:int}", async (string id, int version, JobStore jobs, CancellationToken ct) =>
        {
            var snapshot = await jobs.GetVersionAsync(id, version, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        app.MapPost("/api/jobs/{id}/versions/{version:int}/publish", async (
            string id,
            int version,
            DateTimeOffset? expectedUpdatedAt,
            JobStore jobs,
            VisionWorkflowCompiler compiler,
            WorkflowModuleExpander modules,
            RuntimeDependencyManifestService dependencies,
            CancellationToken ct) =>
        {
            var snapshot = await jobs.GetVersionAsync(id, version, ct)
                ?? throw new ApiNotFoundException($"Version {version} does not exist for job '{id}'.");
            await ValidateWorkflowAsync(snapshot.Workflow, compiler, modules, ct);
            var manifest = await dependencies.CaptureAsync(snapshot.Workflow, ct);
            return Results.Ok(await jobs.PublishAsync(id, version, "Publish", manifest, ct, expectedUpdatedAt));
        }).RequireEngineer("job.publish", "job");

        app.MapPost("/api/jobs/{id}/rollback/{version:int}", async (
            string id,
            int version,
            DateTimeOffset? expectedUpdatedAt,
            JobStore jobs,
            VisionWorkflowCompiler compiler,
            WorkflowModuleExpander modules,
            RuntimeDependencyManifestService dependencies,
            CancellationToken ct) =>
        {
            var snapshot = await jobs.GetVersionAsync(id, version, ct)
                ?? throw new ApiNotFoundException($"Version {version} does not exist for job '{id}'.");
            await ValidateWorkflowAsync(snapshot.Workflow, compiler, modules, ct);
            var manifest = await dependencies.CaptureAsync(snapshot.Workflow, ct);
            return Results.Ok(await jobs.PublishAsync(id, version, "Rollback", manifest, ct, expectedUpdatedAt));
        }).RequireEngineer("job.rollback", "job");

        app.MapGet("/api/jobs/{id}/dependencies", async (string id, JobStore jobs, CancellationToken ct) =>
        {
            var snapshot = await jobs.GetPublishedSnapshotAsync(id, ct);
            return Results.Ok(snapshot.DependencyManifest);
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/jobs/{id}/dependencies/validate", async (
            string id,
            JobStore jobs,
            RuntimeDependencyManifestService dependencies,
            CancellationToken ct) =>
        {
            var snapshot = await jobs.GetPublishedSnapshotAsync(id, ct);
            return Results.Ok(await dependencies.ValidateAsync(snapshot.DependencyManifest, snapshot.Workflow, ct));
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/jobs/{id}/run", async (
            string id,
            JobStore jobs,
            IVisionWorkflowRunner runner,
            RunStore store,
            RunTraceRecorder recorder,
            RuntimeDependencyManifestService dependencies,
            ProductionRuntimeService production,
            DeviceActionAuthorizationService auth,
            WorkflowModuleExpander modules,
            VisionNodeRegistry nodes,
            CancellationToken ct) =>
        {
            var snapshot = await jobs.GetPublishedSnapshotAsync(id, ct);
            await dependencies.ValidateOrThrowAsync(snapshot.DependencyManifest, snapshot.Workflow, ct);
            var context = new RunTraceContext("PublishedJob", id, snapshot.Version, snapshot.WorkflowHash, DependencyManifestHash: snapshot.DependencyManifest.ManifestHash);
            var runId = RunTraceRecorder.NewRunId();
            // 运行期硬件租约：与 ad-hoc/debug 运行一致，执行结束（含失败/取消）即释放
            using var lease = await production.AcquireRunLeaseAsync(runId, snapshot.Workflow, $"Cannot run job '{id}'", ct);
            // A02：进入所有权环境——节点真实派发边界据此校验租约代次（失去所有权的旧执行被拒绝）。
            using var commandScope = DeviceCommandOwnership.Enter(lease);
            // F01：手动触发的已发布作业运行同样受统一动作授权约束，并在执行前耐久登记意图；
            // 含副作用流程的起始证据写失败绝不盲跑（与 /api/run 同一契约）。
            // N01/N06：展开执行计划后判定副作用与完成度；纯计算作业不登记动作意图。
            var effectiveWorkflow = (await modules.ExpandAsync(snapshot.Workflow, ct)).Workflow;
            var hasSideEffects = DeviceSideEffectClassifier.HasDeviceSideEffects(effectiveWorkflow, nodes);
            using var actionIntent = hasSideEffects
                ? await auth.BeginWorkflowIntentAsync("workflow.run.publishedJob", effectiveWorkflow, runId, ct)
                : null;
            if (hasSideEffects)
                await recorder.BeginStrictAsync(runId, DateTimeOffset.UtcNow, snapshot.Workflow, context);
            else
                await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, snapshot.Workflow, context);
            // 手动触发的作业运行同样支持按节点图像检查（自动化的 Production Runtime 不走此端点）
            var result = await runner.RunAsync(snapshot.Workflow, new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true), runId, ct);
            await recorder.FinalizeAsync(result, snapshot.Workflow, context);
            // C01：只有流程成功且**所有已受理的设备动作都确认完成**才解除意图。
            if (actionIntent is not null)
            {
                if (result.Success && !DeviceSideEffectClassifier.RequiresManualReconciliation(effectiveWorkflow, result, nodes))
                    actionIntent.Complete();
                else if (result.Success)
                    actionIntent.MarkUnknown("the published job returned success but an accepted device action still awaits completion confirmation");
            }
            return EndpointResults.StoreAndMapRunResult(result, store);
        }).RequireOperator("job.run", "job");

        return app;
    }

    private static async Task ValidateWorkflowAsync(WorkflowDefinition workflow, VisionWorkflowCompiler compiler, WorkflowModuleExpander modules, CancellationToken ct)
    {
        try
        {
            var expanded = await modules.ExpandAsync(workflow, ct);
            compiler.Compile(expanded.Workflow);
        }
        catch (InvalidOperationException ex) { throw new ApiValidationException(ex.Message, ex); }
    }
}
