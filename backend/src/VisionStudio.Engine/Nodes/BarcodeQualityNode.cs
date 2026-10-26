using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 条码质量分级（recognition.barcodeQuality，2026-10-26 · 缺口分析 P1-15）。
///
/// **能力边界必须先说清楚**：完整的 ISO/IEC 15415（2D）/15416（1D）认证评级需要标准光源、规定光圈、
/// 校准的反射率基准，以及固定图形损伤、未使用纠错、网格非均匀性等参数。本节点只实现**其中三项**
/// 可由普通工业图直接测得的参数，并据此给出"指示性等级"：
///
/// | 参数 | 本节点做法 | ISO 阈值（15415） |
/// |---|---|---|
/// | 符号对比度 SC | ROI 内灰度 5%/95% 分位之差（抗孤立热点） | A≥70 B≥55 C≥40 D≥20 |
/// | 调制比 MOD | 各模块单元灰度偏离全局中值的程度，取最小值 | A≥0.50 B≥0.40 C≥0.30 D≥0.20 |
/// | 轴向非均匀性 AN | 左右半个符号与上下半个符号的均值差比 | A≤0.06 B≤0.08 C≤0.10 D≤0.12 |
///
/// 因此输出叫 `grade`（指示等级）而不是"符合 ISO 15415"；诊断里会写明覆盖了哪几项。
/// 需要认证级报告时仍应使用专用条码检测仪。
///
/// 单轴一维码（15416）的 SC 同样适用，但 MOD 语义不同；本节点按"模块网格"计算，对 1D 码请把网格配成
/// 沿条码方向的单元数，并在报告里注明这是近似。
/// </summary>
public sealed class BarcodeQualityNode : IVisionNodeExecutor
{
    public string Type => "recognition.barcodeQuality";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image").Mat;
        using var gray = RecognitionImageHelpers.ToGray8(source);
        var bounds = ResolveBounds(context, gray);
        var region = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        if (region.Width < 8 || region.Height < 8)
            throw new InvalidOperationException($"recognition.barcodeQuality requires an ROI of at least 8x8 px; got {region.Width}x{region.Height}.");

        // ISO 15415 的 SC = Rmax − Rmin（符号区内极值）；ROI 里若有镜面反光点，应先把 ROI 收窄。
        double rmin, rmax;
        using (var roiMat = new Mat(gray, region))
        {
            Cv2.MinMaxLoc(roiMat, out rmin, out rmax);
        }
        var symbolContrast = rmax - rmin;
        if (!double.IsFinite(symbolContrast) || symbolContrast < 5)
            throw new InvalidOperationException(
                $"recognition.barcodeQuality found symbol contrast {symbolContrast:0.#} in the ROI; there is no readable symbol (needs at least 5 gray levels).");

        var gridMode = node.GetString("gridMode", "modules").Trim().ToLowerInvariant();
        var (rows, cols) = ResolveGrid(node, gridMode, region);
        var means = CellMeans(gray, region, rows, cols);
        var mid = (rmin + rmax) / 2d;

        var darkCells = means.Count(mean => mean < mid);
        var darkFraction = darkCells / (double)means.Count;
        var detail = new Dictionary<string, object?>
        {
            ["gridMode"] = gridMode,
            ["rows"] = rows,
            ["cols"] = cols,
            ["darkFraction"] = darkFraction,
            ["coveredParameters"] = gridMode == "whole" ? "SC" : "SC,MOD,AN"
        };

        var outputs = new Dictionary<string, VisionValue>
        {
            ["symbolContrast"] = VisionValue.Double(symbolContrast),
            ["darkFraction"] = VisionValue.Double(darkFraction)
        };
        var grades = new List<string> { GradeContrast(symbolContrast) };
        double? modulation = null;
        double? axial = null;

        if (gridMode != "whole")
        {
            // MOD 近似：每个单元偏离全局中值的程度（中值上的单元没有调制）。取最差单元。
            var half = symbolContrast / 2d;
            modulation = means.Min(mean => Math.Clamp(Math.Abs(mean - mid) / half, 0d, 1d));
            // AN 按 ISO 15415 定义：左右/上下半区均值差比的较大者。
            var left = RangeMean(means, cols, 0, rows, 0, cols / 2);
            var right = RangeMean(means, cols, 0, rows, cols / 2, cols);
            var top = RangeMean(means, cols, 0, rows / 2, 0, cols);
            var bottom = RangeMean(means, cols, rows / 2, rows, 0, cols);
            axial = Math.Max(Ratio(left, right), Ratio(top, bottom));
            outputs["modulation"] = VisionValue.Double(modulation.Value);
            outputs["axialNonUniformity"] = VisionValue.Double(axial.Value);
            grades.Add(GradeModulation(modulation.Value));
            grades.Add(GradeAxial(axial.Value));
            detail["modulation"] = modulation;
            detail["axialNonUniformity"] = axial;
        }

