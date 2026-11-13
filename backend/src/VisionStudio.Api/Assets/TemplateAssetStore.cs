using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record TemplateAssetVersionInfo(
    int Version,
    string Sha256,
    long SizeBytes,
    int Width,
    int Height,
    string ContentType,
    string Note,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record TemplateAssetDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    IReadOnlyList<TemplateAssetVersionInfo> Versions);

public sealed record CreateTemplateAssetRequest(
    string? Id,
    string Name,
    string? Description,
    string ImageBase64,
    int Width,
    int Height,
    string ContentType = "image/png",
    string? Note = null,
    string? CreatedBy = null);

public sealed record AppendTemplateAssetVersionRequest(
    string ImageBase64,
    int Width,
    int Height,
    string ContentType = "image/png",
    string? Note = null,
    string? CreatedBy = null);

/// <summary>
/// 模板图资产适配器（P2，kind="template"）：验证通用资产层是否真的可复用。
///
/// 与形状模型的差别只在 metadata：这里存宽高与 content-type，没有训练指纹。
/// 字节就是原始图片文件（PNG/JPEG），节点侧用 ImDecode 解回灰度图。
/// </summary>
public sealed class TemplateAssetStore(AssetStore assets)
{
    public const int MaxImageBytes = AssetStore.DefaultMaxBytes;
    private const string Kind = "template";
    private static readonly string[] AllowedContentTypes = ["image/png", "image/jpeg", "image/bmp", "image/tiff"];

    public async Task<IReadOnlyList<TemplateAssetDescriptor>> ListAsync(CancellationToken ct)
        => (await assets.ListAsync(Kind, ct)).Select(ToDescriptor).ToArray();

    public async Task<TemplateAssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        var descriptor = await assets.GetAsync(id, ct);
        return descriptor is null || !IsTemplate(descriptor) ? null : ToDescriptor(descriptor);
    }

    public async Task<TemplateAssetDescriptor> CreateAsync(CreateTemplateAssetRequest request, CancellationToken ct)
    {
        var (width, height) = ResolveDimensions(request.ImageBase64, request.Width, request.Height);
        var created = await assets.CreateAsync(new CreateAssetRequest(
            request.Id, Kind, request.Name, request.Description,
            Metadata(width, height, request.ContentType),
            request.ImageBase64, request.Note, request.CreatedBy, MaxImageBytes), ct);
        return ToDescriptor(created);
    }

    public async Task<TemplateAssetVersionInfo> AppendVersionAsync(string id, AppendTemplateAssetVersionRequest request, CancellationToken ct)
    {
        await RequireTemplateAsync(id, ct);
        var (width, height) = ResolveDimensions(request.ImageBase64, request.Width, request.Height);
        var info = await assets.AppendVersionAsync(id, new AppendAssetVersionRequest(
            Metadata(width, height, request.ContentType),
            request.ImageBase64, request.Note, request.CreatedBy, MaxImageBytes), ct);
        return ToVersion(info);
    }

    public async Task<byte[]?> ReadImageAsync(string id, int version, CancellationToken ct)
    {
        var descriptor = await assets.GetAsync(id, ct);
        return descriptor is null || !IsTemplate(descriptor) ? null : await assets.ReadBytesAsync(id, version, ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
        => await GetAsync(id, ct) is not null && await assets.DeleteAsync(id, ct);

    public async Task<TemplateAssetDescriptor?> UpdateAsync(string id, string? name, string? description, CancellationToken ct)
    {
        if (await GetAsync(id, ct) is null) return null;
        var updated = await assets.UpdateAsync(id, name, description, ct);
        return updated is null ? null : ToDescriptor(updated);
    }

    private async Task RequireTemplateAsync(string id, CancellationToken ct)
    {
        var descriptor = await assets.GetAsync(id, ct)
            ?? throw new ApiNotFoundException($"Template asset '{id}' does not exist.");
        if (!IsTemplate(descriptor))
            throw new ApiValidationException($"Asset '{id}' has kind '{descriptor.Kind}', not '{Kind}'.");
    }

    /// <summary>
    /// 尺寸以解码结果为准：既验证"这是一张能解码的图"，也避免前端声明的宽高与实际不符。
    /// 声明值与实际不一致时拒绝，而不是静默改用实际值——静默纠正会掩盖调用方的 bug。
    /// </summary>
    private static (int Width, int Height) ResolveDimensions(string? imageBase64, int declaredWidth, int declaredHeight)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ApiValidationException("Template image payload is required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(imageBase64.Trim()); }
        catch (FormatException ex) { throw new ApiValidationException("Template image payload must be valid base64.", ex); }
        using var image = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
        if (image.Empty())
            throw new ApiValidationException("Template image could not be decoded; expected PNG, JPEG, BMP or TIFF.");
        if (declaredWidth > 0 && declaredWidth != image.Width || declaredHeight > 0 && declaredHeight != image.Height)
            throw new ApiValidationException(
                $"Declared template size {declaredWidth}×{declaredHeight} does not match the decoded image {image.Width}×{image.Height}.");
        return (image.Width, image.Height);
    }

    private static bool IsTemplate(AssetDescriptor descriptor)
        => string.Equals(descriptor.Kind, Kind, StringComparison.OrdinalIgnoreCase);

    private static TemplateAssetDescriptor ToDescriptor(AssetDescriptor descriptor)
        => new(descriptor.Id, descriptor.Name, descriptor.Description, descriptor.CreatedAt, descriptor.UpdatedAt,
            descriptor.LatestVersion, descriptor.Versions.Select(ToVersion).ToArray());

    private static TemplateAssetVersionInfo ToVersion(AssetVersionInfo info)
    {
        var (width, height, contentType) = ParseMetadata(info.MetadataJson);
        return new TemplateAssetVersionInfo(info.Version, info.Sha256, info.SizeBytes, width, height, contentType,
            info.Note, info.CreatedAt, info.CreatedBy);
    }

    private static string Metadata(int width, int height, string? contentType)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384)
            throw new ApiValidationException("Template image width/height must be 1..16384.");
        var type = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Length == 0) type = "image/png";
        if (!AllowedContentTypes.Contains(type, StringComparer.Ordinal))
            throw new ApiValidationException($"Unsupported template content type '{type}'; expected one of {string.Join(", ", AllowedContentTypes)}.");
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["width"] = width,
            ["height"] = height,
            ["contentType"] = type
        });
    }

    private static (int Width, int Height, string ContentType) ParseMetadata(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            var width = root.TryGetProperty("width", out var w) && w.TryGetInt32(out var parsedWidth) ? parsedWidth : 0;
            var height = root.TryGetProperty("height", out var h) && h.TryGetInt32(out var parsedHeight) ? parsedHeight : 0;
            var type = root.TryGetProperty("contentType", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty : string.Empty;
            return (width, height, type);
        }
        catch (JsonException)
        {
            return (0, 0, string.Empty);
        }
    }
}
