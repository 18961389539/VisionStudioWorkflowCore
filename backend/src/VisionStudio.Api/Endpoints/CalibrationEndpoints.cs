using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class CalibrationEndpoints
{
    public static IEndpointRouteBuilder MapCalibrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/calibration/providers", (IEnumerable<ICalibrationPointProvider> providers) =>
            Results.Ok(providers.Select(x => x.Descriptor)));

        app.MapPost("/api/calibration/capture-grid", async (
            CalibrationGridCaptureRequest request,
            IEnumerable<ICalibrationPointProvider> providers,
            CancellationToken ct) =>
        {
            var provider = providers.FirstOrDefault(x => string.Equals(x.Id, request.ProviderId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiValidationException($"Calibration provider '{request.ProviderId}' is not registered.");
            try
            {
                return Results.Ok(await provider.CaptureGridAsync(request, ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                throw new ApiValidationException(ex.Message, ex);
            }
        }).RequireEngineer("calibration.capture-grid", "calibration");

        app.MapPost("/api/calibration/solve", (CalibrationWorkspaceRequest request, CalibrationWorkspaceService solver) =>
        {
            try { return Results.Ok(solver.Solve(request)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                throw new ApiValidationException(ex.Message, ex);
            }
        }).RequireEngineer("calibration.solve", "calibration");

        app.MapGet("/api/calibrations", async (CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        app.MapPost("/api/calibrations", async (CreateCalibrationAssetRequest request, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateAsync(request, ct))).RequireEngineer("calibration.create", "calibration");

        app.MapGet("/api/calibrations/{id}", async (string id, CalibrationAssetStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAsync(id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        });

        app.MapPost("/api/calibrations/{id}/versions", async (string id, SaveCalibrationVersionRequest request, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.AddVersionAsync(id, request, ct))).RequireEngineer("calibration.version.create", "calibration");

        app.MapGet("/api/calibrations/{id}/versions/{version:int}", async (string id, int version, CalibrationAssetStore store, CancellationToken ct) =>
        {
            var snapshot = await store.GetVersionAsync(id, version, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        app.MapGet("/api/calibrations/{id}/published", async (string id, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.GetPublishedAsync(id, ct)));

        app.MapPost("/api/calibrations/{id}/versions/{version:int}/publish", async (string id, int version, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Publish", ct))).RequireEngineer("calibration.publish", "calibration");

        app.MapPost("/api/calibrations/{id}/rollback/{version:int}", async (string id, int version, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Rollback", ct))).RequireEngineer("calibration.rollback", "calibration");

        return app;
    }
}
