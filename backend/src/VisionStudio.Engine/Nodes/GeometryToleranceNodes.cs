using System.Text.Json;
using JLVisionLib;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

public sealed class ContourFromPointsNode : IVisionNodeExecutor
{
    public string Type => "contour.fromPoints";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var closed = node.GetBool("closed", true);
        VisionPoint2D[] raw;
        var source = context.TryGetValue("pointList",out var wired)?"pointList-port":context.TryGetValue("pointsJson",out _)?"pointsJson-port":"pointsJson-parameter";
        if (source=="pointList-port")
        {
            // 断链修复：轮廓/检测点集可以直连，不再强制走 JSON 文本
            var list = wired.Require<IReadOnlyList<VisionPoint2D>>("pointList");
            if (list.Count > ToleranceGeometry.MaxPoints) throw new InvalidOperationException("Contour requires at most 20000 points.");
            raw = list.ToArray();
        }
        else
        {
            var json = context.TryGetValue("pointsJson", out var input) ? input.Require<string>("pointsJson") : node.GetString("pointsJson", "[[100,100],[200,100],[200,200],[100,200]]");
            if (json.Length > 2_097_152) throw new InvalidOperationException("Contour JSON exceeds 2 MiB of text.");
            List<double[]> data;
            try { data = JsonSerializer.Deserialize<List<double[]>>(json) ?? throw new JsonException("null points"); }
            catch (JsonException ex) { throw new InvalidOperationException("Contour points JSON must be an array of [x,y] pairs.", ex); }
            if (data.Count > ToleranceGeometry.MaxPoints || data.Any(p => p is null || p.Length != 2)) throw new InvalidOperationException("Contour requires at most 20000 [x,y] pairs.");
            raw = data.Select(p => new VisionPoint2D(p[0], p[1])).ToArray();
        }
        var points = ToleranceGeometry.Points(raw, closed);
        var removedCount=raw.Length-points.Length;
        var sourceIndices=new List<int>();
        for(var i=0;i<raw.Length;i++)
        {
            if(sourceIndices.Count==0 || raw[i]!=points[sourceIndices.Count-1]) { sourceIndices.Add(i); if(sourceIndices.Count==points.Length) break; }
        }
        if(sourceIndices.Count!=points.Length)
        {
            sourceIndices=[];
            for(var i=0;i<raw.Length&&sourceIndices.Count<points.Length;i++) if(raw[i]==points[sourceIndices.Count]) sourceIndices.Add(i);
        }
        var indexMap=JsonSerializer.Serialize(sourceIndices);
        if (points.Distinct().Count() < 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", new() { ["contours"] = VisionValue.Contours([]), ["count"] = VisionValue.Integer(0),["warningCount"]=VisionValue.Integer(removedCount),["sourceIndicesJson"]=VisionValue.String(indexMap),["inputSource"]=VisionValue.String(source) }, nodeType: Type));
        using var curve = ToleranceGeometry.Curve(points, closed);
        using var length = curve.LengthXld();
        var area = closed && points.Length >= 3 ? Math.Abs(curve.AreaCenterXld(out double _, out double _, out string _)) : 0;
        curve.SmallestRectangle1Xld(out double top, out double left, out double bottom, out double right);
        curve.SmallestCircleXld(out double row, out double col, out double radius);
        var perimeter = length.D;
        var contour = new VisionContour(0, points, -1, false, area, perimeter, perimeter > 0 ? Math.Min(1, 4 * Math.PI * area / (perimeter * perimeter)) : 0,
            new(left, top, right - left, bottom - top), new(col, row, radius)) { Closed = closed };
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", points.Length), ("area", area),
            ("perimeter", perimeter), ("closed", closed), ("boundingWidth", right - left), ("boundingHeight", bottom - top),
            ("inputSource",source),("inputCount",raw.Length),("removedCount",removedCount),("sourceIndices",sourceIndices));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([contour]), ["found"] = VisionValue.Boolean(true),
            ["count"] = VisionValue.Integer(1), ["sampleCount"] = VisionValue.Integer(points.Length),
            ["warningCount"] = VisionValue.Integer(removedCount),["sourceIndicesJson"] = VisionValue.String(indexMap),["inputSource"] = VisionValue.String(source),
            ["diagnostics"] = VisionValue.String(diag)
        },
            new Dictionary<string, object?> { ["found"] = true, ["sampleCount"] = points.Length, ["area"] = area, ["perimeter"] = perimeter, ["closed"] = closed, ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag },
            [new("point-contour", VisionOverlayType.Contour, Points: (closed ? points.Append(points[0]) : points).Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray())]));
    }
}

