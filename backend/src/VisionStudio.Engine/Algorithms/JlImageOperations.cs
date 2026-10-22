using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// 一个 JLVision 图像算子条目。<see cref="Key"/> 是节点参数里的稳定标识，
/// <see cref="Label"/> 直接进目录下拉，<see cref="Note"/> 说明用到哪些参数。
/// </summary>
public sealed record JlImageOperator(string Key, string Label, string Note, Func<JlImageOperatorArgs, JlImage> Run,
    JlPixelKind Pixels = JlPixelKind.Any, bool NeedsStructuringElement = false);

/// <summary>算子对输入像素类型的最低要求。节点在执行前校验并给出可操作的错误，而不是让原生错误码冒出来。</summary>
public enum JlPixelKind
{
    /// <summary>不做校验，交由原生算子判断。</summary>
    Any,
    /// <summary>实数图（CV_32F），如三角函数、指数对数。</summary>
    Real,
    /// <summary>实数图且取值在 [-1, 1]，如 asin / acos。</summary>
    UnitReal,
    /// <summary>三通道 8 位图。</summary>
    Color
}

/// <summary>
/// 图像算子的统一入参包。所有算子共用同一组节点参数，靠 <see cref="Note"/> 说明各自取哪几个，
/// 这样新增一个算子只需要加一行表项，不需要新节点类型或新参数。
/// </summary>
public sealed class JlImageOperatorArgs
{
    public required JlImage Image { get; init; }

    /// <summary>第二输入图；未连接时等于 <see cref="Image"/>（双图算子仍然可执行）。</summary>
    public required JlImage Secondary { get; init; }

    public JlImage? Third { get; init; }
    public string LutJson { get; init; } = string.Empty;
    public double Size { get; init; } = 3;
    public double Row { get; init; }
    public double Column { get; init; }
    public double Mult { get; init; } = 1;
    public double Offset { get; init; }
    public double Threshold { get; init; } = 128;
    public int Count { get; init; } = 1;
    public string Mode { get; init; } = "byte";
    public double Size2 { get; init; } = 3;
}

