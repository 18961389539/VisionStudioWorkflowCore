using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", async (JobStore jobs, CancellationToken ct) => Results.Ok(await jobs.ListAsync(ct)));

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
            return Results.Ok(await jobs.PublishAsync(id, version, "Publish", manifest, ct));
        }).RequireEngineer("job.publish", "job");

        app.MapPost("/api/jobs/{id}/rollback/{version:int}", async (
            string id,
            int version,
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
            return Results.Ok(await jobs.PublishAsync(id, version, "Rollback", manifest, ct));
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
            CancellationToken ct) =>
        {
            var snapshot = await jobs.GetPublishedSnapshotAsync(id, ct);
            await dependencies.ValidateOrThrowAsync(snapshot.DependencyManifest, snapshot.Workflow, ct);
            var context = new RunTraceContext("PublishedJob", id, snapshot.Version, snapshot.WorkflowHash, DependencyManifestHash: snapshot.DependencyManifest.ManifestHash);
            var runId = RunTraceRecorder.NewRunId();
            // 运行期硬件租约：与 ad-hoc/debug 运行一致，执行结束（含失败/取消）即释放
            using var lease = await production.AcquireRunLeaseAsync(runId, snapshot.Workflow, $"Cannot run job '{id}'", ct);
            await recorder.BeginAsync(runId, DateTimeOffset.UtcNow, snapshot.Workflow, context);
            // 手动触发的作业运行同样支持按节点图像检查（自动化的 Production Runtime 不走此端点）
            var result = await runner.RunAsync(snapshot.Workflow, new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true), runId, ct);
            await recorder.FinalizeAsync(result, snapshot.Workflow, context);
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
