using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 轮廓度（tolerance.profile，2026-10-27 · 缺口分析 P1-14）。
///
/// 现场缺口：形状公差里唯一还没落地的是"轮廓度"。`measure.contourDeviation` 做的是
/// **实测轮廓 vs 另一条实测轮廓**的距离，而图纸上的轮廓度是**实测轮廓 vs 理论轮廓**：
/// 公差带的宽度是按理论几何算出来的，跟金标件无关。
///
/// 本节点用理论直线或理论圆做基准，逐点算**有符号法向距离**：
/// - 直线：正值在 +90° 法向侧（右向线的图像下方）；
/// - 圆：正值在圆外（半径偏大）。
///
/// 三种公差带口径，与图纸标注一一对应：
/// - bilateral：公差带对称跨在理论轮廓两侧，实测带宽 = max − min；
/// - unilateralOutside：公差带只在材料外侧，要求所有偏差 ≥ −ε，带宽 = max(0, max)；
/// - unilateralInside：同理在内侧，带宽 = max(0, −min)。
/// 单侧模式下若有点落在错误一侧，直接判不合格并在 diagnostics 写明 crossedSampleCount——
/// 这比"把越界点也算进带宽"更接近计量室的口径。
/// </summary>
public sealed class ProfileToleranceNode : IVisionNodeExecutor
{
    public string Type => "tolerance.profile";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var mode = node.GetString("mode", "bilateral").Trim().ToLowerInvariant();
        if (mode is not ("bilateral" or "unilateraloutside" or "unilateralinside"))
            throw new InvalidOperationException(
                "tolerance.profile mode must be bilateral, unilateralOutside or unilateralInside.");

        var tolerance = node.GetDouble("tolerance", 0);
        if (!double.IsFinite(tolerance) || tolerance is < 0 or > 1_000_000)
            throw new InvalidOperationException("tolerance.profile tolerance must be finite within 0..1000000 (0 disables).");

        var samples = context.Require<IReadOnlyList<VisionPoint2D>>("samples");
        if (samples.Count > ToleranceGeometry.MaxPoints)
            throw new InvalidOperationException(
                $"tolerance.profile sample budget exceeded (max {ToleranceGeometry.MaxPoints} points).");
        if (samples.Count < 3)
        {
            return ValueTask.FromResult(ToleranceGeometry.Missing(samples.Count,
                $"profile needs at least 3 samples, got {samples.Count}", nodeType: Type));
        }

        var hasLine = context.TryGetValue("nominalLine", out _);
        var hasCircle = context.TryGetValue("nominalCircle", out _);
        if (hasLine == hasCircle)
            throw new InvalidOperationException(
                "tolerance.profile requires exactly one nominal: nominalLine or nominalCircle.");

        var deviations = new double[samples.Count];
        var nominalRadius = 0d;
        if (hasLine)
        {
            var line = context.Require<VisionLine2D>("nominalLine");
            var (_, ux, uy, _) = ToleranceGeometry.Line(line);
            for (var index = 0; index < samples.Count; index++)
            {
                var point = samples[index];
                GdtMath.Finite(point.X, point.Y);
                var rx = point.X - line.Start.X;
                var ry = point.Y - line.Start.Y;
                // 有符号法向距离：正 = 线的 +90° 侧（右向线即图像下方）。
                deviations[index] = ry * ux - rx * uy;
            }
        }
        else
        {
            var circle = context.Require<VisionCircle>("nominalCircle");
            GdtMath.Finite(circle.X, circle.Y, circle.Radius);
            if (circle.Radius <= 0)
                throw new InvalidOperationException("tolerance.profile nominalCircle radius must be positive.");
            nominalRadius = circle.Radius;
            for (var index = 0; index < samples.Count; index++)
            {
                var point = samples[index];
                GdtMath.Finite(point.X, point.Y);
                var dx = point.X - circle.X;
                var dy = point.Y - circle.Y;
                deviations[index] = Math.Sqrt(dx * dx + dy * dy) - circle.Radius;
            }
        }

        var maxIndex = 0;
        var minIndex = 0;
        var sum = 0d;
        var squared = 0d;
        for (var index = 0; index < deviations.Length; index++)
        {
            if (deviations[index] > deviations[maxIndex]) maxIndex = index;
            if (deviations[index] < deviations[minIndex]) minIndex = index;
            sum += deviations[index];
            squared += deviations[index] * deviations[index];
        }

        var maxDeviation = deviations[maxIndex];
        var minDeviation = deviations[minIndex];
        var mean = sum / deviations.Length;
        var rms = Math.Sqrt(squared / deviations.Length);

        var epsilon = 1e-9;
        var crossed = 0;
        double profile;
        switch (mode)
        {
            case "unilateraloutside":
                crossed = deviations.Count(value => value < -epsilon);
                profile = Math.Max(0, maxDeviation);
                break;
            case "unilateralinside":
                crossed = deviations.Count(value => value > epsilon);
                profile = Math.Max(0, -minDeviation);
                break;
            default:
                profile = maxDeviation - minDeviation;
                break;
        }

        var coverageLength = 0d;
        var maxAdjacentPointDistance = 0d;
        for (var index = 1; index < samples.Count; index++)
        {
            var spacing = MeasurementMath.Hypot(
                samples[index].X - samples[index - 1].X, samples[index].Y - samples[index - 1].Y);
            coverageLength += spacing;
            maxAdjacentPointDistance = Math.Max(maxAdjacentPointDistance, spacing);
        }
        var meanSampleSpacing = samples.Count > 1 ? coverageLength / (samples.Count - 1) : 0;

