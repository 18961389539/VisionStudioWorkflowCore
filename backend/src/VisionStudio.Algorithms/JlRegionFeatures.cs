using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

/// <summary>区域特征的返回：一个主值 + 若干具名分量。</summary>
public sealed record JlRegionFeatureResult(double Value, IReadOnlyList<(string Name, double Value)> Extra);

/// <summary>区域特征条目的运行参数。</summary>
public sealed class JlRegionFeatureArgs
{
    public required JlRegion Region { get; init; }
    public JlImage? Image { get; init; }
    public double Size { get; init; } = 5;
    public int Count { get; init; } = 1;
    public double Row { get; init; } = 10;
    public double Column { get; init; } = 10;
    public double Min { get; init; }
    public double Max { get; init; } = 1;
    public string Mode { get; init; } = string.Empty;
}

public sealed record JlRegionFeature(
    string Key,
    string Label,
    string Note,
    Func<JlRegionFeatureArgs, JlRegionFeatureResult> Run,
    bool NeedsImage = false,
    bool KeepsComponents = false);

/// <summary>
/// 区域特征算子表：一个区域进，一个主数值（外加具名分量）出。
/// 覆盖几何、矩、游程、灰度统计与纹理方向等原本只能写代码调用的特征算子。
/// </summary>
public static class JlRegionFeatures
{
    private static JlRegionFeatureResult R(double value, params (string Name, double Value)[] extra)
        => new(value, extra);

