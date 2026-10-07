using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class PluginEndpoints
{
    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plugins", (PluginManager plugins) => Results.Ok(plugins.Plugins));

        app.MapGet("/api/plugins/sdk", (PluginManager plugins) => Results.Ok(plugins.SdkInfo));

        app.MapPost("/api/plugins/rescan", (PluginManager plugins, ProductionRuntimeService production) =>
        {
            if (production.Status.ProductionLocked)
                throw new ApiConflictException("Stop Production Runtime before rescanning plugin packages. V0.59 can discover new packages at runtime but never mutates the active plugin registry while Production is locked.");
            return Results.Ok(plugins.Rescan());
        }).RequireEngineer("plugin.rescan", "plugin-registry");

        return app;
    }
}
