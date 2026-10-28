using System.Diagnostics;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 最佳拟合对齐与固定半径拟合的算法核心（2026-10-28）。
/// 两者都是纯 managed 二维闭式解：不迭代随机初值、不依赖 SVD，同样输入必得同样输出，
/// 这是计量节点能被复现的前提。
/// </summary>
internal static class BestFitMath
{
    internal const int MaxRawPoints = 20000;

    /// <summary>2x3 仿射（A B Tx / C D Ty）。刚体时 A=D=s·cosθ、B=-s·sinθ、C=s·sinθ。</summary>
    internal readonly record struct Affine(double A, double B, double Tx, double C, double D, double Ty)
    {
        public VisionPoint2D Apply(VisionPoint2D point)
            => new(A * point.X + B * point.Y + Tx, C * point.X + D * point.Y + Ty);

        /// <summary>先应用自己，再应用 <paramref name="next"/>。</summary>
        public Affine Then(Affine next) => new(
            next.A * A + next.B * C,
            next.A * B + next.B * D,
            next.A * Tx + next.B * Ty + next.Tx,
            next.C * A + next.D * C,
            next.C * B + next.D * D,
            next.C * Tx + next.D * Ty + next.Ty);

        public double RotationDeg => Math.Atan2(C, A) * 180.0 / Math.PI;

        public double Scale => Math.Sqrt(A * A + C * C);
    }

    internal static VisionPoint2D[] Thin(IReadOnlyList<VisionPoint2D> source, int maxPoints)
    {
        if (source.Count <= maxPoints) return source.ToArray();
        var result = new VisionPoint2D[maxPoints];
        var step = (double)(source.Count - 1) / (maxPoints - 1);
        for (var index = 0; index < maxPoints; index++)
            result[index] = source[(int)Math.Round(index * step)];
        return result;
    }

    /// <summary>点到折线（可闭合）最近点的精确解；段数受 maxPoints 约束，无需空间索引。</summary>
    internal static (double Distance, VisionPoint2D Point) NearestOnPolyline(
        VisionPoint2D[] polyline, bool closed, VisionPoint2D point)
    {
        var count = polyline.Length;
        var segments = closed ? count : count - 1;
        var best = double.MaxValue;
        var bestX = 0d;
        var bestY = 0d;
        for (var index = 0; index < segments; index++)
        {
            var a = polyline[index];
            var b = polyline[(index + 1) % count];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var length2 = ex * ex + ey * ey;
            var t = length2 > 1e-18 ? ((point.X - a.X) * ex + (point.Y - a.Y) * ey) / length2 : 0d;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            var qx = a.X + t * ex;
            var qy = a.Y + t * ey;
            var dx = point.X - qx;
            var dy = point.Y - qy;
            var distance2 = dx * dx + dy * dy;
            if (distance2 < best) { best = distance2; bestX = qx; bestY = qy; }
        }
        return (Math.Sqrt(best), new VisionPoint2D(bestX, bestY));
    }

    /// <summary>
    /// 最小二乘二维刚体/相似变换（Kabsch 闭式解）。
    /// θ = atan2(Σ(px·qy − py·qx), Σ(px·qx + py·qy))，不需要 SVD。
    /// </summary>
    internal static Affine Solve(VisionPoint2D[] source, VisionPoint2D[] target, bool allowScale)
    {
        if (source.Length != target.Length)
            throw new InvalidOperationException("Best-fit correspondence arrays must have equal length.");
        double sourceX = 0, sourceY = 0, targetX = 0, targetY = 0;
        for (var index = 0; index < source.Length; index++)
        {
            sourceX += source[index].X; sourceY += source[index].Y;
            targetX += target[index].X; targetY += target[index].Y;
        }
        var n = source.Length;
        sourceX /= n; sourceY /= n; targetX /= n; targetY /= n;

        double cross = 0, dot = 0, variance = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var px = source[index].X - sourceX;
            var py = source[index].Y - sourceY;
            var qx = target[index].X - targetX;
            var qy = target[index].Y - targetY;
            cross += px * qy - py * qx;
            dot += px * qx + py * qy;
            variance += px * px + py * py;
        }
        if (variance < 1e-18)
            throw new InvalidOperationException("Best-fit source points are degenerate (all coincident).");