public sealed class WallThicknessNode : IVisionNodeExecutor
{
    public string Type => "measure.wallThickness";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var inner = ToleranceGeometry.Circle(context, "inner"); var outer = ToleranceGeometry.Circle(context, "outer");
        if (outer.Radius <= inner.Radius)
            throw new InvalidOperationException("measure.wallThickness requires outer.radius > inner.radius.");
        var count = node.GetInt("sampleCount", 360); var start = node.GetDouble("startAngleDeg", 0);
        if (count is < 8 or > 4096 || !double.IsFinite(start) || start is < -3600 or > 3600) throw new InvalidOperationException("Wall samples must be 8..4096 and start angle -3600..3600 degrees.");
        var dx = outer.X - inner.X; var dy = outer.Y - inner.Y; var distance = MeasurementMath.Hypot(dx, dy);
        if (distance + inner.Radius > outer.Radius)
        {
            var missDiag = CoordinateValidation.Diagnostics(Type, ("found", false), ("reason", "inner-disk-not-contained"),
                ("contained", false), ("centerDistance", distance), ("innerRadius", inner.Radius), ("outerRadius", outer.Radius));
            return ValueTask.FromResult(ToleranceGeometry.Missing(0, "inner-disk-not-contained", new()
            {
                ["contained"] = VisionValue.Boolean(false), ["centerDistance"] = VisionValue.Double(distance),
                ["toleranceEnabled"] = VisionValue.Boolean(false),
                ["samplesJson"] = VisionValue.String("[]"), ["diagnostics"] = VisionValue.String(missDiag)
            }, nodeType: Type));
        }
        var values = new double[count]; var samples = new List<object>(); var overlays = new List<VisionOverlay>();
        // 闭式厚度函数：min/max 由几何直接给出；均值只能靠角度积分，有限采样是近似。
        double ThicknessAt(double angleDeg)
        {
            var radians = angleDeg * Math.PI / 180;
            var ux = Math.Cos(radians); var uy = Math.Sin(radians);
            var projection = dx * ux + dy * uy;
            var t = projection + Math.Sqrt(Math.Max(0, outer.Radius * outer.Radius - distance * distance + projection * projection));
            return Math.Max(0, t - inner.Radius);
        }
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var angle = start + 360d * i / count; var radians = angle * Math.PI / 180; var ux = Math.Cos(radians); var uy = Math.Sin(radians);
            values[i] = ThicknessAt(angle);
            var projection = dx * ux + dy * uy;
            var t = projection + Math.Sqrt(Math.Max(0, outer.Radius * outer.Radius - distance * distance + projection * projection));
            var a = new VisionPoint2D(inner.X + inner.Radius * ux, inner.Y + inner.Radius * uy);
            var b = new VisionPoint2D(inner.X + t * ux, inner.Y + t * uy);
            samples.Add(new { AngleDeg = angle, Inner = a, Outer = b, Thickness = values[i] });
            if (i % (int)Math.Ceiling(count / 200d) == 0) overlays.Add(ToleranceGeometry.Segment($"wall-{i}", new(a, b)));
        }
        var min = Math.Max(0, outer.Radius - inner.Radius - distance); var max = outer.Radius - inner.Radius + distance;
        var mean = values.Average(); var deviation = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        // 同一闭式函数用 8 倍密的网格再积一次，得到均值近似误差与采样密度证据。
        var fineCount = Math.Max(4096, count * 8);
        double fineSum = 0;
        for (var i = 0; i < fineCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fineSum += ThicknessAt(start + 360d * i / fineCount);
        }
        var meanAnalytic = fineSum / fineCount;
        var meanApproximationError = Math.Abs(mean - meanAnalytic);
        var angularStepDeg = 360d / count;
        var samplingPhaseDeg = ((start % 360) + 360) % 360;
        var maxMeanApproximationError = node.GetDouble("maxMeanApproximationError", 0);
        if (!double.IsFinite(maxMeanApproximationError) || maxMeanApproximationError < 0)
            throw new InvalidOperationException("measure.wallThickness maxMeanApproximationError must be finite and >= 0 (0 disables).");
        var samplingDensitySufficient = maxMeanApproximationError <= 0 || meanApproximationError <= maxMeanApproximationError;
        var nominal = node.GetDouble("nominalThickness", 0);
        var tolerance = node.GetDouble("tolerance", 0);
        if (!double.IsFinite(nominal) || nominal < 0) throw new InvalidOperationException("nominalThickness must be finite and >= 0 (0 disables the gate).");
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new InvalidOperationException("tolerance must be finite and >= 0.");
        if (nominal > 0 && tolerance <= 0) throw new InvalidOperationException("tolerance must be > 0 when nominalThickness is set.");
        var minimumLimit = node.GetDouble("minThicknessLimit", 0);
        var maximumLimit = node.GetDouble("maxThicknessLimit", 0);
        if (!double.IsFinite(minimumLimit) || minimumLimit < 0 || !double.IsFinite(maximumLimit) || maximumLimit < 0
            || maximumLimit > 0 && maximumLimit < minimumLimit)
            throw new InvalidOperationException("Wall thickness extrema limits must be finite, non-negative and ordered.");
        var meanWithin = nominal <= 0 || Math.Abs(mean - nominal) <= tolerance;
        var within = meanWithin && (minimumLimit <= 0 || min >= minimumLimit)
            && (maximumLimit <= 0 || max <= maximumLimit) && samplingDensitySufficient;
        var reason = !meanWithin ? "mean-thickness-exceeded"
            : minimumLimit > 0 && min < minimumLimit ? "min-thickness-below-limit"
            : maximumLimit > 0 && max > maximumLimit ? "max-thickness-above-limit"
            : !samplingDensitySufficient ? "sampling-density-insufficient"
            : "ok";
        var variation = max - min;
        var cv = Math.Abs(mean) > 1e-12 ? deviation / Math.Abs(mean) : 0;
        var uniformity = mean > 1e-12 ? Math.Max(0, 1 - variation / mean) : 0;
        var diag = CoordinateValidation.Diagnostics(Type,
            ("found", true), ("contained", true), ("sampleCount", count), ("centerDistance", distance),
            ("minThickness", min), ("maxThickness", max), ("meanThickness", mean), ("stdDev", deviation),
            ("variation", variation), ("cv", cv), ("uniformity", uniformity),
            ("nominalThickness", nominal), ("tolerance", tolerance),
            ("minThicknessLimit", minimumLimit), ("maxThicknessLimit", maximumLimit),
            ("meanWithinTolerance", meanWithin), ("withinTolerance", within), ("reason", reason),
            ("minMaxSource", "analytic-closed-form"), ("integrationMethod", "midpoint-uniform-angle"),
            ("meanIsApproximate", true), ("angularStepDeg", angularStepDeg), ("samplingPhaseDeg", samplingPhaseDeg),
            ("meanAnalytic", meanAnalytic), ("meanApproximationError", meanApproximationError),
            ("samplingDensitySufficient", samplingDensitySufficient));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["contained"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(count), ["centerDistance"] = VisionValue.Double(distance),
            ["minThickness"] = VisionValue.Double(min), ["maxThickness"] = VisionValue.Double(max), ["meanThickness"] = VisionValue.Double(mean), ["variation"] = VisionValue.Double(variation),
            ["stdDev"] = VisionValue.Double(deviation), ["cv"] = VisionValue.Double(cv), ["uniformity"] = VisionValue.Double(uniformity),
            ["withinTolerance"] = VisionValue.Boolean(within), ["reason"] = VisionValue.String(reason),
            ["toleranceEnabled"] = VisionValue.Boolean(nominal > 0 || minimumLimit > 0 || maximumLimit > 0),
            ["minMaxSource"] = VisionValue.String("analytic-closed-form"),
            ["integrationMethod"] = VisionValue.String("midpoint-uniform-angle"),
            ["meanIsApproximate"] = VisionValue.Boolean(true),
            ["angularStepDeg"] = VisionValue.Double(angularStepDeg), ["samplingPhaseDeg"] = VisionValue.Double(samplingPhaseDeg),
            ["meanAnalytic"] = VisionValue.Double(meanAnalytic), ["meanApproximationError"] = VisionValue.Double(meanApproximationError),
            ["samplingDensitySufficient"] = VisionValue.Boolean(samplingDensitySufficient),
            ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)), ["diagnostics"] = VisionValue.String(diag)
        }, new Dictionary<string, object?> { ["found"] = true, ["contained"] = true, ["minThickness"] = min, ["maxThickness"] = max, ["meanThickness"] = mean, ["meanAnalytic"] = meanAnalytic, ["meanApproximationError"] = meanApproximationError, ["angularStepDeg"] = angularStepDeg, ["samplingPhaseDeg"] = samplingPhaseDeg, ["sampleCount"] = count, ["unit"] = "px", ["withinTolerance"] = within, ["reason"] = reason, ["diagnostics"] = diag }, overlays));
    }
}

