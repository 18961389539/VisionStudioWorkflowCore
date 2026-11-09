using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

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

        app.MapDelete("/api/model-assets/{id}", async (string id, ShapeModelAssetStore store, WorkflowStore workflows, CancellationToken ct) =>
        {
            var references = await FindReferencesAsync(id, workflows, ct);
            if (references.Count > 0)
                throw new ApiConflictException(
                    $"Shape model asset '{id}' is referenced by {references.Count} workflow node(s): {string.Join(", ", references.Take(8))}. " +
                    "Repoint or clear those nodes before deleting the asset.");
            return await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireEngineer("model-asset.delete", "model-assets");

        // 内联 → 资产：把节点参数里的 trainedModelData 存成资产，然后把节点切到 modelAssetId/modelAssetVersion。
        // 只改这两个键，其余参数原样保留；工作流保存带 expectedRevision，避免覆盖别人的并发编辑。
        app.MapPost("/api/model-assets/extract", async (
            ExtractShapeModelAssetRequest request,
            ShapeModelAssetStore store,
            WorkflowStore workflows,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkflowId) || string.IsNullOrWhiteSpace(request.NodeId))
                throw new ApiValidationException("workflowId and nodeId are required.");

            var loaded = await workflows.LoadWithRevisionAsync(request.WorkflowId, ct)
                ?? throw new ApiNotFoundException($"Workflow '{request.WorkflowId}' does not exist.");
            var node = loaded.Workflow.Nodes.FirstOrDefault(x => x.Id.Equals(request.NodeId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiNotFoundException($"Workflow '{request.WorkflowId}' has no node '{request.NodeId}'.");
            if (!node.Type.Equals("match.shape", StringComparison.OrdinalIgnoreCase))
                throw new ApiValidationException($"Node '{node.Id}' is '{node.Type}'; only match.shape nodes carry an inline shape model.");

            var parameters = node.Parameters ?? new Dictionary<string, JsonElement>();
            if (!parameters.TryGetValue("trainedModelData", out var modelElement) || modelElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(modelElement.GetString()))
                throw new ApiValidationException(
                    $"Node '{node.Id}' has no inline trainedModelData to extract. Train the model first, or it already references a model asset.");
            var trainingConfig = parameters.TryGetValue("trainedModelConfig", out var configElement) && configElement.ValueKind == JsonValueKind.String
                ? configElement.GetString() ?? string.Empty
                : string.Empty;

            var created = await store.CreateAsync(new CreateShapeModelAssetRequest(
                Id: null,
                Name: string.IsNullOrWhiteSpace(request.Name) ? $"{loaded.Workflow.Name}/{node.Name ?? node.Id}" : request.Name.Trim(),
                Description: $"Extracted from workflow '{loaded.Workflow.Id}' node '{node.Id}'.",
                ModelBase64: modelElement.GetString()!,
                TrainingConfig: trainingConfig,
                SourceImageSha256: request.SourceImageSha256,
                Note: "Extracted from inline node model",
                CreatedBy: request.CreatedBy), ct);

            var updatedParameters = new Dictionary<string, JsonElement>(parameters, StringComparer.OrdinalIgnoreCase)
            {
                ["modelAssetId"] = JsonSerializer.SerializeToElement(created.Id),
                ["modelAssetVersion"] = JsonSerializer.SerializeToElement(created.LatestVersion)
            };
            updatedParameters.Remove("trainedModelData");
            var updatedNode = node with { Parameters = updatedParameters };
            var updatedWorkflow = loaded.Workflow with
            {
                Nodes = loaded.Workflow.Nodes.Select(x => ReferenceEquals(x, node) || x.Id == node.Id ? updatedNode : x).ToArray()
            };
            var outcome = await workflows.SaveAsync(loaded.Workflow.Id, updatedWorkflow, ct, loaded.Revision);
            var version = created.Versions.Single(x => x.Version == created.LatestVersion);
            return Results.Ok(new ExtractShapeModelAssetResponse(
                created.Id, created.LatestVersion, version.Sha256, version.SizeBytes, outcome.Revision, outcome.RevisionRecordDeferred));
        }).RequireEngineer("model-asset.extract", "model-assets");

        return app;
    }

    private static async Task<IReadOnlyList<string>> FindReferencesAsync(string assetId, WorkflowStore workflows, CancellationToken ct)
    {
        var references = new List<string>();
        foreach (var summary in await workflows.ListAsync(ct))
        {
            var workflow = await workflows.LoadAsync(summary.Id, ct);
            if (workflow is null) continue;
            foreach (var node in workflow.Nodes)
            {
                if (node.Parameters is null || !node.Parameters.TryGetValue("modelAssetId", out var value)) continue;
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                if (string.Equals(text, assetId, StringComparison.OrdinalIgnoreCase))
                    references.Add($"{workflow.Id}/{node.Id}");
            }
        }
        return references;
    }
}
