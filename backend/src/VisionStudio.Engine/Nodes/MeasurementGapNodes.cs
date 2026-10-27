using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

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
        var count = (int)Math.Max(2, Math.Ceiling(length / step) + 1);
        if (count > budget) throw new InvalidOperationException($"Profile sample budget exceeded ({budget}); increase sampleStep.");
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

    /// <summary>最小二乘主轴 + 轴向等距重采样，供滤波/粗糙度直接使用。</summary>
    public static (double[] U, double[] Z, VisionPoint2D Origin, double Ux, double Uy) AxialProfile(
        VisionPoint2D[] points, double step, int budget)
    {
        var (u, z, origin, ux, uy) = Project(points);
        var (uniformU, uniformZ) = Uniformize(u, z, step, budget);
        return (uniformU, uniformZ, origin, ux, uy);
    }

    /// <summary>
    /// ISO 16610-21 高斯权函数：s(x)=1/(αλc)·exp(-π(x/(αλc))²)，α=√(ln2/π)。
    /// 与标准正态对照可得 σ = αλc/√(2π) ≈ 0.1874λc，这里直接用该 σ 生成核。
    /// </summary>
    public static double[] Gaussian(double[] values, double cutoff, double step, bool reflect, out int edge)
    {
        if (cutoff <= 0) throw new InvalidOperationException("Cutoff wavelength must be positive for Gaussian filtering.");
        var sigma = Math.Sqrt(Math.Log(2) / Math.PI) * cutoff / Math.Sqrt(2 * Math.PI);
        var samplesPerSigma = sigma / step;
        edge = (int)Math.Ceiling(3 * samplesPerSigma);
        if (edge < 1 || edge > values.Length / 2) edge = Math.Clamp(values.Length / 4, 0, edge);
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

    /// <summary>Kåsa 代数圆拟合（先中心化再解 3×3 正规方程），逐样本调用也够快。</summary>
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
        if (Math.Abs(det) < 1e-12) return false;
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

public sealed class ContourToPointsNode : IVisionNodeExecutor
{
    public string Type => "contour.toPoints";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var closed = node.GetBool("closed", true);
        var budget = node.GetInt("maxPoints", 5000);
        if (budget is < 2 or > ToleranceGeometry.MaxPoints) throw new InvalidOperationException("contour.toPoints maxPoints must be 2..20000.");
        var mode = node.GetString("resample", "arcLength");
        var step = node.GetDouble("sampleStep", 2);
        var points = ProfileMath.SelectContour(context, node, closed);
        if (points.Length < 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));

        double length;
        double stepUsed;
        VisionPoint2D[] samples;
        switch (mode.ToLowerInvariant())
        {
            case "everypoint":
                if (points.Length > budget) throw new InvalidOperationException($"Contour has {points.Length} points; raise maxPoints or use arcLength resampling.");
                samples = closed ? points.Append(points[0]).ToArray() : points;
                length = Perimeter(points, closed);
                stepUsed = 0;
                break;
            case "uniform":
                var stride = Math.Max(1, (int)Math.Ceiling(points.Length / (double)budget));
                var thinned = new List<VisionPoint2D>();
                for (var i = 0; i < points.Length; i += stride) thinned.Add(points[i]);
                samples = thinned.Count >= 2 ? thinned.ToArray() : [points[0], points[^1]];
                length = Perimeter(points, closed);
                stepUsed = 0;
                break;
            case "arclength":
                samples = ProfileMath.Resample(points, closed, step, budget);
                length = Perimeter(points, closed);
                stepUsed = step;
                break;
            default:
                throw new InvalidOperationException($"contour.toPoints resample mode '{mode}' is not supported (arcLength | uniform | everyPoint).");
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("pointCount", samples.Length),
            ("length", length), ("stepUsed", stepUsed), ("resample", mode), ("closed", closed));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pointList"] = VisionValue.PointList(samples), ["found"] = VisionValue.Boolean(true),
                ["pointCount"] = VisionValue.Integer(samples.Length), ["length"] = VisionValue.Double(length),
                ["stepUsed"] = VisionValue.Double(stepUsed), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["pointCount"] = samples.Length, ["length"] = length, ["stepUsed"] = stepUsed,
                ["resample"] = mode, ["closed"] = closed, ["unit"] = "px", ["algorithmBackend"] = "managed",
                ["diagnostics"] = diagnostics
            },
            samples.Take(400).Select((p, i) => new VisionOverlay($"profile-{i}", VisionOverlayType.Point, X: p.X, Y: p.Y)).ToArray()));
    }

    private static double Perimeter(VisionPoint2D[] points, bool closed)
    {
        var total = 0.0;
        for (var i = 1; i < points.Length; i++) total += MeasurementMath.Hypot(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y);
        if (closed && points.Length > 1) total += MeasurementMath.Hypot(points[0].X - points[^1].X, points[0].Y - points[^1].Y);
        return total;
    }
}

public sealed class ArcFitNode : IVisionNodeExecutor
{
    public string Type => "measure.arcFit";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var coverageLimit = node.GetDouble("minCoverageDeg", 20);
        if (!double.IsFinite(coverageLimit) || coverageLimit is < 0 or > 359) throw new InvalidOperationException("measure.arcFit minCoverageDeg must be 0..359.");
        var maxDeviation = node.GetDouble("maxDeviation", 0);
        if (!double.IsFinite(maxDeviation) || maxDeviation < 0) throw new InvalidOperationException("measure.arcFit maxDeviation must be finite and >= 0 (0 disables the gate).");

        var points = ProfileMath.SelectContour(context, node, false);
        if (points.Distinct().Count() < 4)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));

        var circle = JLVisionPointFitting.Circle(points);
        ToleranceGeometry.Finite(circle.X, circle.Y, circle.Radius);
        var angles = points.Select(p => Math.Atan2(p.Y - circle.Y, p.X - circle.X) * 180 / Math.PI).Order().ToArray();
        var gap = angles[0] + 360 - angles[^1];
        var gapEnd = angles[0]; var gapStart = angles[^1];
        for (var i = 1; i < angles.Length; i++)
        {
            var candidate = angles[i] - angles[i - 1];
            if (candidate > gap) { gap = candidate; gapStart = angles[i - 1]; gapEnd = angles[i]; }
        }
        var sweep = 360 - gap;
        if (sweep + 1e-9 < coverageLimit)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-arc-coverage",
                new() { ["coverageDeg"] = VisionValue.Double(sweep) }, nodeType: Type));

        // 弧段端点是"缺口两侧"的角度，正好是采样点的首/末角
        var startAngle = gapEnd; var endAngle = gapStart;
        var radii = points.Select(p => MeasurementMath.Hypot(p.X - circle.X, p.Y - circle.Y)).ToArray();
        var minimum = radii.Min(); var maximum = radii.Max();
        var rms = Math.Sqrt(radii.Average(r => (r - circle.Radius) * (r - circle.Radius)));
        var deviation = maximum - minimum;
        var within = maxDeviation <= 0 || deviation <= maxDeviation;
        var chord = MeasurementMath.Hypot(points[0].X - points[^1].X, points[0].Y - points[^1].Y);
        var arcLength = sweep * Math.PI / 180 * circle.Radius;

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", points.Length),
            ("radius", circle.Radius), ("sweepDeg", sweep), ("coverageDeg", sweep), ("arcLength", arcLength),
            ("chordLength", chord), ("deviation", deviation), ("rms", rms), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(points.Length),
                ["coverageDeg"] = VisionValue.Double(sweep), ["circle"] = VisionValue.Circle(circle),
                ["center"] = VisionValue.Point(new VisionPoint2D(circle.X, circle.Y)), ["radius"] = VisionValue.Double(circle.Radius),
                ["startAngleDeg"] = VisionValue.Double(startAngle), ["endAngleDeg"] = VisionValue.Double(endAngle),
                ["sweepDeg"] = VisionValue.Double(sweep), ["arcLength"] = VisionValue.Double(arcLength),
                ["chordLength"] = VisionValue.Double(chord), ["minRadius"] = VisionValue.Double(minimum),
                ["maxRadius"] = VisionValue.Double(maximum), ["deviation"] = VisionValue.Double(deviation),
                ["rms"] = VisionValue.Double(rms), ["withinTolerance"] = VisionValue.Boolean(within),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["radius"] = circle.Radius, ["sweepDeg"] = sweep, ["arcLength"] = arcLength,
                ["chordLength"] = chord, ["deviation"] = deviation, ["rms"] = rms, ["sampleCount"] = points.Length,
                ["withinTolerance"] = within, ["maxDeviation"] = maxDeviation, ["unit"] = "px",
                ["algorithmBackend"] = "jlvision", ["diagnostics"] = diagnostics
            },
            [new("arc-fit", VisionOverlayType.Circle, X: circle.X, Y: circle.Y, Radius: circle.Radius, Label: $"R{circle.Radius:0.###} @ {sweep:0.#} deg")]));
    }
}

