using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 轮廓剖面的公共算量：重采样、主轴投影、高斯滤波、代数圆拟合。
///
/// 刻意不依赖 JLVision 原生调用：曲率/滤波/粗糙度都是**逐样本**运算，一个轮廓几千点，
/// 每点一次原生拟合会把单节点耗时推到秒级；这里是纯托管实现，行为可预测、可单测。
/// </summary>
internal static class ProfileMath
{
    /// <summary>等弧长重采样；closed 时首尾相连。budget 超限直接失败，不静默截断。</summary>
    public static VisionPoint2D[] Resample(VisionPoint2D[] points, bool closed, double step, int budget)
    {
        if (!double.IsFinite(step) || step <= 0) throw new InvalidOperationException("Profile sample step must be a positive finite value.");
        var path = closed && points.Length > 1 ? points.Append(points[0]).ToArray() : points;
        var cumulative = new double[path.Length];
        for (var i = 1; i < path.Length; i++) cumulative[i] = cumulative[i - 1] + MeasurementMath.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
        var length = cumulative[^1];
        if (length < 1e-9) throw new InvalidOperationException("Profile length is degenerate; a nonzero polyline is required.");
        var intervals = Math.Max(1, Math.Ceiling(length / step));
        var count = (int)Math.Max(2, intervals + (closed ? 0 : 1));
        if (count > budget) throw new InvalidOperationException($"Profile sample budget exceeded ({budget}); increase sampleStep.");
        var result = new VisionPoint2D[count];
        var segment = 1;
        for (var i = 0; i < count; i++)
        {
            var distance = length * i / (closed ? count : count - 1);
            while (segment < path.Length - 1 && cumulative[segment] < distance) segment++;
            var span = cumulative[segment] - cumulative[segment - 1];
            var fraction = span > 1e-12 ? (distance - cumulative[segment - 1]) / span : 0;
            result[i] = new VisionPoint2D(
                path[segment - 1].X + fraction * (path[segment].X - path[segment - 1].X),
                path[segment - 1].Y + fraction * (path[segment].Y - path[segment - 1].Y));
        }
        return result;
    }

    /// <summary>把折线投影到最小二乘主轴：u = 沿轴位置（排序后），z = 有符号法向偏差。</summary>
    public static (double[] U, double[] Z, VisionPoint2D Origin, double Ux, double Uy) Project(VisionPoint2D[] points)
    {
        if (points.Length < 2) throw new InvalidOperationException("A profile needs at least two points.");
        var cx = points.Average(p => p.X);
        var cy = points.Average(p => p.Y);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in points)
        {
            var dx = p.X - cx; var dy = p.Y - cy;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        // 主轴方向：协方差矩阵最大特征向量（闭式解，避免引入线性代数库）
        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var ux = Math.Cos(angle); var uy = Math.Sin(angle);
        var pairs = points.Select(p => ((p.X - cx) * ux + (p.Y - cy) * uy, -(p.X - cx) * uy + (p.Y - cy) * ux))
            .OrderBy(v => v.Item1).ToArray();
        return (pairs.Select(v => v.Item1).ToArray(), pairs.Select(v => v.Item2).ToArray(),
            new VisionPoint2D(cx, cy), ux, uy);
    }

    /// <summary>主轴坐标反投影回像素坐标（z 沿左法向 (-uy, ux)）。</summary>
    public static VisionPoint2D Unproject(VisionPoint2D origin, double ux, double uy, double u, double z)
        => new(origin.X + ux * u - uy * z, origin.Y + uy * u + ux * z);

