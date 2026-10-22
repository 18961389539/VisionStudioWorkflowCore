using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

public sealed class JlContourFeatureArgs
{
    public required JlXLDCont Contour { get; init; }
    public JlXLDCont? Second { get; init; }
    public double Row { get; init; }
    public double Column { get; init; }
    public double Row2 { get; init; } = 10;
    public double Column2 { get; init; } = 10;
    public string Mode { get; init; } = string.Empty;
}

public sealed record JlContourFeatureResult(double Value, IReadOnlyList<(string Name, double Value)>? Extra = null);

public sealed record JlContourFeature(
    string Key,
    string Label,
    string Note,
    Func<JlContourFeatureArgs, JlContourFeatureResult> Run,
    bool NeedsSecond = false);

/// <summary>
/// JLVision XLD 特征算子表：轮廓的形状特征与轮廓间/轮廓到点线的距离。
/// 与区域特征互补——XLD 特征不依赖区域填充，适合开放轮廓与亚像素边缘。
/// </summary>
public static class JlContourFeatures
{
    private static JlContourFeatureResult R(double value, params (string Name, double Value)[] extra)
        => new(value, extra);

    private static readonly Dictionary<string, JlContourFeature> Table = new(StringComparer.Ordinal)
    {
        ["areaCenter"] = new("areaCenter", "Point count (+centroid)", "无参数（原生返回点数量，不是面积）", a =>
        {
            var points = a.Contour.AreaCenterPointsXld(out double row, out double column);
            return R(points, ("row", row), ("column", column), ("points", points));
        }),
        ["circularity"] = new("circularity", "Circularity", "无参数", a => R(a.Contour.CircularityXld().D)),
        ["compactness"] = new("compactness", "Compactness", "无参数", a => R(a.Contour.CompactnessXld().D)),
        ["convexity"] = new("convexity", "Convexity", "无参数", a => R(a.Contour.ConvexityXld().D)),
        ["rectangularity"] = new("rectangularity", "Rectangularity", "无参数", a => R(a.Contour.RectangularityXld().D)),
        ["orientation"] = new("orientation", "Orientation", "无参数", a =>
        {
            var phi = a.Contour.OrientationXld().D;
            return R(phi, ("phiRad", phi), ("phiDeg", phi * 180d / Math.PI));
        }),
        ["eccentricity"] = new("eccentricity", "Eccentricity (+bulkiness)", "无参数", a =>
        {
            var value = a.Contour.EccentricityXld(out double bulkiness, out double structureFactor);
            return R(value, ("bulkiness", bulkiness), ("structureFactor", structureFactor));
        }),
        ["ellipticAxis"] = new("ellipticAxis", "Elliptic axis (+rb/phi)", "无参数", a =>
        {
            var ra = a.Contour.EllipticAxisXld(out double rb, out double phi);
            return R(ra, ("ra", ra), ("rb", rb), ("phiDeg", phi * 180d / Math.PI));
        }),
        ["diameter"] = new("diameter", "Diameter (max chord)", "无参数", a =>
        {
            a.Contour.DiameterXld(out double row1, out double column1, out double row2, out double column2, out double diameter);
            return R(diameter, ("row1", row1), ("column1", column1), ("row2", row2), ("column2", column2));
        }),
        ["heightWidthRatio"] = new("heightWidthRatio", "Height/width ratio", "无参数", a =>
        {
            var height = a.Contour.HeightWidthRatioXld(out double width, out double ratio);
            return R(height, ("width", width), ("ratio", ratio));
        }),
        ["moments"] = new("moments", "Second-order moments", "无参数", a =>
        {
            var m11 = a.Contour.MomentsXld(out double m20, out double m02);
            return R(m11, ("m11", m11), ("m20", m20), ("m02", m02));
        }),
        ["pointCount"] = new("pointCount", "Contour point count", "无参数", a => R(a.Contour.ContourPointNumXld().D)),
        ["testClosed"] = new("testClosed", "Is closed", "无参数", a => R(a.Contour.TestClosedXld().D)),
        ["distanceCc"] = new("distanceCc", "Contour to contour distance", "第二条轮廓, mode=point_to_point", a =>
        {
            a.Contour.DistanceCc(a.Second!, string.IsNullOrWhiteSpace(a.Mode) ? "point_to_point" : a.Mode,
                out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }, NeedsSecond: true),
        ["distancePc"] = new("distancePc", "Point to contour distance", "row, column", a =>
        {
            a.Contour.DistancePc(a.Row, a.Column, out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }),
        ["distanceLc"] = new("distanceLc", "Line to contour distance", "row/column → row2/column2", a =>
        {
            a.Contour.DistanceLc(a.Row, a.Column, a.Row2, a.Column2, out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }),
        ["distanceSc"] = new("distanceSc", "Segment to contour distance", "row/column → row2/column2", a =>
        {
            a.Contour.DistanceSc(a.Row, a.Column, a.Row2, a.Column2, out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }),

        // ── 点集（points）特征 / 任意阶矩 / 平行轮廓 ─────────────────
        ["eccentricityPoints"] = new("eccentricityPoints", "Eccentricity (points)", "无参数", a =>
            R(a.Contour.EccentricityPointsXld().D)),
        ["ellipticAxisPoints"] = new("ellipticAxisPoints", "Elliptic axis (points)", "无参数", a =>
        {
            var ra = a.Contour.EllipticAxisPointsXld(out double rb, out double phi);
            return R(ra, ("ra", ra), ("rb", rb), ("phiDeg", phi * 180d / Math.PI));
        }),
        ["orientationPoints"] = new("orientationPoints", "Orientation (points)", "无参数", a =>
        {
            var phi = a.Contour.OrientationPointsXld().D;
            return R(phi, ("phiRad", phi), ("phiDeg", phi * 180d / Math.PI));
        }),
        ["momentsPoints"] = new("momentsPoints", "Second-order moments (points)", "无参数", a =>
        {
            a.Contour.MomentsPointsXld(out double m20, out double m02);
            return R(m20, ("m20", m20), ("m02", m02));
        })
    };

    public static IReadOnlyDictionary<string, JlContourFeature> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlContourFeature Resolve(string key)
        => Table.TryGetValue(key, out var feature)
            ? feature
            : throw new InvalidOperationException(
                $"Unknown JLVision contour feature '{key}'. Known features: {string.Join(", ", Keys)}.");
}
