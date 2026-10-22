using VisionStudio.Api.Diagnostics;
using VisionStudio.Engine.Diagnostics;

namespace VisionStudio.Api.Endpoints;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnostics/summary", (DiagnosticsCenterService diagnostics) => Results.Ok(diagnostics.Summary()));
        app.MapGet("/api/diagnostics/assets", (DiagnosticsCenterService diagnostics) => Results.Ok(diagnostics.Assets()));
        app.MapGet("/api/diagnostics/events", (int? take, AssetKind? kind, string? assetId, DiagnosticsCenterService diagnostics) =>
            Results.Ok(diagnostics.Events(take ?? 200, kind, assetId)));
        return app;
    }
}
