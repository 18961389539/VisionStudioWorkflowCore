using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 数值列表组装（value.numberList，2026-10-26 · 缺口分析 P1-8）。
///
/// 背景：目录里只有 DetectionList 一种集合类型，导致"多个测量值"无法在图上传递，
/// 统计/趋势类节点因此根本无法存在。本节点把最多 8 个标量汇成一个 NumberList。
///
/// 语义：按 a..h 的端口顺序收集**已接线**的值；未接线视为"这一路没有值"，不补 0
/// （补 0 会让均值/极值静默失真，是统计类节点最危险的错误）。一个都没接则明确失败。
/// </summary>
public sealed class ValueNumberListNode : IVisionNodeExecutor
{
    public string Type => "value.numberList";

    /// <summary>固定 8 路输入：足够拼一次多站测量；再多就该用检测列表提取。</summary>
    private static readonly string[] Ports = ["a", "b", "c", "d", "e", "f", "g", "h"];

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var values = new List<double>(Ports.Length);
        foreach (var port in Ports)
        {
            if (!context.TryGetValue(port, out _)) continue;
            var value = context.RequireNumber(port);
            if (!double.IsFinite(value))
                throw new InvalidOperationException($"value.numberList input '{port}' is not finite; statistics over NaN/Infinity would be meaningless.");
            values.Add(value);
        }
        if (values.Count == 0)
            throw new InvalidOperationException("value.numberList requires at least one connected numeric input (a..h); an empty list would silently produce meaningless statistics.");

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("count", values.Count),
            ("values", string.Join(",", values.Take(16).Select(value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["numberList"] = VisionValue.NumberList(values),
                ["count"] = VisionValue.Integer(values.Count),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?> { ["count"] = values.Count, ["values"] = values, ["diagnostics"] = diagnostics }));
    }
}

/// <summary>
/// 从检测列表提取数值列（collection.detectionNumbers）。
/// 用于"批次内各目标的置信度/面积/位置分布"这类统计：先取出一列，再交给 value.statistics。
/// </summary>
public sealed class DetectionNumbersNode : IVisionNodeExecutor
{
    public string Type => "collection.detectionNumbers";

    private static readonly string[] Fields =
        ["x", "y", "width", "height", "area", "perimeter", "circularity", "confidence", "classId", "index"];

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var field = node.GetString("field", "confidence").Trim().ToLowerInvariant();
        if (!Fields.Contains(field))
            throw new InvalidOperationException($"collection.detectionNumbers field '{field}' is not supported; use {string.Join(", ", Fields)}.");

        var detections = DetectionListInput.Read(context, cancellationToken);
        var values = new List<double>(detections.Count);
        var skipped = 0;
        foreach (var detection in detections)
        {
            double? value = field switch
            {
                "x" => detection.X,
                "y" => detection.Y,
                "width" => detection.Width,
                "height" => detection.Height,
                "area" => detection.Area,
                "perimeter" => detection.Perimeter,
                "circularity" => detection.Circularity,
                "confidence" => detection.Confidence,
                "classId" => detection.ClassId,
                _ => detection.Index
            };
            // 可选字段（confidence/classId）为 null 时计数跳过，而不是当 0 参与统计。
            if (value is not { } number) { skipped++; continue; }
            if (!double.IsFinite(number))
                throw new InvalidOperationException($"collection.detectionNumbers found a non-finite '{field}' on detection #{detection.Index}.");
            values.Add(number);
        }

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("field", field),
            ("count", values.Count),
            ("skipped", skipped),
            ("detections", detections.Count));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["numberList"] = VisionValue.NumberList(values),
                ["count"] = VisionValue.Integer(values.Count),
                ["skipped"] = VisionValue.Integer(skipped),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?> { ["field"] = field, ["count"] = values.Count, ["skipped"] = skipped, ["diagnostics"] = diagnostics }));
    }
}

/// <summary>
/// 数值统计（value.statistics）。NumberList → 均值/标准差/极值/中位数/极差/合计，可选 CP/CPK。
///
/// 口径必须写清楚（统计节点最容易"看起来对"）：
/// - 标准差默认用**样本**口径（除以 n-1），可切换为总体口径（除以 n）；
/// - 中位数在偶数个样本时取中间两值平均；
/// - CP = (USL-LSL)/(6σ)，CPK = min(USL-mean, mean-LSL)/(3σ)，σ 与上面选择的偏差口径一致；
/// - 只有启用规格上下限时才输出 cp/cpk/pass，避免下游把"没有规格"误读成"合格"。
/// </summary>
public sealed class StatisticsNode : IVisionNodeExecutor
{
    public string Type => "value.statistics";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var raw = context.RequireValue("numberList");
        if (raw.Type != VisionDataType.NumberList || raw.Value is not IReadOnlyList<double> values)
            throw new InvalidOperationException($"value.statistics requires a NumberList input; got {raw.Type}.");
        if (values.Count == 0)
            throw new InvalidOperationException("value.statistics received an empty NumberList; there is nothing to summarize.");
        foreach (var value in values)
            if (!double.IsFinite(value))
                throw new InvalidOperationException("value.statistics received a non-finite value (NaN/Infinity).");

