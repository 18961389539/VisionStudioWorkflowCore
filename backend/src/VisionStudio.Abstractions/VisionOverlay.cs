namespace VisionStudio.Abstractions;

public enum VisionOverlayType
{
    Point,
    Line,
    Circle,
    Rectangle,
    Polygon,
    Contour,
    Text
}

public sealed record VisionOverlayPoint(double X, double Y);

public sealed record VisionOverlay(
    string Id,
    VisionOverlayType Type,
    string? NodeId = null,
    string? Label = null,
    double? X = null,
    double? Y = null,
    double? X2 = null,
    double? Y2 = null,
    double? Radius = null,
    double? Width = null,
    double? Height = null,
    double? AngleDeg = null,
    IReadOnlyList<VisionOverlayPoint>? Points = null,
    string Stroke = "#52c41a",
    double StrokeWidth = 2,
    string? Fill = null)
{
    public VisionOverlay ForNode(string nodeId) => this with { NodeId = nodeId };
}

public sealed record VisionPreview(byte[] Jpeg, int Width, int Height);
