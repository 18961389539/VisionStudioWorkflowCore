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
        if (node.Parameters is null || !node.Parameters.TryGetValue(parameterName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"ROI parameter '{parameterName}' must be an object or null.");

        var typeValue = value.EnumerateObject()
            .FirstOrDefault(property => property.Name.Equals(nameof(VisionRoi.Type), StringComparison.OrdinalIgnoreCase)).Value;
        var knownType = typeValue.ValueKind == JsonValueKind.String
            ? Enum.GetNames<VisionRoiType>().FirstOrDefault(name => name.Equals(typeValue.GetString(), StringComparison.OrdinalIgnoreCase))
            : null;
        if (knownType is null)
            throw new InvalidOperationException($"ROI parameter '{parameterName}' must declare a known string type: Rectangle, Circle, Polygon, or Brush.");

        try
        {
            return JsonSerializer.Deserialize<VisionRoi>(value.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            }) ?? throw new InvalidOperationException($"ROI parameter '{parameterName}' could not be read as a ROI object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"ROI parameter '{parameterName}' is malformed: {exception.Message}", exception);
        }
    }
}
