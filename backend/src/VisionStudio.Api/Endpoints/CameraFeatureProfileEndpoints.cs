using VisionStudio.Api.Contracts;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Endpoints;

public static class CameraFeatureProfileEndpoints
{
    public static IEndpointRouteBuilder MapCameraFeatureProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cameras/{id}/commissioning", async (string id, CameraManager cameras, CancellationToken ct) =>
        {
            var profile = await cameras.ReadCommissioningProfileAsync(id, ct);
            return Results.Ok(new
            {
                cameraId = id,
                driver = cameras.Get(id).Driver,
                capabilities = cameras.GetCommissioningCapabilities(id),
                profileHash = cameras.GetCommissioningProfileHash(id),
                profile
            });
        }).RequireEngineer("camera.commissioning.read", "camera");

        app.MapGet("/api/cameras/{id}/features", async (string id, CameraManager cameras, CancellationToken ct) =>
            Results.Ok(await cameras.ListFeaturesAsync(id, ct)))
            .RequireEngineer("camera.features.list", "camera");

        app.MapPut("/api/cameras/{id}/commissioning", async (
            string id, CameraCommissioningProfile request, CameraManager cameras, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureDependencyMutationAllowed("camera", id);
            return Results.Ok(await cameras.ApplyCommissioningProfileAsync(id, request, ct));
        }).RequireEngineer("camera.commissioning.apply", "camera");

        app.MapPut("/api/cameras/{id}/features/{key}", async (
            string id, string key, CameraFeatureValueRequest request, CameraManager cameras, ProductionRuntimeService production, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ApiValidationException("Feature key is required.");
            production.EnsureDependencyMutationAllowed("camera", id);
            await cameras.SetFeatureAsync(id, key, request.Value, ct);
            return Results.Ok(new { cameraId = id, key, request.Value, profileHash = cameras.GetCommissioningProfileHash(id) });
        }).RequireEngineer("camera.feature.update", "camera");

        app.MapGet("/api/camera-feature-profiles", async (string? driver, CameraFeatureProfileStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(driver, ct)))
            .RequireEngineer("camera.profile.list", "camera-profile");

        app.MapGet("/api/camera-feature-profiles/{id}", async (string id, CameraFeatureProfileStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAsync(id, ct)))
            .RequireEngineer("camera.profile.get", "camera-profile");

        app.MapPost("/api/camera-feature-profiles/capture", async (
            CameraFeatureProfileCaptureRequest request, CameraManager cameras, CameraFeatureProfileStore store, CancellationToken ct) =>
        {
            var camera = cameras.Get(request.CameraId);
            var profile = await cameras.ReadCommissioningProfileAsync(request.CameraId, ct);
            return Results.Ok(await store.UpsertAsync(request.Id, request.Name, camera.Driver, profile, request.CameraId, ct));
        }).RequireEngineer("camera.profile.capture", "camera-profile");

        app.MapPost("/api/camera-feature-profiles/import", async (
            CameraFeatureProfileImportRequest request, CameraFeatureProfileStore store, CancellationToken ct) =>
            Results.Ok(await store.UpsertAsync(request.Id, request.Name, request.Driver, request.Profile, request.SourceCameraId, ct)))
            .RequireEngineer("camera.profile.import", "camera-profile");

        app.MapPost("/api/camera-feature-profiles/{profileId}/apply/{cameraId}", async (
            string profileId, string cameraId, CameraFeatureProfileStore store, CameraManager cameras, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureDependencyMutationAllowed("camera", cameraId);
            var saved = await store.GetAsync(profileId, ct);
            var camera = cameras.Get(cameraId);
            if (!string.Equals(saved.Driver, camera.Driver, StringComparison.OrdinalIgnoreCase))
                throw new ApiConflictException($"Profile '{profileId}' targets driver '{saved.Driver}' but camera '{cameraId}' uses '{camera.Driver}'.");
            var result = await cameras.ApplyCommissioningProfileAsync(cameraId, saved.Profile, ct);
            return Results.Ok(new { profile = saved, result });
        }).RequireEngineer("camera.profile.apply", "camera-profile");

        app.MapDelete("/api/camera-feature-profiles/{id}", async (string id, CameraFeatureProfileStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequireEngineer("camera.profile.delete", "camera-profile");

        return app;
    }
}