        var theta = Math.Atan2(cross, dot);
        var scale = allowScale ? Math.Sqrt(cross * cross + dot * dot) / variance : 1d;
        if (!double.IsFinite(scale) || scale <= 0)
            throw new InvalidOperationException("Best-fit solved a non-positive scale; the point sets are degenerate.");
        var a = Math.Cos(theta) * scale;
        var b = -Math.Sin(theta) * scale;
        var c = Math.Sin(theta) * scale;
        var d = Math.Cos(theta) * scale;
        return new Affine(a, b, targetX - (a * sourceX + b * sourceY),
                          c, d, targetY - (c * sourceX + d * sourceY));
    }

    internal static void ValidatePoints(IReadOnlyList<VisionPoint2D> points, string nodeType, string port)
    {
        if (points.Count > MaxRawPoints)
            throw new InvalidOperationException(
                $"{nodeType} {port} budget exceeded (max {MaxRawPoints} points; thin the contour first).");
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || Math.Abs(point.X) > 100_000_000 || Math.Abs(point.Y) > 100_000_000)
                throw new InvalidOperationException($"{nodeType} {port} contains a non-finite or out-of-range coordinate.");
        }
    }

    internal static VisionPoint2D Centroid(IReadOnlyList<VisionPoint2D> points)
    {
        double x = 0, y = 0;
        foreach (var point in points) { x += point.X; y += point.Y; }
        return new VisionPoint2D(x / points.Count, y / points.Count);
    }

    /// <summary>把源质心旋到目标质心的起始变换（旋转绕原点，随后平移重合）。</summary>
    internal static Affine StartTransform(VisionPoint2D source, VisionPoint2D target, double rotationDeg)
    {
        var radians = rotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Affine(cos, -sin, target.X - (cos * source.X - sin * source.Y),
                          sin, cos, target.Y - (sin * source.X + cos * source.Y));
    }

    internal static double QuickResidual(
        VisionPoint2D[] measured, VisionPoint2D[] reference, bool closed, Affine transform, int sampleBudget)
    {
        var stride = Math.Max(1, measured.Length / Math.Max(1, sampleBudget));
        var sum = 0d;
        var count = 0;
        for (var index = 0; index < measured.Length; index += stride)
        {
            var (distance, _) = NearestOnPolyline(reference, closed, transform.Apply(measured[index]));
            sum += distance * distance;
            count++;
        }
        return count > 0 ? Math.Sqrt(sum / count) : 0d;
    }

    /// <summary>
    /// 整圈粗搜（30° 步长）挑最优起始角。ICP 只收敛到最近的局部极小，
    /// 不给初值时 25° 的旋转会被配到完全错误的弧段上——这是 best-fit 最常见的现场失败。
    /// </summary>
    internal static Affine BestStart(
        VisionPoint2D[] measured, VisionPoint2D[] reference, bool closed,
        VisionPoint2D sourceCentre, VisionPoint2D targetCentre, Affine fallback)
    {
        var best = fallback;
        var bestResidual = QuickResidual(measured, reference, closed, fallback, 200);
        var bestDegrees = double.NaN;

        for (var degrees = -180; degrees < 180; degrees += 30)
        {
            var candidate = StartTransform(sourceCentre, targetCentre, degrees);
            var residual = QuickResidual(measured, reference, closed, candidate, 200);
            if (residual < bestResidual - 1e-12) { bestResidual = residual; best = candidate; bestDegrees = degrees; }
        }

        // 二级细搜：光滑圆弧上点到点 ICP 的旋转梯度很弱，5° 步长的起点才能保证精修落到正确极小。
        if (!double.IsNaN(bestDegrees))
        {
            for (var offset = -30.0; offset <= 30.0; offset += 5.0)
            {
                var candidate = StartTransform(sourceCentre, targetCentre, bestDegrees + offset);
                var residual = QuickResidual(measured, reference, closed, candidate, 200);
                if (residual < bestResidual - 1e-12) { bestResidual = residual; best = candidate; }
            }
        }
        return best;
    }

    internal static (Affine Transform, int Iterations, bool Converged) Refine(
        VisionPoint2D[] measured, VisionPoint2D[] reference, bool closed, Affine start,
        int maxIterations, double trim, double tolerance, bool allowScale, CancellationToken token)
    {
        var current = start;
        var moved = new VisionPoint2D[measured.Length];
        var matched = new VisionPoint2D[measured.Length];
        var distances = new double[measured.Length];
        var keep = Math.Max(3, (int)Math.Ceiling(measured.Length * (1 - trim)));
        var iterations = 0;
        var converged = false;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            token.ThrowIfCancellationRequested();
            iterations = iteration;
            for (var index = 0; index < measured.Length; index++)
            {
                moved[index] = current.Apply(measured[index]);
                var (distance, nearest) = NearestOnPolyline(reference, closed, moved[index]);
                matched[index] = nearest;
                distances[index] = distance;
            }

            // trimmed ICP：按距离取前 keep 个对应点，剪掉毛刺与遮挡段。
            var order = Enumerable.Range(0, measured.Length).ToArray();
            Array.Sort(distances, order);
            var sourceKeep = new VisionPoint2D[keep];
            var targetKeep = new VisionPoint2D[keep];
            for (var slot = 0; slot < keep; slot++)
            {
                sourceKeep[slot] = moved[order[slot]];
                targetKeep[slot] = matched[order[slot]];
            }

            var delta = Solve(sourceKeep, targetKeep, allowScale);
            current = current.Then(delta);
            var move = Math.Abs(delta.Tx) + Math.Abs(delta.Ty);
            var turn = Math.Abs(delta.RotationDeg);
            if (move < tolerance && turn < 1e-7) { converged = true; break; }
        }

        return (current, iterations, converged);
    }
}