public sealed class ConcentricityNode : IVisionNodeExecutor
{
    public string Type => "measure.concentricity";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var a = ToleranceGeometry.Circle(context, "circleA"); var b = ToleranceGeometry.Circle(context, "circleB");
        var offset = new VisionPoint2D(b.X - a.X, b.Y - a.Y); var distance = MeasurementMath.Hypot(offset.X, offset.Y);
        if (!double.IsFinite(distance) || !double.IsFinite(offset.X) || !double.IsFinite(offset.Y)) throw new InvalidOperationException("measure.concentricity produced non-finite output.");
        var limit = node.GetDouble("maxCenterDistance", 0); if (!double.IsFinite(limit) || limit < 0) throw new InvalidOperationException("measure.concentricity maxCenterDistance must be finite and >= 0 (0 disables the tolerance).");
        var requireQualityKnown = node.GetBool("requireQualityKnown", false);
        var maxFitError = node.GetDouble("maxFitError", 0);
        var minSampleCount = node.GetInt("minSampleCount", 0);
        var minCoverageDeg = node.GetDouble("minCoverageDeg", 0);
        if (!double.IsFinite(maxFitError) || maxFitError < 0)
            throw new InvalidOperationException("measure.concentricity maxFitError must be finite and >= 0 (0 disables the gate).");
        if (minSampleCount < 0)
            throw new InvalidOperationException("measure.concentricity minSampleCount must be >= 0 (0 disables the gate).");
        if (!double.IsFinite(minCoverageDeg) || minCoverageDeg is < 0 or > 360)
            throw new InvalidOperationException("measure.concentricity minCoverageDeg must be within 0..360 (0 disables the gate).");

        var qualityA = ReadQuality(context, "circleA", "qualityA");
        var qualityB = ReadQuality(context, "circleB", "qualityB");
        // 质量门一旦配置就必须有证据：缺少元数据时不得给出合格结论。
        string? qualityFailure = null;
        foreach (var (name, quality) in new[] { ("circleA", qualityA), ("circleB", qualityB) })
        {
            if (quality is null)
            {
                if (requireQualityKnown) qualityFailure ??= "quality-unknown:" + name;
                continue;
            }
            if (maxFitError > 0)
            {
                if (quality.FitError is not { } fitError) qualityFailure ??= "quality-unknown:" + name;
                else if (fitError > maxFitError + 1e-12) qualityFailure ??= "quality-insufficient:" + name;
            }
            if (minSampleCount > 0 && (quality.SampleCount ?? 0) < minSampleCount) qualityFailure ??= "quality-insufficient:" + name;
            if (minCoverageDeg > 0 && (quality.CoverageDeg ?? 0) < minCoverageDeg) qualityFailure ??= "quality-insufficient:" + name;
        }
        var qualityOk = qualityFailure is null;
        var toleranceEnabled = limit > 0;
        var within = toleranceEnabled && distance <= limit + 1e-9 && qualityOk;
        var evaluationState = !toleranceEnabled ? "not-evaluated" : !qualityOk ? "quality-insufficient" : within ? "pass" : "fail";
        var reason = !toleranceEnabled ? "not-evaluated" : !qualityOk ? qualityFailure! : within ? "ok" : "center-distance-exceeded";
        // circleA 是基准：centerOffset = circleB - circleA，角度按图像坐标系逆时针为正。
        var angle = Math.Atan2(offset.Y, offset.X) * 180 / Math.PI;
        var diag = CoordinateValidation.Diagnostics(Type,
            ("centerDistance", distance), ("diametralOffset", 2 * distance), ("offsetAngleDeg", angle),
            ("deltaX", offset.X), ("deltaY", offset.Y), ("referenceCircle", "circleA"),
            ("maxCenterDistance", limit), ("toleranceEnabled", toleranceEnabled),
            ("requireQualityKnown", requireQualityKnown), ("maxFitError", maxFitError),
            ("minSampleCount", minSampleCount), ("minCoverageDeg", minCoverageDeg),
            ("fitErrorA", qualityA?.FitError), ("fitErrorB", qualityB?.FitError),
            ("sampleCountA", qualityA?.SampleCount), ("sampleCountB", qualityB?.SampleCount),
            ("coverageDegA", qualityA?.CoverageDeg), ("coverageDegB", qualityB?.CoverageDeg),
            ("evaluationState", evaluationState), ("reason", reason), ("withinTolerance", within));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["centerDistance"] = VisionValue.Double(distance), ["diametralOffset"] = VisionValue.Double(2 * distance),
            ["centerOffset"] = VisionValue.Point(offset), ["deltaX"] = VisionValue.Double(offset.X), ["deltaY"] = VisionValue.Double(offset.Y),
            ["offsetAngleDeg"] = VisionValue.Double(angle), ["referenceCircle"] = VisionValue.String("circleA"),
            ["withinTolerance"] = VisionValue.Boolean(within), ["toleranceEnabled"] = VisionValue.Boolean(toleranceEnabled),
            ["qualityStatusA"] = VisionValue.String(qualityA is null ? "unknown" : "known"),
            ["qualityStatusB"] = VisionValue.String(qualityB is null ? "unknown" : "known"),
            ["evaluationState"] = VisionValue.String(evaluationState), ["reason"] = VisionValue.String(reason),
            ["diagnostics"] = VisionValue.String(diag)
        };
        if (qualityA?.FitError is { } errorA) outputs["fitErrorA"] = VisionValue.Double(errorA);
        if (qualityB?.FitError is { } errorB) outputs["fitErrorB"] = VisionValue.Double(errorB);
        if (qualityA?.SampleCount is { } countA) outputs["sampleCountA"] = VisionValue.Integer(countA);
        if (qualityB?.SampleCount is { } countB) outputs["sampleCountB"] = VisionValue.Integer(countB);
        if (qualityA?.CoverageDeg is { } coverageA) outputs["coverageDegA"] = VisionValue.Double(coverageA);
        if (qualityB?.CoverageDeg is { } coverageB) outputs["coverageDegB"] = VisionValue.Double(coverageB);
        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["centerDistance"] = distance, ["diametralOffset"] = 2 * distance, ["unit"] = "px",
                ["offsetAngleDeg"] = angle, ["referenceCircle"] = "circleA",
                ["withinTolerance"] = within, ["toleranceEnabled"] = toleranceEnabled,
                ["qualityStatusA"] = qualityA is null ? "unknown" : "known",
                ["qualityStatusB"] = qualityB is null ? "unknown" : "known",
                ["evaluationState"] = evaluationState, ["reason"] = reason, ["diagnostics"] = diag
            },
            [ToleranceGeometry.Segment("circle-centre-offset", new(new(a.X, a.Y), new(b.X, b.Y)))]));
    }

    /// <summary>上游圆拟合的可选质量证据：缺省表示调用方没有提供，不等于质量合格。</summary>
    private sealed record FitQuality(double? FitError, int? SampleCount, double? CoverageDeg);

    private static FitQuality? ReadQuality(NodeExecutionContext context, string label, string port)
    {
        if (!context.TryGetValue(port, out var value)) return null;
        var json = value.Require<string>(port);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{label} quality '{port}' must be a JSON object with fitError/sampleCount/coverageDeg.");
            double? fitError = null; int? sampleCount = null; double? coverageDeg = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name;
                if (key.Equals("fitError", StringComparison.OrdinalIgnoreCase) || key.Equals("rmsError", StringComparison.OrdinalIgnoreCase) || key.Equals("residualPx", StringComparison.OrdinalIgnoreCase))
                    fitError = property.Value.GetDouble();
                else if (key.Equals("sampleCount", StringComparison.OrdinalIgnoreCase) || key.Equals("inlierCount", StringComparison.OrdinalIgnoreCase))
                    sampleCount = property.Value.GetInt32();
                else if (key.Equals("coverageDeg", StringComparison.OrdinalIgnoreCase))
                    coverageDeg = property.Value.GetDouble();
            }
            if (fitError is < 0 || sampleCount is < 0 || coverageDeg is < 0 or > 360)
                throw new InvalidOperationException($"{label} quality '{port}' has out-of-range values.");
            return new FitQuality(fitError, sampleCount, coverageDeg);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{label} quality '{port}' is not valid JSON: {ex.Message}");
        }
    }
}