    private static readonly Dictionary<string, JlRegionFeature> Table = new(StringComparer.Ordinal)
    {
        // ── 基础几何 ────────────────────────────────────────────────
        ["area"] = new("area", "Area (+ row/column)", "无参数", a =>
        {
            var area = a.Region.AreaCenter(out double row, out double column);
            return R(area, ("row", row), ("column", column));
        }),
        ["areaHoles"] = new("areaHoles", "Hole count / areas", "无参数", a =>
        {
            using var holes = a.Region.AreaHoles();
            var count = holes.Length;
            var sum = 0d;
            for (var i = 0; i < count; i++) sum += holes[i].D;
            return R(count, ("holes", count), ("areaSum", sum));
        }),
        ["circularity"] = new("circularity", "Circularity", "无参数", a => R(a.Region.Circularity().D)),
        ["compactness"] = new("compactness", "Compactness", "无参数", a => R(a.Region.Compactness().D)),
        ["convexity"] = new("convexity", "Convexity", "无参数", a => R(a.Region.Convexity().D)),
        ["rectangularity"] = new("rectangularity", "Rectangularity", "无参数", a => R(a.Region.Rectangularity().D)),
        ["eulerNumber"] = new("eulerNumber", "Euler number", "无参数", a => R(a.Region.EulerNumber().D)),
        ["eccentricity"] = new("eccentricity", "Eccentricity (+bulkiness/structure factor)", "无参数", a =>
        {
            var value = a.Region.Eccentricity(out double bulkiness, out double structureFactor);
            return R(value, ("bulkiness", bulkiness), ("structureFactor", structureFactor));
        }),
        ["ellipticAxis"] = new("ellipticAxis", "Elliptic axis (+rb/phi)", "无参数", a =>
        {
            var ra = a.Region.EllipticAxis(out double rb, out double phi);
            return R(ra, ("rb", rb), ("phiDeg", phi * 180d / Math.PI));
        }),
        ["roundness"] = new("roundness", "Roundness (+sigma/sides)", "无参数", a =>
        {
            var value = a.Region.Roundness(out double sigma, out double roundness, out double sides);
            return R(value, ("sigma", sigma), ("roundness", roundness), ("sides", sides));
        }),
        ["heightWidthRatio"] = new("heightWidthRatio", "Height/width ratio (+width)", "无参数", a =>
        {
            var height = a.Region.HeightWidthRatio(out int width, out double ratio);
            return R(height, ("width", width), ("ratio", ratio));
        }),

        // ── 外接 / 内接几何 ─────────────────────────────────────────
        ["smallestCircle"] = new("smallestCircle", "Smallest enclosing circle", "无参数", a =>
        {
            a.Region.SmallestCircle(out double row, out double column, out double radius);
            return R(radius, ("row", row), ("column", column), ("radius", radius));
        }),
        ["smallestRectangle1"] = new("smallestRectangle1", "Smallest axis-aligned rectangle", "无参数", a =>
        {
            a.Region.SmallestRectangle1(out int row1, out int column1, out int row2, out int column2);
            return R((row2 - row1 + 1d) * (column2 - column1 + 1d),
                ("row1", row1), ("column1", column1), ("row2", row2), ("column2", column2));
        }),
        ["smallestRectangle2"] = new("smallestRectangle2", "Smallest rotated rectangle", "无参数", a =>
        {
            a.Region.SmallestRectangle2(out double row, out double column, out double phi, out double length1, out double length2);
            return R(4d * length1 * length2, ("row", row), ("column", column), ("phiDeg", phi * 180d / Math.PI),
                ("length1", length1), ("length2", length2));
        }),
        ["innerCircle"] = new("innerCircle", "Largest inner circle", "无参数", a =>
        {
            a.Region.InnerCircle(out double row, out double column, out double radius);
            return R(radius, ("row", row), ("column", column), ("radius", radius));
        }),
        ["innerRectangle1"] = new("innerRectangle1", "Largest inner rectangle", "无参数", a =>
        {
            a.Region.InnerRectangle1(out int row1, out int column1, out int row2, out int column2);
            return R((row2 - row1 + 1d) * (column2 - column1 + 1d),
                ("row1", row1), ("column1", column1), ("row2", row2), ("column2", column2));
        }),
        ["diameterRegion"] = new("diameterRegion", "Diameter (max chord)", "无参数", a =>
        {
            a.Region.DiameterRegion(out int row1, out int column1, out int row2, out int column2, out double diameter);
            return R(diameter, ("row1", row1), ("column1", column1), ("row2", row2), ("column2", column2));
        }),
        ["connectAndHoles"] = new("connectAndHoles", "Connected components (+holes)", "无参数", a =>
        {
            var components = a.Region.ConnectAndHoles(out int holes);
            return R(components, ("components", components), ("holes", holes));
        }, KeepsComponents: true),

        // ── 矩 ─────────────────────────────────────────────────────
        ["momentsCentral"] = new("momentsCentral", "Central moments i2/i3/i4", "无参数", a =>
        {
            a.Region.MomentsRegionCentral(out double i2, out double i3, out double i4);
            return R(i2, ("i2", i2), ("i3", i3), ("i4", i4));
        }),
        ["momentsCentralInvar"] = new("momentsCentralInvar", "Central invariant moments", "无参数", a =>
        {
            a.Region.MomentsRegionCentralInvar(out double psi2, out double psi3, out double psi4);
            return R(psi2, ("psi2", psi2), ("psi3", psi3), ("psi4", psi4));
        }),
        ["moments2nd"] = new("moments2nd", "Second-order moments", "无参数", a =>
        {
            a.Region.MomentsRegion2nd(out double m20, out double m02, out double ia, out double ib);
            return R(m20, ("m20", m20), ("m02", m02), ("ia", ia), ("ib", ib));
        }),
        ["moments2ndInvar"] = new("moments2ndInvar", "Second-order invariant moments", "无参数", a =>
        {
            a.Region.MomentsRegion2ndInvar(out double m20, out double m02);
            return R(m20, ("m20", m20), ("m02", m02));
        }),
        ["moments2ndRelInvar"] = new("moments2ndRelInvar", "Relative invariant moment PHI2", "无参数", a =>
        {
            a.Region.MomentsRegion2ndRelInvar(out double phi2);
            return R(phi2, ("phi2", phi2));
        }),
        ["moments3rd"] = new("moments3rd", "Third-order moments", "无参数", a =>
        {
            a.Region.MomentsRegion3rd(out double m12, out double m03, out double m30);
            return R(m12, ("m12", m12), ("m03", m03), ("m30", m30));
        }),
        ["moments3rdInvar"] = new("moments3rdInvar", "Third-order invariant moments", "无参数", a =>
        {
            a.Region.MomentsRegion3rdInvar(out double m12, out double m03, out double m30);
            return R(m12, ("m12", m12), ("m03", m03), ("m30", m30));
        }),

        // ── 游程 ───────────────────────────────────────────────────
        ["runlengthDistribution"] = new("runlengthDistribution", "Run-length distribution", "无参数", a =>
        {
            using var freq = a.Region.RunlengthDistribution(out var background);
            using (background) return R(freq.Length, ("bins", freq.Length), ("backgroundRuns", background.Length));
        }),
        ["runlengthFeatures"] = new("runlengthFeatures", "Run-length features", "无参数", a =>
        {
            var runs = a.Region.RunlengthFeatures(out double kFactor, out double lFactor, out double meanLength, out int bytes);
            return R(runs, ("runs", runs), ("kFactor", kFactor), ("lFactor", lFactor), ("meanLength", meanLength), ("bytes", bytes));
        }),
        ["regionThickness"] = new("regionThickness", "Region thickness histogram", "无参数", a =>
        {
            using var centers = a.Region.GetRegionThickness(out var histogram);
            using (histogram)
            {
                var maxCount = 0d;
                for (var i = 0; i < histogram.Length; i++) maxCount = Math.Max(maxCount, histogram[i].D);
                return R(centers.Length, ("bins", centers.Length), ("maxCount", maxCount));
            }
        }),

        // ── 通用特征查询 ────────────────────────────────────────────
        ["regionFeatures"] = new("regionFeatures", "Named region features", "mode=逗号分隔特征名（area,row,column,width,height,...）", a =>
        {
            var names = string.IsNullOrWhiteSpace(a.Mode) ? "area" : a.Mode;
            var parts = names.Split(',', StringSplitOptions.TrimEntries);
            using var tuple = a.Region.RegionFeatures(new JlTuple(parts));
            var list = new List<(string, double)>();
            for (var i = 0; i < parts.Length; i++) list.Add((parts[i], i < tuple.Length ? tuple[i].D : double.NaN));
            return R(list.Count > 0 ? list[0].Item2 : 0d, list.ToArray());
        }),

        // ── 灰度特征 ────────────────────────────────────────────────
        ["grayFeatures"] = new("grayFeatures", "Named gray features", "image, mode=特征名", a =>
            R(a.Region.GrayFeatures(a.Image!, string.IsNullOrWhiteSpace(a.Mode) ? "mean" : a.Mode)), NeedsImage: true),
        ["areaCenterGray"] = new("areaCenterGray", "Mean gray (+center)", "image", a =>
        {
            var mean = a.Region.AreaCenterGray(a.Image!, out double row, out double column);
            return R(mean, ("row", row), ("column", column));
        }, NeedsImage: true),
        ["entropyGray"] = new("entropyGray", "Gray entropy (+anisotropy)", "image", a =>
        {
            var entropy = a.Region.EntropyGray(a.Image!, out double anisotropy);
            return R(entropy, ("anisotropy", anisotropy));
        }, NeedsImage: true),
        ["fuzzyEntropy"] = new("fuzzyEntropy", "Fuzzy entropy", "image, count=apar, size=cpar", a =>
            R(a.Region.FuzzyEntropy(a.Image!, a.Count, (int)Math.Max(1, a.Size)).D), NeedsImage: true),
        ["fuzzyPerimeter"] = new("fuzzyPerimeter", "Fuzzy perimeter", "image, count=apar, size=cpar", a =>
            R(a.Region.FuzzyPerimeter(a.Image!, a.Count, (int)Math.Max(1, a.Size)).D), NeedsImage: true),
        ["planeDeviation"] = new("planeDeviation", "Plane deviation", "image", a =>
            R(a.Region.PlaneDeviation(a.Image!).D), NeedsImage: true),
        ["noiseDistributionMean"] = new("noiseDistributionMean", "Noise distribution mean", "image, count=滤波尺寸", a =>
            R(a.Region.NoiseDistributionMean(a.Image!, Math.Max(1, a.Count)).D), NeedsImage: true),
        ["grayHistoAbs"] = new("grayHistoAbs", "Absolute gray histogram", "image, count=量化级数", a =>
        {
            using var histo = a.Region.GrayHistoAbs(a.Image!, (double)Math.Max(1, a.Count));
            var bins = histo.Length;
            var sum = 0d;
            for (var i = 0; i < bins; i++) sum += histo[i].D;
            return R(bins, ("bins", bins), ("sum", sum));
        }, NeedsImage: true),
        ["grayHistoRange"] = new("grayHistoRange", "Gray histogram in range", "image, min, max, count=bin 数", a =>
        {
            var count = a.Region.GrayHistoRange(a.Image!, a.Min, a.Max, Math.Max(2, a.Count), out double binSize);
            return R(count, ("binSize", binSize));
        }, NeedsImage: true),
        ["grayProjections"] = new("grayProjections", "Gray projections", "image, mode=simple|complex", a =>
        {
            using var horizontal = a.Region.GrayProjections(a.Image!, string.IsNullOrWhiteSpace(a.Mode) ? "simple" : a.Mode, out var vertical);
            using (vertical) return R(horizontal.Length, ("horizontalBins", horizontal.Length), ("verticalBins", vertical.Length));
        }, NeedsImage: true),

        // ── 纹理方向 ────────────────────────────────────────────────
        ["textLineOrientation"] = new("textLineOrientation", "Text line orientation", "image, count=字符高度", a =>
            R(a.Region.TextLineOrientation(a.Image!, Math.Max(1, a.Count), -0.5, 0.5).D), NeedsImage: true),
        ["textLineSlant"] = new("textLineSlant", "Text line slant", "image, count=字符高度", a =>
            R(a.Region.TextLineSlant(a.Image!, Math.Max(1, a.Count), -0.5, 0.5).D), NeedsImage: true),
        ["shapeHistoAll"] = new("shapeHistoAll", "Shape histogram (all points)", "image, mode=特征名", a =>
        {
            using var histo = a.Region.ShapeHistoAll(a.Image!, string.IsNullOrWhiteSpace(a.Mode) ? "convexity" : a.Mode, out var relative);
            using (relative) return R(histo.Length, ("bins", histo.Length));
        }, NeedsImage: true),
        ["shapeHistoPoint"] = new("shapeHistoPoint", "Shape histogram (single point)", "image, mode=特征名, row, column", a =>
        {
            using var histo = a.Region.ShapeHistoPoint(a.Image!, string.IsNullOrWhiteSpace(a.Mode) ? "convexity" : a.Mode,
                (int)a.Row, (int)a.Column, out var relative);
            using (relative) return R(histo.Length, ("bins", histo.Length));
        }, NeedsImage: true),

        // ── 点测试 / 距离 / 灰度轴 ───────────────────────────────────
        ["testRegionPoint"] = new("testRegionPoint", "Test region point", "row, column", a =>
            R(a.Region.TestRegionPoint((int)a.Row, (int)a.Column))),
        ["testRegionPoints"] = new("testRegionPoints", "Test region points", "row, column", a =>
            R(a.Region.TestRegionPoints((int)a.Row, (int)a.Column))),
        ["getRegionIndex"] = new("getRegionIndex", "Region index at point", "row, column", a =>
            R(a.Region.GetRegionIndex((int)a.Row, (int)a.Column).D)),
        ["distancePr"] = new("distancePr", "Distance point to region", "row, column", a =>
        {
            a.Region.DistancePr(a.Row, a.Column, out double min, out double max);
            return R(min, ("min", min), ("max", max));
        }),
        ["ellipticAxisGray"] = new("ellipticAxisGray", "Elliptic axis (gray)", "image", a =>
        {
            var ra = a.Region.EllipticAxisGray(a.Image!, out double rb, out double phi);
            return R(ra, ("rb", rb), ("phiDeg", phi * 180d / Math.PI));
        }, NeedsImage: true),
        ["momentsGrayPlane"] = new("momentsGrayPlane", "Moments of gray plane", "image", a =>
        {
            a.Region.MomentsGrayPlane(a.Image!, out double mRow, out double mCol, out double alpha, out double beta, out double mean);
            return R(alpha, ("mRow", mRow), ("mCol", mCol), ("beta", beta), ("mean", mean));
        }, NeedsImage: true)
    };

    public static IReadOnlyDictionary<string, JlRegionFeature> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlRegionFeature Resolve(string key)
        => Table.TryGetValue(key, out var feature)
            ? feature
            : throw new InvalidOperationException(
                $"Unknown JLVision region feature '{key}'. Known features: {string.Join(", ", Keys)}.");
}
