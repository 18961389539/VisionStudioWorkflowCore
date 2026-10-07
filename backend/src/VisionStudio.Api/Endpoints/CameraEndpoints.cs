using VisionStudio.Api.Contracts;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Api.Media;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Endpoints;

public static class CameraEndpoints
{
    public static IEndpointRouteBuilder MapCameraEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cameras", (CameraManager cameras) => Results.Ok(cameras.List()));

        app.MapGet("/api/cameras/adapters", (IEnumerable<ICameraAdapterProvider> providers) => Results.Ok(
            providers.OrderBy(x => x.Driver, StringComparer.OrdinalIgnoreCase).Select(x => new
            {
                x.Driver,
                x.Vendor,
                x.IsSdkAvailable,
                x.SdkError
            }).ToArray()));

        app.MapGet("/api/cameras/discover", async (string? driver, IEnumerable<ICameraAdapterProvider> providers, CancellationToken ct) =>
        {
            var selected = string.IsNullOrWhiteSpace(driver)
                ? providers.ToArray()
                : providers.Where(x => string.Equals(x.Driver, driver, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selected.Length == 0) throw new ApiNotFoundException($"Camera adapter '{driver}' is not registered.");
            var result = new List<CameraDiscoveredDevice>();
            foreach (var provider in selected)
            {
                if (!provider.IsSdkAvailable)
                {
                    if (!string.IsNullOrWhiteSpace(driver)) throw new ApiUnavailableException(provider.SdkError ?? $"Camera adapter '{provider.Driver}' SDK is unavailable.");
                    continue;
                }
                result.AddRange(await provider.DiscoverAsync(ct));
            }
            return Results.Ok(result.OrderBy(x => x.Driver).ThenBy(x => x.SerialNumber).ToArray());
        }).RequireEngineer("camera.discover", "camera");

        app.MapPost("/api/cameras/vendor", (VendorCameraRegistrationRequest request, IEnumerable<ICameraAdapterProvider> providers, CameraManager cameras) =>
        {
            if (string.IsNullOrWhiteSpace(request.Driver) || string.IsNullOrWhiteSpace(request.Id))
                throw new ApiValidationException("Driver and Id are required.");
            var provider = providers.FirstOrDefault(x => string.Equals(x.Driver, request.Driver, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiNotFoundException($"Camera adapter '{request.Driver}' is not registered.");
            if (!provider.IsSdkAvailable) throw new ApiUnavailableException(provider.SdkError ?? $"Camera adapter '{provider.Driver}' SDK is unavailable.");
            var device = provider.Create(new CameraAdapterRegistration(
                request.Id.Trim(), request.Name, request.SerialNumber, request.UserDefinedName, request.DeviceKey, request.Settings));
            cameras.Register(device, Math.Clamp(request.RingCapacity, 2, 64));
            return Results.Ok(cameras.Get(request.Id.Trim()));
        }).RequireEngineer("camera.register.vendor", "camera");

        app.MapPost("/api/cameras/file", (FileCameraRegistrationRequest request, CameraManager cameras, MediaLibraryService media) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id))
                throw new ApiValidationException("Id is required.");
            var requestedSource = !string.IsNullOrWhiteSpace(request.Source) ? request.Source : request.Path;
            if (string.IsNullOrWhiteSpace(requestedSource))
                throw new ApiValidationException("Source is required. Use a media:// reference from the Media Library.");

            var source = media.ResolveSource(requestedSource);
            cameras.RegisterFile(
                request.Id.Trim(),
                string.IsNullOrWhiteSpace(request.Name) ? request.Id.Trim() : request.Name.Trim(),
                source.Source,
                source.PhysicalPath);
            return Results.Ok(cameras.Get(request.Id.Trim()));
        }).RequireEngineer("camera.register.file", "camera");

        app.MapPost("/api/cameras/{id}/open", async (string id, CameraManager cameras, CancellationToken ct) =>
        {
            await cameras.OpenAsync(id, ct);
            return Results.Ok(cameras.Get(id));
        }).RequireOperator("camera.open", "camera");

        app.MapPost("/api/cameras/{id}/close", async (string id, CameraManager cameras, CancellationToken ct) =>
        {
            await cameras.CloseAsync(id, ct);
            return Results.Ok(cameras.Get(id));
        }).RequireOperator("camera.close", "camera");

        app.MapPost("/api/cameras/{id}/start", async (string id, CameraManager cameras, CancellationToken ct) =>
        {
            await cameras.StartAsync(id, ct);
            return Results.Ok(cameras.Get(id));
        }).RequireOperator("camera.start", "camera");

        app.MapPost("/api/cameras/{id}/stop", async (string id, CameraManager cameras, CancellationToken ct) =>
        {
            await cameras.StopAsync(id, ct);
            return Results.Ok(cameras.Get(id));
        }).RequireOperator("camera.stop", "camera");

        app.MapPut("/api/cameras/{id}/settings", async (string id, CameraSettings request, CameraManager cameras, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureDependencyMutationAllowed("camera", id);
            await cameras.ApplySettingsAsync(id, request, ct);
            return Results.Ok(cameras.Get(id));
        }).RequireEngineer("camera.settings.update", "camera");

        app.MapPost("/api/cameras/{id}/trigger", (string id, CameraManager cameras, DeviceLeaseRegistry leases) =>
        {
            // 请求期硬件租约：触发期间独占该相机（TTL 仅作为释放路径丢失时的兜底）
            using var lease = leases.AcquireManualOrThrow("camera", id, $"Cannot trigger camera '{id}'");
            cameras.Trigger(id);
            return Results.Ok(cameras.Get(id));
        }).RequireOperator("camera.trigger", "camera");

        app.MapGet("/api/cameras/{id}/transport-telemetry", (string id, CameraManager cameras)
            => Results.Ok(cameras.GetTransportTelemetry(id)));

        app.MapGet("/api/cameras/{id}/preview", async (string id, int? maxWidth, int? quality, CameraManager cameras, CancellationToken ct) =>
        {
            var preview = await cameras.CapturePreviewAsync(id, maxWidth ?? 960, quality ?? 80, ct);
            return Results.File(preview.Jpeg, "image/jpeg", enableRangeProcessing: false);
        });

        return app;
    }
}