public sealed class RoundnessNode : IVisionNodeExecutor
{
    public string Type => "measure.roundness";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var coverageLimit = node.GetDouble("minCoverageDeg", 270);
        if (!double.IsFinite(coverageLimit) || coverageLimit is < 0 or > 360) throw new InvalidOperationException("Minimum angular coverage must be 0..360 degrees.");
        var maxDeviation = node.GetDouble("maxDeviation", 0);
        if (!double.IsFinite(maxDeviation) || maxDeviation < 0)
            throw new InvalidOperationException("measure.roundness maxDeviation must be finite and >= 0 (0 disables the tolerance).");
        var maxAngularGapDeg = node.GetDouble("maxAngularGapDeg", 0);
        var minPointsPerSector = node.GetInt("minPointsPerSector", 0);
        var maxRobustShift = node.GetDouble("maxRobustShift", 0);
        if (!double.IsFinite(maxAngularGapDeg) || maxAngularGapDeg is < 0 or > 360 ||
            minPointsPerSector is < 0 or > 100000 ||
            !double.IsFinite(maxRobustShift) || maxRobustShift < 0)
            throw new InvalidOperationException("measure.roundness sampling/robustness gates are invalid (0 disables each).");
        var points = ToleranceGeometry.Select(context, node);
        if (points.Distinct().Count() < 6) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));
        var mx = points.Average(p => p.X); var my = points.Average(p => p.Y);
        var xx = points.Sum(p => (p.X - mx) * (p.X - mx)); var yy = points.Sum(p => (p.Y - my) * (p.Y - my)); var xy = points.Sum(p => (p.X - mx) * (p.Y - my));
        if (xx * yy - xy * xy <= (xx + yy) * (xx + yy) * 1e-12) throw new InvalidOperationException("Circle samples are collinear or ill-conditioned.");
        var circle = JLVisionPointFitting.Circle(points); ToleranceGeometry.Finite(circle.X, circle.Y, circle.Radius);
        var angles = points.Select(p => Math.Atan2(p.Y - circle.Y, p.X - circle.X) * 180 / Math.PI).Order().ToArray();
        var gap = angles[0] + 360 - angles[^1]; for (var i = 1; i < angles.Length; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
        var coverage = 360 - gap;
        if (coverage + 1e-8 < coverageLimit)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-angular-coverage", new() { ["coverageDeg"] = VisionValue.Double(coverage) }, nodeType: Type));

        // 总覆盖够不代表采样均匀：8 个 45° 扇区里只要出现空洞，径向残差就会被少数弧段支配。
        var sectorCounts = new int[8];
        foreach (var angle in angles)
        {
            var normalized = ((angle % 360) + 360) % 360;
            var sector = Math.Min(7, (int)Math.Floor(normalized / 45));
            sectorCounts[sector]++;
        }
        var minSectorPointCount = sectorCounts.Min();
        var emptySectorCount = sectorCounts.Count(count => count == 0);
        var meanAngularStepDeg = angles.Length > 1 ? coverage / (angles.Length - 1) : 0;
        var samplingUniformityOk =
            (maxAngularGapDeg <= 0 && minPointsPerSector <= 0) ||
            ((maxAngularGapDeg <= 0 || gap <= maxAngularGapDeg + 1e-9) &&
             (minPointsPerSector <= 0 || minSectorPointCount >= minPointsPerSector));
        if (!samplingUniformityOk)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "uneven-angular-sampling", new()
            {
                ["coverageDeg"] = VisionValue.Double(coverage),
                ["maxGapDeg"] = VisionValue.Double(gap),
                ["minSectorPointCount"] = VisionValue.Integer(minSectorPointCount),
                ["emptySectorCount"] = VisionValue.Integer(emptySectorCount)
            }, nodeType: Type));

        var radii = points.Select(p => MeasurementMath.Hypot(p.X - circle.X, p.Y - circle.Y)).ToArray();
        var minimum = radii.Min(); var maximum = radii.Max(); var rms = Math.Sqrt(radii.Average(v => (v - circle.Radius) * (v - circle.Radius)));
        cancellationToken.ThrowIfCancellationRequested();
        var deviation = maximum - minimum;
        var within = maxDeviation <= 0 || deviation <= maxDeviation;
        var outOfRoundPct = circle.Radius > 1e-12 ? 100 * deviation / circle.Radius : 0;
        var relativeRms = circle.Radius > 1e-12 ? rms / circle.Radius : 0;
        if (circle.Radius < 1e-9)
            throw new InvalidOperationException("measure.roundness fitted circle radius is degenerate.");

        // 最小二乘圆对单个毛刺敏感：用 MAD 剔除离群点后再拟一次，两套结果的圆心/半径差就是放行风险。
        var robustCircle = RobustCircle(points, circle, out var robustSampleCount);
        var robustCenterShift = MeasurementMath.Hypot(robustCircle.X - circle.X, robustCircle.Y - circle.Y);
        var robustRadiusShift = Math.Abs(robustCircle.Radius - circle.Radius);
        var robustAgreement = maxRobustShift <= 0 || Math.Max(robustCenterShift, robustRadiusShift) <= maxRobustShift + 1e-9;
        if (!robustAgreement) within = false;
        var resultReleasable = within && samplingUniformityOk && robustAgreement;

        var peakOut = maximum - circle.Radius;
        var peakIn = circle.Radius - minimum;
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", points.Length), ("coverageDeg", coverage),
            ("radius", circle.Radius), ("minRadius", minimum), ("maxRadius", maximum), ("deviation", deviation), ("rms", rms),
            ("outOfRoundPct", outOfRoundPct), ("relativeRms", relativeRms),
            ("peakOutward", peakOut), ("peakInward", peakIn), ("maxGapDeg", gap),
            ("minSectorPointCount", minSectorPointCount), ("emptySectorCount", emptySectorCount),
            ("meanAngularStepDeg", meanAngularStepDeg), ("samplingUniformityOk", samplingUniformityOk),
            ("robustCenterShift", robustCenterShift), ("robustRadiusShift", robustRadiusShift),
            ("robustSampleCount", robustSampleCount), ("robustAgreement", robustAgreement),
            ("resultReleasable", resultReleasable), ("maxDeviation", maxDeviation), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(points.Length), ["coverageDeg"] = VisionValue.Double(coverage),
                ["circle"] = VisionValue.Circle(circle), ["center"] = VisionValue.Point(new(circle.X, circle.Y)), ["radius"] = VisionValue.Double(circle.Radius),
                ["minRadius"] = VisionValue.Double(minimum), ["maxRadius"] = VisionValue.Double(maximum), ["deviation"] = VisionValue.Double(deviation),
                ["rms"] = VisionValue.Double(rms), ["outOfRoundPct"] = VisionValue.Double(outOfRoundPct), ["relativeRms"] = VisionValue.Double(relativeRms),
                ["maxGapDeg"] = VisionValue.Double(gap), ["minSectorPointCount"] = VisionValue.Integer(minSectorPointCount),
                ["emptySectorCount"] = VisionValue.Integer(emptySectorCount), ["meanAngularStepDeg"] = VisionValue.Double(meanAngularStepDeg),
                ["samplingUniformityOk"] = VisionValue.Boolean(samplingUniformityOk),
                ["robustCircle"] = VisionValue.Circle(robustCircle), ["robustCenterShift"] = VisionValue.Double(robustCenterShift),
                ["robustRadiusShift"] = VisionValue.Double(robustRadiusShift), ["robustSampleCount"] = VisionValue.Integer(robustSampleCount),
                ["robustAgreement"] = VisionValue.Boolean(robustAgreement), ["resultReleasable"] = VisionValue.Boolean(resultReleasable),
                ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["deviation"] = deviation, ["rms"] = rms, ["outOfRoundPct"] = outOfRoundPct, ["relativeRms"] = relativeRms,
                ["peakOutward"] = peakOut, ["peakInward"] = peakIn,
                ["radius"] = circle.Radius, ["sampleCount"] = points.Length, ["coverageDeg"] = coverage,
                ["maxGapDeg"] = gap, ["minSectorPointCount"] = minSectorPointCount, ["emptySectorCount"] = emptySectorCount,
                ["meanAngularStepDeg"] = meanAngularStepDeg, ["samplingUniformityOk"] = samplingUniformityOk,
                ["robustCenterShift"] = robustCenterShift, ["robustRadiusShift"] = robustRadiusShift,
                ["robustSampleCount"] = robustSampleCount, ["robustAgreement"] = robustAgreement,
                ["resultReleasable"] = resultReleasable,
                ["withinTolerance"] = within, ["maxDeviation"] = maxDeviation, ["unit"] = "px",
                ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag
            },
            [new("roundness-fit", VisionOverlayType.Circle, X: circle.X, Y: circle.Y, Radius: circle.Radius, Label: $"radial range={deviation:0.###}"),
             new("roundness-robust", VisionOverlayType.Circle, X: robustCircle.X, Y: robustCircle.Y, Radius: robustCircle.Radius, Label: "robust")]));
    }

    /// <summary>MAD 剔除离群点后重拟的对照圆，用于判断 LS 结果是否被单个毛刺拉动。</summary>
    private static VisionCircle RobustCircle(VisionPoint2D[] points, VisionCircle seed, out int inlierCount)
    {
        var residuals = points.Select(p => Math.Abs(MeasurementMath.Hypot(p.X - seed.X, p.Y - seed.Y) - seed.Radius))
            .OrderBy(value => value).ToArray();
        var median = residuals[residuals.Length / 2];
        var mad = residuals.Select(value => Math.Abs(value - median)).OrderBy(value => value).ToArray()[residuals.Length / 2];
        var threshold = Math.Max(3 * 1.4826 * mad, 1e-9);
        var inliers = points.Where(p => Math.Abs(MeasurementMath.Hypot(p.X - seed.X, p.Y - seed.Y) - seed.Radius) <= threshold).ToArray();
        if (inliers.Length < 3 || inliers.Length == points.Length)
        {
            inlierCount = inliers.Length;
            return seed;
        }
        inlierCount = inliers.Length;
        return JLVisionPointFitting.Circle(inliers);
    }
}

