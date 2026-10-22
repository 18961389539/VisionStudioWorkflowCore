using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

public sealed class JlContourArgs
{
    public required JlXLDCont Contour { get; init; }
    public JlXLDCont? Second { get; init; }
    public JlRegion? Region { get; init; }
    public double Size { get; init; } = 5;
    public int Count { get; init; } = 3;
    public double Row { get; init; }
    public double Column { get; init; }
    public double Phi { get; init; }
    public double Length1 { get; init; } = 10;
    public double Length2 { get; init; } = 10;
    public double Min { get; init; }
    public double Max { get; init; } = 1e9;
    public string Mode { get; init; } = string.Empty;
}

public sealed record JlContourOperator(
    string Key,
    string Label,
    string Note,
    Func<JlContourArgs, JlXLDCont> Run,
    bool NeedsSecond = false,
    bool NeedsRegion = false,
    bool IgnoresInput = false);

/// <summary>
/// JLVision 轮廓算子表（<see cref="JlXLDCont"/> → <see cref="JlXLDCont"/>）：
/// 轮廓生成、平滑、分段、裁剪、并集/差集/交集、按形状筛选与平行轮廓。
/// </summary>
public static class JlContourOperations
{
    private static readonly Dictionary<string, JlContourOperator> Table = new(StringComparer.Ordinal)
    {
        // ── 生成器（不需要输入轮廓）────────────────────────────────
        ["genCircle"] = new("genCircle", "Generate circle contour", "row, column, size=半径, phi 起止角", a =>
        {
            var contour = new JlXLDCont();
            try { contour.GenCircleContourXld(a.Row, a.Column, a.Size, a.Phi, a.Phi + Math.PI * 2, "positive", 1); return contour; }
            catch { contour.Dispose(); throw; }
        }, IgnoresInput: true),
        ["genEllipse"] = new("genEllipse", "Generate ellipse contour", "row, column, phi, size=radius1, length1=radius2", a =>
        {
            var contour = new JlXLDCont();
            try { contour.GenEllipseContourXld(a.Row, a.Column, a.Phi, a.Size, a.Length1, 0, Math.PI * 2, "positive", 1); return contour; }
            catch { contour.Dispose(); throw; }
        }, IgnoresInput: true),
        ["genRectangle2"] = new("genRectangle2", "Generate rectangle2 contour", "row, column, phi, size=length1, length1=length2", a =>
        {
            var contour = new JlXLDCont();
            try { contour.GenRectangle2ContourXld(a.Row, a.Column, a.Phi, a.Size, a.Length1); return contour; }
            catch { contour.Dispose(); throw; }
        }, IgnoresInput: true),
        ["genCross"] = new("genCross", "Generate cross contour", "row, column, size=长度, phi=角度", a =>
        {
            var contour = new JlXLDCont();
            try { contour.GenCrossContourXld(a.Row, a.Column, a.Size, a.Phi); return contour; }
            catch { contour.Dispose(); throw; }
        }, IgnoresInput: true),

        // ── 形态与简化 ─────────────────────────────────────────────
        ["closeContours"] = new("closeContours", "Close contours", "无参数", a => a.Contour.CloseContoursXld()),
        ["smoothContours"] = new("smoothContours", "Smooth contours", "count=回归点数", a => a.Contour.SmoothContoursXld(Math.Max(1, a.Count))),
        ["segmentContours"] = new("segmentContours", "Segment contours", "count=平滑数, size=maxLineDist1, length1=maxLineDist2", a =>
            a.Contour.SegmentContoursXld("lines", Math.Max(1, a.Count), a.Size, Math.Max(a.Size, a.Length1))),
        ["genParallelContour"] = new("genParallelContour", "Parallel contour", "size=距离, mode=regression_normal|contour_normal", a =>
            a.Contour.GenParallelContourXld(a.Mode.StartsWith("contour", StringComparison.Ordinal) ? "contour_normal" : "regression_normal", a.Size)),

        // ── 裁剪 ───────────────────────────────────────────────────
        ["clipContours"] = new("clipContours", "Clip to rectangle", "row, column, size=高, length1=宽", a =>
            a.Contour.ClipContoursXld((int)a.Row, (int)a.Column, (int)(a.Row + a.Size), (int)(a.Column + a.Length1))),
        ["cropContours"] = new("cropContours", "Crop contours", "row, column, size=高, length1=宽, mode=close", a =>
            a.Contour.CropContoursXld(a.Row, a.Column, a.Row + a.Size, a.Column + a.Length1, a.Mode.Length == 0 ? "false" : a.Mode)),

        // ── 选择 ───────────────────────────────────────────────────
        ["selectShapeXld"] = new("selectShapeXld", "Select by shape", "mode=特征名（默认 contlength）, min, max", a =>
            a.Contour.SelectShapeXld(new JlTuple(string.IsNullOrWhiteSpace(a.Mode) ? "contlength" : a.Mode), "and",
                new JlTuple(a.Min), new JlTuple(a.Max))),
        ["selectContoursXld"] = new("selectContoursXld", "Select by contour feature", "mode=特征名（默认 closed）, min, max", a =>
            a.Contour.SelectContoursXld(string.IsNullOrWhiteSpace(a.Mode) ? "closed" : a.Mode, a.Min, a.Max, 0, 0)),
        ["selectXldPoint"] = new("selectXldPoint", "Select contour at point", "row, column", a =>
            a.Contour.SelectXldPoint(a.Row, a.Column)),

        // ── 并集 / 差集 / 交集 ─────────────────────────────────────
        ["unionAdjacentContours"] = new("unionAdjacentContours", "Union adjacent contours", "size=maxDistAbs, length1=maxDistRel, mode=attr_keep", a =>
            a.Contour.UnionAdjacentContoursXld(a.Size, a.Length1, a.Mode.Length == 0 ? "attr_keep" : a.Mode)),
        ["unionCollinearContours"] = new("unionCollinearContours", "Union collinear contours", "size=maxDistAbs, length1=maxDistRel, min=maxShift, max=maxAngle(rad)", a =>
            a.Contour.UnionCollinearContoursXld(a.Size, a.Length1, a.Min,
                Math.Clamp(a.Max > 1.5 ? 0.1 : a.Max, 0.001, Math.PI / 2), a.Mode.Length == 0 ? "attr_keep" : a.Mode)),
        ["unionCocircularContours"] = new("unionCocircularContours", "Union cocircular contours", "size=maxArcAngleDiff, length1=maxArcOverlap", a =>
            a.Contour.UnionCocircularContoursXld(a.Size, a.Length1, a.Max, a.Size, a.Size, a.Size, "true", 1)),
        ["union2ClosedContours"] = new("union2ClosedContours", "Union two closed contours", "第二条轮廓", a =>
            a.Contour.Union2ClosedContoursXld(a.Second!), NeedsSecond: true),
        ["differenceClosedContours"] = new("differenceClosedContours", "Difference of closed contours", "第二条轮廓", a =>
            a.Contour.DifferenceClosedContoursXld(a.Second!), NeedsSecond: true),
        ["intersectionClosedContours"] = new("intersectionClosedContours", "Intersection of closed contours", "第二条轮廓", a =>
            a.Contour.IntersectionClosedContoursXld(a.Second!), NeedsSecond: true),

        // ── 回归 / 排序 / 端点裁剪 / 噪声 / 生成 ─────────────────────
        ["regressContoursXld"] = new("regressContoursXld", "Regress contours", "mode=回归方式（默认 no）, count=迭代", a =>
            a.Contour.RegressContoursXld(a.Mode.Length == 0 ? "no" : a.Mode, Math.Max(1, a.Count))),
        ["sortContoursXld"] = new("sortContoursXld", "Sort contours", "mode=排序模式（默认 lower_left）", a =>
            a.Contour.SortContoursXld(a.Mode.Length == 0 ? "lower_left" : a.Mode, "true", "row")),
        ["clipEndPointsContoursXld"] = new("clipEndPointsContoursXld", "Clip contour end points", "mode=方式（默认 length）, length1=长度", a =>
            a.Contour.ClipEndPointsContoursXld(a.Mode.Length == 0 ? "length" : a.Mode, a.Length1)),
        ["addNoiseWhiteContourXld"] = new("addNoiseWhiteContourXld", "Add white noise to contour", "count=回归点数, size=幅值", a =>
            a.Contour.AddNoiseWhiteContourXld(Math.Max(2, a.Count), a.Size)),
        ["genContourPolygonRoundedXld"] = new("genContourPolygonRoundedXld", "Generate rounded polygon contour", "row/column 起点, size=边长, length1=圆角半径", a =>
        {
            using var rows = new JlTuple(new[] { a.Row, a.Row + a.Size, a.Row });
            using var cols = new JlTuple(new[] { a.Column, a.Column, a.Column + a.Size });
            using var radius = new JlTuple(new[] { 0.1, 0.1, 0.1 });
            a.Contour.GenContourPolygonRoundedXld(rows, cols, radius, 2d);
            return a.Contour;
        }, IgnoresInput: true),

        // ── 并集（扩展 / 同切 / 直线）───────────────────────────────
        ["unionCollinearContoursExtXld"] = new("unionCollinearContoursExtXld", "Union collinear contours (ext)",
            "size=maxDistAbs, length1=maxDistRel, min=maxShift", a =>
            a.Contour.UnionCollinearContoursExtXld(a.Size, a.Length1, a.Min, 0.1, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, "attr_keep")),
        ["unionCotangentialContoursXld"] = new("unionCotangentialContoursXld", "Union cotangential contours",
            "size=maxTangAngle, length1=fitLength, min=maxDist", a =>
            a.Contour.UnionCotangentialContoursXld(a.Length1, a.Length1, 0.1, a.Size, a.Min, a.Min, "attr_keep")),
        ["unionStraightContoursHistoXld"] = new("unionStraightContoursHistoXld", "Union straight contours (histo)",
            "size=maxDist, 参考线由固定端点给出", a =>
        {
            var contour = a.Contour.UnionStraightContoursHistoXld(out var selected, 0, 0, 64, 64,
                (int)a.Size, (int)a.Size, 1, out var histo);
            using (selected) using (histo) return contour;
        }),
        ["symmDifferenceClosedContoursXld"] = new("symmDifferenceClosedContoursXld", "Symmetric difference of closed contours",
            "第二条轮廓", a => a.Contour.SymmDifferenceClosedContoursXld(a.Second!), NeedsSecond: true),

        // ── 对象管理 / 形状变换 ─────────────────────────────────────
        ["insertXldCont"] = new("insertXldCont", "Insert contour object", "第二条轮廓", a => a.Contour.InsertObj(a.Second!, 1), NeedsSecond: true),
        ["removeXldCont"] = new("removeXldCont", "Remove contour object", "count=索引", a => a.Contour.RemoveObj(Math.Max(1, a.Count))),
        ["replaceXldCont"] = new("replaceXldCont", "Replace contour object", "第二条轮廓", a => a.Contour.ReplaceObj(a.Second!, 1), NeedsSecond: true),
        ["shapeTransXld"] = new("shapeTransXld", "Shape transform", "mode=类型（默认 convex）", a => a.Contour.ShapeTransXld(a.Mode.Length == 0 ? "convex" : a.Mode))
    };

    public static IReadOnlyDictionary<string, JlContourOperator> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlContourOperator Resolve(string key)
        => Table.TryGetValue(key, out var op)
            ? op
            : throw new InvalidOperationException(
                $"Unknown JLVision contour operator '{key}'. Known operators: {string.Join(", ", Keys)}.");
}