public sealed class MinimumZoneCircleNode : IVisionNodeExecutor
{
    public string Type => "measure.mzCircle";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var mode = node.GetString("mode", "minZone").ToLowerInvariant();
        if (mode is not ("minzone" or "minimumcircumscribed" or "maximuminscribed"))
            throw new InvalidOperationException($"measure.mzCircle mode '{mode}' is not supported (minZone | minimumCircumscribed | maximumInscribed).");
        var precision = node.GetDouble("precision", .0001);
        var maxIterations = node.GetInt("maxIterations", 400);
        if (!double.IsFinite(precision) || precision is < 1e-6 or > 1) throw new InvalidOperationException("measure.mzCircle precision must be within 1e-6..1 px.");
        if (maxIterations is < 10 or > 10000) throw new InvalidOperationException("measure.mzCircle maxIterations must be within 10..10000.");
        var maxZone = node.GetDouble("maxZoneWidth", 0);
        if (!double.IsFinite(maxZone) || maxZone < 0) throw new InvalidOperationException("measure.mzCircle maxZoneWidth must be finite and >= 0 (0 disables the gate).");

        var points = ProfileMath.SelectContour(context, node, true);
        if (points.Length < 5) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));
        var lsq = JLVisionPointFitting.Circle(points);

        (double Cx, double Cy, double Radius, double Width) Evaluate(double cx, double cy)
        {
            var min = double.MaxValue; var max = double.MinValue;
            foreach (var p in points)
            {
                var r = MeasurementMath.Hypot(p.X - cx, p.Y - cy);
                if (r < min) min = r;
                if (r > max) max = r;
            }
            var radius = mode switch
            {
                "minimumcircumscribed" => max,
                "maximuminscribed" => min,
                _ => (max + min) / 2
            };
            return (cx, cy, radius, max - min);
        }

        double Cost(double cx, double cy)
        {
            var e = Evaluate(cx, cy);
            return mode switch
            {
                "minimumcircumscribed" => e.Radius,
                "maximuminscribed" => -e.Radius,
                _ => e.Width
            };
        }

        // 确定性模式搜索：从最小二乘圆心出发，8 方向试探 + 步长减半。
        // 目标函数关于圆心的分段光滑（max/min 切换处有拐点），坐标下降足够且从不依赖点序。
        var best = (Cx: lsq.X, Cy: lsq.Y);
        var bestCost = Cost(best.Cx, best.Cy);
        var step = Math.Max(precision, lsq.Radius * .05);
        var iterations = 0;
        var maxSteps = maxIterations * 12;
        while (step > precision && iterations < maxSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var improved = false;
            for (var k = 0; k < 8; k++)
            {
                var angle = k * Math.PI / 4;
                var candidate = (Cx: best.Cx + step * Math.Cos(angle), Cy: best.Cy + step * Math.Sin(angle));
                var cost = Cost(candidate.Cx, candidate.Cy);
                if (cost < bestCost - 1e-12) { best = candidate; bestCost = cost; improved = true; }
            }
            iterations++;
            if (!improved) step *= .5;
        }
        if (iterations >= maxSteps)
            throw new InvalidOperationException("measure.mzCircle did not converge within maxIterations; loosen precision or raise the budget.");

        var final = Evaluate(best.Cx, best.Cy);
        var within = maxZone <= 0 || final.Width <= maxZone;
        var shift = MeasurementMath.Hypot(final.Cx - lsq.X, final.Cy - lsq.Y);
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("mode", mode),
            ("radius", final.Radius), ("zoneWidth", final.Width), ("lsqRadius", lsq.Radius), ("centerShift", shift),
            ("iterations", iterations), ("sampleCount", points.Length), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(points.Length),
                ["circle"] = VisionValue.Circle(new VisionCircle(final.Cx, final.Cy, final.Radius)),
                ["center"] = VisionValue.Point(new VisionPoint2D(final.Cx, final.Cy)), ["radius"] = VisionValue.Double(final.Radius),
                ["zoneWidth"] = VisionValue.Double(final.Width), ["lsqRadius"] = VisionValue.Double(lsq.Radius),
                ["centerShift"] = VisionValue.Double(shift), ["iterations"] = VisionValue.Integer(iterations),
                ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["mode"] = mode, ["radius"] = final.Radius, ["zoneWidth"] = final.Width,
                ["lsqRadius"] = lsq.Radius, ["centerShift"] = shift, ["iterations"] = iterations,
                ["sampleCount"] = points.Length, ["withinTolerance"] = within, ["maxZoneWidth"] = maxZone,
                ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            [new("mz-circle", VisionOverlayType.Circle, X: final.Cx, Y: final.Cy, Radius: final.Radius,
                Label: $"zone {final.Width:0.####}")]));
    }
}

public sealed class ContourCurvatureNode : IVisionNodeExecutor
{
    public string Type => "measure.curvature";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var window = node.GetInt("fittingWindow", 5);
        if (window is < 1 or > 200) throw new InvalidOperationException("measure.curvature fittingWindow must be within 1..200.");
        var cornerRadius = node.GetDouble("cornerRadius", 0);
        if (!double.IsFinite(cornerRadius) || cornerRadius < 0) throw new InvalidOperationException("measure.curvature cornerRadius must be finite and >= 0 (0 disables corner counting).");
        var closed = node.GetBool("closed", true);