public sealed class StraightnessNode : IVisionNodeExecutor
{
    public string Type => "measure.straightness";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var maxDeviation = node.GetDouble("maxDeviation", 0);
        if (!double.IsFinite(maxDeviation) || maxDeviation < 0)
            throw new InvalidOperationException("measure.straightness maxDeviation must be finite and >= 0 (0 disables the tolerance).");
        var evaluationMode = node.GetString("evaluationMode", "leastSquares").Trim().ToLowerInvariant();
        if (evaluationMode is not ("leastsquares" or "minimumzone"))
            throw new InvalidOperationException("measure.straightness evaluationMode must be leastSquares or minimumZone.");
        var singleEdgeDeclared = node.GetBool("singleEdgeDeclared", false);
        var points = ToleranceGeometry.Select(context, node);
        if (points.Distinct().Count() < 3) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));
        var line = JLVisionPointFitting.Line(points); var fit = ToleranceGeometry.Line(line);
        var errors = points.Select(p => (p.X - line.Start.X) * -fit.Y + (p.Y - line.Start.Y) * fit.X).ToArray();
        var lsDeviation = errors.Max() - errors.Min();
        var rms = Math.Sqrt(errors.Average(v => v * v)); var maxAbs = errors.Max(Math.Abs);
        var length = MeasurementMath.Hypot(line.End.X - line.Start.X, line.End.Y - line.Start.Y);
        if (length < 1e-12) throw new InvalidOperationException("measure.straightness fitted line is degenerate.");
        var angleDeg = Math.Atan2(line.End.Y - line.Start.Y, line.End.X - line.Start.X) * 180 / Math.PI;

        // 最小二乘峰峰值不是最小包容带：误差方向/弯曲形态不同会让 LS 结果偏大。
        var minimumZoneDeviation = MinimumZoneWidth(points);

        // 闭合粗带或两条边同时进点集时，LS 会给出数值稳定但物理错误的“直线度”。
        var spacing = 0d;
        var pathLength = 0d;
        for (var index = 1; index < points.Length; index++)
        {
            var step = MeasurementMath.Hypot(points[index].X - points[index - 1].X, points[index].Y - points[index - 1].Y);
            spacing += step;
            pathLength += step;
        }
        spacing = points.Length > 1 ? spacing / (points.Length - 1) : 0;
        var closureGap = MeasurementMath.Hypot(points[0].X - points[^1].X, points[0].Y - points[^1].Y);
        // 闭合必须相对整条路径长度判断；一条直线点集的首尾距离等于直线长度，不能被误判为闭合。
        var closedInput = points.Length >= 4 && closureGap <= Math.Max(2 * spacing, 0.05 * pathLength) + 1e-9;

        // 有符号残差的双峰性：两组点分居拟合线两侧，且各自占比可观，即疑似量到了粗带的两条边。
        var positive = errors.Where(value => value > 0).ToArray();
        var negative = errors.Where(value => value < 0).ToArray();
        var doubleEdgeSeparation = positive.Length > 0 && negative.Length > 0 ? positive.Min() - negative.Max() : 0;
        // 锯齿状采样（逐点交替在拟合线两侧）是单条轮廓的高频噪声，不是粗带的两条边：
        // 用符号翻转率把“长行程两侧”与“逐点交替”分开。
        var signChanges = 0;
        for (var index = 1; index < errors.Length; index++)
            if (Math.Sign(errors[index]) != Math.Sign(errors[index - 1])) signChanges++;
        var alternationRatio = errors.Length > 1 ? signChanges / (double)(errors.Length - 1) : 0;
        var suspectedDoubleEdge = points.Length >= 6
                                  && lsDeviation > 1e-6 * length
                                  && alternationRatio <= 0.2
                                  && positive.Length >= Math.Max(3, points.Length / 5)
                                  && negative.Length >= Math.Max(3, points.Length / 5)
                                  && doubleEdgeSeparation > 0.6 * lsDeviation;
        if ((closedInput || suspectedDoubleEdge) && !singleEdgeDeclared)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "suspected-closed-or-double-edge", new()
            {
                ["closureGap"] = VisionValue.Double(closureGap),
                ["closedInput"] = VisionValue.Boolean(closedInput),
                ["suspectedDoubleEdge"] = VisionValue.Boolean(suspectedDoubleEdge)
            }, nodeType: Type));

        var deviation = evaluationMode == "minimumzone" ? minimumZoneDeviation : lsDeviation;
        var within = maxDeviation <= 0 || deviation <= maxDeviation;
        var relativeDeviation = length > 1e-12 ? deviation / length : 0;
        var peakPositive = errors.Max();
        var peakNegative = errors.Min();
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", points.Length),
            ("fitMethod", evaluationMode == "minimumzone" ? "minimumZone" : "leastSquares"),
            ("deviation", deviation), ("lsDeviation", lsDeviation), ("minimumZoneDeviation", minimumZoneDeviation),
            ("rms", rms), ("maxAbsError", maxAbs),
            ("relativeDeviation", relativeDeviation), ("peakPositive", peakPositive), ("peakNegative", peakNegative),
            ("length", length), ("angleDeg", angleDeg), ("closureGap", closureGap), ("closedInput", closedInput),
            ("suspectedDoubleEdge", suspectedDoubleEdge), ("singleEdgeDeclared", singleEdgeDeclared),
            ("residualAlternationRatio", alternationRatio),
            ("maxDeviation", maxDeviation), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(points.Length), ["line"] = VisionValue.Line(line),
            ["fitMethod"] = VisionValue.String(evaluationMode == "minimumzone" ? "minimumZone" : "leastSquares"),
            ["deviation"] = VisionValue.Double(deviation), ["lsDeviation"] = VisionValue.Double(lsDeviation),
            ["minimumZoneDeviation"] = VisionValue.Double(minimumZoneDeviation),
            ["rms"] = VisionValue.Double(rms), ["maxAbsError"] = VisionValue.Double(maxAbs),
            ["relativeDeviation"] = VisionValue.Double(relativeDeviation),
            ["length"] = VisionValue.Double(length), ["angleDeg"] = VisionValue.Double(angleDeg),
            ["closureGap"] = VisionValue.Double(closureGap), ["closedInput"] = VisionValue.Boolean(closedInput),
            ["suspectedDoubleEdge"] = VisionValue.Boolean(suspectedDoubleEdge),
            ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diag)
        },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["deviation"] = deviation, ["lsDeviation"] = lsDeviation,
                ["minimumZoneDeviation"] = minimumZoneDeviation, ["rms"] = rms, ["maxAbsError"] = maxAbs,
                ["relativeDeviation"] = relativeDeviation, ["peakPositive"] = peakPositive, ["peakNegative"] = peakNegative,
                ["length"] = length, ["angleDeg"] = angleDeg, ["sampleCount"] = points.Length,
                ["fitMethod"] = evaluationMode == "minimumzone" ? "minimumZone" : "leastSquares",
                ["closureGap"] = closureGap, ["closedInput"] = closedInput,
                ["suspectedDoubleEdge"] = suspectedDoubleEdge, ["singleEdgeDeclared"] = singleEdgeDeclared,
                ["withinTolerance"] = within, ["maxDeviation"] = maxDeviation, ["unit"] = "px",
                ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag
            }, [ToleranceGeometry.Segment("straightness-fit", line)]));
    }

    /// <summary>最小包容带宽度：最优方向必与凸包某条边平行，逐边投影取最小宽度。</summary>
    private static double MinimumZoneWidth(VisionPoint2D[] points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3) return 0;
        var best = double.MaxValue;
        for (var index = 0; index < hull.Count; index++)
        {
            var a = hull[index];
            var b = hull[(index + 1) % hull.Count];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var norm = MeasurementMath.Hypot(dx, dy);
            if (norm < 1e-12) continue;
            // 边方向的法向 (dy, -dx) 上投影，宽度即 max-min。
            var minimum = double.MaxValue;
            var maximum = double.MinValue;
            foreach (var point in points)
            {
                var projection = (point.X * dy - point.Y * dx) / norm;
                if (projection < minimum) minimum = projection;
                if (projection > maximum) maximum = projection;
            }
            best = Math.Min(best, maximum - minimum);
        }
        return double.IsFinite(best) ? best : 0;
    }

    private static List<VisionPoint2D> ConvexHull(VisionPoint2D[] points)
    {
        var ordered = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (ordered.Length < 3) return [.. ordered];
        var hull = new List<VisionPoint2D>();
        static double Cross(VisionPoint2D o, VisionPoint2D a, VisionPoint2D b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var point in ordered)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        var lowerCount = hull.Count + 1;
        for (var index = ordered.Length - 2; index >= 0; index--)
        {
            var point = ordered[index];
            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }
}

