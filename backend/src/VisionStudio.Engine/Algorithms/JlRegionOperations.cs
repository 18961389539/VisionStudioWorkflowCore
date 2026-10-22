using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

/// <summary>区域算子条目：输入一个区域（可选第二区域/图像），输出一个新区域。</summary>
public sealed record JlRegionOperator(
    string Key,
    string Label,
    string Note,
    Func<JlRegionOperatorArgs, JlRegion> Run,
    bool NeedsStructuringElement = false,
    bool NeedsSecondRegion = false,
    bool NeedsImage = false);

/// <summary>区域算子统一入参。结构元由节点按 <see cref="Size"/> 自动生成，不需要用户手画。</summary>
public sealed class JlRegionOperatorArgs
{
    public required JlRegion Region { get; init; }
    public required JlRegion Second { get; init; }
    public JlRegion? StructuringElement { get; init; }
    public JlImage? Image { get; init; }

    public double Size { get; init; } = 5;
    public int Count { get; init; } = 1;
    public double Row { get; init; } = 10;
    public double Column { get; init; } = 10;
    public double Phi { get; init; }
    public double Length1 { get; init; } = 10;
    public double Length2 { get; init; } = 10;
    public double Min { get; init; }
    public double Max { get; init; } = 1e9;
    public double Percent { get; init; } = 50;
    public string Mode { get; init; } = string.Empty;
}

