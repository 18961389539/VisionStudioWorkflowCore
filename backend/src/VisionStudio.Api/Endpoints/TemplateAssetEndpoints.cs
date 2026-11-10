using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public sealed record TemplateAssetReference(string WorkflowId, string NodeId);

/// <summary>
/// 模板图资产端点（P2）。与模型资产同一套语义：版本不可变、字节内容寻址、删除前扫描引用。
/// 模板图不参与生产发布清单的 pin（它随流程文档走），因此这里没有 export/import。
/// </summary>
public static class TemplateAssetEndpoints
{
    public static IEndpointRouteBuilder MapTemplateAssetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/template-assets", async (TemplateAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        app.MapPost("/api/template-assets", async (CreateTemplateAssetRequest request, TemplateAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateAsync(request, ct)))
            .RequireEngineer("template-asset.create", "template-assets");

        app.MapGet("/api/template-assets/{id}", async (string id, TemplateAssetStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAsync(id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        });

        app.MapPost("/api/template-assets/{id}/versions", async (string id, AppendTemplateAssetVersionRequest request, TemplateAssetStore store, CancellationToken ct) =>
            Results.Ok(await store.AppendVersionAsync(id, request, ct)))
            .RequireEngineer("template-asset.version.create", "template-assets");

        app.MapGet("/api/template-assets/{id}/versions/{version:int}/blob", async (string id, int version, TemplateAssetStore store, CancellationToken ct) =>
        {
            var bytes = await store.ReadImageAsync(id, version, ct);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, "image/png", $"{id}-v{version}.png");
        });

        app.MapDelete("/api/template-assets/{id}", async (string id, TemplateAssetStore store, WorkflowStore workflows, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is null) return Results.NotFound();
            var references = await FindReferencesAsync(id, workflows, ct);
            if (references.Count > 0)
                throw new ApiConflictException(
                    $"Template asset '{id}' is referenced by {references.Count} workflow node(s): " +
                    $"{string.Join(", ", references.Take(8).Select(x => $"{x.WorkflowId}/{x.NodeId}"))}. " +
                    "Repoint or clear those nodes before deleting the asset.");
            return await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireEngineer("template-asset.delete", "template-assets");

        return app;
    }

    internal static async Task<IReadOnlyList<TemplateAssetReference>> FindReferencesAsync(
        string assetId, WorkflowStore workflows, CancellationToken ct)
    {
        var references = new List<TemplateAssetReference>();
        foreach (var summary in await workflows.ListAsync(ct))
        {
            var workflow = await workflows.LoadAsync(summary.Id, ct);
            if (workflow is null) continue;
            foreach (var node in workflow.Nodes)
            {
                if (!node.Type.Equals("match.template", StringComparison.OrdinalIgnoreCase) || node.Parameters is null) continue;
                if (!node.Parameters.TryGetValue("templateAssetId", out var value)) continue;
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                if (string.Equals(text, assetId, StringComparison.OrdinalIgnoreCase))
                    references.Add(new TemplateAssetReference(workflow.Id, node.Id));
            }
        }
        return references;
    }
}
