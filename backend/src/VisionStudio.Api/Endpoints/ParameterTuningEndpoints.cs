using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class ParameterTuningEndpoints
{
    public static IEndpointRouteBuilder MapParameterTuningEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tuning/preview", async (ParameterTuningPreviewRequest request, ParameterTuningService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(request, ct)))
            .RequireEngineer("tuning.preview", "parameter-tuning");

        return app;
    }
}
