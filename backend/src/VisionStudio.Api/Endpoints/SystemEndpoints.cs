using VisionStudio.Api.Contracts;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            service = "VisionStudio.Api",
            workflowEngine = "Workflow Core 3.21.0",
            pluginLoader = "McMaster.NETCore.Plugins 2.0.0",
            mvp = "V0.63 case verification gate + dataset regression evidence"
        })).AllowAnonymous();

        app.MapGet("/api/host-mode", (IConfiguration configuration) =>
        {
            var runtimeOnly = configuration.GetValue("VisionStudio:RuntimeOnly", false);
            return Results.Ok(new { runtimeOnly, mode = runtimeOnly ? "Runtime" : "Designer" });
        });

        app.MapGet("/api/catalog", (HttpContext http, VisionNodeRegistry registry) =>
        {
            var catalog = registry.Catalog;
            var hash = VisionCatalogSnapshot.ComputeHash(catalog);
            http.Response.Headers["ETag"] = $"\"{hash}\"";
            http.Response.Headers["X-VisionStudio-Catalog-Schema"] = VisionCatalogSnapshot.SchemaVersion.ToString();
            http.Response.Headers["X-VisionStudio-Catalog-Hash"] = hash;
            return Results.Ok(catalog);
        });

        app.MapGet("/api/catalog/meta", (VisionNodeRegistry registry) =>
        {
            var catalog = registry.Catalog;
            return Results.Ok(new
            {
                schemaVersion = VisionCatalogSnapshot.SchemaVersion,
                hash = VisionCatalogSnapshot.ComputeHash(catalog),
                nodeCount = catalog.Count,
                builtInNodeCount = catalog.Count(x => string.Equals(x.PluginId, "builtin", StringComparison.OrdinalIgnoreCase)),
                pluginNodeCount = catalog.Count(x => !string.Equals(x.PluginId, "builtin", StringComparison.OrdinalIgnoreCase))
            });
        });
        app.MapPost("/api/frames/resolve", (FrameResolveRequest request) =>
        {
            try
            {
                var transforms = request.Transforms
                    .Select(x => VisionTransform2DMath.Rigid(x.SourceFrame, x.TargetFrame, x.Unit, x.X, x.Y, x.AngleDeg))
                    .ToArray();
                var resolved = new VisionFrameTree2D(transforms).Resolve(request.SourceFrame, request.TargetFrame);
                return Results.Ok(new { resolved.Transform, resolved.Path });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                throw new ApiValidationException(ex.Message, ex);
            }
        }).RequireAuthorization(SecurityPolicies.Engineer);

        return app;
    }
}
