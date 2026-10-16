using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Abstractions;

public enum VisionRoiType
{
    Rectangle,
    Circle,
    Polygon,
    Brush
}

public enum VisionRoiStrokeMode
{
    Add,
    Erase
}

public sealed record VisionRoiPoint(double X, double Y);

public sealed record VisionRoiBrushStroke(
    VisionRoiStrokeMode Mode,
    double Width,
    IReadOnlyList<VisionRoiPoint> Points);

public sealed record VisionRoi(
    VisionRoiType Type,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    double? Radius = null,
    IReadOnlyList<VisionRoiPoint>? Points = null,
    IReadOnlyList<VisionRoiBrushStroke>? Strokes = null);

public static class VisionRoiParameterExtensions
{
    public static VisionRoi? GetRoi(this NodeDefinition node, string parameterName = "roi")
    {
        if (node.Parameters is null || !node.Parameters.TryGetValue(parameterName, out var value) || value.ValueKind != JsonValueKind.Object)
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
