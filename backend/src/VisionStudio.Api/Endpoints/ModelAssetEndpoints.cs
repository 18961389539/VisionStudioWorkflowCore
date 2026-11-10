using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public sealed record UpdateShapeModelAssetRequest(string? Name = null, string? Description = null);

public sealed record ShapeModelAssetReference(string WorkflowId, string NodeId);

public sealed record ExtractShapeModelAssetRequest(
    string WorkflowId,
    string NodeId,
    string? Name = null,
    string? SourceImageSha256 = null,
    string? CreatedBy = null);

public sealed record ExtractShapeModelAssetResponse(
    string AssetId,
    int Version,
    string Sha256,
    long SizeBytes,
    string Revision,
    bool RevisionRecordDeferred);

/// <summary>
/// 模型资产端点（P0）。写操作沿用标定资产的 <c>RequireEngineer</c> 审计口径，读操作与
/// <c>/api/camera-intrinsics</c> 一致；删除前扫描工作流引用，仍被引用返回 409 而不是静默软删。
/// </summary>
public static class ModelAssetEndpoints
{
    public static IEndpointRouteBuilder MapModelAssetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/model-assets", async (ShapeModelAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        app.MapPost("/api/model-assets", async (CreateShapeModelAssetRequest request, ShapeModelAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateAsync(request, ct)))
            .RequireEngineer("model-asset.create", "model-assets");

        app.MapGet("/api/model-assets/{id}", async (string id, ShapeModelAssetStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAsync(id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        });

        app.MapPost("/api/model-assets/{id}/versions", async (string id, AppendShapeModelVersionRequest request, ShapeModelAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.AppendVersionAsync(id, request, ct)))
            .RequireEngineer("model-asset.version.create", "model-assets");

        app.MapGet("/api/model-assets/{id}/versions/{version:int}", async (string id, int version, ShapeModelAssetStore store, CancellationToken ct) =>
        {
            var info = await store.GetVersionAsync(id, version, ct);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        app.MapGet("/api/model-assets/{id}/versions/{version:int}/blob", async (string id, int version, ShapeModelAssetStore store, CancellationToken ct) =>
        {
            var bytes = await store.ReadBytesAsync(id, version, ct);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, "application/octet-stream", $"{id}-v{version}.model");
        });

        app.MapPatch("/api/model-assets/{id}", async (string id, UpdateShapeModelAssetRequest request, ShapeModelAssetStore store, CancellationToken ct) =>
        {
            var updated = await store.UpdateAsync(id, request.Name, request.Description, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireEngineer("model-asset.update", "model-assets");

        app.MapGet("/api/model-assets/{id}/references", async (string id, ShapeModelAssetStore store, WorkflowStore workflows, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is null) return Results.NotFound();
            return Results.Ok(await FindReferencesAsync(id, workflows, ct));
        });

        app.MapDelete("/api/model-assets/{id}", async (string id, ShapeModelAssetStore store, WorkflowStore workflows, CancellationToken ct) =>
        {
            var references = await FindReferencesAsync(id, workflows, ct);
            if (references.Count > 0)
                throw new ApiConflictException(
                    $"Shape model asset '{id}' is referenced by {references.Count} workflow node(s): " +
                    $"{string.Join(", ", references.Take(8).Select(x => $"{x.WorkflowId}/{x.NodeId}"))}. " +
                    "Repoint or clear those nodes before deleting the asset.");
            return await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireEngineer("model-asset.delete", "model-assets");

        // 内联 → 资产：把节点参数里的 trainedModelData 存成资产，然后把节点切到 modelAssetId/modelAssetVersion。
        app.MapPost("/api/model-assets/extract", async (
            ExtractShapeModelAssetRequest request,
            ShapeModelAssetExtraction extraction,
            CancellationToken ct) =>
        {
            var result = await extraction.ExtractAsync(
                request.WorkflowId, request.NodeId, request.Name, request.SourceImageSha256, request.CreatedBy, ct);
            return Results.Ok(new ExtractShapeModelAssetResponse(
                result.AssetId, result.Version, result.Sha256, result.SizeBytes, result.Revision, result.RevisionRecordDeferred));
        }).RequireEngineer("model-asset.extract", "model-assets");

        return app;
    }

    internal static async Task<IReadOnlyList<ShapeModelAssetReference>> FindReferencesAsync(
        string assetId, WorkflowStore workflows, CancellationToken ct)
    {
        var references = new List<ShapeModelAssetReference>();
        foreach (var summary in await workflows.ListAsync(ct))
        {
            var workflow = await workflows.LoadAsync(summary.Id, ct);
            if (workflow is null) continue;
            foreach (var node in workflow.Nodes)
            {
                if (node.Parameters is null || !node.Parameters.TryGetValue("modelAssetId", out var value)) continue;
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                if (string.Equals(text, assetId, StringComparison.OrdinalIgnoreCase))
                    references.Add(new ShapeModelAssetReference(workflow.Id, node.Id));
            }
        }
        return references;
    }
}