        var points = ProfileMath.SelectContour(context, node, closed);
        var minimumNeeded = 2 * window + 3;
        if (points.Length < minimumNeeded)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));

        var count = points.Length;
        var radii = new double[count];
        double sumCurvature = 0; var fitted = 0;
        var buffer = new List<VisionPoint2D>(minimumNeeded);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Clear();
            for (var k = -window; k <= window; k++)
            {
                var index = i + k;
                if (closed) index = ((index % count) + count) % count;
                else if (index < 0 || index >= count) { buffer.Clear(); break; }
                buffer.Add(points[index]);
            }
            if (buffer.Count < 3) { radii[i] = double.NaN; continue; }
            if (!ProfileMath.TryFitCircle(buffer, out var circle)) { radii[i] = double.NaN; continue; }
            radii[i] = circle.Radius; sumCurvature += 1 / circle.Radius; fitted++;
        }
        var finite = radii.Select((r, i) => (r, i)).Where(v => double.IsFinite(v.r)).ToArray();
        if (finite.Length < 3)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "degenerate-curvature", nodeType: Type));

        var minEntry = finite.MinBy(v => v.r);
        var meanCurvature = sumCurvature / fitted;
        var corners = new List<(int Index, double Radius, VisionPoint2D Point)>();
        if (cornerRadius > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (!double.IsFinite(radii[i]) || radii[i] > cornerRadius) continue;
                var isMinimum = true;
                for (var k = -window; k <= window && isMinimum; k++)
                {
                    var index = i + k;
                    if (closed) index = ((index % count) + count) % count;
                    else if (index < 0 || index >= count) continue;
                    if (index != i && double.IsFinite(radii[index]) && radii[index] < radii[i]) isMinimum = false;
                }
                if (!isMinimum) continue;
                // 非极大抑制：同一段圆角只报一次
                if (corners.Count > 0 && i - corners[^1].Index <= window) continue;
                corners.Add((i, radii[i], points[i]));
            }
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", count),
            ("minRadius", minEntry.r), ("minRadiusIndex", minEntry.i), ("meanCurvature", meanCurvature),
            ("cornerCount", corners.Count), ("window", window), ("cornerRadius", cornerRadius));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(count),
                ["minRadius"] = VisionValue.Double(minEntry.r), ["minRadiusPoint"] = VisionValue.Point(points[minEntry.i]),
                ["maxCurvature"] = VisionValue.Double(1 / minEntry.r), ["meanCurvature"] = VisionValue.Double(meanCurvature),
                ["cornerCount"] = VisionValue.Integer(corners.Count),
                ["cornerJson"] = VisionValue.String(JsonSerializer.Serialize(corners.Select(c => new
                {
                    index = c.Index, radius = Math.Round(c.Radius, 6), x = Math.Round(c.Point.X, 6), y = Math.Round(c.Point.Y, 6)
                }))),
                ["withinTolerance"] = VisionValue.Boolean(true), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["minRadius"] = minEntry.r, ["maxCurvature"] = 1 / minEntry.r,
                ["meanCurvature"] = meanCurvature, ["cornerCount"] = corners.Count, ["sampleCount"] = count,
                ["withinTolerance"] = true, ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            corners.Select((c, i) => new VisionOverlay($"corner-{i}", VisionOverlayType.Point, X: c.Point.X, Y: c.Point.Y,
                Label: $"R{c.Radius:0.###}")).ToArray()));
    }
}

public sealed class ProfileFilterNode : IVisionNodeExecutor
{
    public string Type => "measure.profileFilter";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var step = node.GetDouble("sampleStep", 1);
        if (!double.IsFinite(step) || step is <= 0 or > 10000) throw new InvalidOperationException("measure.profileFilter sampleStep must be within 0.01..10000 px.");
        var cutoffParam = node.GetDouble("cutoffWavelength", 0);
        if (!double.IsFinite(cutoffParam) || cutoffParam < 0) throw new InvalidOperationException("measure.profileFilter cutoffWavelength must be finite and >= 0 (0 derives length/5).");
        var endEffect = node.GetString("endEffect", "reflect").ToLowerInvariant();
        if (endEffect is not ("reflect" or "discard")) throw new InvalidOperationException($"measure.profileFilter endEffect '{endEffect}' is not supported (reflect | discard).");
        var closed = node.GetBool("closed", false);

        var points = ProfileMath.SelectContour(context, node, closed);
        if (points.Length < 3) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));

        var (u, z, origin, ux, uy) = ProfileMath.AxialProfile(points, step, 20000);
        var length = u[^1] - u[0];
        var cutoff = cutoffParam > 0 ? cutoffParam : length / 5;
        if (cutoff <= step * 2) throw new InvalidOperationException("measure.profileFilter cutoffWavelength must exceed two sample steps; lower sampleStep or raise the cutoff.");
        var mean = ProfileMath.Gaussian(z, cutoff, step, endEffect == "reflect", out var edge);
        var start = 0; var end = u.Length;
        if (endEffect == "discard") { start = Math.Min(edge, u.Length / 4); end = Math.Max(start + 2, u.Length - start); }
        var removed = u.Length - (end - start);

        var waviness = new VisionPoint2D[end - start];
        var residual = new VisionPoint2D[end - start];
        var residualPeak = 0.0; var wavinessPeak = 0.0; var sumSquares = 0.0;
        for (var i = start; i < end; i++)
        {
            var deviation = z[i] - mean[i];
            waviness[i - start] = ProfileMath.Unproject(origin, ux, uy, u[i], mean[i]);
            residual[i - start] = ProfileMath.Unproject(origin, ux, uy, u[i], deviation);
            residualPeak = Math.Max(residualPeak, Math.Abs(deviation));
            wavinessPeak = Math.Max(wavinessPeak, Math.Abs(mean[i]));
            sumSquares += deviation * deviation;
        }
        // 幅值单独出标量：PointList 是像素坐标（含主轴倾角），直接量 Y 会混入线性趋势
        var residualRms = end > start ? Math.Sqrt(sumSquares / (end - start)) : 0;
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", residual.Length),
            ("cutoff", cutoff), ("length", length), ("removedPointCount", removed), ("endEffect", endEffect),
            ("residualPeak", residualPeak), ("wavinessPeak", wavinessPeak));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(residual.Length),
                ["cutoff"] = VisionValue.Double(cutoff), ["waviness"] = VisionValue.PointList(waviness),
                ["residual"] = VisionValue.PointList(residual), ["length"] = VisionValue.Double(length),
                ["residualPeak"] = VisionValue.Double(residualPeak), ["wavinessPeak"] = VisionValue.Double(wavinessPeak),
                ["residualRms"] = VisionValue.Double(residualRms),
                ["removedPointCount"] = VisionValue.Integer(removed), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["cutoff"] = cutoff, ["length"] = length, ["sampleCount"] = residual.Length,
                ["removedPointCount"] = removed, ["endEffect"] = endEffect, ["residualPeak"] = residualPeak,
                ["wavinessPeak"] = wavinessPeak, ["residualRms"] = residualRms, ["unit"] = "px",
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }
}

public sealed class ProfileRoughnessNode : IVisionNodeExecutor
{
    public string Type => "measure.roughness";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var step = node.GetDouble("sampleStep", 1);
        if (!double.IsFinite(step) || step is <= 0 or > 10000) throw new InvalidOperationException("measure.roughness sampleStep must be within 0.01..10000 px.");
        var cutoffParam = node.GetDouble("cutoffWavelength", 0);
        if (!double.IsFinite(cutoffParam) || cutoffParam < 0) throw new InvalidOperationException("measure.roughness cutoffWavelength must be finite and >= 0 (0 derives length/5).");
        var evaluationParam = node.GetDouble("evaluationLength", 0);
        if (!double.IsFinite(evaluationParam) || evaluationParam < 0) throw new InvalidOperationException("measure.roughness evaluationLength must be finite and >= 0 (0 uses the whole profile).");
        var maxRa = node.GetDouble("maxRa", 0);
        if (!double.IsFinite(maxRa) || maxRa < 0) throw new InvalidOperationException("measure.roughness maxRa must be finite and >= 0 (0 disables the gate).");