internal static class LineOrientationQuality
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string type, bool perpendicular, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var a = ToleranceGeometry.Line(context, "a"); var b = ToleranceGeometry.Line(context, "b");
        var dot = Math.Clamp(Math.Abs(a.X * b.X + a.Y * b.Y), 0, 1); var cross = Math.Clamp(Math.Abs(a.X * b.Y - a.Y * b.X), 0, 1);
        var angle = Math.Atan2(cross, dot) * 180 / Math.PI; var error = perpendicular ? 90 - angle : angle; var deviation = b.Length * (perpendicular ? dot : cross);
        if (!double.IsFinite(angle) || !double.IsFinite(error) || !double.IsFinite(deviation)) throw new InvalidOperationException($"{type} produced non-finite output.");
        var limit = node.GetDouble("maxAngleErrorDeg", 0);
        if (!double.IsFinite(limit) || limit < 0 || limit > 90)
            throw new InvalidOperationException($"{type} maxAngleErrorDeg must be in [0, 90].");
        // 0 不再等同于“关闭”：用 toleranceEnabled 显式区分“没设公差”和“零公差”。
        var toleranceEnabled = node.GetBool("toleranceEnabled", limit > 0);
        var within = !toleranceEnabled || Math.Abs(error) <= limit + 1e-12;
        // orientationDeviation 是 B 端点相对 A 方向的漂移量，随 B 长度线性变化；
        // zoneWidth 只是同一漂移在“评价长度 = B 全长”口径下的写法，绝不是含形状误差的 GD&T 公差带。
        var orientationDeviation = deviation;
        var evaluationLength = b.Length;
        var zoneWidth = orientationDeviation;
        var diag = CoordinateValidation.Diagnostics(type, ("measuredAngleDeg", angle), ("angleErrorDeg", error),
            ("orientationDeviation", orientationDeviation), ("deviation", deviation), ("evaluationLength", evaluationLength),
            ("zoneWidth", zoneWidth), ("zoneWidthBasis", "endpointSpread"), ("measuredLength", b.Length),
            ("maxAngleErrorDeg", limit), ("toleranceEnabled", toleranceEnabled), ("withinTolerance", within),
            ("perpendicular", perpendicular));
        return new(new Dictionary<string, VisionValue>
            {
                ["angleError"] = VisionValue.Double(error), ["deviation"] = VisionValue.Double(deviation),
                ["orientationDeviation"] = VisionValue.Double(orientationDeviation),
                ["evaluationLength"] = VisionValue.Double(evaluationLength),
                ["zoneWidth"] = VisionValue.Double(zoneWidth),
                ["zoneWidthBasis"] = VisionValue.String("endpointSpread"),
                ["measuredLength"] = VisionValue.Double(b.Length), ["measuredAngleDeg"] = VisionValue.Double(angle),
                ["toleranceEnabled"] = VisionValue.Boolean(toleranceEnabled),
                ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["angleError"] = error, ["deviation"] = deviation, ["orientationDeviation"] = orientationDeviation,
                ["evaluationLength"] = evaluationLength, ["zoneWidth"] = zoneWidth, ["zoneWidthBasis"] = "endpointSpread",
                ["measuredLength"] = b.Length, ["unit"] = "px", ["angleUnit"] = "deg", ["toleranceEnabled"] = toleranceEnabled,
                ["withinTolerance"] = within, ["diagnostics"] = diag
            }, [ToleranceGeometry.Segment("datum-a", a.Line, "A"), ToleranceGeometry.Segment("measured-b", b.Line, "B")]);
    }
}
public sealed class ParallelismNode : IVisionNodeExecutor
{
    public string Type => "measure.parallelism";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        // parallel / perpendicular 共用同一套角度误差计算：差别只在参考角是 0° 还是 90°。
        var mode = (node.GetString("mode", "parallel") ?? "parallel").Trim().ToLowerInvariant();
        if (mode is not ("parallel" or "perpendicular"))
            throw new InvalidOperationException("measure.parallelism mode must be parallel or perpendicular.");
        var limit = node.GetDouble("maxAngleErrorDeg", 0);
        if (!double.IsFinite(limit) || limit < 0 || limit > 90)
            throw new InvalidOperationException("measure.parallelism maxAngleErrorDeg must be finite in [0, 90].");
        _ = context.Require<VisionLine2D>("a");
        _ = context.Require<VisionLine2D>("b");
        var result = LineOrientationQuality.Execute(context, node, Type, mode == "perpendicular", cancellationToken);
        if (!result.Outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("measure.parallelism diagnostics missing.");
        return ValueTask.FromResult(result);
    }
}

