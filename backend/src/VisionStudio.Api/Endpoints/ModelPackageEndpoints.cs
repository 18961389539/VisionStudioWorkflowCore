using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class ModelPackageEndpoints
{
    public static IEndpointRouteBuilder MapModelPackageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/model-packages", (ModelPackageService packages) =>
            Results.Ok(packages.List())).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/model-packages/{id}", (string id, ModelPackageService packages) =>
            Results.Ok(packages.Get(id))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/model-packages/preflight", (ModelPackageImportRequest request, ModelPackageService packages) =>
            Results.Ok(packages.Preflight(request.SourcePath, request.ExpectedKind)))
            .RequireEngineer("model-package.preflight", "model-package");

        app.MapPost("/api/model-packages/import", (ModelPackageImportRequest request, ModelPackageService packages) =>
            Results.Ok(packages.Import(request)))
            .RequireEngineer("model-package.import", "model-package");

        app.MapPost("/api/model-packages/{id}/smoke", (string id, ModelPackageSmokeRequest? request, ModelPackageService packages) =>
            Results.Ok(packages.Smoke(id, request?.OpenOnnxSession ?? false)))
            .RequireEngineer("model-package.smoke", "model-package");

        app.MapDelete("/api/model-packages/{id}", (string id, ModelPackageService packages) =>
        {
            packages.Delete(id);
            return Results.NoContent();
        }).RequireEngineer("model-package.delete", "model-package");

        return app;
    }
}