        VisionPoint2D[] points;
        if (context.TryGetValue("pointList", out var wired))
        {
            var list = wired.Require<IReadOnlyList<VisionPoint2D>>("pointList");
            if (list.Count > ToleranceGeometry.MaxPoints) throw new InvalidOperationException("Point list exceeds the 20000-point budget.");
            points = ToleranceGeometry.Points(list.ToArray(), closed: false);
        }
        else
        {
            points = ProfileMath.SelectContour(context, node, false);
        }
        if (points.Length < 4) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));

        var (u, z, _, _, _) = ProfileMath.AxialProfile(points, step, 20000);
        var length = u[^1] - u[0];
        var cutoff = cutoffParam > 0 ? cutoffParam : length / 5;
        if (cutoff <= step * 2) throw new InvalidOperationException("measure.roughness cutoffWavelength must exceed two sample steps; lower sampleStep or raise the cutoff.");
        var mean = ProfileMath.Gaussian(z, cutoff, step, true, out _);
        var residual = z.Select((value, i) => value - mean[i]).ToArray();

        var evaluated = evaluationParam > 0 ? Math.Min(evaluationParam, length) : length;
        var keep = Math.Max(3, (int)Math.Round(evaluated / step));
        if (keep >= residual.Length) keep = residual.Length;
        var window = residual[(residual.Length - keep)..];
        var ra = window.Average(Math.Abs);
        var rq = Math.Sqrt(window.Average(v => v * v));
        var rp = window.Max();
        var rv = -window.Min();
        var rmax = rp + rv;
        var segments = 5;
        var per = Math.Max(1, window.Length / segments);
        var rzValues = new List<double>();
        for (var s = 0; s < segments; s++)
        {
            var from = s * per; var to = s == segments - 1 ? window.Length : Math.Min(window.Length, from + per);
            if (to - from < 2) break;
            var slice = window[from..to];
            rzValues.Add(slice.Max() + -slice.Min());
        }
        var rz = rzValues.Count > 0 ? rzValues.Average() : rmax;
        var crossings = 0;
        for (var i = 1; i < window.Length; i++) if (Math.Sign(window[i]) != Math.Sign(window[i - 1])) crossings++;
        var within = maxRa <= 0 || ra <= maxRa;
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", window.Length),
            ("cutoff", cutoff), ("evaluationLength", evaluated), ("ra", ra), ("rq", rq), ("rz", rz), ("rmax", rmax),
            ("samplingLengths", rzValues.Count), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(window.Length),
                ["evaluationLength"] = VisionValue.Double(evaluated), ["cutoff"] = VisionValue.Double(cutoff),
                ["ra"] = VisionValue.Double(ra), ["rq"] = VisionValue.Double(rq), ["rz"] = VisionValue.Double(rz),
                ["rp"] = VisionValue.Double(rp), ["rv"] = VisionValue.Double(rv), ["rmax"] = VisionValue.Double(rmax),
                ["zeroCrossings"] = VisionValue.Integer(crossings), ["withinTolerance"] = VisionValue.Boolean(within),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["ra"] = ra, ["rq"] = rq, ["rz"] = rz, ["rp"] = rp, ["rv"] = rv, ["rmax"] = rmax,
                ["cutoff"] = cutoff, ["evaluationLength"] = evaluated, ["sampleCount"] = window.Length,
                ["zeroCrossings"] = crossings, ["withinTolerance"] = within, ["maxRa"] = maxRa,
                ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }
}

public sealed class ContourDistanceNode : IVisionNodeExecutor
{
    public string Type => "measure.contourDistance";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var mode = node.GetString("mode", "gap").ToLowerInvariant();
        if (mode is not ("gap" or "deviation")) throw new InvalidOperationException($"measure.contourDistance mode '{mode}' is not supported (gap | deviation).");
        var step = node.GetDouble("sampleStep", 2);
        if (!double.IsFinite(step) || step is <= 0 or > 10000) throw new InvalidOperationException("measure.contourDistance sampleStep must be within 0.01..10000 px.");
        var budget = node.GetInt("maxSamples", 1500);
        if (budget is < 2 or > 4000) throw new InvalidOperationException("measure.contourDistance maxSamples must be within 2..4000.");
        var limit = node.GetDouble("limit", 0);
        if (!double.IsFinite(limit) || limit < 0) throw new InvalidOperationException("measure.contourDistance limit must be finite and >= 0 (0 disables the gate).");
        var closed = node.GetBool("closed", true);

        var a = ProfileMath.SelectContour(context, node, closed);
        var b = ProfileMath.SelectContour(context, node, closed, "referenceContours", "referenceIndex");
        if (a.Length < 2 || b.Length < 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(0, "insufficient-points", nodeType: Type));

        var sa = ProfileMath.Resample(a, closed, step, budget);
        var sb = ProfileMath.Resample(b, closed, step, budget);
        var min = double.MaxValue; var max = 0.0; var sum = 0.0;
        VisionPoint2D closestA = default; VisionPoint2D closestB = default;
        foreach (var p in sa)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var direct = double.MaxValue; VisionPoint2D best = default;
            foreach (var q in sb)
            {
                var dx = p.X - q.X; var dy = p.Y - q.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < direct) { direct = distance; best = q; }
            }
            if (direct < min) { min = direct; closestA = p; closestB = best; }
            if (direct > max) max = direct;
            sum += direct;
        }
        var mean = sum / sa.Length;
        var reported = mode == "gap" ? min : max;
        var within = limit <= 0 || reported <= limit;
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", sa.Length),
            ("mode", mode), ("minDistance", min), ("maxDistance", max), ("meanDistance", mean), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(sa.Length),
                ["distance"] = VisionValue.Double(reported), ["minDistance"] = VisionValue.Double(min),
                ["maxDistance"] = VisionValue.Double(max), ["meanDistance"] = VisionValue.Double(mean),
                ["closestPointA"] = VisionValue.Point(closestA), ["closestPointB"] = VisionValue.Point(closestB),
                ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["distance"] = reported, ["minDistance"] = min, ["maxDistance"] = max,
                ["meanDistance"] = mean, ["mode"] = mode, ["sampleCount"] = sa.Length,
                ["withinTolerance"] = within, ["limit"] = limit, ["unit"] = "px",
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            [ToleranceGeometry.Segment("closest-gap", new VisionLine2D(closestA, closestB), $"{min:0.###}")]));
    }
}

public sealed class ThreadMeasurementNode : IVisionNodeExecutor
{
    public string Type => "measure.thread";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var polarity = node.GetString("polarity", "profile").ToLowerInvariant();
        if (polarity is not ("profile" or "inverted")) throw new InvalidOperationException($"measure.thread polarity '{polarity}' is not supported (profile | inverted).");
        var minCrests = node.GetInt("minCrests", 3);
        if (minCrests is < 2 or > 500) throw new InvalidOperationException("measure.thread minCrests must be within 2..500.");
        var pitchTolerance = node.GetDouble("pitchTolerance", 0);
        if (!double.IsFinite(pitchTolerance) || pitchTolerance < 0) throw new InvalidOperationException("measure.thread pitchTolerance must be finite and >= 0 (0 disables the gate).");
        var step = node.GetDouble("sampleStep", 1);
        if (!double.IsFinite(step) || step is <= 0 or > 10000) throw new InvalidOperationException("measure.thread sampleStep must be within 0.01..10000 px.");

        var (axis, dx, dy, axisLength) = ToleranceGeometry.Line(context, "axis");
        var normalX = -dy; var normalY = dx;
        var origin = axis.Start;