        var sample = node.GetString("deviation", "sample").Trim().ToLowerInvariant() switch
        {
            "sample" => true,
            "population" => false,
            _ => throw new InvalidOperationException("value.statistics deviation must be sample or population.")
        };
        var enableLimits = node.GetBool("enableLimits", false);
        var lsl = node.GetDouble("lsl", 0);
        var usl = node.GetDouble("usl", 0);
        if (enableLimits)
        {
            if (!double.IsFinite(lsl) || !double.IsFinite(usl))
                throw new InvalidOperationException("value.statistics limits must be finite.");
            if (lsl >= usl)
                throw new InvalidOperationException($"value.statistics requires LSL < USL; got {lsl} >= {usl}.");
        }

        var count = values.Count;
        var sum = values.Sum();
        var mean = sum / count;
        double variance = 0;
        foreach (var value in values) variance += (value - mean) * (value - mean);
        // n=1 时样本口径没有自由度：明确退化为 0，并在诊断里标注，避免除零得到 NaN。
        var divisor = sample ? count - 1 : count;
        var std = divisor > 0 ? Math.Sqrt(variance / divisor) : 0d;
        var sorted = values.OrderBy(value => value).ToArray();
        var median = count % 2 == 1
            ? sorted[count / 2]
            : (sorted[count / 2 - 1] + sorted[count / 2]) / 2d;

        var outputs = new Dictionary<string, VisionValue>
        {
            ["count"] = VisionValue.Integer(count),
            ["mean"] = VisionValue.Double(mean),
            ["std"] = VisionValue.Double(std),
            ["min"] = VisionValue.Double(sorted[0]),
            ["max"] = VisionValue.Double(sorted[^1]),
            ["median"] = VisionValue.Double(median),
            ["range"] = VisionValue.Double(sorted[^1] - sorted[0]),
            ["sum"] = VisionValue.Double(sum)
        };
        var summary = new Dictionary<string, object?>
        {
            ["count"] = count,
            ["mean"] = mean,
            ["std"] = std,
            ["min"] = sorted[0],
            ["max"] = sorted[^1],
            ["median"] = median,
            ["deviation"] = sample ? "sample" : "population"
        };

        var detail = new List<(string Key, object? Value)>
        {
            ("count", count), ("mean", mean), ("std", std), ("min", sorted[0]), ("max", sorted[^1]),
            ("median", median), ("deviation", sample ? "sample" : "population"), ("limitsEnabled", enableLimits)
        };

        if (enableLimits)
        {
            // σ=0 时 CP/CPK 数学上无穷大；不输出数值，改为明确失败——无穷大下游无法判断。
            if (std <= 0)
                throw new InvalidOperationException("value.statistics cannot compute CP/CPK because the standard deviation is 0 (all samples identical).");
            var cp = (usl - lsl) / (6d * std);
            var cpk = Math.Min(usl - mean, mean - lsl) / (3d * std);
            var pass = mean >= lsl && mean <= usl;
            outputs["cp"] = VisionValue.Double(cp);
            outputs["cpk"] = VisionValue.Double(cpk);
            outputs["pass"] = VisionValue.Boolean(pass);
            summary["cp"] = cp;
            summary["cpk"] = cpk;
            summary["pass"] = pass;
            detail.Add(("lsl", lsl));
            detail.Add(("usl", usl));
            detail.Add(("cp", cp));
            detail.Add(("cpk", cpk));
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, [.. detail]);
        outputs["diagnostics"] = VisionValue.String(diagnostics);
        summary["diagnostics"] = diagnostics;
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }
}

/// <summary>
/// 检测列表 → 点集（collection.detectionPoints）。取各目标圆心作为二维点，
/// 供 geometry.fitFromPoints 之类的拟合节点使用。
/// </summary>
public sealed class DetectionPointsNode : IVisionNodeExecutor
{
    public string Type => "collection.detectionPoints";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var detections = DetectionListInput.Read(context, cancellationToken);
        var points = detections.Select(detection => new VisionPoint2D(detection.X, detection.Y)).ToArray();
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("count", points.Length), ("source", "detection center"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pointList"] = VisionValue.PointList(points),
                ["count"] = VisionValue.Integer(points.Length),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?> { ["count"] = points.Length, ["diagnostics"] = diagnostics }));
    }
}

/// <summary>
/// 点集拟合（geometry.fitFromPoints）。点集 → 直线或圆。
///
/// 算法口径：
/// - 直线：总体最小二乘（对质心去均值后取协方差主方向），返回沿主方向的两个端点；
///   与"以 x 为自变量做最小二乘"不同，对竖直方向的线同样稳定。
/// - 圆：Kåsa 代数拟合求圆心初值，再用平均半径收尾（比直接取 Kåsa 半径更稳）。
/// - residualPx 为各点到拟合几何的正交距离均方根，作为拟合质量参考。
/// </summary>
public sealed class FitFromPointsNode : IVisionNodeExecutor
{
    public string Type => "geometry.fitFromPoints";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var raw = context.RequireValue("pointList");
        if (raw.Type != VisionDataType.PointList || raw.Value is not IReadOnlyList<VisionPoint2D> points)
            throw new InvalidOperationException($"geometry.fitFromPoints requires a PointList input; got {raw.Type}.");