/// <summary>
/// 最佳拟合对齐（measure.bestFitAlign）：实测轮廓 → 理论轮廓的刚体对齐，GD&T 节点的共同前置。
/// 用 trimmed ICP 求对应点、Kabsch 闭式解求变换，全流程确定性。
/// </summary>
public sealed class BestFitAlignNode : IVisionNodeExecutor
{
    public string Type => "measure.bestFitAlign";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var measuredRaw = context.Require<IReadOnlyList<VisionPoint2D>>("measured");
        var referenceRaw = context.Require<IReadOnlyList<VisionPoint2D>>("reference");
        BestFitMath.ValidatePoints(measuredRaw, Type, "measured");
        BestFitMath.ValidatePoints(referenceRaw, Type, "reference");

        var fitMode = (node.GetString("fitMode", "rigid") ?? "rigid").Trim().ToLowerInvariant();
        if (fitMode is not ("rigid" or "similarity"))
            throw new InvalidOperationException("measure.bestFitAlign fitMode must be rigid or similarity.");
        var allowScale = fitMode == "similarity";

        var trim = node.GetDouble("trimFraction", 0.1);
        if (!double.IsFinite(trim) || trim is < 0 or > 0.45)
            throw new InvalidOperationException("measure.bestFitAlign trimFraction must be within 0..0.45.");

        var maxIterations = node.GetInt("maxIterations", 40);
        if (maxIterations is < 1 or > 200)
            throw new InvalidOperationException("measure.bestFitAlign maxIterations must be within 1..200.");

        var tolerance = node.GetDouble("tolerancePx", 0.0001);
        if (!double.IsFinite(tolerance) || tolerance is <= 0 or > 10)
            throw new InvalidOperationException("measure.bestFitAlign tolerancePx must be within (0, 10].");

        var maxPoints = node.GetInt("maxPoints", 600);
        if (maxPoints is < 60 or > 1500)
            throw new InvalidOperationException("measure.bestFitAlign maxPoints must be within 60..1500.");

        var initialRotation = node.GetDouble("initialRotationDeg", 0);
        if (!double.IsFinite(initialRotation) || initialRotation is < -180 or > 180)
            throw new InvalidOperationException("measure.bestFitAlign initialRotationDeg must be within -180..180.");

        var closed = node.GetBool("closedReference", true);
        var autoRotation = node.GetBool("autoInitialRotation", true);