        var points = ProfileMath.SelectContour(context, node, false);
        if (points.Length < 8) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));
        var projectedU = new double[points.Length]; var projectedZ = new double[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var px = points[i].X - origin.X; var py = points[i].Y - origin.Y;
            projectedU[i] = px * dx + py * dy;
            projectedZ[i] = (px * normalX + py * normalY) * (polarity == "inverted" ? -1 : 1);
        }
        // 沿给定轴线等距重采样（不是等弧长）：齿距是轴向长度，采样间隔必须与轴向一致
        var order = Enumerable.Range(0, projectedU.Length).OrderBy(i => projectedU[i]).ToArray();
        var sortedU = order.Select(i => projectedU[i]).ToArray();
        var sortedZ = order.Select(i => projectedZ[i]).ToArray();
        var (u, z) = ProfileMath.Uniformize(sortedU, sortedZ, step, 20000);
        var samples = new VisionPoint2D[u.Length];
        for (var i = 0; i < u.Length; i++)
            samples[i] = new VisionPoint2D(origin.X + u[i] * dx + normalX * z[i], origin.Y + u[i] * dy + normalY * z[i]);

        // 轻量三点平滑，抑制边缘抖动造成的伪极值
        var smooth = new double[z.Length];
        for (var i = 0; i < z.Length; i++)
        {
            var from = Math.Max(0, i - 1); var to = Math.Min(z.Length - 1, i + 1);
            var sum = 0.0; for (var k = from; k <= to; k++) sum += z[k];
            smooth[i] = sum / (to - from + 1);
        }
        var threshold = smooth.Min() + .6 * (smooth.Max() - smooth.Min());
        var minSpacing = Math.Max(2, z.Length / 200);
        var crests = new List<int>(); var valleys = new List<int>();
        for (var i = 1; i < smooth.Length - 1; i++)
        {
            if (smooth[i] >= smooth[i - 1] && smooth[i] > smooth[i + 1] && smooth[i] >= threshold)
            {
                if (crests.Count == 0 || i - crests[^1] >= minSpacing) crests.Add(i);
                else if (smooth[i] > smooth[crests[^1]]) crests[^1] = i;
            }
            else if (smooth[i] <= smooth[i - 1] && smooth[i] < smooth[i + 1])
            {
                if (valleys.Count == 0 || i - valleys[^1] >= minSpacing) valleys.Add(i);
                else if (smooth[i] < smooth[valleys[^1]]) valleys[^1] = i;
            }
        }
        if (crests.Count < minCrests)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, $"thread-crests-{crests.Count}",
                new() { ["crestCount"] = VisionValue.Integer(crests.Count) }, nodeType: Type));

        // 直径取**原始** z（不平滑）：平滑会削峰，直接用会让大径系统偏小一个百分点量级
        var crestZ = crests.Select(i => z[i]).Order().ToArray();
        var valleyZ = valleys.Count > 0 ? valleys.Select(i => z[i]).Order().ToArray() : crestZ;
        var major = 2 * Median(crestZ);
        var minor = 2 * Median(valleyZ);
        var pitches = new List<double>();
        for (var i = 1; i < crests.Count; i++) pitches.Add(u[crests[i]] - u[crests[i - 1]]);
        var pitch = Median(pitches.Order().ToArray());
        var halfAngles = new List<double>();
        for (var i = 0; i < crests.Count - 1; i++)
        {
            var between = valleys.Where(v => v > crests[i] && v < crests[i + 1]).ToArray();
            if (between.Length == 0) continue;
            var valley = between.OrderByDescending(v => u[crests[i + 1]] - u[v]).First();
            var du = u[valley] - u[crests[i]]; var dz = smooth[crests[i]] - smooth[valley];
            if (du > 1e-9 && dz > 1e-9) halfAngles.Add(Math.Atan2(dz, du) * 180 / Math.PI);
        }
        var threadAngle = halfAngles.Count > 0 ? 2 * Median(halfAngles.Order().ToArray()) : 0;
        var pitchDiameter = (major + minor) / 2;
        // 栅距一致性即"栅距公差"：任一跨距超出容差就判 NG
        var within = pitchTolerance <= 0 || pitches.All(p => Math.Abs(p - pitch) <= pitchTolerance);
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("crestCount", crests.Count),
            ("sampleCount", samples.Length), ("pitch", pitch), ("majorDiameter", major), ("minorDiameter", minor),
            ("threadAngleDeg", threadAngle), ("axisLength", axisLength), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["crestCount"] = VisionValue.Integer(crests.Count),
                ["sampleCount"] = VisionValue.Integer(samples.Length), ["pitch"] = VisionValue.Double(pitch),
                ["majorDiameter"] = VisionValue.Double(major), ["minorDiameter"] = VisionValue.Double(minor),
                ["pitchDiameter"] = VisionValue.Double(pitchDiameter), ["threadAngleDeg"] = VisionValue.Double(threadAngle),
                ["threadDepth"] = VisionValue.Double((major - minor) / 2), ["withinTolerance"] = VisionValue.Boolean(within),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["crestCount"] = crests.Count, ["pitch"] = pitch, ["majorDiameter"] = major,
                ["minorDiameter"] = minor, ["pitchDiameter"] = pitchDiameter, ["threadAngleDeg"] = threadAngle,
                ["sampleCount"] = samples.Length, ["withinTolerance"] = within, ["unit"] = "px",
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            crests.Select((i, k) => new VisionOverlay($"crest-{k}", VisionOverlayType.Point, X: samples[i].X,
                Y: samples[i].Y, Label: k == 0 ? "crest" : null)).ToArray()));
    }

    private static double Median(double[] sorted)
        => sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}

public sealed class SpurGearNode : IVisionNodeExecutor
{
    public string Type => "measure.gear";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var module = node.GetDouble("module", 1);
        if (!double.IsFinite(module) || module <= 0) throw new InvalidOperationException("measure.gear module must be finite and > 0.");
        var teeth = node.GetInt("teeth", 20);
        if (teeth is < 3 or > 2000) throw new InvalidOperationException("measure.gear teeth must be within 3..2000.");
        var alpha = node.GetDouble("pressureAngleDeg", 20) * Math.PI / 180;
        if (!double.IsFinite(alpha) || alpha <= 0 || alpha >= Math.PI / 2) throw new InvalidOperationException("measure.gear pressureAngleDeg must be within (0, 90).");
        var shift = node.GetDouble("profileShift", 0);
        if (!double.IsFinite(shift) || shift is < -2 or > 2) throw new InvalidOperationException("measure.gear profileShift must be within -2..2.");
        var pinParam = node.GetDouble("pinDiameter", 0);
        if (!double.IsFinite(pinParam) || pinParam < 0) throw new InvalidOperationException("measure.gear pinDiameter must be finite and >= 0 (0 derives 1.728*m).");

