using System.Text.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record ShapeModelVersionInfo(
    int Version,
    string Sha256,
    long SizeBytes,
    string TrainingConfig,
    string SourceImageSha256,
    string Note,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record ShapeModelAssetDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    IReadOnlyList<ShapeModelVersionInfo> Versions);

public sealed record ShapeModelAssetVersion(
    string AssetId,
    int Version,
    string Sha256,
    long SizeBytes,
    string TrainingConfig,
    string SourceImageSha256,
    string Note,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    byte[] Bytes);

public sealed record CreateShapeModelAssetRequest(
    string? Id,
    string Name,
    string? Description,
    string ModelBase64,
    string TrainingConfig,
    string? SourceImageSha256 = null,
    string? Note = null,
    string? CreatedBy = null);

public sealed record AppendShapeModelVersionRequest(
    string ModelBase64,
    string TrainingConfig,
    string? SourceImageSha256 = null,
    string? Note = null,
    string? CreatedBy = null);

/// <summary>
/// 形状模型资产适配器（P2）：存储层已抽成通用 <see cref="AssetStore"/>，
/// 这里只负责 kind="shape" 与类型字段（trainingConfig / sourceImageSha256）的映射。
///
/// 对外 API 与改造前完全一致，端点、GC、发布清单、引擎读取与测试都不用改——
/// 这也是这次重构的验收标准：shape 的 9 项存储测试与 5 项回收测试必须原样通过。
/// </summary>
public sealed class ShapeModelAssetStore(AssetStore assets)
{
    /// <summary>与引擎侧 match.shape 的 16 MB 模型上限保持一致；超限明确拒绝，不截断。</summary>
    public const int MaxModelBytes = AssetStore.DefaultMaxBytes;
    private const string Kind = "shape";

    public async Task<IReadOnlyList<ShapeModelAssetDescriptor>> ListAsync(CancellationToken ct)
        => (await assets.ListAsync(Kind, ct)).Select(ToShapeDescriptor).ToArray();

    public async Task<ShapeModelAssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        var descriptor = await assets.GetAsync(id, ct);
        return descriptor is null || !IsShape(descriptor) ? null : ToShapeDescriptor(descriptor);
    }

    public async Task<ShapeModelAssetDescriptor> CreateAsync(CreateShapeModelAssetRequest request, CancellationToken ct)
    {
        var created = await assets.CreateAsync(new CreateAssetRequest(
            request.Id, Kind, request.Name, request.Description,
            Metadata(request.TrainingConfig, request.SourceImageSha256),
            request.ModelBase64, request.Note, request.CreatedBy), ct);
        return ToShapeDescriptor(created);
    }

    public async Task<ShapeModelVersionInfo> AppendVersionAsync(string id, AppendShapeModelVersionRequest request, CancellationToken ct)
    {
        await RequireShapeAsync(id, ct);
        var info = await assets.AppendVersionAsync(id, new AppendAssetVersionRequest(
            Metadata(request.TrainingConfig, request.SourceImageSha256),
            request.ModelBase64, request.Note, request.CreatedBy), ct);
        return ToShapeVersion(info);
    }

    public async Task<ShapeModelVersionInfo?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        var descriptor = await assets.GetAsync(id, ct);
        if (descriptor is null || !IsShape(descriptor)) return null;
        var info = await assets.GetVersionAsync(id, version, ct);
        return info is null ? null : ToShapeVersion(info);
    }

    public async Task<byte[]?> ReadBytesAsync(string id, int version, CancellationToken ct)
    {
        var descriptor = await assets.GetAsync(id, ct);
        return descriptor is null || !IsShape(descriptor) ? null : await assets.ReadBytesAsync(id, version, ct);
    }

    /// <summary>同步读取（引擎侧 <c>IModelAssetSource</c>）；version&lt;=0 表示 latest。</summary>
    public byte[]? ReadBytesSync(string id, int version) => assets.ReadBytesSync(id, version);

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
        => await GetAsync(id, ct) is not null && await assets.DeleteAsync(id, ct);

    public async Task<ShapeModelAssetDescriptor?> UpdateAsync(string id, string? name, string? description, CancellationToken ct)
    {
        if (await GetAsync(id, ct) is null) return null;
        var updated = await assets.UpdateAsync(id, name, description, ct);
        return updated is null ? null : ToShapeDescriptor(updated);
    }

    internal static string ComputeSha256(byte[] bytes) => AssetStore.ComputeSha256(bytes);

    private async Task RequireShapeAsync(string id, CancellationToken ct)
    {
        var descriptor = await assets.GetAsync(id, ct)
            ?? throw new ApiNotFoundException($"Shape model asset '{id}' does not exist.");
        if (!IsShape(descriptor))
            throw new ApiValidationException($"Asset '{id}' has kind '{descriptor.Kind}', not '{Kind}'.");
    }

    private static bool IsShape(AssetDescriptor descriptor)
        => string.Equals(descriptor.Kind, Kind, StringComparison.OrdinalIgnoreCase);

    private static ShapeModelAssetDescriptor ToShapeDescriptor(AssetDescriptor descriptor)
        => new(descriptor.Id, descriptor.Name, descriptor.Description, descriptor.CreatedAt, descriptor.UpdatedAt,
            descriptor.LatestVersion, descriptor.Versions.Select(ToShapeVersion).ToArray());

    private static ShapeModelVersionInfo ToShapeVersion(AssetVersionInfo info)
    {
        var (trainingConfig, sourceImageSha256) = ParseMetadata(info.MetadataJson);
        return new ShapeModelVersionInfo(info.Version, info.Sha256, info.SizeBytes, trainingConfig, sourceImageSha256,
            info.Note, info.CreatedAt, info.CreatedBy);
    }

    private static string Metadata(string trainingConfig, string? sourceImageSha256)
    {
        if (string.IsNullOrWhiteSpace(trainingConfig))
            throw new ApiValidationException("trainingConfig is required: it pins which training parameters produced this model.");
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["trainingConfig"] = trainingConfig.Trim(),
            ["sourceImageSha256"] = (sourceImageSha256 ?? string.Empty).Trim()
        });
    }

    private static (string TrainingConfig, string SourceImageSha256) ParseMetadata(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            var trainingConfig = root.TryGetProperty("trainingConfig", out var config) && config.ValueKind == JsonValueKind.String
                ? config.GetString() ?? string.Empty : string.Empty;
            var source = root.TryGetProperty("sourceImageSha256", out var sha) && sha.ValueKind == JsonValueKind.String
                ? sha.GetString() ?? string.Empty : string.Empty;
            return (trainingConfig, source);
        }
        catch (JsonException)
        {
            // 历史/外部写入的 metadata 损坏时不阻断读取：类型字段按缺失处理，字节仍可取。
            return (string.Empty, string.Empty);
        }
    }
}
