namespace VisionStudio.Abstractions;

/// <summary>
/// 第五批（2026-10-14 精密定位与测量）· 轮廓数据契约。
/// 与 <see cref="VisionDetection"/>（检测条目）分工：VisionContour 承载**轮廓本身**——
/// 有序点列 + 层级（孔洞归属）+ 基础几何量；提取/筛选/属性三个节点围绕它组合。
/// </summary>
public sealed record VisionContour(
    int Index,
    IReadOnlyList<VisionPoint2D> Points,
    /// <summary>
    /// -1 表示没有在当前输出列表中保留的父轮廓；否则为当前列表中父轮廓的 Index。
    /// 筛选、数量截断或 holes-only 提取省略父轮廓时可为 -1；IsHole 仍表达其孔洞身份。
    /// </summary>
    int ParentIndex,
    bool IsHole,
    double Area,
    double Perimeter,
    double Circularity,
    VisionRectangle2D BoundingBox,
    VisionCircle MinEnclosingCircle)
{
    /// <summary>Whether the ordered points define a closed boundary. Legacy contour producers default to closed.</summary>
    public bool Closed { get; init; } = true;
    /// <summary>Native per-point numeric attributes, for example line width or edge response.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>>? Attributes { get; init; }
    /// <summary>Managed, serializable native snapshot for lossless per-point attributes. Never contains a native handle.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VisionContourNativeSnapshot? NativeSnapshot { get; init; }
    public string Unit { get; init; } = "px";
    public string? CoordinateFrame { get; init; }
}

public sealed record VisionContourNativeSnapshot(string Format, string Fingerprint, byte[] Data);

/// <summary>
/// 第五批 · 轮廓派生属性（contour.properties 输出）：在 VisionContour 基础量之上
/// 补充质心、最小面积外接旋转矩形、实心度、占比、长短轴比与等效直径。
/// </summary>
public sealed record VisionContourProperties(
    int Index,
    int PointCount,
    double Area,
    double Perimeter,
    double Circularity,
    VisionPoint2D Centroid,
    VisionRectangle2D BoundingBox,
    VisionCircle MinEnclosingCircle,
    VisionRectangle2D MinAreaRect,
    double Solidity,
    double Extent,
    double AspectRatio,
    double EquivalentDiameter);