        var d = module * teeth;
        var db = d * Math.Cos(alpha);
        var da = d + 2 * module * (1 + shift);
        var df = d - 2 * module * (1.25 - shift);
        var circularPitch = Math.PI * module;
        var invAlpha = Math.Tan(alpha) - alpha;
        var k = (int)Math.Round(teeth * alpha / Math.PI + .5);
        var span = module * Math.Cos(alpha) * (Math.PI * (k - .5) + teeth * invAlpha) + 2 * shift * module * Math.Sin(alpha);
        var baseTooth = db * (Math.PI / (2 * teeth) + invAlpha) + 2 * shift * module * Math.Sin(alpha);
        var pin = pinParam > 0 ? pinParam : 1.728 * module;
        var invPin = invAlpha + pin / d - Math.PI / (2 * teeth) + 2 * shift * Math.Tan(alpha) / teeth;
        var alphaPin = InverseInvolute(invPin);
        var overPins = d * Math.Cos(alpha) / Math.Cos(alphaPin) + pin;
        if (teeth % 2 == 1) overPins = d * Math.Cos(alpha) / Math.Cos(alphaPin) * Math.Cos(Math.PI / (2 * teeth)) + pin;

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("module", module), ("teeth", teeth),
            ("pressureAngleDeg", alpha * 180 / Math.PI), ("profileShift", shift), ("spanTeeth", k),
            ("pitchDiameter", d), ("baseDiameter", db), ("outsideDiameter", da), ("rootDiameter", df));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["pitchDiameter"] = VisionValue.Double(d),
                ["baseDiameter"] = VisionValue.Double(db), ["outsideDiameter"] = VisionValue.Double(da),
                ["rootDiameter"] = VisionValue.Double(df), ["circularPitch"] = VisionValue.Double(circularPitch),
                ["spanTeeth"] = VisionValue.Integer(k), ["spanLength"] = VisionValue.Double(span),
                ["pinDiameter"] = VisionValue.Double(pin), ["measurementOverPins"] = VisionValue.Double(overPins),
                ["baseToothThickness"] = VisionValue.Double(baseTooth), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["pitchDiameter"] = d, ["baseDiameter"] = db, ["outsideDiameter"] = da,
                ["rootDiameter"] = df, ["circularPitch"] = circularPitch, ["spanTeeth"] = k, ["spanLength"] = span,
                ["pinDiameter"] = pin, ["measurementOverPins"] = overPins, ["baseToothThickness"] = baseTooth,
                ["module"] = module, ["teeth"] = teeth, ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    /// <summary>渐开线函数求逆：solve tan(a)-a = v（Newton，v=0 时退化为 a=0）。</summary>
    private static double InverseInvolute(double v)
    {
        if (v <= 0) return 0;
        var a = Math.Cbrt(3 * v);
        for (var i = 0; i < 40; i++)
        {
            var f = Math.Tan(a) - a - v;
            var derivative = Math.Tan(a) * Math.Tan(a);
            if (Math.Abs(derivative) < 1e-12) break;
            var next = a - f / derivative;
            if (!double.IsFinite(next)) break;
            if (Math.Abs(next - a) < 1e-12) { a = next; break; }
            a = next;
        }
        return a;
    }
}

public sealed class ToleranceStackupNode : IVisionNodeExecutor
{
    public string Type => "tolerance.stackup";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var method = node.GetString("method", "worstCase").ToLowerInvariant();
        if (method is not ("worstcase" or "rss")) throw new InvalidOperationException($"tolerance.stackup method '{method}' is not supported (worstCase | rss).");
        var target = node.GetDouble("targetNominal", 0);
        var tolerance = node.GetDouble("tolerance", 0);
        if (!double.IsFinite(target) || !double.IsFinite(tolerance) || tolerance < 0)
            throw new InvalidOperationException("tolerance.stackup targetNominal and tolerance must be finite, tolerance >= 0 (0 disables the gate).");

        var json = node.GetString("contributionsJson", "[]");
        if (json.Length > 65536) throw new InvalidOperationException("tolerance.stackup contributions JSON exceeds 64 KiB.");
        List<Contribution> contributions;
        try
        {
            contributions = JsonSerializer.Deserialize<List<Contribution>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException ex) { throw new InvalidOperationException("tolerance.stackup contributionsJson must be an array of {name, nominal, plus, minus, sign}.", ex); }
        if (contributions.Count == 0) throw new InvalidOperationException("tolerance.stackup needs at least one contribution.");
        if (contributions.Count > 64) throw new InvalidOperationException("tolerance.stackup supports at most 64 contributions.");

        // a..f 覆盖前六个贡献的名义值：把上游实测值接进尺寸链，而不必改 JSON
        var ports = new[] { "a", "b", "c", "d", "e", "f" };
        for (var i = 0; i < ports.Length && i < contributions.Count; i++)
        {
            if (context.TryGetValue(ports[i], out var wired)) contributions[i].Nominal = context.RequireNumber(ports[i]);
        }
        foreach (var c in contributions)
        {
            if (!double.IsFinite(c.Nominal) || !double.IsFinite(c.Plus) || !double.IsFinite(c.Minus) || c.Plus < 0 || c.Minus < 0)
                throw new InvalidOperationException($"tolerance.stackup contribution '{c.Name}' has non-finite values or negative tolerances.");
            if (c.Sign is not (1 or -1)) throw new InvalidOperationException($"tolerance.stackup contribution '{c.Name}' sign must be +1 or -1.");
        }

        var nominal = contributions.Sum(c => c.Sign * c.Nominal);
        var upper = 0.0; var lower = 0.0; var variance = 0.0;
        var shares = new List<object>();
        foreach (var c in contributions)
        {
            var plus = c.Sign > 0 ? c.Plus : c.Minus;
            var minus = c.Sign > 0 ? c.Minus : c.Plus;
            upper += c.Sign * c.Nominal + plus;
            lower += c.Sign * c.Nominal - minus;
            var sigma = (c.Plus + c.Minus) / 6;
            variance += sigma * sigma;
            shares.Add(new { name = c.Name, sign = c.Sign, nominal = Math.Round(c.Sign * c.Nominal, 6), upper = Math.Round(plus, 6), lower = Math.Round(minus, 6) });
        }
        var sigmaTotal = Math.Sqrt(variance);
        var rssTolerance = 6 * sigmaTotal;
        var useRss = method == "rss";
        var reportedUpper = useRss ? nominal + 3 * sigmaTotal : upper;
        var reportedLower = useRss ? nominal - 3 * sigmaTotal : lower;
        var gap = Math.Abs(nominal - target);
        var margin = tolerance > 0 ? tolerance - gap : 0;
        var within = tolerance <= 0 || gap <= tolerance;
        var utilization = tolerance > 0 ? gap / tolerance : 0;

        if (useRss)
        {
            // RSS 的贡献占比按方差分配，最坏情况的占比按半带宽分配
            shares = contributions.Select(c =>
            {
                var sigma = (c.Plus + c.Minus) / 6;
                return (object)new
                {
                    name = c.Name, sign = c.Sign, nominal = Math.Round(c.Sign * c.Nominal, 6),
                    upper = Math.Round(c.Sign > 0 ? c.Plus : c.Minus, 6), lower = Math.Round(c.Sign > 0 ? c.Minus : c.Plus, 6),
                    varianceShare = sigmaTotal > 0 ? Math.Round(sigma * sigma / (variance is 0 ? 1 : variance), 6) : 0
                };
            }).ToList();
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("method", method),
            ("contributorCount", contributions.Count), ("nominal", nominal), ("upper", reportedUpper),
            ("lower", reportedLower), ("rssTolerance", rssTolerance), ("margin", margin), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["contributorCount"] = VisionValue.Integer(contributions.Count),
                ["nominal"] = VisionValue.Double(nominal), ["worstCaseMax"] = VisionValue.Double(upper),
                ["worstCaseMin"] = VisionValue.Double(lower), ["rssTolerance"] = VisionValue.Double(rssTolerance),
                ["upperLimit"] = VisionValue.Double(reportedUpper), ["lowerLimit"] = VisionValue.Double(reportedLower),
                ["margin"] = VisionValue.Double(margin), ["utilization"] = VisionValue.Double(utilization),
                ["contributionsJson"] = VisionValue.String(JsonSerializer.Serialize(shares)),
                ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["method"] = method, ["nominal"] = nominal, ["worstCaseMax"] = upper,
                ["worstCaseMin"] = lower, ["rssTolerance"] = rssTolerance, ["margin"] = margin,
                ["utilization"] = utilization, ["contributorCount"] = contributions.Count,
                ["withinTolerance"] = within, ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    private sealed class Contribution
    {
        public string Name { get; set; } = "c";
        public double Nominal { get; set; }
        public double Plus { get; set; }
        public double Minus { get; set; }
        public int Sign { get; set; } = 1;
    }
}

public sealed class DatumReferenceFrameNode : IVisionNodeExecutor
{
    public string Type => "tolerance.drf";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var name = CoordinateValidation.RequireNonEmpty(node.GetString("datumName", "A|B"), "datumName");
        if (name.Length > 32) throw new InvalidOperationException("tolerance.drf datumName must be at most 32 characters.");
        var limit = node.GetDouble("maxOrthogonalityErrorDeg", 0);
        if (!double.IsFinite(limit) || limit is < 0 or > 90) throw new InvalidOperationException("tolerance.drf maxOrthogonalityErrorDeg must be within 0..90 (0 disables the gate).");
        var flip = node.GetBool("flipAxis", false);
        var unit = CoordinateValidation.RequireNonEmpty(node.GetString("unit", "px"), "unit");

