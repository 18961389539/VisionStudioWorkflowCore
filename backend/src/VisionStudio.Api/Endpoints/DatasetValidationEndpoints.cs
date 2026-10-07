using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class DatasetValidationEndpoints
{
    public static IEndpointRouteBuilder MapDatasetValidationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/validation/datasets", async (DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.ListDatasetsAsync(ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/validation/datasets", async (CreateValidationDatasetRequest request, DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateDatasetAsync(request, ct))).RequireEngineer("validation.dataset.create", "validation-dataset");

        app.MapGet("/api/validation/datasets/{datasetId}", async (string datasetId, DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDatasetAsync(datasetId, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapDelete("/api/validation/datasets/{datasetId}", async (string datasetId, DatasetValidationStore store, CancellationToken ct) =>
        {
            await store.DeleteDatasetAsync(datasetId, ct);
            return Results.NoContent();
        }).RequireEngineer("validation.dataset.delete", "validation-dataset");

        app.MapPost("/api/validation/datasets/{datasetId}/items", async (string datasetId, AddValidationDatasetItemsRequest request, DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.AddItemsAsync(datasetId, request, ct))).RequireEngineer("validation.dataset.items.add", "validation-dataset");

        app.MapPut("/api/validation/datasets/{datasetId}/items/{itemId}", async (string datasetId, string itemId, UpdateValidationDatasetItemRequest request, DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.UpdateItemAsync(datasetId, itemId, request, ct))).RequireEngineer("validation.dataset.item.update", "validation-dataset");

        app.MapDelete("/api/validation/datasets/{datasetId}/items/{itemId}", async (string datasetId, string itemId, DatasetValidationStore store, CancellationToken ct) =>
        {
            await store.DeleteItemAsync(datasetId, itemId, ct);
            return Results.NoContent();
        }).RequireEngineer("validation.dataset.item.delete", "validation-dataset");

        app.MapPost("/api/validation/datasets/{datasetId}/runs", async (string datasetId, ValidationRunRequest request, DatasetValidationService service, CancellationToken ct) =>
            Results.Ok(await service.StartAsync(datasetId, request, ct))).RequireEngineer("validation.run.start", "validation-run");

        app.MapGet("/api/validation/datasets/{datasetId}/runs", async (string datasetId, DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.ListRunsAsync(datasetId, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/validation/runs/{runId}", async (string runId, bool? includeResults, DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.GetRunAsync(runId, includeResults ?? true, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/validation/runs/{runId}/workflow", async (string runId, DatasetValidationStore store, CancellationToken ct) =>
            Results.Ok(await store.GetRunWorkflowAsync(runId, ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/validation/runs/{runId}/cancel", async (string runId, DatasetValidationService service, CancellationToken ct) =>
        {
            await service.CancelAsync(runId, ct);
            return Results.Accepted();
        }).RequireEngineer("validation.run.cancel", "validation-run");

        return app;
    }
}
