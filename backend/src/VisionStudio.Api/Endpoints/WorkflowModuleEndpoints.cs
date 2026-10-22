using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class WorkflowModuleEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowModuleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/modules", async (WorkflowModuleStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/modules/extract", async (ExtractWorkflowModuleRequest request, WorkflowModuleAuthoringService service, CancellationToken ct) =>
            Results.Ok(await service.ExtractAsync(request,ct))).RequireEngineer("module.extract","workflow-module");

        app.MapGet("/api/modules/{id}", async (string id, WorkflowModuleStore store, CancellationToken ct) =>
        {
            var module=await store.GetAsync(id,ct);
            return module is null?Results.NotFound():Results.Ok(module);
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/modules/{id}/versions/{version:int}", async (string id,int version,WorkflowModuleStore store,CancellationToken ct) =>
        {
            var snapshot=await store.GetVersionAsync(id,version,ct);
            return snapshot is null?Results.NotFound():Results.Ok(snapshot);
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/modules/{id}/versions", async (string id,SaveWorkflowModuleVersionRequest request,WorkflowModuleAuthoringService service,CancellationToken ct) =>
            Results.Ok(await service.AddVersionAsync(id,request,ct))).RequireEngineer("module.version.create","workflow-module");

        app.MapPost("/api/modules/expand", async (ExpandWorkflowModulesRequest request,WorkflowModuleExpander expander,CancellationToken ct) =>
        {
            var expanded=await expander.ExpandAsync(request.Workflow,ct);
            return Results.Ok(new{expanded.Workflow,expanded.Dependencies,expandedNodeCount=expanded.Workflow.Nodes.Count});
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/modules/validate", async (ExpandWorkflowModulesRequest request,WorkflowModuleExpander expander,VisionWorkflowCompiler compiler,CancellationToken ct) =>
        {
            try
            {
                var expanded=await expander.ExpandAsync(request.Workflow,ct);
                var compiled=compiler.Compile(expanded.Workflow);
                return Results.Ok(new{valid=true,expanded.Dependencies,expandedNodeCount=expanded.Workflow.Nodes.Count,compiled.OrderedNodeIds,compiled.PipelineSegments,compiled.ControlRegions});
            }
            catch(Exception ex)
            {
                return Results.BadRequest(new{valid=false,error=ex.Message});
            }
        }).RequireAuthorization(SecurityPolicies.Engineer);

        return app;
    }
}
