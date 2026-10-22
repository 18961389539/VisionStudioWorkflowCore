using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

/// <summary>几何计算条目的参数包：四条线的端点、两个半径、角度与标量。</summary>
public sealed class JlGeometryArgs
{
    public double Row1 { get; init; }
    public double Column1 { get; init; }
    public double Row2 { get; init; } = 10;
    public double Column2 { get; init; } = 10;
    public double Row3 { get; init; } = 10;
    public double Column3 { get; init; }
    public double Row4 { get; init; }
    public double Column4 { get; init; } = 10;
    public double Radius1 { get; init; } = 10;
    public double Radius2 { get; init; } = 10;
    public double Phi { get; init; }
    public double Angle { get; init; } = Math.PI / 2;
    public double Size { get; init; } = 1;
    public string Mode { get; init; } = string.Empty;
}

public sealed record JlGeometryResult(double Value, IReadOnlyList<(string Name, double Value)> Extra, string? Text = null);

public sealed record JlGeometryOperator(
    string Key,
    string Label,
    string Note,
    Func<JlGeometryArgs, JlGeometryResult> Run);

/// <summary>
/// JLVision 几何与工具算子表（<see cref="JlMisc"/>）：点/线/线段距离、夹角、交点、
/// 直线位姿、椭圆取点、高斯核与滤波信息。这些是机器人引导与标定换算里最常用的数学算子。
/// </summary>
public static class JlGeometryOperations
{
    private static JlGeometryResult R(double value, params (string Name, double Value)[] extra)
        => new(value, extra);