        // 解不出来就是不合格：ISO 里解码失败直接 F，不看其他参数。
        var decodeOk = !context.TryGetValue("decodeOk", out var decodeValue) || decodeValue.Require<bool>("decodeOk");
        detail["decodeOk"] = decodeOk;
        var grade = decodeOk ? Worst(grades) : "F";
        var numericGrade = NumericGrade(grade);

        if (node.GetBool("enableGrade", true))
        {
            outputs["grade"] = VisionValue.String(grade);
            outputs["numericGrade"] = VisionValue.Double(numericGrade);
            var minGrade = node.GetString("minGrade", "C").Trim().ToUpperInvariant();
            if (minGrade is not ("A" or "B" or "C" or "D"))
                throw new InvalidOperationException("recognition.barcodeQuality minGrade must be A, B, C or D.");
            outputs["pass"] = VisionValue.Boolean(numericGrade >= NumericGrade(minGrade));
            detail["minGrade"] = minGrade;
        }

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            [.. new (string Key, object? Value)[]
            {
                ("symbolContrast", symbolContrast),
                ("modulation", modulation),
                ("axialNonUniformity", axial),
                ("grade", grade),
                ("numericGrade", numericGrade),
                ("coveredParameters", detail["coveredParameters"]),
                ("standard", "ISO/IEC 15415 partial (SC/MOD/AN) - indicator grade, not a certified report")
            }, .. detail.Select(pair => (pair.Key, pair.Value))]);
        outputs["diagnostics"] = VisionValue.String(diagnostics);

        var summary = new Dictionary<string, object?>(detail)
        {
            ["symbolContrast"] = symbolContrast,
            ["modulation"] = modulation,
            ["axialNonUniformity"] = axial,
            ["grade"] = grade,
            ["numericGrade"] = numericGrade,
            ["diagnostics"] = diagnostics
        };
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }

    /// <summary>ROI 存在则取包围盒并裁剪到图像内，否则整幅图。</summary>
    private static Rect ResolveBounds(NodeExecutionContext context, Mat gray, string port = "roi")
    {
        if (!context.TryGetValue(port, out var roiValue)) return new Rect(0, 0, gray.Width, gray.Height);
        var roi = roiValue.Require<VisionRoi>(port);
        double left, top, right, bottom;
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                left = roi.X ?? 0; top = roi.Y ?? 0;
                var width = roi.Width ?? 0; var height = roi.Height ?? 0;
                if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                    throw new InvalidOperationException("recognition.barcodeQuality rectangle ROI needs positive width/height.");
                right = left + width; bottom = top + height;
                break;
            case VisionRoiType.Circle:
                var radius = roi.Radius ?? 0;
                if (!double.IsFinite(radius) || radius <= 0)
                    throw new InvalidOperationException("recognition.barcodeQuality circle ROI needs a positive radius.");
                left = (roi.X ?? 0) - radius; top = (roi.Y ?? 0) - radius;
                right = (roi.X ?? 0) + radius; bottom = (roi.Y ?? 0) + radius;
                break;
            case VisionRoiType.Polygon:
            case VisionRoiType.Brush:
                var points = roi.Type == VisionRoiType.Polygon
                    ? roi.Points ?? []
                    : (roi.Strokes ?? []).SelectMany(stroke => stroke.Points ?? []).ToArray();
                if (points.Count == 0)
                    throw new InvalidOperationException("recognition.barcodeQuality ROI has no points.");
                left = points.Min(point => point.X); top = points.Min(point => point.Y);
                right = points.Max(point => point.X); bottom = points.Max(point => point.Y);
                break;
            default:
                throw new InvalidOperationException($"recognition.barcodeQuality does not support ROI type {roi.Type}.");
        }

        var x = (int)Math.Floor(Math.Max(0, left));
        var y = (int)Math.Floor(Math.Max(0, top));
        var x2 = (int)Math.Ceiling(Math.Min(gray.Width, right));
        var y2 = (int)Math.Ceiling(Math.Min(gray.Height, bottom));
        if (x2 <= x || y2 <= y)
            throw new InvalidOperationException("recognition.barcodeQuality ROI does not overlap the image.");
        return new Rect(x, y, x2 - x, y2 - y);
    }

    /// <summary>
    /// 模块网格：modules 用给定行列数；cellSize 由单元像素尺寸推算；whole 只算 SC。
    /// 1D 码也可以走 cellSize（行数配 1）。
    /// </summary>
    private static (int Rows, int Cols) ResolveGrid(NodeDefinition node, string gridMode, Rect region)
    {
        switch (gridMode)
        {
            case "whole":
                return (1, 1);
            case "modules":
                var rows = node.GetInt("rows", 20);
                var cols = node.GetInt("cols", 20);
                if (rows is < 1 or > 256 || cols is < 1 or > 256)
                    throw new InvalidOperationException("recognition.barcodeQuality rows/cols must be within 1..256.");
                return (Math.Min(rows, region.Height), Math.Min(cols, region.Width));
            case "cellsize":
                var cellSize = node.GetDouble("cellSizePx", 4);
                if (!double.IsFinite(cellSize) || cellSize is < 1 or > 512)
                    throw new InvalidOperationException("recognition.barcodeQuality cellSizePx must be within 1..512.");
                return (
                    Math.Clamp((int)Math.Round(region.Height / cellSize), 1, 256),
                    Math.Clamp((int)Math.Round(region.Width / cellSize), 1, 256));
            default:
                throw new InvalidOperationException("recognition.barcodeQuality gridMode must be modules, cellSize or whole.");
        }
    }

    /// <summary>
    /// 单遍桶统计每个单元的平均灰度：只扫 ROI 一次，把像素累加到所属单元。
    /// 不用积分图/逐单元 Cv2.Mean，避免上千次分配，也不依赖具体 OpenCV 重载。
    /// </summary>
    private static List<double> CellMeans(Mat gray, Rect region, int rows, int cols)
    {
        var sums = new double[rows * cols];
        var counts = new int[rows * cols];
        var indexer = gray.GetGenericIndexer<byte>();
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var row = Math.Min(rows - 1, (y - region.Y) * rows / region.Height);
            for (var x = region.X; x < region.Right; x++)
            {
                var col = Math.Min(cols - 1, (x - region.X) * cols / region.Width);
                var slot = row * cols + col;
                sums[slot] += indexer[y, x];
                counts[slot]++;
            }
        }

        var means = new List<double>(rows * cols);
        for (var slot = 0; slot < sums.Length; slot++)
            means.Add(counts[slot] == 0 ? 0 : sums[slot] / counts[slot]);
        return means;
    }

    /// <summary>按行列区间取单元均值（左右/上下半区）。</summary>
    private static double RangeMean(List<double> means, int cols, int rowStart, int rowEnd, int colStart, int colEnd)
    {
        double sum = 0; var count = 0;
        for (var row = rowStart; row < rowEnd; row++)
            for (var col = colStart; col < colEnd; col++)
            {
                sum += means[row * cols + col];
                count++;
            }
        return count == 0 ? 0 : sum / count;
    }

    private static double Ratio(double first, double second)
    {
        var denominator = first + second;
        return denominator <= 0 ? 0 : Math.Abs(first - second) / denominator;
    }

    private static string GradeContrast(double sc) => sc >= 70 ? "A" : sc >= 55 ? "B" : sc >= 40 ? "C" : sc >= 20 ? "D" : "F";

    private static string GradeModulation(double mod) => mod >= 0.50 ? "A" : mod >= 0.40 ? "B" : mod >= 0.30 ? "C" : mod >= 0.20 ? "D" : "F";

    private static string GradeAxial(double an) => an <= 0.06 ? "A" : an <= 0.08 ? "B" : an <= 0.10 ? "C" : an <= 0.12 ? "D" : "F";

    private static string Worst(IEnumerable<string> grades) => grades.OrderByDescending(NumericGrade).Last();

    private static double NumericGrade(string grade) => grade switch
    {
        "A" => 4,
        "B" => 3,
        "C" => 2,
        "D" => 1,
        _ => 0
    };
}
