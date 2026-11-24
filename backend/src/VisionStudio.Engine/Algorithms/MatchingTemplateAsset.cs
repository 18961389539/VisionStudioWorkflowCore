using OpenCvSharp;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Algorithms;

public static class MatchingTemplateAsset
{
    public static bool SupportsNode(string? type)
        => string.Equals(type, "match.template", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "match.feature", StringComparison.OrdinalIgnoreCase);

    internal static Mat Load(NodeExecutionContext context, NodeDefinition node, string assetId, int version, ImreadModes decodeMode)
    {
        if (version < 0)
            throw new InvalidOperationException($"Match node '{node.Id}' templateAssetVersion must be >= 0 (0 means latest).");
        var source = context.ModelAssets
            ?? throw new InvalidOperationException($"Match node '{node.Id}' references template asset '{assetId}', but the host did not provide an asset source.");
        var bytes = source.TryReadTemplate(assetId, version)
            ?? throw new InvalidOperationException($"Match node '{node.Id}' references template asset '{assetId}' v{(version == 0 ? "latest" : version.ToString())}, which does not exist or is not a template image.");
        var image = Cv2.ImDecode(bytes, decodeMode);
        if (!image.Empty()) return image;
        image.Dispose();
        throw new InvalidOperationException($"Match node '{node.Id}' template asset '{assetId}' ({bytes.Length} bytes) could not be decoded as an image.");
    }
}