    /// <summary>
    /// 按**轴向位置**等距重采样：z = f(u)。
    ///
    /// 这里刻意不用等弧长采样：粗糙度/波纹度的截止波长是横向长度单位，只有 u 等距才能直接卷积；
    /// 等弧长采样在陡坡处 u 间距变大，会把同一波长摊平，实测 Ra 会凭空缩小一半。
    /// </summary>
    public static (double[] U, double[] Z) Uniformize(double[] sortedU, double[] z, double step, int budget)
    {
        if (!double.IsFinite(step) || step <= 0) throw new InvalidOperationException("Profile sample step must be a positive finite value.");
        var length = sortedU[^1] - sortedU[0];
        if (length < 1e-9) throw new InvalidOperationException("Profile axial length is degenerate; a nonzero polyline is required.");
        var requestedCount = Math.Max(2, Math.Ceiling(length / step) + 1);
        if (!double.IsFinite(requestedCount) || requestedCount > budget)
            throw new InvalidOperationException($"Profile sample budget exceeded ({budget}); increase sampleStep.");
        var count = (int)requestedCount;
        var resultU = new double[count]; var resultZ = new double[count];
        var segment = 1;
        for (var i = 0; i < count; i++)
        {
            var target = sortedU[0] + length * i / (count - 1);
            while (segment < sortedU.Length - 1 && sortedU[segment] < target) segment++;
            var span = sortedU[segment] - sortedU[segment - 1];
            var fraction = span > 1e-12 ? (target - sortedU[segment - 1]) / span : 0;
            resultU[i] = target;
            resultZ[i] = z[segment - 1] + fraction * (z[segment] - z[segment - 1]);
        }
        return (resultU, resultZ);
    }

    /// <summary>Measure the largest projected input gap before interpolation.</summary>
    public static (double[] U, double[] Z, VisionPoint2D Origin, double Ux, double Uy, double InputGap) ProjectWithGap(
        VisionPoint2D[] points)
    {
        var (u, z, origin, ux, uy) = Project(points);
        var inputGap = 0d;
        for (var i = 1; i < u.Length; i++) inputGap = Math.Max(inputGap, u[i] - u[i - 1]);
        return (u, z, origin, ux, uy, inputGap);
    }

    public static int GaussianRadius(double cutoff, double step)
        => (int)Math.Min(int.MaxValue, Math.Ceiling(GaussianSigmaPerCutoff / step * cutoff * 3));

    private static readonly double GaussianSigmaPerCutoff = Math.Sqrt(Math.Log(2) / Math.PI) / Math.Sqrt(2 * Math.PI);