    private static readonly Dictionary<string, JlGeometryOperator> Table = new(StringComparer.Ordinal)
    {
        ["distancePp"] = new("distancePp", "Point to point distance", "row1/column1 → row2/column2", a =>
            R(JlMisc.DistancePp(a.Row1, a.Column1, a.Row2, a.Column2))),
        ["distancePl"] = new("distancePl", "Point to line distance", "row1/column1=点；row2..column3=直线", a =>
            R(JlMisc.DistancePl(a.Row1, a.Column1, a.Row2, a.Column2, a.Row3, a.Column3))),
        ["distancePs"] = new("distancePs", "Point to segment distance", "row1/column1=点；row2..column3=线段", a =>
        {
            JlMisc.DistancePs(a.Row1, a.Column1, a.Row2, a.Column2, a.Row3, a.Column3, out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }),
        ["distanceSl"] = new("distanceSl", "Segment to line distance", "row1..column2=线段A；row3..column4=直线B", a =>
        {
            JlMisc.DistanceSl(a.Row1, a.Column1, a.Row2, a.Column2, a.Row3, a.Column3, a.Row4, a.Column4, out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }),
        ["distanceSs"] = new("distanceSs", "Segment to segment distance", "row1..column2=线段A；row3..column4=线段B", a =>
        {
            JlMisc.DistanceSs(a.Row1, a.Column1, a.Row2, a.Column2, a.Row3, a.Column3, a.Row4, a.Column4, out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }),
        ["angleLx"] = new("angleLx", "Line angle", "row1/column1 → row2/column2（弧度）", a =>
            R(JlMisc.AngleLx(a.Row1, a.Column1, a.Row2, a.Column2))),
        ["angleLl"] = new("angleLl", "Angle between two lines", "row1..column2=线A；row3..column4=线B（弧度）", a =>
            R(JlMisc.AngleLl(a.Row1, a.Column1, a.Row2, a.Column2, a.Row3, a.Column3, a.Row4, a.Column4))),
        ["intersectionLl"] = new("intersectionLl", "Line intersection", "row1..column2=线A；row3..column4=线B", a =>
        {
            JlMisc.IntersectionLl(a.Row1, a.Column1, a.Row2, a.Column2, a.Row3, a.Column3, a.Row4, a.Column4,
                out double row, out double column, out int isParallel);
            return R(isParallel, ("row", row), ("column", column), ("isParallel", isParallel));
        }),
        ["lineOrientation"] = new("lineOrientation", "Line orientation", "row1/column1 → row2/column2（弧度）", a =>
            R(JlMisc.LineOrientation(a.Row1, a.Column1, a.Row2, a.Column2))),
        ["linePosition"] = new("linePosition", "Line position", "row1/column1 → row2/column2", a =>
        {
            JlMisc.LinePosition(a.Row1, a.Column1, a.Row2, a.Column2,
                out double rowCenter, out double columnCenter, out double length, out double phi);
            return R(length, ("rowCenter", rowCenter), ("columnCenter", columnCenter), ("length", length), ("phiDeg", phi * 180d / Math.PI));
        }),
        ["getPointsEllipse"] = new("getPointsEllipse", "Point on ellipse", "angle, row1/column1=中心, phi, radius1, radius2", a =>
        {
            JlMisc.GetPointsEllipse(a.Angle, a.Row1, a.Column1, a.Phi, a.Radius1, a.Radius2, out double row, out double column);
            return R(row, ("row", row), ("column", column));
        }),
        ["gaussDistribution"] = new("gaussDistribution", "Gaussian 1D kernel", "size=sigma", a =>
        {
            using var kernel = JlMisc.GaussDistribution(a.Size <= 0 ? 1 : a.Size);
            var count = kernel.Length;
            var sum = 0d;
            for (var i = 0; i < count; i++) sum += kernel[i].D;
            return R(count, ("count", count), ("sum", sum));
        }),
        ["infoSmooth"] = new("infoSmooth", "Smoothing filter info", "mode=filter, size=alpha", a =>
        {
            var count = JlMisc.InfoSmooth(a.Mode.Length == 0 ? "gauss" : a.Mode, a.Size, out var coeffs);
            using (coeffs) return R(count, ("coeffCount", count));
        }),

        // ── 过滤信息 / 三维点线 / 点投影 / 链近似 ────────────────────
        ["distancePointLine"] = new("distancePointLine", "3D point to line distance", "row1/column1/radius1=点；row2..radius2 与 row3..size=直线两点", a =>
        {
            var distance = JlMisc.DistancePointLine(a.Row1, a.Column1, a.Radius1, a.Row2, a.Column2, a.Radius2,
                a.Row3, a.Column3, a.Size);
            return R(distance, ("distance", distance));
        }),
        ["distancePointPlueckerLine"] = new("distancePointPlueckerLine", "3D point to Pluecker line distance",
            "row1/column1/radius1=点；其余标量构成 Pluecker 直线", a =>
            {
                var distance = JlMisc.DistancePointPlueckerLine(a.Row1, a.Column1, a.Radius1, a.Row2, a.Column2, a.Radius2,
                    a.Row3, a.Column3, a.Size);
                return R(distance, ("distance", distance));
            }),
        ["convertPoint3dCartToSpher"] = new("convertPoint3dCartToSpher", "Cartesian to spherical (3D)",
            "row1/column1/radius1=x,y,z；mode=赤道面法线（x|y|z）", a =>
            {
                var equat = a.Mode.Length == 0 ? "x" : a.Mode;
                var longitude = JlMisc.ConvertPoint3dCartToSpher(a.Row1, a.Column1, a.Radius1, equat, "y",
                    out double latitude, out double radius);
                return R(longitude, ("longitude", longitude), ("latitude", latitude), ("radius", radius));
            }),
        ["convertPoint3dSpherToCart"] = new("convertPoint3dSpherToCart", "Spherical to Cartesian (3D)",
            "row1/column1/radius1=经度,纬度,半径；mode=赤道面法线", a =>
            {
                var equat = a.Mode.Length == 0 ? "x" : a.Mode;
                JlMisc.ConvertPoint3dSpherToCart(a.Row1, a.Column1, a.Radius1, equat, "y",
                    out double x, out double y, out double z);
                return R(x, ("x", x), ("y", y), ("z", z));
            }),
        ["projectionPl"] = new("projectionPl", "Project point onto line", "row1/column1=点；row2..column4=直线", a =>
        {
            JlMisc.ProjectionPl(a.Row1, a.Column1, a.Row2, a.Column2, a.Row3, a.Column3, out double row, out double column);
            return R(column, ("row", row), ("column", column));
        }),
        ["pointsToPlueckerLine"] = new("pointsToPlueckerLine", "Points to Pluecker line",
            "row1/column1/radius1 与 row2/column2/radius2=两点", a =>
            {
                JlMisc.PointsToPlueckerLine(a.Row1, a.Column1, a.Radius1, a.Row2, a.Column2, a.Radius2,
                    out double dx, out double dy, out double dz, out double mx, out double my, out double mz);
                return R(dx, ("dirX", dx), ("dirY", dy), ("dirZ", dz), ("momentX", mx), ("momentY", my), ("momentZ", mz));
            }),
        ["plueckerLineToPoints"] = new("plueckerLineToPoints", "Pluecker line to points",
            "row1..radius2 构成方向/矩", a =>
            {
                JlMisc.PlueckerLineToPoints(a.Row1, a.Column1, a.Radius1, a.Row2, a.Column2, a.Radius2,
                    out double p1x, out double p1y, out double p1z, out double p2x, out double p2y, out double p2z);
                return R(p1x, ("point1X", p1x), ("point1Y", p1y), ("point1Z", p1z), ("point2X", p2x), ("point2Y", p2y), ("point2Z", p2z));
            }),
        ["pointDirectionToPlueckerLine"] = new("pointDirectionToPlueckerLine", "Point + direction to Pluecker line",
            "row1..radius2=点与方向", a =>
            {
                JlMisc.PointDirectionToPlueckerLine(a.Row1, a.Column1, a.Radius1, a.Row2, a.Column2, a.Radius2,
                    out double dx, out double dy, out double dz, out double mx, out double my, out double mz);
                return R(dx, ("dirX", dx), ("dirY", dy), ("dirZ", dz), ("momentX", mx), ("momentY", my), ("momentZ", mz));
            }),
        ["plueckerLineToPointDirection"] = new("plueckerLineToPointDirection", "Pluecker line to point + direction",
            "row1..radius2 构成方向/矩", a =>
            {
                JlMisc.PlueckerLineToPointDirection(a.Row1, a.Column1, a.Radius1, a.Row2, a.Column2, a.Radius2,
                    out double px, out double py, out double pz, out double dx, out double dy, out double dz);
                return R(px, ("pointX", px), ("pointY", py), ("pointZ", pz), ("dirX", dx), ("dirY", dy), ("dirZ", dz));
            }),
        ["approxChain"] = new("approxChain", "Approximate a point chain", "row1..column4=链点, size=起点宽", a =>
        {
            using var rows = new JlTuple(new[] { a.Row1, a.Row2, a.Row3, a.Row4 });
            using var cols = new JlTuple(new[] { a.Column1, a.Column2, a.Column3, a.Column4 });
            JlMisc.ApproxChain(rows, cols, 1, 10, 0.1, 1, 0.1, 1, 10, 1, 10, 1, 1, 1,
                out var acr, out var acc, out var arcAngle, out _, out _, out _, out _, out _, out _, out var order);
            using (acr) using (acc) using (arcAngle) using (order)
                return R(arcAngle.Length > 0 ? arcAngle[0].D : 0d, ("arcCenterRow", acr.Length > 0 ? acr[0].D : 0d));
        }),
        ["approxChainSimple"] = new("approxChainSimple", "Approximate a point chain (simple)", "row1..column4=链点", a =>
        {
            using var rows = new JlTuple(new[] { a.Row1, a.Row2, a.Row3, a.Row4 });
            using var cols = new JlTuple(new[] { a.Column1, a.Column2, a.Column3, a.Column4 });
            JlMisc.ApproxChainSimple(rows, cols, out var acr, out var acc, out var arcAngle,
                out _, out _, out _, out _, out _, out _, out var order);
            using (acr) using (acc) using (arcAngle) using (order)
                return R(order.Length > 0 ? order[0].D : 0d, ("arcAngle", arcAngle.Length > 0 ? arcAngle[0].D : 0d));
        }),
        ["spDistribution"] = new("spDistribution", "Salt & pepper distribution", "size=盐比例(%), phi=椒比例(%)", a =>
        {
            using var histo = JlMisc.SpDistribution(a.Size, a.Phi);
            return R(histo.Length, ("count", histo.Length));
        }),

        // ── 文件 / 线选择 / 卡尔曼 ───────────────────────────────────
        ["getCurrentDir"] = new("getCurrentDir", "Get current directory", "无参数", a =>
        {
            var directory = JlMisc.GetCurrentDir();
            return R(directory.Length, ("length", directory.Length));
        }),
        ["fileExists"] = new("fileExists", "File exists", "mode=文件名（默认 .）", a =>
        {
            var exists = JlMisc.FileExists(a.Mode.Length == 0 ? "." : a.Mode);
            return R(exists, ("exists", exists));
        }),
        ["listFiles"] = new("listFiles", "List files", "mode=目录（默认 .）", a =>
        {
            using var files = JlMisc.ListFiles(a.Mode.Length == 0 ? "." : a.Mode, "files");
            return R(files.Length, ("count", files.Length));
        }),
        ["selectLinesLongest"] = new("selectLinesLongest", "Select longest lines", "row1..column4=线段端点, count=条数", a =>
        {
            using var rb = new JlTuple(new[] { a.Row1 });
            using var cb = new JlTuple(new[] { a.Column1 });
            using var re = new JlTuple(new[] { a.Row2 });
            using var ce = new JlTuple(new[] { a.Column2 });
            JlMisc.SelectLinesLongest(rb, cb, re, ce, 1,
                out var obr, out var obc, out var oer, out var oec);
            using (obr) using (obc) using (oer) using (oec) return R(obr.Length, ("kept", obr.Length));
        })
    };

    public static IReadOnlyDictionary<string, JlGeometryOperator> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlGeometryOperator Resolve(string key)
        => Table.TryGetValue(key, out var op)
            ? op
            : throw new InvalidOperationException(
                $"Unknown JLVision geometry operator '{key}'. Known operators: {string.Join(", ", Keys)}.");
}
