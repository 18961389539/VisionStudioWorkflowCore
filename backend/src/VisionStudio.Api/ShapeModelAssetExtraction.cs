using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record ShapeModelAssetExtractionResult(
    string AssetId,
    int Version,
    string Sha256,
    long SizeBytes,
    string Revision,
    bool RevisionRecordDeferred);

/// <summary>
/// 内联模型 → 资产（P0 M4 从端点抽出的可测服务）。
///
/// 只改两个参数：写入 modelAssetId/modelAssetVersion，删除 trainedModelData；
/// 其余参数与模型字节原样保留，因此迁移是可逆的（把字节重新内联即可回退）。
/// 走 WorkflowStore 的 expectedRevision，避免覆盖别人的并发编辑。
/// </summary>
public sealed class ShapeModelAssetExtraction(ShapeModelAssetStore store, WorkflowStore workflows)
{
    public async Task<ShapeModelAssetExtractionResult> ExtractAsync(
        string workflowId,
        string nodeId,
        string? name,
        string? sourceImageSha256,
        string? createdBy,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workflowId) || string.IsNullOrWhiteSpace(nodeId))
            throw new ApiValidationException("workflowId and nodeId are required.");

        var loaded = await workflows.LoadWithRevisionAsync(workflowId, ct)
            ?? throw new ApiNotFoundException($"Workflow '{workflowId}' does not exist.");
        var node = loaded.Workflow.Nodes.FirstOrDefault(x => x.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ApiNotFoundException($"Workflow '{workflowId}' has no node '{nodeId}'.");
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
            Name: string.IsNullOrWhiteSpace(name) ? $"{loaded.Workflow.Name}/{node.Name ?? node.Id}" : name.Trim(),
            Description: $"Extracted from workflow '{loaded.Workflow.Id}' node '{node.Id}'.",
            ModelBase64: modelElement.GetString()!,
            TrainingConfig: trainingConfig,
            SourceImageSha256: sourceImageSha256,
            Note: "Extracted from inline node model",
            CreatedBy: createdBy), ct);

        var updatedParameters = new Dictionary<string, JsonElement>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["modelAssetId"] = JsonSerializer.SerializeToElement(created.Id),
            ["modelAssetVersion"] = JsonSerializer.SerializeToElement(created.LatestVersion)
        };
        updatedParameters.Remove("trainedModelData");
        var updatedNode = node with { Parameters = updatedParameters };
        var updatedWorkflow = loaded.Workflow with
        {
            Nodes = loaded.Workflow.Nodes.Select(x => x.Id == node.Id ? updatedNode : x).ToArray()
        };
        var outcome = await workflows.SaveAsync(loaded.Workflow.Id, updatedWorkflow, ct, loaded.Revision);
        var version = created.Versions.Single(x => x.Version == created.LatestVersion);
        return new ShapeModelAssetExtractionResult(
            created.Id, created.LatestVersion, version.Sha256, version.SizeBytes, outcome.Revision, outcome.RevisionRecordDeferred);
    }
}
