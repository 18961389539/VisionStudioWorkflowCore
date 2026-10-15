using VisionStudio.Api.Security;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public sealed record CreateRecipeRequest(string Name, WorkflowDefinition Workflow, string? Description = null, string? Id = null, string? ChangeNote = null, Dictionary<string, string>? ParameterBindings = null);
public sealed record CopyRecipeRequest(string Name, string? NewId = null, string? Description = null, int? SourceVersion = null);

public static class ProductRecipeEndpoints
{
    public static IEndpointRouteBuilder MapProductRecipeEndpoints(this IEndpointRouteBuilder app)
    {
        // Recipes are independent authoring units; no Product needs to exist first.
        app.MapPost("/api/recipes", async (CreateRecipeRequest request, JobStore jobs, VisionWorkflowCompiler compiler, WorkflowModuleExpander modules, CancellationToken ct) =>
        {
            try
            {
                var expanded = await modules.ExpandAsync(request.Workflow, ct);
                compiler.Compile(expanded.Workflow);
            }
            catch (InvalidOperationException ex) { throw new ApiValidationException(ex.Message, ex); }
            return Results.Ok(await jobs.CreateAsync(new CreateJobRequest(
                request.Id ?? $"recipe-{Guid.NewGuid():N}", request.Name, request.Description, request.Workflow,
                request.ChangeNote ?? "Initial recipe version", ParameterBindings: request.ParameterBindings, RequiresValidation: true), ct));
        }).RequireEngineer("recipe.create", "recipe");

        app.MapPost("/api/jobs/{id}/clone", async (string id, CopyRecipeRequest request, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.CloneRecipeAsync(id, new CloneRecipeRequest(request.NewId ?? $"recipe-{Guid.NewGuid():N}", null, null, request.Name, request.Description, request.SourceVersion), ct))).RequireEngineer("recipe.clone", "recipe");

        app.MapGet("/api/jobs/{id}/versions/{fromVersion:int}/diff/{toVersion:int}", async (string id, int fromVersion, int toVersion, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.DiffAsync(id, fromVersion, toVersion, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/jobs/{id}/versions/{version:int}/validation-candidates", async (string id, int version, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.ValidationCandidatesAsync(id, version, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/jobs/{id}/versions/{version:int}/validation", async (string id, int version, LinkRecipeValidationRequest request, ProductRecipeStore store, CancellationToken ct) =>
            Results.Ok(await store.LinkValidationAsync(id, version, request, ct))).RequireEngineer("recipe.validation.link", "recipe");

        return app;
    }
}