        var primary = ToleranceGeometry.Line(context, "primary");
        var secondary = ToleranceGeometry.Line(context, "secondary");
        var cross = primary.X * secondary.Y - primary.Y * secondary.X;
        if (Math.Abs(cross) < 1e-9)
            return ValueTask.FromResult(ToleranceGeometry.Missing(0, "parallel-datums", nodeType: Type));

        // 两条基准线的交点即基准系原点（A|B 交点），不是投影点
        var t = ((secondary.Line.Start.X - primary.Line.Start.X) * secondary.Y - (secondary.Line.Start.Y - primary.Line.Start.Y) * secondary.X) / cross;
        var origin = new VisionPoint2D(primary.Line.Start.X + t * primary.X, primary.Line.Start.Y + t * primary.Y);
        ToleranceGeometry.Finite(origin.X, origin.Y);
        var angle = primary.Line.AngleDeg;
        // 基准 B 相对基准 A 的正交误差：|90 - 两线夹角|
        var cosine = Math.Clamp(Math.Abs(primary.X * secondary.X + primary.Y * secondary.Y), 0, 1);
        var orthogonality = Math.Abs(90 - Math.Acos(cosine) * 180 / Math.PI);
        var secondaryAngle = secondary.Line.AngleDeg;

        double? originOffset = null;
        if (context.TryGetValue("tertiary", out var tertiaryValue))
        {
            var tertiary = tertiaryValue.Require<VisionPoint2D>("tertiary");
            ToleranceGeometry.Finite(tertiary.X, tertiary.Y);
            originOffset = (tertiary.X - origin.X) * primary.X + (tertiary.Y - origin.Y) * primary.Y;
        }
        var pose = new VisionPose2D(origin.X, origin.Y, flip ? angle + 180 : angle);
        var within = limit <= 0 || orthogonality <= limit;
        var payload = JsonSerializer.Serialize(new
        {
            name, origin = new { x = Math.Round(origin.X, 6), y = Math.Round(origin.Y, 6) },
            primaryAngleDeg = Math.Round(angle, 6), secondaryAngleDeg = Math.Round(secondaryAngle, 6),
            orthogonalityErrorDeg = Math.Round(orthogonality, 6), unit, flipped = flip
        });
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("datum", name),
            ("orthogonalityErrorDeg", orthogonality), ("primaryAngleDeg", angle), ("secondaryAngleDeg", secondaryAngle),
            ("withinTolerance", within), ("unit", unit));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["pose"] = VisionValue.Pose(pose),
                ["origin"] = VisionValue.Point(origin), ["primaryAngleDeg"] = VisionValue.Double(angle),
                ["secondaryAngleDeg"] = VisionValue.Double(secondaryAngle), ["orthogonalityErrorDeg"] = VisionValue.Double(orthogonality),
                ["originOffset"] = originOffset is { } offset ? VisionValue.Double(offset) : VisionValue.Double(0),
                ["datumJson"] = VisionValue.String(payload), ["withinTolerance"] = VisionValue.Boolean(within),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["datum"] = name, ["origin"] = origin, ["orthogonalityErrorDeg"] = orthogonality,
                ["primaryAngleDeg"] = angle, ["secondaryAngleDeg"] = secondaryAngle, ["originOffset"] = originOffset,
                ["withinTolerance"] = within, ["unit"] = unit, ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            [ToleranceGeometry.Segment("datum-a", primary.Line, "A"), ToleranceGeometry.Segment("datum-b", secondary.Line, "B"),
             new("datum-origin", VisionOverlayType.Point, X: origin.X, Y: origin.Y, Label: name)]));
    }
}

public sealed class ConstructLineNode : IVisionNodeExecutor
{
    public string Type => "geometry.lineConstruct";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var operation = node.GetString("operation", "perpendicular").ToLowerInvariant();
        var offsetParam = node.GetDouble("offset", 0);
        if (!double.IsFinite(offsetParam)) throw new InvalidOperationException("geometry.lineConstruct offset must be finite.");
        var span = node.GetDouble("spanScale", 1);
        if (!double.IsFinite(span) || span is <= 0 or > 100) throw new InvalidOperationException("geometry.lineConstruct spanScale must be within 0.01..100.");

        VisionLine2D? line = context.TryGetValue("line", out var lineValue) ? lineValue.Require<VisionLine2D>("line") : null;
        VisionLine2D? line2 = context.TryGetValue("line2", out var line2Value) ? line2Value.Require<VisionLine2D>("line2") : null;
        VisionPoint2D? point = context.TryGetValue("point", out var pointValue) ? pointValue.Require<VisionPoint2D>("point") : null;
        VisionCircle? circle = context.TryGetValue("circle", out var circleValue) ? circleValue.Require<VisionCircle>("circle") : null;