        if (measuredRaw.Count < 3 || referenceRaw.Count < 3)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"best-fit needs at least 3 points on both sides (measured={measuredRaw.Count}, reference={referenceRaw.Count})",
                measuredRaw.Count, referenceRaw.Count, timer));
        }

        var measured = BestFitMath.Thin(measuredRaw, maxPoints);
        var reference = BestFitMath.Thin(referenceRaw, maxPoints + 1);

        // 初值：质心对齐 + 可选整圈粗搜，然后跑 trimmed ICP 精修。
        var sourceCentre = BestFitMath.Centroid(measured);
        var targetCentre = BestFitMath.Centroid(reference);
        var start = BestFitMath.StartTransform(sourceCentre, targetCentre, initialRotation);
        if (autoRotation)
            start = BestFitMath.BestStart(measured, reference, closed, sourceCentre, targetCentre, start);

        var (current, iterations, converged) = BestFitMath.Refine(
            measured, reference, closed, start, maxIterations, trim, tolerance, allowScale, cancellationToken);

        // 最终残差用全部采样点（含被剪掉的），才能如实反映毛刺。
        var residualSum = 0d;
        var residualMax = 0d;
        var inliers = 0;
        for (var index = 0; index < measured.Length; index++)
        {
            var point = current.Apply(measured[index]);
            var (distance, _) = BestFitMath.NearestOnPolyline(reference, closed, point);
            residualSum += distance;
            if (distance > residualMax) residualMax = distance;
            if (distance <= tolerance * 1000) inliers++;
        }
        var rms = measured.Length > 0 ? Math.Sqrt(residualSum / measured.Length) : 0;
        var mean = measured.Length > 0 ? residualSum / measured.Length : 0;

        var alignedPoints = new VisionPoint2D[measuredRaw.Count];
        for (var index = 0; index < measuredRaw.Count; index++)
            alignedPoints[index] = current.Apply(measuredRaw[index]);

        var transform = new VisionTransform2D(
            "image", "reference", "px",
            current.A, current.B, current.Tx,
            current.C, current.D, current.Ty,
            0, 0, 1);
        timer.Stop();

        var summary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["fitMode"] = fitMode, ["trimFraction"] = trim, ["maxIterations"] = maxIterations,
            ["maxPoints"] = maxPoints, ["initialRotationDeg"] = initialRotation,
            ["autoInitialRotation"] = autoRotation, ["closedReference"] = closed,
            ["rotationDeg"] = current.RotationDeg, ["deltaX"] = current.Tx, ["deltaY"] = current.Ty,
            ["scale"] = current.Scale, ["rmsError"] = rms, ["maxError"] = residualMax, ["meanError"] = mean,
            ["inlierCount"] = inliers, ["pointCount"] = measuredRaw.Count, ["iterations"] = iterations,
            ["converged"] = converged,
            ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
            ["algorithmBackend"] = "managed"
        };
        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("fitMode", fitMode), ("trimFraction", trim), ("iterations", iterations), ("converged", converged),
            ("maxPoints", maxPoints), ("measuredPoints", measuredRaw.Count), ("referencePoints", referenceRaw.Count),
            ("rotationDeg", current.RotationDeg), ("deltaX", current.Tx), ("deltaY", current.Ty),
            ("scale", current.Scale), ("rmsError", rms), ("maxError", residualMax), ("meanError", mean),
            ("inlierCount", inliers), ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        summary["diagnostics"] = diagnostics;

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["transform"] = VisionValue.Transform(transform),
                ["alignedPoints"] = VisionValue.PointList(alignedPoints),
                ["rotationDeg"] = VisionValue.Double(current.RotationDeg),
                ["deltaX"] = VisionValue.Double(current.Tx),
                ["deltaY"] = VisionValue.Double(current.Ty),
                ["scale"] = VisionValue.Double(current.Scale),
                ["rmsError"] = VisionValue.Double(rms),
                ["maxError"] = VisionValue.Double(residualMax),
                ["meanError"] = VisionValue.Double(mean),
                ["inlierCount"] = VisionValue.Integer(inliers),
                ["pointCount"] = VisionValue.Integer(measuredRaw.Count),
                ["iterations"] = VisionValue.Integer(iterations),
                ["found"] = VisionValue.Boolean(true),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            summary));
    }

    private static NodeExecutorResult Missing(string reason, int measuredCount, int referenceCount, Stopwatch timer)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "measure.bestFitAlign", ("found", false), ("reason", reason),
            ("measuredPoints", measuredCount), ("referencePoints", referenceCount),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(false),
                ["pointCount"] = VisionValue.Integer(measuredCount),
                ["inlierCount"] = VisionValue.Integer(0),
                ["iterations"] = VisionValue.Integer(0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason,
                ["measuredPoints"] = measuredCount, ["referencePoints"] = referenceCount,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            });
    }
}

/// <summary>
/// 固定半径拟合（measure.fitCircleFixedRadius）：半径已知，只解圆心。
/// 用 Späth 定点迭代最小化 Σ(‖p−c‖−R)²，半径全程不参与优化。
/// </summary>
public sealed class FixedRadiusCircleFitNode : IVisionNodeExecutor
{
    public string Type => "measure.fitCircleFixedRadius";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var points = context.Require<IReadOnlyList<VisionPoint2D>>("points");
        BestFitMath.ValidatePoints(points, Type, "points");

        var radius = node.GetDouble("radius", 50);
        if (!double.IsFinite(radius) || radius is <= 0 or > 10_000_000)
            throw new InvalidOperationException("measure.fitCircleFixedRadius radius must be finite within (0, 10000000].");