/// <summary>
/// JLVision 图像算子表（<see cref="JlImage"/> → <see cref="JlImage"/>）。
/// 只收录"返回一张新图"的算子；返回区域/轮廓/数值的算子在各自的节点里接线。
/// </summary>
public static class JlImageOperations
{
    private static readonly Dictionary<string, JlImageOperator> Table = new(StringComparer.Ordinal)
    {
        // ── 逐像素代数 ───────────────────────────────────────────────
        ["abs"] = new("abs", "Absolute value", "无参数", a => a.Image.AbsImage()),
        ["invert"] = new("invert", "Invert (max - x)", "无参数", a => a.Image.InvertImage()),
        ["sqrt"] = new("sqrt", "Square root", "无参数", a => a.Image.SqrtImage()),
        ["exp"] = new("exp", "Exponential (base e)", "无参数，底数固定为 e", a => a.Image.ExpImage("e"), JlPixelKind.Real),
        ["log"] = new("log", "Logarithm (base e)", "无参数，底数固定为 e", a => a.Image.LogImage("e"), JlPixelKind.Real),
        ["pow"] = new("pow", "Power", "offset=指数", a => a.Image.PowImage(a.Offset <= 0 ? 2d : a.Offset), JlPixelKind.Real),
        ["scale"] = new("scale", "Scale (x*mult + add)", "mult, offset", a => a.Image.ScaleImage(a.Mult, a.Offset)),
        ["scaleMax"] = new("scaleMax", "Scale to full range", "无参数", a => a.Image.ScaleImageMax()),
        ["gamma"] = new("gamma", "Gamma correction", "mult=gamma, offset, threshold, mode=encode", a =>
            a.Image.GammaImage(a.Mult <= 0 ? 1d : a.Mult, a.Offset, a.Threshold, 255d, a.Mode == "true" ? "true" : "false")),
        ["convertType"] = new("convertType", "Convert pixel type", "mode=byte|int1|int2|uint2|real", a => a.Image.ConvertImageType(a.Mode.Length == 0 ? "byte" : a.Mode)),

        // ── 三角函数 ─────────────────────────────────────────────────
        ["sin"] = new("sin", "Sine", "无参数", a => a.Image.SinImage(), JlPixelKind.Real),
        ["cos"] = new("cos", "Cosine", "无参数", a => a.Image.CosImage(), JlPixelKind.Real),
        ["tan"] = new("tan", "Tangent", "无参数", a => a.Image.TanImage(), JlPixelKind.Real),
        ["asin"] = new("asin", "Arc sine", "无参数", a => a.Image.AsinImage(), JlPixelKind.UnitReal),
        ["acos"] = new("acos", "Arc cosine", "无参数", a => a.Image.AcosImage(), JlPixelKind.UnitReal),
        ["atan"] = new("atan", "Arc tangent", "无参数", a => a.Image.AtanImage(), JlPixelKind.Real),
        ["atan2"] = new("atan2", "Arc tangent (y/x)", "第二输入图", a => a.Image.Atan2Image(a.Secondary), JlPixelKind.Real),

        // ── 双图算术 ─────────────────────────────────────────────────
        ["add"] = new("add", "Add images", "第二输入图, mult, offset", a => a.Image.AddImage(a.Secondary, a.Mult, a.Offset)),
        ["sub"] = new("sub", "Subtract images", "第二输入图, mult, offset", a => a.Image.SubImage(a.Secondary, a.Mult, a.Offset)),
        ["mult"] = new("mult", "Multiply images", "第二输入图, mult, offset", a => a.Image.MultImage(a.Secondary, a.Mult, a.Offset)),
        ["div"] = new("div", "Divide images", "第二输入图, mult, offset", a => a.Image.DivImage(a.Secondary, a.Mult, a.Offset)),
        ["absDiff"] = new("absDiff", "Absolute difference", "第二输入图, mult", a => a.Image.AbsDiffImage(a.Secondary, a.Mult)),
        ["min"] = new("min", "Minimum of two images", "第二输入图", a => a.Image.MinImage(a.Secondary)),
        ["max"] = new("max", "Maximum of two images", "第二输入图", a => a.Image.MaxImage(a.Secondary)),

        // ── 位运算 ───────────────────────────────────────────────────
        ["bitAnd"] = new("bitAnd", "Bitwise AND", "第二输入图", a => a.Image.BitAnd(a.Secondary)),
        ["bitOr"] = new("bitOr", "Bitwise OR", "第二输入图", a => a.Image.BitOr(a.Secondary)),
        ["bitXor"] = new("bitXor", "Bitwise XOR", "第二输入图", a => a.Image.BitXor(a.Secondary)),
        ["bitNot"] = new("bitNot", "Bitwise NOT", "无参数", a => a.Image.BitNot()),
        ["bitLshift"] = new("bitLshift", "Bit shift left", "count=位数", a => a.Image.BitLshift(a.Count)),
        ["bitRshift"] = new("bitRshift", "Bit shift right", "count=位数", a => a.Image.BitRshift(a.Count)),
        ["bitMask"] = new("bitMask", "Bit mask", "count=掩膜", a => a.Image.BitMask(a.Count)),
        ["bitSlice"] = new("bitSlice", "Bit slice", "count=位号", a => a.Image.BitSlice(a.Count)),

        // ── 滤波 ─────────────────────────────────────────────────────
        ["gauss"] = new("gauss", "Gaussian filter", "size=核尺寸", a => a.Image.GaussFilter(Kernel(a.Size))),
        ["gaussImage"] = new("gaussImage", "Gaussian filter (siemens)", "size=核尺寸", a => a.Image.GaussImage(Kernel(a.Size))),
        ["mean"] = new("mean", "Mean filter", "size, size2", a => a.Image.MeanImage(Kernel(a.Size), Kernel(a.Size2))),
        ["binomial"] = new("binomial", "Binomial filter", "size, size2", a => a.Image.BinomialFilter(Kernel(a.Size), Kernel(a.Size2))),
        ["medianRect"] = new("medianRect", "Median filter (rect)", "size, size2", a => a.Image.MedianRect(Kernel(a.Size), Kernel(a.Size2))),
        ["sigma"] = new("sigma", "Sigma filter", "size, size2, threshold=sigma", a =>
            a.Image.SigmaImage(Kernel(a.Size), Kernel(a.Size2), Math.Max(1, (int)Math.Round(a.Threshold)))),
        ["laplaceOfGauss"] = new("laplaceOfGauss", "Laplacian of Gaussian", "mult=sigma", a =>
            a.Image.LaplaceOfGauss(a.Mult <= 0 ? 1d : a.Mult)),
        ["sobelAmp"] = new("sobelAmp", "Sobel amplitude", "size=核尺寸, mode=sum_abs|sum_sqrt", a =>
            a.Image.SobelAmp(SobelMode(a.Mode), Kernel(a.Size))),
        ["smooth"] = new("smooth", "Smooth image", "mode=derivate_gauss, mult=alpha", a =>
            a.Image.SmoothImage("gauss", a.Mult)),

        // ── 灰度形态学 ───────────────────────────────────────────────
        ["grayErosion"] = new("grayErosion", "Gray erosion", "第二输入图=结构元", a => a.Image.GrayErosion(a.Secondary), NeedsStructuringElement: true),
        ["grayDilation"] = new("grayDilation", "Gray dilation", "第二输入图=结构元", a => a.Image.GrayDilation(a.Secondary), NeedsStructuringElement: true),
        ["grayOpening"] = new("grayOpening", "Gray opening", "第二输入图=结构元", a => a.Image.GrayOpening(a.Secondary), NeedsStructuringElement: true),
        ["grayClosing"] = new("grayClosing", "Gray closing", "第二输入图=结构元", a => a.Image.GrayClosing(a.Secondary), NeedsStructuringElement: true),
        ["grayTophat"] = new("grayTophat", "Gray top-hat", "第二输入图=结构元", a => a.Image.GrayTophat(a.Secondary), NeedsStructuringElement: true),
        ["grayBothat"] = new("grayBothat", "Gray bottom-hat", "第二输入图=结构元", a => a.Image.GrayBothat(a.Secondary), NeedsStructuringElement: true),
        ["graySkeleton"] = new("graySkeleton", "Gray skeleton", "无参数", a => a.Image.GraySkeleton()),
        ["grayInside"] = new("grayInside", "Gray inside", "无参数", a => a.Image.GrayInside()),

        // ── 第二批：裁剪 / 域 / 增强 / 卷积 / 金字塔 ────────────────
        ["addBorder"] = new("addBorder", "Add image border", "size=边框宽度", a => a.Image.AddImageBorder(Kernel(a.Size), 0)),
        ["changeFormat"] = new("changeFormat", "Change image format", "size=宽, size2=高", a => a.Image.ChangeFormat(Kernel(a.Size), Kernel(a.Size2))),
        ["cropPart"] = new("cropPart", "Crop part", "row, column, size=宽, size2=高", a =>
            a.Image.CropPart((int)a.Row, (int)a.Column, Kernel(a.Size), Kernel(a.Size2))),
        ["cropRectangle2"] = new("cropRectangle2", "Crop rotated rectangle", "row, column, mult=phi, size=length1, size2=length2", a =>
            a.Image.CropRectangle2(a.Row, a.Column, a.Mult, a.Size, a.Size2, "true", "bilinear")),
        ["cropDomainRel"] = new("cropDomainRel", "Crop domain relative", "row=top, column=left, size=bottom, size2=right", a =>
            a.Image.CropDomainRel((int)a.Row, (int)a.Column, (int)a.Size, (int)a.Size2)),
        ["expandDomainGray"] = new("expandDomainGray", "Expand gray domain", "size=扩展半径", a => a.Image.ExpandDomainGray(Kernel(a.Size))),
        ["emphasize"] = new("emphasize", "Emphasize (unsharp)", "size, size2, mult=factor", a =>
            a.Image.Emphasize(Kernel(a.Size), Kernel(a.Size2), a.Mult)),
        ["diffOfGauss"] = new("diffOfGauss", "Difference of Gaussians", "mult=sigma, offset=sigFactor", a =>
            a.Image.DiffOfGauss(a.Mult <= 0 ? 1 : a.Mult, a.Offset <= 1 ? 1.6 : a.Offset)),
        ["deviationImage"] = new("deviationImage", "Deviation image", "size, size2", a =>
            a.Image.DeviationImage(Kernel(a.Size), Kernel(a.Size2))),
        ["entropyImage"] = new("entropyImage", "Entropy image", "size, size2", a =>
            a.Image.EntropyImage(Kernel(a.Size), Kernel(a.Size2))),
        ["cornerResponse"] = new("cornerResponse", "Corner response", "size=窗口, mult=weight", a =>
            a.Image.CornerResponse(Kernel(a.Size), a.Mult)),
        ["eliminateMinMax"] = new("eliminateMinMax", "Eliminate min/max", "size, size2, mult=gap, count=mode", a =>
            a.Image.EliminateMinMax(Kernel(a.Size), Kernel(a.Size2), a.Mult, a.Count)),
        ["eliminateSp"] = new("eliminateSp", "Eliminate salt & pepper", "size, size2, threshold=minThresh, offset=maxThresh", a =>
            a.Image.EliminateSp(Kernel(a.Size), Kernel(a.Size2), (int)a.Threshold, (int)Math.Max(a.Threshold, a.Offset))),
        ["fillInterlace"] = new("fillInterlace", "Fill interlace", "mode=even|odd", a =>
            a.Image.FillInterlace(string.IsNullOrWhiteSpace(a.Mode) ? "even" : a.Mode)),
        ["cfaToRgb"] = new("cfaToRgb", "CFA to RGB", "mode=bayer 类型（默认 bayer_gb）", a =>
            a.Image.CfaToRgb(string.IsNullOrWhiteSpace(a.Mode) ? "bayer_gb" : a.Mode, "bilinear")),
        ["edgesImage"] = new("edgesImage", "Edges image", "mult=alpha, threshold=low, offset=high", a =>
            a.Image.EdgesImage(out var direction, "canny", a.Mult <= 0 ? 1 : a.Mult, "nms", (int)a.Threshold, (int)Math.Max(a.Threshold, a.Offset))),


        // ── 通道与域 ─────────────────────────────────────────────────
        ["channelsToImage"] = new("channelsToImage", "Channels to image", "无参数", a => a.Image.ChannelsToImage(), JlPixelKind.Color),
        ["imageToChannels"] = new("imageToChannels", "Image to channels", "无参数", a => a.Image.ImageToChannels(), JlPixelKind.Color),
        ["fullDomain"] = new("fullDomain", "Full domain", "无参数", a => a.Image.FullDomain()),
        ["cropDomain"] = new("cropDomain", "Crop to domain", "无参数", a => a.Image.CropDomain()),

        // ── 统计与增强 ───────────────────────────────────────────────
        ["meanN"] = new("meanN", "Mean of neighbours", "无参数", a => a.Image.MeanN()),
        ["deviationN"] = new("deviationN", "Deviation of neighbours", "无参数", a => a.Image.DeviationN()),
        ["equHisto"] = new("equHisto", "Histogram equalisation", "无参数", a => a.Image.EquHistoImage()),
        ["topographic"] = new("topographic", "Topographic sketch", "无参数", a => a.Image.TopographicSketch()),
        ["addNoiseWhite"] = new("addNoiseWhite", "Add white noise", "mult=幅值", a => a.Image.AddNoiseWhite(a.Mult)),

        // ── 第三批：通道 / 卷积 / 秩滤波 / 几何 / 生成 ────────────────
        ["accessChannel"] = new("accessChannel", "Access channel", "count=通道号（1 起）", a => a.Image.AccessChannel(a.Count)),
        ["compose3"] = new("compose3", "Compose three images", "image2=第二通道, third=第三通道", a => a.Image.Compose3(a.Secondary, a.Third ?? throw new InvalidOperationException("compose3 需要 image3 输入。"))),
        ["dotsImage"] = new("dotsImage", "Dots image", "size=直径, count=像素移位", a => a.Image.DotsImage(Kernel(a.Size), "light", a.Count)),
        ["grayClosingRect"] = new("grayClosingRect", "Gray closing (rect)", "size, size2", a => a.Image.GrayClosingRect(Kernel(a.Size), Kernel(a.Size2))),
        ["grayOpeningRect"] = new("grayOpeningRect", "Gray opening (rect)", "size, size2", a => a.Image.GrayOpeningRect(Kernel(a.Size), Kernel(a.Size2))),
        ["grayErosionRect"] = new("grayErosionRect", "Gray erosion (rect)", "size, size2", a => a.Image.GrayErosionRect(Kernel(a.Size), Kernel(a.Size2))),
        ["grayDilationRect"] = new("grayDilationRect", "Gray dilation (rect)", "size, size2", a => a.Image.GrayDilationRect(Kernel(a.Size), Kernel(a.Size2))),
        ["grayErosionShape"] = new("grayErosionShape", "Gray erosion (shape)", "size, size2, mode=形状（默认 rectangle）", a =>
            a.Image.GrayErosionShape((double)Kernel(a.Size), (double)Kernel(a.Size2), a.Mode.Length == 0 ? "rectangle" : a.Mode)),
        ["grayDilationShape"] = new("grayDilationShape", "Gray dilation (shape)", "size, size2, mode=形状（默认 rectangle）", a =>
            a.Image.GrayDilationShape((double)Kernel(a.Size), (double)Kernel(a.Size2), a.Mode.Length == 0 ? "rectangle" : a.Mode)),
        ["grayOpeningShape"] = new("grayOpeningShape", "Gray opening (shape)", "size, size2, mode=形状（默认 rectangle）", a =>
            a.Image.GrayOpeningShape((double)Kernel(a.Size), (double)Kernel(a.Size2), a.Mode.Length == 0 ? "rectangle" : a.Mode)),
        ["grayClosingShape"] = new("grayClosingShape", "Gray closing (shape)", "size, size2, mode=形状（默认 rectangle）", a =>
            a.Image.GrayClosingShape((double)Kernel(a.Size), (double)Kernel(a.Size2), a.Mode.Length == 0 ? "rectangle" : a.Mode)),
        ["grayRangeRect"] = new("grayRangeRect", "Gray range (rect)", "size, size2", a => a.Image.GrayRangeRect(Kernel(a.Size), Kernel(a.Size2))),
        ["highpassImage"] = new("highpassImage", "Highpass filter", "size, size2", a => a.Image.HighpassImage(Kernel(a.Size), Kernel(a.Size2))),
        ["illuminate"] = new("illuminate", "Illuminate", "size, size2, mult=factor", a => a.Image.Illuminate(Kernel(a.Size), Kernel(a.Size2), a.Mult)),
        ["isotropicDiffusion"] = new("isotropicDiffusion", "Isotropic diffusion", "mult=sigma, count=迭代", a =>
            a.Image.IsotropicDiffusion(a.Mult <= 0 ? 1 : a.Mult, Math.Max(1, a.Count))),
        ["meanSp"] = new("meanSp", "Mean (salt & pepper)", "size, size2, threshold=minThresh, offset=maxThresh", a =>
            a.Image.MeanSp(Kernel(a.Size), Kernel(a.Size2), (int)a.Threshold, (int)Math.Max(a.Threshold, a.Offset))),
        ["medianSeparate"] = new("medianSeparate", "Median (separate)", "size, size2", a => a.Image.MedianSeparate(Kernel(a.Size), Kernel(a.Size2), "mirrored")),
        ["medianWeighted"] = new("medianWeighted", "Median (weighted)", "size=核尺寸", a => a.Image.MedianWeighted("gauss", Kernel(a.Size))),
        ["rankN"] = new("rankN", "Rank filter (N)", "count=秩", a => a.Image.RankN(a.Count)),
        ["rankRect"] = new("rankRect", "Rank filter (rect)", "size, size2, count=秩", a => a.Image.RankRect(Kernel(a.Size), Kernel(a.Size2), a.Count)),
        ["rectangle1Domain"] = new("rectangle1Domain", "Rectangle domain", "row, column, size=行距, size2=列距", a =>
            a.Image.Rectangle1Domain((int)a.Row, (int)a.Column, (int)(a.Row + a.Size), (int)(a.Column + a.Size2))),
        ["roberts"] = new("roberts", "Roberts filter", "mode=滤波类型（默认 roberts）", a => a.Image.Roberts(a.Mode.Length == 0 ? "roberts" : a.Mode)),
        ["rotateImage"] = new("rotateImage", "Rotate image", "mult=角度(rad)", a => a.Image.RotateImage(a.Mult, "bilinear")),
        ["symmetry"] = new("symmetry", "Symmetry filter", "size=窗口", a => a.Image.Symmetry(Kernel(a.Size), 0d, 1d)),
        ["zoomImageFactor"] = new("zoomImageFactor", "Zoom by factor", "size=宽比例, size2=高比例", a =>
            a.Image.ZoomImageFactor(a.Size <= 0 ? 2 : a.Size, a.Size2 <= 0 ? 2 : a.Size2, "bilinear")),
        ["genImageProto"] = new("genImageProto", "Generate constant image", "mult=灰度值", a => a.Image.GenImageProto(a.Mult)),
        ["genImageGrayRamp"] = new("genImageGrayRamp", "Generate gray ramp", "size=宽, size2=高", a =>
            Generated(i => i.GenImageGrayRamp(1d, 0d, 128d, 0, 0, Kernel(a.Size), Kernel(a.Size2)))),
        ["genImageSurfaceFirstOrder"] = new("genImageSurfaceFirstOrder", "Generate first-order surface", "size=宽, size2=高", a =>
            Generated(i => i.GenImageSurfaceFirstOrder("byte", 1d, 1d, 1d, 0d, 0d, Kernel(a.Size), Kernel(a.Size2)))),
        ["genImageSurfaceSecondOrder"] = new("genImageSurfaceSecondOrder", "Generate second-order surface", "size=宽, size2=高", a =>
            Generated(i => i.GenImageSurfaceSecondOrder("byte", 1d, 1d, 1d, 1d, 1d, 1d, 0d, 0d, Kernel(a.Size), Kernel(a.Size2)))),
        ["lutTrans"] = new("lutTrans", "LUT transform", "lutJson=256 项灰度映射（JSON 整数数组）", a => ApplyLut(a.Image, a.LutJson)),

        // ── 颜色通道（三通道图）─────────────────────────────────────
        ["rgb1ToGray"] = new("rgb1ToGray", "RGB1 to gray", "无参数", a => a.Image.Rgb1ToGray(), JlPixelKind.Color),
        ["rgb3ToGray"] = new("rgb3ToGray", "RGB3 to gray", "image2=绿色通道, third=蓝色通道", a => a.Image.Rgb3ToGray(a.Secondary, a.Third ?? throw new InvalidOperationException("rgb3ToGray 需要 image3 输入。"))),
        ["tileChannels"] = new("tileChannels", "Tile channels", "count=列数", a => a.Image.TileChannels(Math.Max(1, a.Count), "horizontal"), JlPixelKind.Color)
    };