        var firstCrossedIndex = -1;
        if (crossed > 0)
        {
            for (var index = 0; index < deviations.Length; index++)
            {
                var wrongSide = mode == "unilateraloutside" ? deviations[index] < -epsilon : deviations[index] > epsilon;
                if (wrongSide) { firstCrossedIndex = index; break; }
            }
        }

        var worstPointIndex = mode == "unilateralinside" ? minIndex : maxIndex;
        var worstSignedDeviation = deviations[worstPointIndex];
        var gateEnabled = tolerance > 0;
        var within = !gateEnabled || (profile <= tolerance + epsilon && crossed == 0);

        // 稀疏采样会漏掉两点之间的窄高点：贴着公差上限时要求加密复测，而不是直接放行。
        // 圆基准用弦高 s²/(8R) 作误差上界；直线基准没有曲率可依，取保守的半个采样间距。
        var samplingErrorBoundPx = hasCircle && nominalRadius > 1e-12
            ? maxAdjacentPointDistance * maxAdjacentPointDistance / (8 * nominalRadius)
            : maxAdjacentPointDistance / 2;
        var samplingDensitySufficient = !gateEnabled || samplingErrorBoundPx <= tolerance / 4 + epsilon;
        var denseResampleRequired = !samplingDensitySufficient && within && profile > tolerance * 0.8;
        if (denseResampleRequired) within = false;

        var reason = !gateEnabled ? "tolerance-disabled"
            : crossed > 0 ? "crossed-material-side"
            : profile > tolerance + epsilon ? "profile-exceeded"
            : denseResampleRequired ? "dense-resample-required"
            : "ok";
        var worstPoint = samples[worstPointIndex];

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("mode", mode), ("sampleCount", samples.Count), ("tolerance", tolerance),
            ("profile", profile), ("maxDeviation", maxDeviation), ("minDeviation", minDeviation),
            ("meanDeviation", mean), ("rmsDeviation", rms), ("crossedSampleCount", crossed),
            ("firstCrossedIndex", firstCrossedIndex), ("worstPointIndex", worstPointIndex),
            ("worstSignedDeviation", worstSignedDeviation),
            ("nominal", hasLine ? "line" : "circle"), ("nominalType", hasLine ? "line" : "circle"),
            ("meanSampleSpacing", meanSampleSpacing), ("maxAdjacentPointDistance", maxAdjacentPointDistance),
            ("samplingErrorBoundPx", samplingErrorBoundPx),
            ("coverageLength", coverageLength), ("samplingDensitySufficient", samplingDensitySufficient),
            ("reason", reason), ("withinTolerance", within));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(true),
                ["sampleCount"] = VisionValue.Integer(samples.Count),
                ["profile"] = VisionValue.Double(profile),
                ["maxDeviation"] = VisionValue.Double(maxDeviation),
                ["minDeviation"] = VisionValue.Double(minDeviation),
                ["meanDeviation"] = VisionValue.Double(mean),
                ["rmsDeviation"] = VisionValue.Double(rms),
                ["crossedSampleCount"] = VisionValue.Integer(crossed),
                ["firstCrossedIndex"] = VisionValue.Integer(firstCrossedIndex),
                ["worstPointIndex"] = VisionValue.Integer(worstPointIndex),
                ["worstSignedDeviation"] = VisionValue.Double(worstSignedDeviation),
                ["worstPoint"] = VisionValue.Point(worstPoint),
                ["nominalType"] = VisionValue.String(hasLine ? "line" : "circle"),
                ["meanSampleSpacing"] = VisionValue.Double(meanSampleSpacing),
                ["maxAdjacentPointDistance"] = VisionValue.Double(maxAdjacentPointDistance),
                ["samplingErrorBoundPx"] = VisionValue.Double(samplingErrorBoundPx),
                ["coverageLength"] = VisionValue.Double(coverageLength),
                ["samplingDensitySufficient"] = VisionValue.Boolean(samplingDensitySufficient),
                ["reason"] = VisionValue.String(reason),
                ["withinTolerance"] = VisionValue.Boolean(within),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["mode"] = mode, ["sampleCount"] = samples.Count, ["tolerance"] = tolerance,
                ["profile"] = profile, ["maxDeviation"] = maxDeviation, ["minDeviation"] = minDeviation,
                ["meanDeviation"] = mean, ["rmsDeviation"] = rms, ["crossedSampleCount"] = crossed,
                ["firstCrossedIndex"] = firstCrossedIndex, ["worstPointIndex"] = worstPointIndex,
                ["worstSignedDeviation"] = worstSignedDeviation,
                ["meanSampleSpacing"] = meanSampleSpacing, ["maxAdjacentPointDistance"] = maxAdjacentPointDistance,
                ["samplingErrorBoundPx"] = samplingErrorBoundPx, ["coverageLength"] = coverageLength,
                ["samplingDensitySufficient"] = samplingDensitySufficient,
                ["nominal"] = hasLine ? "line" : "circle", ["nominalType"] = hasLine ? "line" : "circle",
                ["reason"] = reason, ["withinTolerance"] = within,
                ["diagnostics"] = diagnostics
            }));
    }
}
