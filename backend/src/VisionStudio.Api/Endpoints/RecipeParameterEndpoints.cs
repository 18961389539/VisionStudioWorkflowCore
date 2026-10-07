using VisionStudio.Api.Security;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Endpoints;

public static class RecipeParameterEndpoints
{
    public static IEndpointRouteBuilder MapRecipeParameterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/products/{productId}/parameters", async (string productId, RecipeParameterStore store, CancellationToken ct) =>
            Results.Ok(await store.GetProductAsync(productId, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPut("/api/products/{productId}/parameters", async (string productId, ReplaceParameterValuesRequest request, RecipeParameterStore store, CancellationToken ct) =>
            Results.Ok(await store.ReplaceProductAsync(productId, request, ct))).RequireEngineer("product.parameters.update", "product");

        app.MapGet("/api/jobs/{id}/parameters", async (string id, RecipeParameterStore store, CancellationToken ct) =>
            Results.Ok(await store.GetRecipeAsync(id, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPut("/api/jobs/{id}/parameters", async (string id, ReplaceParameterValuesRequest request, RecipeParameterStore store, CancellationToken ct) =>
            Results.Ok(await store.ReplaceRecipeAsync(id, request, ct))).RequireEngineer("recipe.parameters.update", "recipe");

        app.MapPost("/api/jobs/{id}/parameterization/resolve", async (string id, ResolveRecipeParametersRequest request, RecipeParameterStore store, CancellationToken ct) =>
            Results.Ok(await store.ResolveForJobAsync(id, request.Workflow, request.Bindings, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/jobs/{id}/versions/{version:int}/parameterization", async (string id, int version, JobStore jobs, CancellationToken ct) =>
        {
            var snapshot = await jobs.GetVersionAsync(id, version, ct)
                ?? throw new ApiNotFoundException($"Recipe '{id}' V{version} does not exist.");
            return Results.Ok(new
            {
                snapshot.JobId,
                snapshot.Version,
                snapshot.BaseWorkflowHash,
                snapshot.WorkflowHash,
                snapshot.BaseWorkflow,
                effectiveWorkflow = snapshot.Workflow,
                parameterBindings = snapshot.ParameterBindings,
                parameterSnapshot = snapshot.ParameterSnapshot,
                bindingCount = snapshot.ParameterBindings?.Count ?? 0
            });
        }).RequireAuthorization(SecurityPolicies.Engineer);

        return app;
    }
}