        VisionLine2D result; VisionLine2D? second = null; double? reportedOffset = null; double? angle = null;
        switch (operation)
        {
            case "perpendicular":
            {
                var (source, dx, dy, length) = RequireLine(line, "perpendicular needs a line input.");
                if (point is not { } p) throw new InvalidOperationException("geometry.lineConstruct perpendicular needs a point input.");
                ToleranceGeometry.Finite(p.X, p.Y);
                var half = length * span / 2;
                // 过点的法向线段：方向取源线左法向
                result = new VisionLine2D(new VisionPoint2D(p.X + dy * half, p.Y - dx * half),
                    new VisionPoint2D(p.X - dy * half, p.Y + dx * half));
                angle = NormalizeDeg(Math.Atan2(result.End.Y - result.Start.Y, result.End.X - result.Start.X) * 180 / Math.PI);
                break;
            }
            case "parallel":
            {
                var (source, dx, dy, length) = RequireLine(line, "parallel needs a line input.");
                double signed;
                if (point is { } p)
                {
                    ToleranceGeometry.Finite(p.X, p.Y);
                    signed = (p.X - source.Start.X) * -dy + (p.Y - source.Start.Y) * dx;
                }
                else signed = offsetParam;
                // 左法向 (-dy, dx) 平移；signed 为点到源线的有符号距离，因此 point 与 offset 走同一条路径
                result = new VisionLine2D(
                    new VisionPoint2D(source.Start.X - dy * signed, source.Start.Y + dx * signed),
                    new VisionPoint2D(source.End.X - dy * signed, source.End.Y + dx * signed));
                reportedOffset = signed;
                angle = NormalizeDeg(Math.Atan2(result.End.Y - result.Start.Y, result.End.X - result.Start.X) * 180 / Math.PI);
                break;
            }
            case "midline":
            {
                var (a, ax, ay, al) = RequireLine(line, "midline needs line and line2 inputs.");
                var (b, bx, by, bl) = RequireLine(line2, "midline needs line and line2 inputs.");
                var dirX = ax + bx; var dirY = ay + by;
                var norm = MeasurementMath.Hypot(dirX, dirY);
                if (norm < 1e-9) throw new InvalidOperationException("geometry.lineConstruct midline is undefined for opposing directions (180 deg).");
                dirX /= norm; dirY /= norm;
                var mid = new VisionPoint2D((a.Start.X + a.End.X + b.Start.X + b.End.X) / 4, (a.Start.Y + a.End.Y + b.Start.Y + b.End.Y) / 4);
                var half = (al + bl) / 4 * span;
                result = new VisionLine2D(new VisionPoint2D(mid.X - dirX * half, mid.Y - dirY * half),
                    new VisionPoint2D(mid.X + dirX * half, mid.Y + dirY * half));
                angle = NormalizeDeg(Math.Atan2(dirY, dirX) * 180 / Math.PI);
                break;
            }
            case "bisector":
            {
                var (a, ax, ay, al) = RequireLine(line, "bisector needs line and line2 inputs.");
                var (b, bx, by, bl) = RequireLine(line2, "bisector needs line and line2 inputs.");
                var cross = ax * by - ay * bx;
                if (Math.Abs(cross) < 1e-9) throw new InvalidOperationException("geometry.lineConstruct bisector needs non-parallel lines.");
                var t = ((b.Start.X - a.Start.X) * by - (b.Start.Y - a.Start.Y) * bx) / cross;
                var apex = new VisionPoint2D(a.Start.X + t * ax, a.Start.Y + t * ay);
                var dirX = ax + bx; var dirY = ay + by; var norm = MeasurementMath.Hypot(dirX, dirY);
                if (norm < 1e-9) { dirX = -ay; dirY = ax; norm = 1; }
                dirX /= norm; dirY /= norm;
                var half = Math.Min(al, bl) / 2 * span;
                result = new VisionLine2D(apex, new VisionPoint2D(apex.X + dirX * 2 * half, apex.Y + dirY * 2 * half));
                angle = NormalizeDeg(Math.Atan2(dirY, dirX) * 180 / Math.PI);
                break;
            }
            case "tangent":
            {
                if (circle is not { } c) throw new InvalidOperationException("geometry.lineConstruct tangent needs a circle input.");
                if (point is not { } p) throw new InvalidOperationException("geometry.lineConstruct tangent needs a point input.");
                ToleranceGeometry.Finite(c.X, c.Y, c.Radius, p.X, p.Y);
                if (c.Radius <= 0) throw new InvalidOperationException("geometry.lineConstruct tangent requires a positive circle radius.");
                var distance = MeasurementMath.Hypot(p.X - c.X, p.Y - c.Y);
                if (distance <= c.Radius + 1e-9) throw new InvalidOperationException("geometry.lineConstruct tangent needs a point outside the circle.");
                var baseAngle = Math.Atan2(p.Y - c.Y, p.X - c.X);
                var spread = Math.Acos(c.Radius / distance);
                var t1 = new VisionPoint2D(c.X + c.Radius * Math.Cos(baseAngle + spread), c.Y + c.Radius * Math.Sin(baseAngle + spread));
                var t2 = new VisionPoint2D(c.X + c.Radius * Math.Cos(baseAngle - spread), c.Y + c.Radius * Math.Sin(baseAngle - spread));
                result = new VisionLine2D(p, t1); second = new VisionLine2D(p, t2);
                angle = NormalizeDeg(Math.Atan2(t1.Y - p.Y, t1.X - p.X) * 180 / Math.PI);
                break;
            }
            default:
                throw new InvalidOperationException($"geometry.lineConstruct operation '{operation}' is not supported (perpendicular | parallel | midline | bisector | tangent).");
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("operation", operation),
            ("angleDeg", angle ?? 0), ("length", result.Length), ("offset", reportedOffset ?? 0));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["line"] = VisionValue.Line(result),
            ["angleDeg"] = VisionValue.Double(angle ?? 0), ["length"] = VisionValue.Double(result.Length),
            ["offset"] = VisionValue.Double(reportedOffset ?? 0), ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (second is { } other) outputs["line2"] = VisionValue.Line(other);
        var overlays = new List<VisionOverlay> { ToleranceGeometry.Segment("constructed", result) };
        if (second is { } secondLine) overlays.Add(ToleranceGeometry.Segment("constructed-2", secondLine));
        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["found"] = true, ["operation"] = operation, ["angleDeg"] = angle ?? 0, ["length"] = result.Length,
                ["offset"] = reportedOffset, ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }, overlays));
    }

    private static (VisionLine2D Line, double Dx, double Dy, double Length) RequireLine(VisionLine2D? line, string message)
    {
        if (line is not { } value) throw new InvalidOperationException("geometry.lineConstruct " + message);
        var (_, dx, dy, length) = ToleranceGeometry.Line(value);
        return (value, dx, dy, length);
    }

    private static double NormalizeDeg(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle <= -180) angle += 360;
        return angle;
    }
}

public sealed class AffineTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformAffine";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var scaleX = Read(context, node, "scaleX", 1);
        var scaleY = Read(context, node, "scaleY", 1);
        var rotation = Read(context, node, "rotationDeg", 0);
        var translateX = Read(context, node, "translateX", 0);
        var translateY = Read(context, node, "translateY", 0);
        var shear = Read(context, node, "shear", 0);
        if (Math.Abs(scaleX) < 1e-12 || Math.Abs(scaleY) < 1e-12)
            throw new InvalidOperationException("coordinate.transformAffine rejects a zero scale: a collapsed axis is not an invertible transform.");
        var sourceFrame = CoordinateValidation.RequireFrame(node.GetString("sourceFrame", "Image"), "sourceFrame");
        var targetFrame = CoordinateValidation.RequireFrame(node.GetString("targetFrame", "World"), "targetFrame");
        var sourceUnit = CoordinateValidation.RequireUnit(node.GetString("sourceUnit", "px"), "sourceUnit");
        var targetUnit = CoordinateValidation.RequireUnit(node.GetString("targetUnit", "mm"), "targetUnit");

        var radians = rotation * Math.PI / 180;
        var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var transform = new VisionTransform2D(sourceFrame, targetFrame, targetUnit,
            cos * scaleX, cos * shear * scaleY - sin * scaleY, translateX,
            sin * scaleX, sin * shear * scaleY + cos * scaleY, translateY,
            0, 0, 1, sourceUnit);
        var determinant = CoordinateValidation.ValidateTransform(transform, "coordinate.transformAffine");
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("determinant", determinant), ("scaleX", scaleX),
            ("scaleY", scaleY), ("rotationDeg", rotation), ("shear", shear),
            ("sourceFrame", sourceFrame), ("targetFrame", targetFrame));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["transform"] = VisionValue.Transform(transform), ["determinant"] = VisionValue.Double(determinant),
                ["scaleX"] = VisionValue.Double(scaleX), ["scaleY"] = VisionValue.Double(scaleY),
                ["rotationDeg"] = VisionValue.Double(rotation), ["translateX"] = VisionValue.Double(translateX),
                ["translateY"] = VisionValue.Double(translateY), ["shear"] = VisionValue.Double(shear),
                ["sourceFrame"] = VisionValue.String(sourceFrame), ["targetFrame"] = VisionValue.String(targetFrame),
                ["unit"] = VisionValue.String(targetUnit), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["determinant"] = determinant, ["scaleX"] = scaleX, ["scaleY"] = scaleY, ["rotationDeg"] = rotation,
                ["translateX"] = translateX, ["translateY"] = translateY, ["shear"] = shear,
                ["sourceFrame"] = sourceFrame, ["targetFrame"] = targetFrame, ["unit"] = targetUnit,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    private static double Read(NodeExecutionContext context, NodeDefinition node, string name, double fallback)
        => context.TryGetValue(name, out _) ? context.RequireNumber(name) : node.GetDouble(name, fallback);
}