public sealed class ContourDeviationNode : IVisionNodeExecutor
{
    public string Type => "measure.contourDeviation";
    private sealed record DistanceSample(string Direction, VisionPoint2D Point, double Distance);
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var closed = node.GetBool("closed", true); var symmetric = node.GetBool("symmetric", true); var step = node.GetDouble("sampleStep", 2); var budget = node.GetInt("maxSamples", 2000);
        var maxDeviation = node.GetDouble("maxDeviation", 0);
        if (!double.IsFinite(step) || step is < .01 or > 10000 || budget is < 2 or > 10000) throw new InvalidOperationException("Deviation step must be 0.01..10000 px and sample budget 2..10000 per direction.");
        if (!double.IsFinite(maxDeviation) || maxDeviation < 0) throw new InvalidOperationException("measure.contourDeviation maxDeviation must be finite and >= 0 (0 disables the tolerance).");
        var points = ToleranceGeometry.Select(context, node, closed: closed); var reference = ToleranceGeometry.Select(context, node, "referenceContours", "referenceIndex", closed);
        if (points.Distinct().Count() < 2 || reference.Distinct().Count() < 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(0, "insufficient-points",
                new() { ["samplesJson"] = VisionValue.String("[]"), ["withinTolerance"] = VisionValue.Boolean(false) }, nodeType: Type));
        var samples = new List<DistanceSample>();
        var primaryQuery = Resample(points, closed, step, budget, cancellationToken);
        var reverseQuery = symmetric ? Resample(reference, closed, step, budget, cancellationToken) : [];
        var forward = Distances(primaryQuery, reference, "current-to-reference");
        var reverse = symmetric ? Distances(reverseQuery, points, "reference-to-current") : [];
        var all = forward.Concat(reverse).ToArray(); var maximum = all.Max(); var mean = all.Average(); var rms = Math.Sqrt(all.Average(v => v * v));

        // 采样误差上界：两点之间可能漏掉的峰不会超过半个采样间距。
        var maxAdjacentSampleDistance = 0d;
        for (var index = 1; index < primaryQuery.Length; index++)
            maxAdjacentSampleDistance = Math.Max(maxAdjacentSampleDistance,
                MeasurementMath.Hypot(primaryQuery[index].X - primaryQuery[index - 1].X, primaryQuery[index].Y - primaryQuery[index - 1].Y));
        if (closed && primaryQuery.Length > 1)
            maxAdjacentSampleDistance = Math.Max(maxAdjacentSampleDistance,
                MeasurementMath.Hypot(primaryQuery[0].X - primaryQuery[^1].X, primaryQuery[0].Y - primaryQuery[^1].Y));
        var samplingErrorBoundPx = maxAdjacentSampleDistance / 2;
        var deviationUpperBoundPx = maximum + samplingErrorBoundPx;
        var gateEnabled = maxDeviation > 0;
        var denseResampleRequired = gateEnabled && maximum <= maxDeviation && deviationUpperBoundPx > maxDeviation;
        var within = gateEnabled && maximum <= maxDeviation && !denseResampleRequired;
        var reason = !gateEnabled ? "tolerance-disabled"
            : maximum > maxDeviation ? "deviation-exceeded"
            : denseResampleRequired ? "dense-resample-required"
            : "ok";