    public static NodeExecutorResult? CheckInputGap(NodeDefinition node, double inputGap, int pointCount,
        double sampleStep = 0, IReadOnlyList<double>? axis = null)
    {
        var maximum = node.GetDouble("maxInputGap", 0);
        if (!double.IsFinite(maximum) || maximum < 0)
            throw new InvalidOperationException($"{node.Type} maxInputGap must be finite and >= 0.");
        // 0 不再等于“关闭断口检查”：默认只允许 3 个采样步长的缺口，否则重采样会把
        // 实际缺失的轮廓段线性桥接，凭空造出 waviness/residual。显式阈值才允许跨更大的洞。
        var inferred = maximum == 0 && sampleStep > 0;
        var limit = inferred ? 3 * sampleStep : maximum;
        if (limit <= 0 || inputGap <= limit + 1e-9) return null;
        var gapIndices = new List<int>();
        if (axis is not null)
        {
            for (var index = 1; index < axis.Count; index++)
                if (axis[index] - axis[index - 1] > limit + 1e-9) gapIndices.Add(index - 1);
        }
        var gapIndicesJson = System.Text.Json.JsonSerializer.Serialize(gapIndices.Take(64));
        var diagnostics = CoordinateValidation.Diagnostics(node.Type, ("found", false), ("sampleCount", pointCount),
            ("reason", "profile-input-gap-exceeded"), ("maxObservedInputGap", inputGap), ("maxInputGap", limit),
            ("gapLimitInferred", inferred), ("gapCount", gapIndices.Count), ("gapIndicesJson", gapIndicesJson));
        return new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(false), ["sampleCount"] = VisionValue.Integer(pointCount),
            ["maxObservedInputGap"] = VisionValue.Double(inputGap),
            ["gapIndicesJson"] = VisionValue.String(gapIndicesJson),
            ["diagnostics"] = VisionValue.String(diagnostics)
        }, new Dictionary<string, object?>
        {
            ["found"] = false, ["sampleCount"] = pointCount, ["reason"] = "profile-input-gap-exceeded",
            ["maxObservedInputGap"] = inputGap, ["maxInputGap"] = limit, ["gapLimitInferred"] = inferred,
            ["gapCount"] = gapIndices.Count, ["gapIndicesJson"] = gapIndicesJson,
            ["unit"] = "px", ["diagnostics"] = diagnostics
        });
    }

    /// <summary>
    /// ISO 16610-21 高斯权函数：s(x)=1/(αλc)·exp(-π(x/(αλc))²)，α=√(ln2/π)。
    /// 与标准正态对照可得 σ = αλc/√(2π) ≈ 0.1874λc，这里直接用该 σ 生成核。
    /// </summary>
    public static double[] Gaussian(double[] values, double cutoff, double step, bool reflect, CancellationToken ct, out int edge)
    {
        if (cutoff <= 0) throw new InvalidOperationException("Cutoff wavelength must be positive for Gaussian filtering.");
        var samplesPerSigma = GaussianSigmaPerCutoff / step * cutoff;
        edge = GaussianRadius(cutoff, step);
        if (edge > values.Length / 2) edge = values.Length / 4;
        var kernel = new double[2 * edge + 1];
        var total = 0.0;
        for (var i = -edge; i <= edge; i++)
        {
            var weight = Math.Exp(-0.5 * (i / samplesPerSigma) * (i / samplesPerSigma));
            kernel[i + edge] = weight; total += weight;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= total;
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sum = 0.0;
            for (var k = -edge; k <= edge; k++)
            {
                var index = i + k;
                if (reflect)
                {
                    if (index < 0) index = -index;
                    if (index >= values.Length) index = 2 * values.Length - 2 - index;
                    index = Math.Clamp(index, 0, values.Length - 1);
                }
                else index = Math.Clamp(index, 0, values.Length - 1);
                sum += kernel[k + edge] * values[index];
            }
            result[i] = sum;
        }
        return result;
    }

    /// <summary>Kåsa 代数圆拟合（先中心化再解 2×2 正规方程），逐样本调用也够快。</summary>
    public static bool TryFitCircle(IReadOnlyList<VisionPoint2D> points, out VisionCircle circle)
    {
        circle = default;
        if (points.Count < 3) return false;
        var cx = points.Average(p => p.X); var cy = points.Average(p => p.Y);
        double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
        foreach (var p in points)
        {
            var u = p.X - cx; var v = p.Y - cy;
            suu += u * u; svv += v * v; suv += u * v;
            suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
        }
        var a11 = suu; var a12 = suv; var a22 = svv;
        var b1 = 0.5 * (suuu + suvv); var b2 = 0.5 * (svvv + svuu);
        var det = a11 * a22 - a12 * a12;
        var spread = suu + svv;
        // det has px^4 units, so an absolute epsilon lets nearly straight short arcs through and
        // produces a finite but meaningless radius. Use the same scale-free conditioning test as
        // SubpixelNodes' robust circle fit.
        const double minimumNormalizedSpread = 2e-3;
        if (!double.IsFinite(spread) || !double.IsFinite(det) || spread < 1e-8
            || det <= spread * spread * minimumNormalizedSpread) return false;
        var uc = (b1 * a22 - b2 * a12) / det;
        var vc = (a11 * b2 - a12 * b1) / det;
        var radius = Math.Sqrt(uc * uc + vc * vc + (suu + svv) / points.Count);
        if (!double.IsFinite(radius) || radius <= 0) return false;
        circle = new VisionCircle(cx + uc, cy + vc, radius);
        return true;
    }

    public static VisionPoint2D[] SelectContour(NodeExecutionContext context, NodeDefinition node, bool closed, string port = "contours", string parameter = "index")
        => ToleranceGeometry.Select(context, node, port, parameter, closed);
}