        var maxRms = node.GetDouble("maxRmsError", 0);
        if (!double.IsFinite(maxRms) || maxRms is < 0 or > 1_000_000)
            throw new InvalidOperationException("measure.fitCircleFixedRadius maxRmsError must be within 0..1000000 (0 disables).");

        var maxIterations = node.GetInt("maxIterations", 100);
        if (maxIterations is < 1 or > 1000)
            throw new InvalidOperationException("measure.fitCircleFixedRadius maxIterations must be within 1..1000.");

        var tolerance = node.GetDouble("tolerancePx", 1e-6);
        if (!double.IsFinite(tolerance) || tolerance is <= 0 or > 1)
            throw new InvalidOperationException("measure.fitCircleFixedRadius tolerancePx must be within (0, 1].");

        if (points.Count < 3)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"fixed-radius fit needs at least 3 points, got {points.Count}", points.Count, radius, timer));
        }

        double cx = 0, cy = 0;
        foreach (var point in points) { cx += point.X; cy += point.Y; }
        cx /= points.Count; cy /= points.Count;

        double bestX = cx, bestY = cy, bestRms = ResidualRms(points, cx, cy, radius);
        var iterations = 0;
        var converged = false;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterations = iteration;
            double sumX = 0, sumY = 0;
            var used = 0;
            for (var index = 0; index < points.Count; index++)
            {
                var dx = points[index].X - cx;
                var dy = points[index].Y - cy;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < 1e-12) continue;
                // Späth 定点迭代：把每个点沿径向外推 R 后取平均，等价于一步高斯-牛顿。
                sumX += points[index].X - radius * dx / distance;
                sumY += points[index].Y - radius * dy / distance;
                used++;
            }
            if (used < 3)
            {
                timer.Stop();
                return ValueTask.FromResult(Missing(
                    "fixed-radius fit degenerate: fewer than 3 distinct points around the centre",
                    points.Count, radius, timer));
            }

            var moved = Math.Sqrt((sumX / used - cx) * (sumX / used - cx) + (sumY / used - cy) * (sumY / used - cy));
            cx = sumX / used;
            cy = sumY / used;
            var rms = ResidualRms(points, cx, cy, radius);
            if (rms < bestRms) { bestRms = rms; bestX = cx; bestY = cy; }
            if (moved < tolerance) { converged = true; break; }
        }

        // 迭代可能振荡：始终交出历史最优圆心，而不是最后一次。
        cx = bestX; cy = bestY;
        var maxError = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var dx = points[index].X - cx;
            var dy = points[index].Y - cy;
            var error = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius);
            if (error > maxError) maxError = error;
        }
        var withinTolerance = maxRms <= 0 || bestRms <= maxRms;
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("radius", radius), ("centerX", cx), ("centerY", cy),
            ("rmsError", bestRms), ("maxError", maxError), ("pointCount", points.Count),
            ("iterations", iterations), ("converged", converged), ("withinTolerance", withinTolerance),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["circle"] = VisionValue.Circle(new VisionCircle(cx, cy, radius)),
                ["centerX"] = VisionValue.Double(cx),
                ["centerY"] = VisionValue.Double(cy),
                ["radius"] = VisionValue.Double(radius),
                ["rmsError"] = VisionValue.Double(bestRms),
                ["maxError"] = VisionValue.Double(maxError),
                ["pointCount"] = VisionValue.Integer(points.Count),
                ["iterations"] = VisionValue.Integer(iterations),
                ["found"] = VisionValue.Boolean(true),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["radius"] = radius, ["centerX"] = cx, ["centerY"] = cy,
                ["rmsError"] = bestRms, ["maxError"] = maxError, ["pointCount"] = points.Count,
                ["iterations"] = iterations, ["converged"] = converged,
                ["withinTolerance"] = withinTolerance,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    private static double ResidualRms(IReadOnlyList<VisionPoint2D> points, double cx, double cy, double radius)
    {
        var sum = 0d;
        foreach (var point in points)
        {
            var dx = point.X - cx;
            var dy = point.Y - cy;
            var error = Math.Sqrt(dx * dx + dy * dy) - radius;
            sum += error * error;
        }
        return Math.Sqrt(sum / points.Count);
    }

    private static NodeExecutorResult Missing(string reason, int count, double radius, Stopwatch timer)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "measure.fitCircleFixedRadius", ("found", false), ("reason", reason), ("pointCount", count), ("radius", radius),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(false),
                ["pointCount"] = VisionValue.Integer(count),
                ["iterations"] = VisionValue.Integer(0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason, ["pointCount"] = count, ["radius"] = radius,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            });
    }
}