        var operation = node.GetString("operation", "line").Trim().ToLowerInvariant();
        if (operation is not ("line" or "circle"))
            throw new InvalidOperationException("geometry.fitFromPoints operation must be line or circle.");
        if (operation == "line" && points.Count < 2)
            throw new InvalidOperationException($"geometry.fitFromPoints line fitting requires at least 2 points; got {points.Count}.");
        if (operation == "circle" && points.Count < 3)
            throw new InvalidOperationException($"geometry.fitFromPoints circle fitting requires at least 3 points; got {points.Count}.");
        foreach (var point in points)
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new InvalidOperationException("geometry.fitFromPoints received a non-finite point.");

        var outputs = new Dictionary<string, VisionValue>();
        var summary = new Dictionary<string, object?> { ["operation"] = operation, ["pointCount"] = points.Count };
        double residual;

        if (operation == "line")
        {
            var meanX = points.Average(point => point.X);
            var meanY = points.Average(point => point.Y);
            double sxx = 0, syy = 0, sxy = 0;
            foreach (var point in points)
            {
                var dx = point.X - meanX;
                var dy = point.Y - meanY;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            // 主方向 = 协方差矩阵最大特征值对应的特征向量。
            var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var dirX = Math.Cos(angle);
            var dirY = Math.Sin(angle);
            // 端点按投影长度覆盖全部点，便于下游当线段使用。
            double minT = double.MaxValue, maxT = double.MinValue;
            foreach (var point in points)
            {
                var t = (point.X - meanX) * dirX + (point.Y - meanY) * dirY;
                minT = Math.Min(minT, t);
                maxT = Math.Max(maxT, t);
            }
            var line = new VisionLine2D(
                new VisionPoint2D(meanX + dirX * minT, meanY + dirY * minT),
                new VisionPoint2D(meanX + dirX * maxT, meanY + dirY * maxT));
            residual = RmsDistance(points, point => Math.Abs((point.X - meanX) * -dirY + (point.Y - meanY) * dirX));
            outputs["line"] = VisionValue.Line(line);
            summary["angleDeg"] = line.AngleDeg;
            summary["length"] = line.Length;
        }
        else
        {
            // Kåsa 代数拟合：解 [2x 2y 1] * [cx cy c] = x²+y²。
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
            foreach (var point in points)
            {
                var z = point.X * point.X + point.Y * point.Y;
                sxx += point.X * point.X; sxy += point.X * point.Y; syy += point.Y * point.Y;
                sx += point.X; sy += point.Y; sz += z;
                sxz += point.X * z; syz += point.Y * z;
            }
            var a = new double[3, 3] { { 2 * sxx, 2 * sxy, sx }, { 2 * sxy, 2 * syy, sy }, { 2 * sx, 2 * sy, points.Count } };
            var b = new[] { sxz, syz, sz };
            var solution = Solve3By3(a, b) ?? throw new InvalidOperationException("geometry.fitFromPoints circle fit is degenerate (points are collinear).");
            var centerX = solution[0];
            var centerY = solution[1];
            var radius = points.Average(point => double.Hypot(point.X - centerX, point.Y - centerY));
            residual = RmsDistance(points, point => Math.Abs(double.Hypot(point.X - centerX, point.Y - centerY) - radius));
            outputs["circle"] = VisionValue.Circle(new VisionCircle(centerX, centerY, radius));
            summary["centerX"] = centerX;
            summary["centerY"] = centerY;
            summary["radius"] = radius;
        }

        if (!double.IsFinite(residual))
            throw new InvalidOperationException("geometry.fitFromPoints produced a non-finite residual; the point set is degenerate.");
        outputs["residualPx"] = VisionValue.Double(residual);
        outputs["pointCount"] = VisionValue.Integer(points.Count);
        var diagnostics = CoordinateValidation.Diagnostics(Type,
            ("operation", operation), ("pointCount", points.Count), ("residualPx", residual));
        outputs["diagnostics"] = VisionValue.String(diagnostics);
        summary["residualPx"] = residual;
        summary["diagnostics"] = diagnostics;
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }

    private static double RmsDistance(IReadOnlyList<VisionPoint2D> points, Func<VisionPoint2D, double> distance)
    {
        var sum = 0d;
        foreach (var point in points) sum += Math.Pow(distance(point), 2);
        return Math.Sqrt(sum / points.Count);
    }

    /// <summary>3×3 线性方程组（Cramer 法则）；行列式接近 0 时返回 null 表示退化。</summary>
    private static double[]? Solve3By3(double[,] matrix, double[] rhs)
    {
        double Det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
            - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        var determinant = Det(matrix);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12) return null;
        var result = new double[3];
        for (var column = 0; column < 3; column++)
        {
            var replaced = (double[,])matrix.Clone();
            for (var row = 0; row < 3; row++) replaced[row, column] = rhs[row];
            result[column] = Det(replaced) / determinant;
        }
        return result;
    }
}