/// <summary>
/// JLVision 区域算子表（<see cref="JlRegion"/> → <see cref="JlRegion"/>）。
/// 结构形态学统一用节点按 Size 生成的居中矩形结构元，避免每条流程都要先画一个结构元区域。
/// </summary>
public static class JlRegionOperations
{
    private static readonly Dictionary<string, JlRegionOperator> Table = new(StringComparer.Ordinal)
    {
        // ── 集合运算 ─────────────────────────────────────────────────
        ["complement"] = new("complement", "Complement", "无参数", a => a.Region.Complement()),
        ["difference"] = new("difference", "Difference", "第二区域", a => a.Region.ObjDiff(a.Second), NeedsSecondRegion: true),
        ["backgroundSeg"] = new("backgroundSeg", "Background segmentation", "无参数", a => a.Region.BackgroundSeg()),

        // ── 结构形态学（自动结构元）───────────────────────────────────
        ["erosion1"] = new("erosion1", "Erosion (struct element)", "count=迭代次数", a => a.Region.Erosion1(a.StructuringElement!, a.Count), NeedsStructuringElement: true),
        ["erosion2"] = new("erosion2", "Erosion (reference point)", "count=迭代次数, row, column", a => a.Region.Erosion2(a.StructuringElement!, (int)a.Row, (int)a.Column, a.Count), NeedsStructuringElement: true),
        ["erosionCircle"] = new("erosionCircle", "Erosion (circle)", "size=直径", a => a.Region.ErosionCircle(Radius(a.Size))),
        ["dilation1"] = new("dilation1", "Dilation (struct element)", "count=迭代次数", a => a.Region.Dilation1(a.StructuringElement!, a.Count), NeedsStructuringElement: true),
        ["dilation2"] = new("dilation2", "Dilation (reference point)", "count=迭代次数, row, column", a => a.Region.Dilation2(a.StructuringElement!, (int)a.Row, (int)a.Column, a.Count), NeedsStructuringElement: true),
        ["opening"] = new("opening", "Opening", "结构元", a => a.Region.Opening(a.StructuringElement!), NeedsStructuringElement: true),
        ["openingCircle"] = new("openingCircle", "Opening (circle)", "size=直径", a => a.Region.OpeningCircle(Radius(a.Size))),
        ["closing"] = new("closing", "Closing", "结构元", a => a.Region.Closing(a.StructuringElement!), NeedsStructuringElement: true),
        ["closingCircle"] = new("closingCircle", "Closing (circle)", "size=直径", a => a.Region.ClosingCircle(Radius(a.Size))),
        ["topHat"] = new("topHat", "Top-hat", "结构元", a => a.Region.TopHat(a.StructuringElement!), NeedsStructuringElement: true),
        ["bottomHat"] = new("bottomHat", "Bottom-hat", "结构元", a => a.Region.BottomHat(a.StructuringElement!), NeedsStructuringElement: true),
        ["morphHat"] = new("morphHat", "Morphological hat", "结构元", a => a.Region.MorphHat(a.StructuringElement!), NeedsStructuringElement: true),
        ["minkowskiAdd1"] = new("minkowskiAdd1", "Minkowski addition", "count=迭代次数", a => a.Region.MinkowskiAdd1(a.StructuringElement!, a.Count), NeedsStructuringElement: true),
        ["minkowskiSub1"] = new("minkowskiSub1", "Minkowski subtraction", "count=迭代次数", a => a.Region.MinkowskiSub1(a.StructuringElement!, a.Count), NeedsStructuringElement: true),
        ["morphSkeleton"] = new("morphSkeleton", "Morphological skeleton", "无参数", a => a.Region.MorphSkeleton()),
        ["morphSkiz"] = new("morphSkiz", "Morphological SKIZ", "count, size 作为两轮迭代", a => a.Region.MorphSkiz(new JlTuple(a.Count), new JlTuple(Math.Max(1, (int)Math.Round(a.Size))))),
        ["pruning"] = new("pruning", "Pruning", "count=长度", a => a.Region.Pruning(a.Count)),
        ["rankRegion"] = new("rankRegion", "Rank region", "size=宽/高, count=秩", a => a.Region.RankRegion((int)a.Size, (int)a.Size, a.Count)),
        ["eliminateRuns"] = new("eliminateRuns", "Eliminate runs", "count=短阈值, size=长阈值", a => a.Region.EliminateRuns(a.Count, (int)Math.Max(a.Count, a.Size))),
        ["fitting"] = new("fitting", "Fitting", "结构元", a => a.Region.Fitting(a.StructuringElement!), NeedsStructuringElement: true),

        // ── 骨架细化 / 加粗 ─────────────────────────────────────────
        ["thinningSeq"] = new("thinningSeq", "Thinning (Golay sequence)", "mode=golay 元素（默认 l）", a => a.Region.ThinningSeq(a.Mode.Length == 0 ? "l" : a.Mode, new JlTuple(a.Count))),
        ["thickeningSeq"] = new("thickeningSeq", "Thickening (Golay sequence)", "mode=golay 元素（默认 l）", a => a.Region.ThickeningSeq(a.Mode.Length == 0 ? "l" : a.Mode, a.Count)),
        ["thinningGolay"] = new("thinningGolay", "Thinning (Golay)", "mode=golay 元素, count=旋转角", a => a.Region.ThinningGolay(a.Mode.Length == 0 ? "l" : a.Mode, a.Count)),
        ["thickeningGolay"] = new("thickeningGolay", "Thickening (Golay)", "mode=golay 元素, count=旋转角", a => a.Region.ThickeningGolay(a.Mode.Length == 0 ? "l" : a.Mode, a.Count)),
        ["hitOrMissGolay"] = new("hitOrMissGolay", "Hit-or-miss (Golay)", "mode=golay 元素, count=旋转角", a => a.Region.HitOrMissGolay(a.Mode.Length == 0 ? "l" : a.Mode, a.Count)),

        // ── 几何变换 ────────────────────────────────────────────────
        ["clipRegion"] = new("clipRegion", "Clip to rectangle", "row, column, size=高, length1=宽", a => a.Region.ClipRegion((int)a.Row, (int)a.Column, (int)(a.Row + a.Size), (int)(a.Column + a.Length1))),
        ["clipRegionRel"] = new("clipRegionRel", "Clip relative", "row=上, column=左, size=下, length1=右", a => a.Region.ClipRegionRel((int)a.Row, (int)a.Column, (int)a.Size, (int)a.Length1)),
        ["moveRegion"] = new("moveRegion", "Move region", "row, column", a => a.Region.MoveRegion((int)a.Row, (int)a.Column)),
        ["zoomRegion"] = new("zoomRegion", "Zoom region", "size=宽比例, length1=高比例", a => a.Region.ZoomRegion(a.Size <= 0 ? 1 : a.Size, a.Length1 <= 0 ? 1 : a.Length1)),
        ["mirrorRegion"] = new("mirrorRegion", "Mirror region", "mode=row|column|diagonal, size=图像宽/高", a => a.Region.MirrorRegion(a.Mode.Length == 0 ? "row" : a.Mode, (int)a.Size)),
        ["transposeRegion"] = new("transposeRegion", "Transpose region", "row, column", a => a.Region.TransposeRegion((int)a.Row, (int)a.Column)),
        ["partitionRectangle"] = new("partitionRectangle", "Partition rectangle", "size=宽, length1=高", a => a.Region.PartitionRectangle(a.Size, a.Length1)),
        ["partitionDynamic"] = new("partitionDynamic", "Partition dynamic", "size=距离, percent=百分比", a => a.Region.PartitionDynamic(a.Size, a.Percent)),
        ["interjacent"] = new("interjacent", "Interjacent", "mode=border|mixed", a => a.Region.Interjacent(a.Mode.Length == 0 ? "border" : a.Mode)),

        // ── 选择与排序 ──────────────────────────────────────────────
        ["selectShapeStd"] = new("selectShapeStd", "Select by shape", "mode=shape 名（默认 max_area）, percent=百分比", a => a.Region.SelectShapeStd(a.Mode.Length == 0 ? "max_area" : a.Mode, a.Percent)),
        ["selectRegionPoint"] = new("selectRegionPoint", "Select region at point", "row, column", a => a.Region.SelectRegionPoint((int)a.Row, (int)a.Column)),
        ["sortRegion"] = new("sortRegion", "Sort regions", "mode=sort 模式（默认 first_point）", a => a.Region.SortRegion(new JlTuple(a.Mode.Length == 0 ? "first_point" : a.Mode), "true", "row")),
        ["expandRegion"] = new("expandRegion", "Expand region", "第二区域=禁区, count=迭代, mode=形态", a => a.Region.ExpandRegion(a.Second, new JlTuple(a.Count), a.Mode.Length == 0 ? "image" : a.Mode), NeedsSecondRegion: true),
        ["fillUpShape"] = new("fillUpShape", "Fill up shape", "mode=特征名, min, max", a => a.Region.FillUpShape(a.Mode.Length == 0 ? "area" : a.Mode, new JlTuple(a.Min), new JlTuple(a.Max))),
        ["hammingChangeRegion"] = new("hammingChangeRegion", "Hamming change region", "size=宽, length1=高, count=距离", a => a.Region.HammingChangeRegion((int)a.Size, (int)a.Length1, a.Count)),

        // ── 灰度相关 ────────────────────────────────────────────────
        ["selectGray"] = new("selectGray", "Select by gray", "image, mode=特征名, min, max", a => a.Region.SelectGray(a.Image!, new JlTuple(a.Mode.Length == 0 ? "mean" : a.Mode), "and", new JlTuple(a.Min), new JlTuple(a.Max)), NeedsImage: true),
        ["expandGray"] = new("expandGray", "Expand gray", "image, 第二区域=禁区, count=迭代", a => a.Region.ExpandGray(a.Image!, a.Second, new JlTuple(a.Count), "image", new JlTuple((int)a.Min)), NeedsImage: true, NeedsSecondRegion: true),

        // ── 生成器 ─────────────────────────────────────────────────
        ["genRectangle2"] = new("genRectangle2", "Generate rectangle2", "row, column, phi(deg), size, length1", a => Generated(region => region.GenRectangle2(a.Row, a.Column, a.Phi * Math.PI / 180d, a.Size, a.Length1))),
        ["genEllipse"] = new("genEllipse", "Generate ellipse", "row, column, phi(deg), size, length1", a => Generated(region => region.GenEllipse(a.Row, a.Column, a.Phi * Math.PI / 180d, a.Size, a.Length1))),
        ["genCircleSector"] = new("genCircleSector", "Generate circle sector", "row, column, size=半径, phi..length1 为起止角", a => Generated(region => region.GenCircleSector(a.Row, a.Column, a.Size, a.Phi * Math.PI / 180d, (a.Phi + a.Length1) * Math.PI / 180d))),
        ["genRegionLine"] = new("genRegionLine", "Generate line region", "row, column, size=终点行, length1=终点列", a => Generated(region => region.GenRegionLine(a.Row, a.Column, a.Size, a.Length1))),
        ["genRegionPolygon"] = new("genRegionPolygon", "Generate polygon", "row/column 起点, size/length1 第二点", a => Generated(region => region.GenRegionPolygon(new JlTuple(new[] { a.Row, a.Size, a.Row + a.Size }), new JlTuple(new[] { a.Column, a.Length1, a.Column + a.Length1 })))),
        ["genCheckerRegion"] = new("genCheckerRegion", "Generate checker", "size=宽, length1=高, count=块尺寸", a => Generated(region => region.GenCheckerRegion((int)a.Size, (int)a.Length1, a.Count, a.Count))),
        ["genRandomRegion"] = new("genRandomRegion", "Generate random region", "size=宽, length1=高", a => Generated(region => region.GenRandomRegion((int)a.Size, (int)a.Length1))),
        ["genStructElements"] = new("genStructElements", "Generate struct element", "mode=类型（默认 rectangle）, row, column", a => Generated(region => region.GenStructElements(a.Mode.Length == 0 ? "rectangle" : a.Mode, (int)a.Row, (int)a.Column))),

        // ── Golay 形态学 / 命中击不中 / Minkowski ─────────────────────
        ["closingGolay"] = new("closingGolay", "Closing (Golay)", "mode=golay 元素（默认 l）, count=旋转角", a => a.Region.ClosingGolay(Golay(a.Mode), a.Count)),
        ["openingGolay"] = new("openingGolay", "Opening (Golay)", "mode=golay 元素（默认 l）, count=旋转角", a => a.Region.OpeningGolay(Golay(a.Mode), a.Count)),
        ["erosionGolay"] = new("erosionGolay", "Erosion (Golay)", "mode=golay 元素（默认 l）, count=迭代, phi=旋转角", a => a.Region.ErosionGolay(Golay(a.Mode), a.Count, (int)a.Phi)),
        ["dilationGolay"] = new("dilationGolay", "Dilation (Golay)", "mode=golay 元素（默认 l）, count=迭代, phi=旋转角", a => a.Region.DilationGolay(Golay(a.Mode), a.Count, (int)a.Phi)),
        ["erosionSeq"] = new("erosionSeq", "Erosion (Golay sequence)", "mode=golay 元素（默认 l）, count=迭代", a => a.Region.ErosionSeq(Golay(a.Mode), a.Count)),
        ["dilationSeq"] = new("dilationSeq", "Dilation (Golay sequence)", "mode=golay 元素（默认 l）, count=迭代", a => a.Region.DilationSeq(Golay(a.Mode), a.Count)),
        ["hitOrMissSeq"] = new("hitOrMissSeq", "Hit-or-miss (Golay sequence)", "mode=golay 元素（默认 l）", a => a.Region.HitOrMissSeq(Golay(a.Mode))),
        ["hitOrMiss"] = new("hitOrMiss", "Hit-or-miss", "结构元（两轮同一结构元）, row, column", a => a.Region.HitOrMiss(a.StructuringElement!, a.StructuringElement!, (int)a.Row, (int)a.Column), NeedsStructuringElement: true),
        ["openingSeg"] = new("openingSeg", "Opening (segmentation)", "结构元", a => a.Region.OpeningSeg(a.StructuringElement!), NeedsStructuringElement: true),
        ["thickening"] = new("thickening", "Thickening", "结构元, row, column, count=迭代", a => a.Region.Thickening(a.StructuringElement!, a.StructuringElement!, (int)a.Row, (int)a.Column, a.Count), NeedsStructuringElement: true),
        ["thinning"] = new("thinning", "Thinning", "结构元, row, column, count=迭代", a => a.Region.Thinning(a.StructuringElement!, a.StructuringElement!, (int)a.Row, (int)a.Column, a.Count), NeedsStructuringElement: true),
        ["minkowskiAdd2"] = new("minkowskiAdd2", "Minkowski addition (ref point)", "结构元, row, column, count=迭代", a => a.Region.MinkowskiAdd2(a.StructuringElement!, (int)a.Row, (int)a.Column, a.Count), NeedsStructuringElement: true),
        ["minkowskiSub2"] = new("minkowskiSub2", "Minkowski subtraction (ref point)", "结构元, row, column, count=迭代", a => a.Region.MinkowskiSub2(a.StructuringElement!, (int)a.Row, (int)a.Column, a.Count), NeedsStructuringElement: true),
        ["golayElements"] = new("golayElements", "Golay elements", "mode=golay 元素（默认 l）, count=旋转角, row, column", a => a.Region.GolayElements(Golay(a.Mode), a.Count, (int)a.Row, (int)a.Column)),

        // ── 图像驱动 / 骨架 / 选择原型 ───────────────────────────────
        ["closeEdges"] = new("closeEdges", "Close edges", "image, size=最小幅度", a => a.Region.CloseEdges(a.Image!, (int)a.Size), NeedsImage: true),
        ["closeEdgesLength"] = new("closeEdgesLength", "Close edges (length)", "image, size=最小幅度, length1=最大间隙", a => a.Region.CloseEdgesLength(a.Image!, (int)a.Size, (int)a.Length1), NeedsImage: true),
        ["splitSkeletonRegion"] = new("splitSkeletonRegion", "Split skeleton region", "size=最大距离", a => a.Region.SplitSkeletonRegion((int)a.Size)),
        ["junctionsSkeleton"] = new("junctionsSkeleton", "Junctions skeleton", "无参数", a =>
        {
            var region = a.Region.JunctionsSkeleton(out var junctionPoints);
            using (junctionPoints) return region;
        }),

        // ── 生成器 ─────────────────────────────────────────────────
        ["genEmptyRegion"] = new("genEmptyRegion", "Generate empty region", "无参数", a => Generated(region => region.GenEmptyRegion())),
        ["genEllipseSector"] = new("genEllipseSector", "Generate ellipse sector", "row, column, phi, size/length1=半径, length2=扇区角", a =>
            Generated(region => region.GenEllipseSector(a.Row, a.Column, a.Phi, a.Size, a.Length1, a.Phi, a.Phi + Math.Clamp(a.Length2, 0.1, Math.PI * 2)))),
        ["genGridRegion"] = new("genGridRegion", "Generate grid region", "size=行步长, length1=列步长, mode=类型（默认 lines）", a =>
            Generated(region => region.GenGridRegion((int)a.Size, (int)a.Length1, a.Mode.Length == 0 ? "lines" : a.Mode, 64, 64))),
        ["genRandomRegions"] = new("genRandomRegions", "Generate random regions", "size/length1=宽范围, row/column=高范围, count=个数", a =>
            Generated(region => region.GenRandomRegions("ellipse",
                Math.Max(1, a.Size), Math.Max(2, a.Size) + Math.Max(1, a.Length1),
                Math.Max(1, a.Row), Math.Max(2, a.Row) + Math.Max(1, a.Length2),
                0, 0, Math.Max(1, a.Count), 64, 64))),
        ["genRegionHline"] = new("genRegionHline", "Generate horizontal line region", "phi=方向, size=距离", a =>
            Generated(region => region.GenRegionHline(a.Phi, a.Size))),
        ["genRegionPoints"] = new("genRegionPoints", "Generate point region", "row/column 与 size/length1=两点", a =>
            Generated(region => region.GenRegionPoints(new JlTuple(new[] { (int)a.Row, (int)a.Size }), new JlTuple(new[] { (int)a.Column, (int)a.Length1 }))))
    };

    public static IReadOnlyDictionary<string, JlRegionOperator> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlRegionOperator Resolve(string key)
        => Table.TryGetValue(key, out var op)
            ? op
            : throw new InvalidOperationException(
                $"Unknown JLVision region operator '{key}'. Known operators: {string.Join(", ", Keys)}.");

    private static JlTuple Radius(double size) => new(Math.Max(1, size / 2d));

    /// <summary>Golay 元素名；空值回落到 "l"（线性元素），与既有 thinningSeq 一致。</summary>
    private static string Golay(string mode) => mode.Length == 0 ? "l" : mode;

    /// <summary>生成器算子是 Void + 就地修改，这里统一包成"新建 → 生成 → 返回"。</summary>
    private static JlRegion Generated(Action<JlRegion> build)
    {
        var region = new JlRegion();
        try { build(region); return region; }
        catch { region.Dispose(); throw; }
    }
}