    public static IReadOnlyDictionary<string, JlImageOperator> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlImageOperator Resolve(string key)
        => Table.TryGetValue(key, out var op)
            ? op
            : throw new InvalidOperationException(
                $"Unknown JLVision image operator '{key}'. Known operators: {string.Join(", ", Keys)}.");

    /// <summary>把 "a,b,c,..." 解析成卷积核；非法内容回落到一个 3x3 均值核。</summary>
    private static JlTuple FilterMask(string mode)
    {
        var parts = mode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 9 or 25 or 49)
        {
            var values = new double[parts.Length];
            var ok = true;
            for (var i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i])) { ok = false; break; }
            if (ok) return new JlTuple(values);
        }
        return new JlTuple(new[] { 1d / 9, 1d / 9, 1d / 9, 1d / 9, 1d / 9, 1d / 9, 1d / 9, 1d / 9, 1d / 9 });
    }

    /// <summary>实测 sobel_amp 只接受这几个取值：sum_abs / sum_sqrt / thin_sum_abs / x / y。</summary>
    private static string SobelMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "sum_sqrt" or "thin_sum_abs" or "x" or "y" => mode.Trim().ToLowerInvariant(),
        _ => "sum_abs"
    };

    /// <summary>生成器算子是 Void + 就地修改，这里统一包成"新建 → 生成 → 返回"。</summary>
    private static JlImage Generated(Action<JlImage> build)
    {
        var image = new JlImage();
        try { build(image); return image; }
        catch { image.Dispose(); throw; }
    }

    private static JlImage ApplyLut(JlImage image, string json)
    {
        double[] values;
        try { values = System.Text.Json.JsonSerializer.Deserialize<double[]>(json) ?? []; }
        catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException("灰度查找表必须为 256 项 JSON 数组。", ex); }
        if (values.Length != 256 || values.Any(v => !double.IsFinite(v) || v < 0 || v > 255 || v != Math.Truncate(v)))
            throw new InvalidOperationException("灰度查找表必须包含 256 个 0..255 范围内的整数。");
        using var tuple = new JlTuple(values.Select(v => (int)v).ToArray());
        return image.LutTrans(tuple);
    }

    /// <summary>核尺寸必须是正奇数；偶数会被钳到相邻奇数，避免原生算子报错。</summary>
    private static int Kernel(double value)
    {
        var size = (int)Math.Round(value);
        if (size < 1) size = 1;
        if (size % 2 == 0) size++;
        return Math.Clamp(size, 1, 511);
    }
}
