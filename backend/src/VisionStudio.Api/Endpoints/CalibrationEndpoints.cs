using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class CalibrationEndpoints
{
    public static IEndpointRouteBuilder MapCalibrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/calibration/providers", (IEnumerable<ICalibrationPointProvider> providers) =>
            Results.Ok(providers.Select(x => x.Descriptor))).Produces<IReadOnlyList<CalibrationProviderDescriptor>>();

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
        }).RequireEngineer("calibration.capture-grid", "calibration")
            .Produces<IReadOnlyList<CalibrationWorkspacePoint>>();

        app.MapPost("/api/calibration/solve", (CalibrationWorkspaceRequest request, CalibrationWorkspaceService solver) =>
        {
            try { return Results.Ok(solver.Solve(request)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                throw new ApiValidationException(ex.Message, ex);
            }
        }).RequireEngineer("calibration.solve", "calibration").Produces<CalibrationWorkspaceResult>();

        app.MapGet("/api/calibrations", async (CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct))).Produces<IReadOnlyList<CalibrationAssetDescriptor>>();

        app.MapPost("/api/calibrations", async (CreateCalibrationAssetRequest request, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateAsync(request, ct))).RequireEngineer("calibration.create", "calibration")
            .Produces<CalibrationAssetDescriptor>();

        app.MapGet("/api/calibrations/{id}", async (string id, CalibrationAssetStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAsync(id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        }).Produces<CalibrationAssetDescriptor>();

        app.MapPost("/api/calibrations/{id}/versions", async (string id, SaveCalibrationVersionRequest request, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.AddVersionAsync(id, request, ct))).RequireEngineer("calibration.version.create", "calibration")
            .Produces<CalibrationVersionSnapshot>();

        app.MapGet("/api/calibrations/{id}/versions/{version:int}", async (string id, int version, CalibrationAssetStore store, CancellationToken ct) =>
        {
            var snapshot = await store.GetVersionAsync(id, version, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).Produces<CalibrationVersionSnapshot>();

        app.MapGet("/api/calibrations/{id}/published", async (string id, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.GetPublishedAsync(id, ct))).Produces<CalibrationVersionSnapshot>();

        app.MapPost("/api/calibrations/{id}/versions/{version:int}/publish", async (string id, int version, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Publish", ct))).RequireEngineer("calibration.publish", "calibration")
            .Produces<CalibrationAssetDescriptor>();

        app.MapPost("/api/calibrations/{id}/rollback/{version:int}", async (string id, int version, CalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Rollback", ct))).RequireEngineer("calibration.rollback", "calibration")
            .Produces<CalibrationAssetDescriptor>();

        app.MapGet("/api/camera-intrinsics", async (CameraIntrinsicAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct))).Produces<IReadOnlyList<CameraIntrinsicAssetDescriptor>>();

        app.MapPost("/api/camera-intrinsics", async (CreateCameraIntrinsicAssetFromSessionRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
            Results.Ok(await wizard.CreateIntrinsicAssetAsync(request, ct))).RequireEngineer("calibration.intrinsic-asset.create", "calibration")
            .Produces<CameraIntrinsicAssetDescriptor>();

        app.MapGet("/api/camera-intrinsics/{id}", async (string id, CameraIntrinsicAssetStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAsync(id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        }).Produces<CameraIntrinsicAssetDescriptor>();

        app.MapPost("/api/camera-intrinsics/{id}/versions", async (string id, SaveCameraIntrinsicVersionFromSessionRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
            Results.Ok(await wizard.AddIntrinsicVersionAsync(id, request, ct))).RequireEngineer("calibration.intrinsic-asset.version.create", "calibration")
            .Produces<CameraIntrinsicVersionSnapshot>();

        app.MapGet("/api/camera-intrinsics/{id}/versions/{version:int}", async (string id, int version, CameraIntrinsicAssetStore store, CancellationToken ct) =>
        {
            var snapshot = await store.GetVersionAsync(id, version, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).Produces<CameraIntrinsicVersionSnapshot>();

        app.MapGet("/api/camera-intrinsics/{id}/published", async (string id, CameraIntrinsicAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.GetPublishedAsync(id, ct))).Produces<CameraIntrinsicVersionSnapshot>();

        app.MapPost("/api/camera-intrinsics/{id}/versions/{version:int}/publish", async (string id, int version, CameraIntrinsicAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Publish", ct))).RequireEngineer("calibration.intrinsic-asset.publish", "calibration")
            .Produces<CameraIntrinsicAssetDescriptor>();

        app.MapPost("/api/camera-intrinsics/{id}/rollback/{version:int}", async (string id, int version, CameraIntrinsicAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Rollback", ct))).RequireEngineer("calibration.intrinsic-asset.rollback", "calibration")
            .Produces<CameraIntrinsicAssetDescriptor>();

        // --- Guided camera-intrinsics wizard (HALCON-assistant style slice) ---
        app.MapPost("/api/calibration/wizard/sessions", (CalibrationWizardCreateRequest request, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.Create(request)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.create", "calibration");

        app.MapGet("/api/calibration/wizard/sessions/{sessionId}", (string sessionId, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.Get(sessionId)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
        });

        app.MapDelete("/api/calibration/wizard/sessions/{sessionId}", (string sessionId, CalibrationWizardService wizard) =>
        {
            return wizard.Delete(sessionId) ? Results.Ok(new { deleted = true }) : Results.NotFound();
        }).RequireEngineer("calibration.wizard.delete", "calibration");

        app.MapPost("/api/calibration/wizard/sessions/{sessionId}/views", (string sessionId, CalibrationWizardAddViewRequest request, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.AddViewJson(sessionId, request)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.add-view", "calibration");

        app.MapPost("/api/calibration/wizard/sessions/{sessionId}/views/from-image", async (string sessionId, HttpRequest http, CalibrationWizardService wizard, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) throw new ApiValidationException("from-image requires multipart/form-data with an image file.");
            var form = await http.ReadFormAsync(ct);
            var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault()
                ?? throw new ApiValidationException("Upload an image file (form field 'image').");
            await using var stream = file.OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var label = form["label"].FirstOrDefault();
            bool? enable = bool.TryParse(form["enable"].FirstOrDefault(), out var e) ? e : null;
            try { return Results.Ok(await wizard.AddViewFromImageAsync(sessionId, ms.ToArray(), label, enable, ct)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.add-view-image", "calibration");

        app.MapPost("/api/calibration/wizard/sessions/{sessionId}/views/{index:int}/enabled", (string sessionId, int index, CalibrationWizardSetEnabledRequest request, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.SetEnabled(sessionId, index, request.Enabled)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.set-enabled", "calibration");

        app.MapDelete("/api/calibration/wizard/sessions/{sessionId}/views/{index:int}", (string sessionId, int index, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.RemoveView(sessionId, index)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.remove-view", "calibration");

        app.MapPost("/api/calibration/wizard/sessions/{sessionId}/calibrate", async (string sessionId, CalibrationWizardSolveRequest? request, CalibrationWizardService wizard, CancellationToken ct) =>
        {
            try { return Results.Ok(await wizard.SolveAsync(sessionId, request ?? new CalibrationWizardSolveRequest(), ct)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.calibrate", "calibration");

        app.MapGet("/api/calibration/wizard/sessions/{sessionId}/export", (string sessionId, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.Export(sessionId)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { throw new ApiValidationException(ex.Message, ex); }
        });

        app.MapPost("/api/calibration/wizard/sessions/import", (CalibrationWizardImportRequest request, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.ImportDocument(request)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.import", "calibration");

        app.MapPost("/api/calibration/wizard/sessions/{sessionId}/preview", async (string sessionId, CalibrationWizardPreviewRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
        {
            try { return Results.Ok(await wizard.PreviewFromCameraAsync(sessionId, request, ct)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or TimeoutException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.preview", "calibration");

        app.MapGet("/api/calibration/wizard/sessions/{sessionId}/hints", (string sessionId, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.Hints(sessionId)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
        });

        app.MapPost("/api/calibration/wizard/sessions/{sessionId}/disable-outliers", (string sessionId, CalibrationWizardDisableOutliersRequest? request, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.DisableOutliers(sessionId, request ?? new CalibrationWizardDisableOutliersRequest())); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.disable-outliers", "calibration");

        app.MapPost("/api/calibration/wizard/handeye/solve", async (CalibrationHandEyeSolveRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
        {
            try { return Results.Ok(await wizard.SolveHandEyeAsync(request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.handeye", "calibration");

        app.MapPost("/api/calibration/wizard/handeye/sessions", (HandEyeWizardSaveRequest request, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.SaveHandEyeSession(request)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.handeye.save", "calibration")
            .Produces<HandEyeWizardSessionDocument>();

        app.MapGet("/api/calibration/wizard/handeye/sessions", (CalibrationWizardService wizard) =>
            Results.Ok(wizard.ListHandEyeSessions()));

        app.MapGet("/api/calibration/wizard/handeye/sessions/{sessionId}", (string sessionId, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.GetHandEyeSession(sessionId)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
        }).Produces<HandEyeWizardSessionDocument>();

        app.MapDelete("/api/calibration/wizard/handeye/sessions/{sessionId}", (string sessionId, CalibrationWizardService wizard) =>
            wizard.DeleteHandEyeSession(sessionId) ? Results.Ok(new { deleted = true }) : Results.NotFound())
            .RequireEngineer("calibration.wizard.handeye.delete", "calibration");

        app.MapGet("/api/hand-eye-calibrations", async (HandEyeCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct))).Produces<IReadOnlyList<HandEyeCalibrationAssetDescriptor>>();

        app.MapPost("/api/hand-eye-calibrations", async (CreateHandEyeAssetFromSessionRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
            Results.Ok(await wizard.CreateHandEyeAssetAsync(request, ct))).RequireEngineer("calibration.handeye-asset.create", "calibration")
            .Produces<HandEyeCalibrationAssetDescriptor>();

        app.MapGet("/api/hand-eye-calibrations/{id}", async (string id, HandEyeCalibrationAssetStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAsync(id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        }).Produces<HandEyeCalibrationAssetDescriptor>();

        app.MapPost("/api/hand-eye-calibrations/{id}/versions", async (string id, SaveHandEyeVersionFromSessionRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
            Results.Ok(await wizard.AddHandEyeVersionAsync(id, request, ct))).RequireEngineer("calibration.handeye-asset.version.create", "calibration")
            .Produces<HandEyeCalibrationVersionSnapshot>();

        app.MapGet("/api/hand-eye-calibrations/{id}/versions/{version:int}", async (string id, int version, HandEyeCalibrationAssetStore store, CancellationToken ct) =>
        {
            var snapshot = await store.GetVersionAsync(id, version, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).Produces<HandEyeCalibrationVersionSnapshot>();

        app.MapGet("/api/hand-eye-calibrations/{id}/published", async (string id, HandEyeCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.GetPublishedAsync(id, ct))).Produces<HandEyeCalibrationVersionSnapshot>();

        app.MapPost("/api/hand-eye-calibrations/{id}/versions/{version:int}/publish", async (string id, int version, HandEyeCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Publish", ct))).RequireEngineer("calibration.handeye-asset.publish", "calibration")
            .Produces<HandEyeCalibrationAssetDescriptor>();

        app.MapPost("/api/hand-eye-calibrations/{id}/rollback/{version:int}", async (string id, int version, HandEyeCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Rollback", ct))).RequireEngineer("calibration.handeye-asset.rollback", "calibration")
            .Produces<HandEyeCalibrationAssetDescriptor>();

        app.MapPost("/api/calibration/wizard/handeye3d/solve", async (CalibrationHandEye3DSolveRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
        {
            try { return Results.Ok(await wizard.SolveHandEye3DAsync(request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.handeye3d", "calibration");

        app.MapPost("/api/calibration/wizard/handeye3d/sessions", (HandEye3DWizardSaveRequest request, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.SaveHandEye3DSession(request)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.handeye3d.save", "calibration")
            .Produces<HandEye3DWizardSessionDocument>();

        app.MapGet("/api/calibration/wizard/handeye3d/sessions", (CalibrationWizardService wizard) =>
            Results.Ok(wizard.ListHandEye3DSessions()));

        app.MapGet("/api/calibration/wizard/handeye3d/sessions/{sessionId}", (string sessionId, CalibrationWizardService wizard) =>
        {
            try { return Results.Ok(wizard.GetHandEye3DSession(sessionId)); }
            catch (KeyNotFoundException ex) { throw new ApiNotFoundException(ex.Message); }
        }).Produces<HandEye3DWizardSessionDocument>();

        app.MapDelete("/api/calibration/wizard/handeye3d/sessions/{sessionId}", (string sessionId, CalibrationWizardService wizard) =>
            wizard.DeleteHandEye3DSession(sessionId) ? Results.Ok(new { deleted = true }) : Results.NotFound())
            .RequireEngineer("calibration.wizard.handeye3d.delete", "calibration");

        app.MapGet("/api/hand-eye3d-calibrations", async (HandEye3DCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct))).Produces<IReadOnlyList<HandEye3DCalibrationAssetDescriptor>>();

        app.MapPost("/api/hand-eye3d-calibrations", async (CreateHandEye3DAssetFromSessionRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
            Results.Ok(await wizard.CreateHandEye3DAssetAsync(request, ct))).RequireEngineer("calibration.handeye3d-asset.create", "calibration")
            .Produces<HandEye3DCalibrationAssetDescriptor>();

        app.MapGet("/api/hand-eye3d-calibrations/{id}", async (string id, HandEye3DCalibrationAssetStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAsync(id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        }).Produces<HandEye3DCalibrationAssetDescriptor>();

        app.MapPost("/api/hand-eye3d-calibrations/{id}/versions", async (string id, SaveHandEye3DVersionFromSessionRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
            Results.Ok(await wizard.AddHandEye3DVersionAsync(id, request, ct))).RequireEngineer("calibration.handeye3d-asset.version.create", "calibration")
            .Produces<HandEye3DCalibrationVersionSnapshot>();

        app.MapGet("/api/hand-eye3d-calibrations/{id}/versions/{version:int}", async (string id, int version, HandEye3DCalibrationAssetStore store, CancellationToken ct) =>
        {
            var snapshot = await store.GetVersionAsync(id, version, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).Produces<HandEye3DCalibrationVersionSnapshot>();

        app.MapGet("/api/hand-eye3d-calibrations/{id}/published", async (string id, HandEye3DCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.GetPublishedAsync(id, ct))).Produces<HandEye3DCalibrationVersionSnapshot>();

        app.MapPost("/api/hand-eye3d-calibrations/{id}/versions/{version:int}/publish", async (string id, int version, HandEye3DCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Publish", ct))).RequireEngineer("calibration.handeye3d-asset.publish", "calibration")
            .Produces<HandEye3DCalibrationAssetDescriptor>();

        app.MapPost("/api/hand-eye3d-calibrations/{id}/rollback/{version:int}", async (string id, int version, HandEye3DCalibrationAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.PublishAsync(id, version, "Rollback", ct))).RequireEngineer("calibration.handeye3d-asset.rollback", "calibration")
            .Produces<HandEye3DCalibrationAssetDescriptor>();
        app.MapPost("/api/calibration/wizard/dual-camera/solve", async (CalibrationDualCameraSolveRequest request, CalibrationWizardService wizard, CancellationToken ct) =>
        {
            try { return Results.Ok(await wizard.SolveDualCameraAsync(request, ct)); }
            catch (Exception ex) when (ex is ApiValidationException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OpenCvSharp.OpenCVException)
            { throw new ApiValidationException(ex.Message, ex); }
        }).RequireEngineer("calibration.wizard.dual-camera", "calibration");

        return app;
    }
}
