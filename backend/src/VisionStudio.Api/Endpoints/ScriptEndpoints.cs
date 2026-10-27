using VisionStudio.Api.Security;
using VisionStudio.Scripting;

namespace VisionStudio.Api.Endpoints;

public static class ScriptEndpoints
{
    public static IEndpointRouteBuilder MapScriptEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/scripts/validate", (ScriptValidateRequest request, CancellationToken ct) =>
                Results.Ok(ScriptEditorService.Validate(request.Source, ct)))
            .RequireEngineer("script.validate", "script");

        app.MapPost("/api/scripts/complete", (ScriptCompletionRequest request) =>
                Results.Ok(ScriptEditorService.Complete(request.Source, request.Offset, request.Ports)))
            .RequireEngineer("script.complete", "script");

        app.MapPost("/api/scripts/format", (ScriptFormatRequest request, CancellationToken ct) =>
                Results.Ok(ScriptEditorService.Format(request.Source, ct)))
            .RequireEngineer("script.format", "script");

        return app;
    }
}

public sealed record ScriptValidateRequest(string Source);

public sealed record ScriptCompletionRequest(string Source, int Offset, IReadOnlyList<string>? Ports);

public sealed record ScriptFormatRequest(string Source);
