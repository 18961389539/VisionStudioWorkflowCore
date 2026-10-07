using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Abstractions;

public enum VisionRoiType
{
    Rectangle,
    Circle,
    Polygon
}

public sealed record VisionRoiPoint(double X, double Y);

public sealed record VisionRoi(
    VisionRoiType Type,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    double? Radius = null,
    IReadOnlyList<VisionRoiPoint>? Points = null);

public static class VisionRoiParameterExtensions
{
    public static VisionRoi? GetRoi(this NodeDefinition node)
    {
        if (node.Parameters is null || !node.Parameters.TryGetValue("roi", out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return JsonSerializer.Deserialize<VisionRoi>(value.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            });
        }
        catch
        {
            return null;
        }
    }
}
