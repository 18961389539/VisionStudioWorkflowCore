using System.Security.Cryptography;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

/// <summary>
/// 模型资产包（.vsasset）：单版本的可移植快照，用于跨项目/跨站点分发。
///
/// 包是**自校验**的：声明 sha256 必须与 base64 解出的字节一致，导入时重新计算，不符直接拒绝——
/// 分发链路里任何一层改动都会被发现。导入永远创建新资产 id，避免覆盖本机已有资产。
/// </summary>
public sealed record ShapeModelAssetPackage(
    string Format,
    string SourceAssetId,
    string Name,
    string? Description,
    int Version,
    string Sha256,
    long SizeBytes,
    string TrainingConfig,
    string? SourceImageSha256,
    string? Note,
    string? CreatedBy,
    string ModelBase64)
{
    public const string CurrentFormat = "visionstudio.model-asset.v1";
}

public sealed class ShapeModelAssetPackageService(ShapeModelAssetStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<(ShapeModelAssetPackage Package, byte[] Bytes)> ExportAsync(string id, int version, CancellationToken ct)
    {
        var info = await store.GetVersionAsync(id, version, ct)
            ?? throw new ApiNotFoundException($"Shape model asset '{id}' v{version} does not exist.");
        var asset = await store.GetAsync(id, ct)
            ?? throw new ApiNotFoundException($"Shape model asset '{id}' does not exist.");
        var bytes = await store.ReadBytesAsync(id, version, ct)
            ?? throw new ApiNotFoundException($"Shape model asset '{id}' v{version} bytes are missing.");
        var package = new ShapeModelAssetPackage(
            ShapeModelAssetPackage.CurrentFormat, asset.Id, asset.Name, asset.Description,
            info.Version, info.Sha256, info.SizeBytes, info.TrainingConfig,
            info.SourceImageSha256, info.Note, info.CreatedBy,
            Convert.ToBase64String(bytes));
        return (package, JsonSerializer.SerializeToUtf8Bytes(package, Json));
    }

    public async Task<ShapeModelAssetDescriptor> ImportAsync(ShapeModelAssetPackage package, CancellationToken ct)
    {
        if (!string.Equals(package.Format, ShapeModelAssetPackage.CurrentFormat, StringComparison.Ordinal))
            throw new ApiValidationException(
                $"Unsupported model asset package format '{package.Format}'; expected '{ShapeModelAssetPackage.CurrentFormat}'.");
        if (string.IsNullOrWhiteSpace(package.ModelBase64))
            throw new ApiValidationException("Package contains no model data.");
        if (string.IsNullOrWhiteSpace(package.Sha256))
            throw new ApiValidationException("Package is missing its sha256 declaration.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(package.ModelBase64); }
        catch (FormatException ex) { throw new ApiValidationException("Package modelBase64 is not valid base64.", ex); }
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException($"Package integrity check failed: declared {package.Sha256}, actual {actual}.");

        // 导入一律新建资产 id：避免覆盖本机同名资产，也避免跨库 id 撞车。
        var nameSuffix = string.IsNullOrWhiteSpace(package.SourceAssetId) ? "imported" : $"from {package.SourceAssetId} v{package.Version}";
        return await store.CreateAsync(new CreateShapeModelAssetRequest(
            Id: null,
            Name: string.IsNullOrWhiteSpace(package.Name) ? "Imported model asset" : $"{package.Name} ({nameSuffix})",
            Description: package.Description,
            ModelBase64: package.ModelBase64,
            TrainingConfig: package.TrainingConfig,
            SourceImageSha256: package.SourceImageSha256,
            Note: string.IsNullOrWhiteSpace(package.Note) ? "Imported from .vsasset" : $"{package.Note} (imported)",
            CreatedBy: package.CreatedBy), ct);
    }
}
