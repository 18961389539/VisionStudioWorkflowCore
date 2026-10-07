using VisionStudio.Api.Security;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class ProductRecipeEndpoints
{
    public static IEndpointRouteBuilder MapProductRecipeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/products", async (ProductRecipeStore store, CancellationToken ct) => Results.Ok(await store.ListProductsAsync(ct)))
            .RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/products", async (CreateProductRequest request, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateProductAsync(request, ct))).RequireEngineer("product.create", "product");

        app.MapPost("/api/products/{productId}/recipes", async (string productId, CreateProductRecipeRequest request, ProductRecipeStore store, VisionWorkflowCompiler compiler, WorkflowModuleExpander modules, CancellationToken ct) =>
        {
            try
            {
                var expanded = await modules.ExpandAsync(request.Workflow, ct);
                compiler.Compile(expanded.Workflow);
            }
            catch (InvalidOperationException ex) { throw new ApiValidationException(ex.Message, ex); }
            return Results.Ok(await store.CreateRecipeAsync(productId, request, ct));
        }).RequireEngineer("recipe.create", "recipe");

        app.MapPost("/api/jobs/{id}/clone", async (string id, CloneRecipeRequest request, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.CloneRecipeAsync(id, request, ct))).RequireEngineer("recipe.clone", "recipe");

        app.MapGet("/api/jobs/{id}/versions/{fromVersion:int}/diff/{toVersion:int}", async (string id, int fromVersion, int toVersion, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.DiffAsync(id, fromVersion, toVersion, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/jobs/{id}/versions/{version:int}/validation-candidates", async (string id, int version, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.ValidationCandidatesAsync(id, version, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/jobs/{id}/versions/{version:int}/validation", async (string id, int version, LinkRecipeValidationRequest request, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.LinkValidationAsync(id, version, request, ct))).RequireEngineer("recipe.validation.link", "recipe");

        return app;
    }
}