        // 闭合轮廓的起点会改变重采样相位：多起点极差就是相位敏感性，过大时只能用于筛查。
        var phaseStartCount = closed ? 3 : 1;
        var phaseMaxima = new double[phaseStartCount];
        phaseMaxima[0] = maximum;
        for (var start = 1; start < phaseStartCount; start++)
        {
            var rotated = Rotate(points, points.Length * start / phaseStartCount);
            var rotatedQuery = Resample(rotated, closed, step, budget, cancellationToken);
            phaseMaxima[start] = Distances(rotatedQuery, reference, "phase-start", record: false).Max();
        }
        var phaseDeviationSpreadPx = phaseMaxima.Max() - phaseMaxima.Min();
        var screeningOnly = closed && phaseDeviationSpreadPx > Math.Max(0.1 * maximum, 1e-9);

        var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(all.Length), ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)),
            ["deviation"] = VisionValue.Double(maximum), ["mean"] = VisionValue.Double(mean), ["rms"] = VisionValue.Double(rms), ["forwardMax"] = VisionValue.Double(forward.Max()) };
        if (symmetric) outputs["reverseMax"] = VisionValue.Double(reverse.Max());
        outputs["withinTolerance"] = VisionValue.Boolean(within);
        outputs["samplingErrorBoundPx"] = VisionValue.Double(samplingErrorBoundPx);
        outputs["deviationUpperBoundPx"] = VisionValue.Double(deviationUpperBoundPx);
        outputs["denseResampleRequired"] = VisionValue.Boolean(denseResampleRequired);
        outputs["maxAdjacentSampleDistancePx"] = VisionValue.Double(maxAdjacentSampleDistance);
        outputs["phaseStartCount"] = VisionValue.Integer(phaseStartCount);
        outputs["phaseDeviationSpreadPx"] = VisionValue.Double(phaseDeviationSpreadPx);
        outputs["screeningOnly"] = VisionValue.Boolean(screeningOnly);
        outputs["reason"] = VisionValue.String(reason);
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("deviation", maximum), ("mean", mean), ("maxDeviation", maxDeviation), ("withinTolerance", within), ("rms", rms),
            ("sampleCount", all.Length), ("symmetric", symmetric), ("sampleStep", step),
            ("samplingErrorBoundPx", samplingErrorBoundPx), ("deviationUpperBoundPx", deviationUpperBoundPx),
            ("maxAdjacentSampleDistancePx", maxAdjacentSampleDistance), ("denseResampleRequired", denseResampleRequired),
            ("phaseStartCount", phaseStartCount), ("phaseDeviationSpreadPx", phaseDeviationSpreadPx),
            ("screeningOnly", screeningOnly), ("reason", reason));
        outputs["diagnostics"] = VisionValue.String(diag);
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?> { ["found"] = true, ["deviation"] = maximum, ["mean"] = mean, ["rms"] = rms, ["sampleCount"] = all.Length, ["symmetric"] = symmetric, ["sampleStep"] = step, ["maxDeviation"] = maxDeviation, ["withinTolerance"] = within,
            ["samplingErrorBoundPx"] = samplingErrorBoundPx, ["deviationUpperBoundPx"] = deviationUpperBoundPx,
            ["denseResampleRequired"] = denseResampleRequired, ["phaseDeviationSpreadPx"] = phaseDeviationSpreadPx,
            ["screeningOnly"] = screeningOnly, ["reason"] = reason,
            ["unit"] = "px", ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag },
            samples.OrderByDescending(s => s.Distance).Take(200).Select((s, i) => new VisionOverlay($"deviation-{i}", VisionOverlayType.Point, X: s.Point.X, Y: s.Point.Y, Label: $"{s.Distance:0.###}")).ToArray()));

        double[] Distances(VisionPoint2D[] query, VisionPoint2D[] target, string direction, bool record = true)
        {
            using var queryCurve = ToleranceGeometry.Curve(query, false); using var targetCurve = ToleranceGeometry.Curve(target, closed);
            using var distances = queryCurve.DistanceContoursXld(targetCurve, "point_to_segment"); using var attribute = distances.GetContourAttribXld("distance");
            if (attribute.Length != query.Length) throw new InvalidOperationException("JLVision contour distance sample count mismatch.");
            var values = new double[query.Length];
            for (var i = 0; i < values.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested(); values[i] = attribute[i].D;
                if (!double.IsFinite(values[i]) || values[i] < 0) throw new InvalidOperationException("JLVision produced invalid contour distances.");
                if (record) samples.Add(new(direction, query[i], values[i]));
            }
            return values;
        }
    }

    private static VisionPoint2D[] Rotate(VisionPoint2D[] source, int offset)
    {
        if (source.Length == 0) return source;
        offset = ((offset % source.Length) + source.Length) % source.Length;
        if (offset == 0) return source;
        var result = new VisionPoint2D[source.Length];
        for (var index = 0; index < source.Length; index++) result[index] = source[(index + offset) % source.Length];
        return result;
    }
    private static VisionPoint2D[] Resample(VisionPoint2D[] points, bool closed, double step, int budget, CancellationToken token)
    {
        var path = closed ? points.Append(points[0]).ToArray() : points;
        var cumulative = new double[path.Length];
        for (var i = 1; i < path.Length; i++) cumulative[i] = cumulative[i - 1] + MeasurementMath.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
        var length = cumulative[^1]; var intervals = Math.Max(1, Math.Ceiling(length / step)); var count = Math.Max(2, intervals + (closed ? 0 : 1));
        if (count > budget) throw new InvalidOperationException($"Contour deviation sample budget exceeded ({budget}); increase sampleStep or budget.");
        var result = new VisionPoint2D[(int)count]; var segment = 1;
        for (var i = 0; i < result.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var distance = length * i / (closed ? result.Length : result.Length - 1);
            while (segment < path.Length - 1 && cumulative[segment] < distance) segment++;
            var fraction = (distance - cumulative[segment - 1]) / (cumulative[segment] - cumulative[segment - 1]);
            result[i] = new(path[segment - 1].X + fraction * (path[segment].X - path[segment - 1].X), path[segment - 1].Y + fraction * (path[segment].Y - path[segment - 1].Y));
        }
        return result;
    }
}
