using System.Text.Json;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

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
        var sourceContours=context.Require<IReadOnlyList<VisionContour>>("contours");
        var sourceIndex=node.GetInt("index",0);
        var sourceClosed = sourceIndex >= 0 && sourceIndex < sourceContours.Count && sourceContours[sourceIndex].Closed;
        if (points.Length < 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));
        var length = Perimeter(points, closed);
        var estimatedCount = mode.ToLowerInvariant() switch
        {
            "everypoint" => points.Length + (closed ? 1 : 0),
            "uniform" => (int)Math.Ceiling(points.Length / (double)Math.Max(1,(int)Math.Ceiling(points.Length / (double)budget))),
            "arclength" => (int)Math.Max(2,Math.Ceiling(length / step) + (closed ? 0 : 1)),
            _ => throw new InvalidOperationException($"contour.toPoints resample mode '{mode}' is not supported (arcLength | uniform | everyPoint).")
        };
        if (!double.IsFinite(estimatedCount) || estimatedCount < 2 || estimatedCount > budget)
        {
            var suggestedStep = mode.Equals("arcLength",StringComparison.OrdinalIgnoreCase) ? length / Math.Max(1,budget-(closed?0:1)) : 0;
            throw new InvalidOperationException($"contour.toPoints stage=pre-sample-budget: estimatedCount={estimatedCount}, maxPoints={budget}, length={length:0.###}, sampleStep={step}, suggestedStep={suggestedStep:0.###}. No points were emitted.");
        }

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
            ("length", length), ("stepUsed", stepUsed), ("resample", mode), ("closed", closed),("sourceClosed",sourceClosed),
            ("closurePolicy","closed appends first point only for everyPoint; arcLength excludes duplicate endpoint"),
            ("includesClosingEndpoint",closed && mode.Equals("everyPoint",StringComparison.OrdinalIgnoreCase)),("estimatedCount",estimatedCount),("maxPoints",budget));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pointList"] = VisionValue.PointList(samples), ["found"] = VisionValue.Boolean(true),
                ["pointCount"] = VisionValue.Integer(samples.Length), ["length"] = VisionValue.Double(length),
                ["sourceClosed"] = VisionValue.Boolean(sourceClosed), ["closurePolicy"] = VisionValue.String("explicit closed parameter overrides source; everyPoint repeats first point, arcLength does not"),
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
        // Canonical angular ordering makes fitting and endpoints independent of input order.
        var ordered = points.Select((Point, Index) => new
            {
                Point, Index,
                Angle = Math.Atan2(Point.Y - circle.Y, Point.X - circle.X) * 180 / Math.PI
            })
            .OrderBy(item => item.Angle).ToArray();
        var angles = ordered.Select(item => item.Angle).ToArray();
        var internalGaps = angles.Skip(1).Select((angle, i) => angle - angles[i]).OrderBy(value => value).ToArray();
        var typicalGap = internalGaps.Length == 0 ? 0 : internalGaps[internalGaps.Length / 2];
        var discontinuityLimit = Math.Max(2, typicalGap * 6);
        if (internalGaps.Any(value => value > discontinuityLimit))
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "multiple-arcs-or-discontinuous-input", nodeType: Type));
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
        var chord = MeasurementMath.Hypot(
            ordered.First(item => item.Angle == gapEnd).Point.X - ordered.First(item => item.Angle == gapStart).Point.X,
            ordered.First(item => item.Angle == gapEnd).Point.Y - ordered.First(item => item.Angle == gapStart).Point.Y);
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
        var minRadiusLimit = node.GetDouble("minRadius", 0);
        var maxCurvatureLimit = node.GetDouble("maxCurvature", 0);
        if (!double.IsFinite(minRadiusLimit) || minRadiusLimit < 0 || !double.IsFinite(maxCurvatureLimit) || maxCurvatureLimit < 0)
            throw new InvalidOperationException("measure.curvature radius/curvature limits must be finite and >= 0.");
        var toleranceEnabled = minRadiusLimit > 0 || maxCurvatureLimit > 0;
        if (minRadiusLimit > 0 && maxCurvatureLimit > 0)
            throw new InvalidOperationException("Enable only one of minRadius or maxCurvature.");
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

        var within = !toleranceEnabled ? false
            : minRadiusLimit > 0 ? minEntry.r >= minRadiusLimit : 1 / minEntry.r <= maxCurvatureLimit;
        var evaluationState = toleranceEnabled ? within ? "pass" : "fail" : "not-evaluated";

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", count),
            ("minRadius", minEntry.r), ("minRadiusIndex", minEntry.i), ("meanCurvature", meanCurvature),
            ("cornerCount", corners.Count), ("window", window), ("cornerRadius", cornerRadius),
            ("toleranceEnabled", toleranceEnabled), ("evaluationState", evaluationState), ("withinTolerance", within));
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
                ["withinTolerance"] = VisionValue.Boolean(within), ["toleranceEnabled"] = VisionValue.Boolean(toleranceEnabled),
                ["evaluationState"] = VisionValue.String(evaluationState), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["minRadius"] = minEntry.r, ["maxCurvature"] = 1 / minEntry.r,
                ["meanCurvature"] = meanCurvature, ["cornerCount"] = corners.Count, ["sampleCount"] = count,
                ["withinTolerance"] = within, ["toleranceEnabled"] = toleranceEnabled,
                ["evaluationState"] = evaluationState, ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
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

        var (inputU, inputZ, origin, ux, uy, inputGap) = ProfileMath.ProjectWithGap(points);
        if (ProfileMath.CheckInputGap(node, inputGap, points.Length) is { } gapFailure) return ValueTask.FromResult(gapFailure);
        var (u, z) = ProfileMath.Uniformize(inputU, inputZ, step, ToleranceGeometry.MaxPoints);
        var length = u[^1] - u[0];
        var actualStep = length / (u.Length - 1);
        var cutoff = cutoffParam > 0 ? cutoffParam : length / 5;
        if (cutoff <= actualStep * 2) throw new InvalidOperationException("measure.profileFilter cutoffWavelength must exceed two sample steps. Lower sampleStep or raise the cutoff.");
        var reflect = endEffect == "reflect";
        var requestedRadius = ProfileMath.GaussianRadius(cutoff, actualStep);
        if (!reflect && requestedRadius > (u.Length - 2) / 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(u.Length, "insufficient-gaussian-support", nodeType: Type));
        var mean = ProfileMath.Gaussian(z, cutoff, actualStep, reflect, cancellationToken, out var edge);
        var start = reflect ? 0 : edge;
        var end = reflect ? u.Length : u.Length - edge;
        var removed = u.Length - (end - start);
        var usableLength = u[end - 1] - u[start];
        var kernelTruncated = edge < requestedRadius;

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
            ("actualSampleStep", actualStep), ("maxObservedInputGap", inputGap),
            ("usableLength", usableLength), ("kernelTruncated", kernelTruncated), ("unit", "px"),
            ("residualPeak", residualPeak), ("wavinessPeak", wavinessPeak));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(residual.Length),
                ["cutoff"] = VisionValue.Double(cutoff), ["waviness"] = VisionValue.PointList(waviness),
                ["residual"] = VisionValue.PointList(residual), ["length"] = VisionValue.Double(length),
                ["residualPeak"] = VisionValue.Double(residualPeak), ["wavinessPeak"] = VisionValue.Double(wavinessPeak),
                ["residualRms"] = VisionValue.Double(residualRms),
                ["actualSampleStep"] = VisionValue.Double(actualStep), ["maxObservedInputGap"] = VisionValue.Double(inputGap),
                ["usableLength"] = VisionValue.Double(usableLength), ["kernelTruncated"] = VisionValue.Boolean(kernelTruncated),
                ["removedPointCount"] = VisionValue.Integer(removed), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["cutoff"] = cutoff, ["length"] = length, ["sampleCount"] = residual.Length,
                ["removedPointCount"] = removed, ["endEffect"] = endEffect, ["residualPeak"] = residualPeak,
                ["actualSampleStep"] = actualStep, ["maxObservedInputGap"] = inputGap,
                ["usableLength"] = usableLength, ["kernelTruncated"] = kernelTruncated,
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
        var endEffect = node.GetString("endEffect", "reflect").Trim().ToLowerInvariant();
        if (endEffect is not ("reflect" or "discard"))
            throw new InvalidOperationException("measure.roughness endEffect must be reflect or discard.");

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

        var (inputU, inputZ, _, _, _, inputGap) = ProfileMath.ProjectWithGap(points);
        if (ProfileMath.CheckInputGap(node, inputGap, points.Length) is { } gapFailure) return ValueTask.FromResult(gapFailure);
        var (u, z) = ProfileMath.Uniformize(inputU, inputZ, step, ToleranceGeometry.MaxPoints);
        var length = u[^1] - u[0];
        var actualStep = length / (u.Length - 1);
        var cutoff = cutoffParam > 0 ? cutoffParam : length / 5;
        if (cutoff <= actualStep * 2) throw new InvalidOperationException("measure.roughness cutoffWavelength must exceed two sample steps. Lower sampleStep or raise the cutoff.");
        var reflect = endEffect == "reflect";
        var requestedRadius = ProfileMath.GaussianRadius(cutoff, actualStep);
        if (!reflect && requestedRadius > (u.Length - 3) / 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(u.Length, "insufficient-gaussian-support", nodeType: Type));
        var start = reflect ? 0 : requestedRadius;
        var end = reflect ? u.Length : u.Length - requestedRadius;
        var available = u[end - 1] - u[start];
        var requestedLength = evaluationParam > 0 ? Math.Min(evaluationParam, available) : available;
        var keep = Math.Min(end - start, (int)Math.Floor(requestedLength / actualStep + 1e-9) + 1);
        if (keep < 3)
            return ValueTask.FromResult(ToleranceGeometry.Missing(keep, "insufficient-evaluation-samples", nodeType: Type));

        var mean = ProfileMath.Gaussian(z, cutoff, actualStep, reflect, cancellationToken, out var edge);
        var kernelTruncated = edge < requestedRadius;
        var evaluated = u[end - 1] - u[end - keep];
        var window = new double[keep];
        for (var i = 0; i < keep; i++)
        {
            var sample = end - keep + i;
            window[i] = z[sample] - mean[sample];
        }
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
            ("samplingLengths", rzValues.Count), ("withinTolerance", within), ("gateEnabled", maxRa > 0),
            ("actualSampleStep", actualStep), ("maxObservedInputGap", inputGap), ("endEffect", endEffect),
            ("kernelTruncated", kernelTruncated), ("unit", "px"), ("measurementKind", "imageTrend"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(window.Length),
                ["evaluationLength"] = VisionValue.Double(evaluated), ["cutoff"] = VisionValue.Double(cutoff),
                ["ra"] = VisionValue.Double(ra), ["rq"] = VisionValue.Double(rq), ["rz"] = VisionValue.Double(rz),
                ["rp"] = VisionValue.Double(rp), ["rv"] = VisionValue.Double(rv), ["rmax"] = VisionValue.Double(rmax),
                ["zeroCrossings"] = VisionValue.Integer(crossings), ["withinTolerance"] = VisionValue.Boolean(within),
                ["actualSampleStep"] = VisionValue.Double(actualStep), ["maxObservedInputGap"] = VisionValue.Double(inputGap),
                ["rzSamplingLengths"] = VisionValue.Integer(rzValues.Count), ["kernelTruncated"] = VisionValue.Boolean(kernelTruncated),
                ["gateEnabled"] = VisionValue.Boolean(maxRa > 0), ["unit"] = VisionValue.String("px"),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["ra"] = ra, ["rq"] = rq, ["rz"] = rz, ["rp"] = rp, ["rv"] = rv, ["rmax"] = rmax,
                ["cutoff"] = cutoff, ["evaluationLength"] = evaluated, ["sampleCount"] = window.Length,
                ["actualSampleStep"] = actualStep, ["maxObservedInputGap"] = inputGap, ["endEffect"] = endEffect,
                ["rzSamplingLengths"] = rzValues.Count, ["kernelTruncated"] = kernelTruncated,
                ["gateEnabled"] = maxRa > 0, ["measurementKind"] = "imageTrend",
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
        var min = double.MaxValue; var max = 0.0; var sum = 0.0;
        VisionPoint2D closestA = default; VisionPoint2D closestB = default;
        // Distance is sample-to-segment, never sample-to-sample. Only the source
        // sampling introduces an approximation; the target retains every segment.
        foreach (var p in sa)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var direct = double.MaxValue; VisionPoint2D best = default;
            var segments = closed ? b.Length : b.Length - 1;
            for (var j = 0; j < segments; j++)
            {
                var q = b[j]; var end = b[(j + 1) % b.Length];
                var ex = end.X - q.X; var ey = end.Y - q.Y;
                var length2 = ex * ex + ey * ey;
                var t = length2 > 0 ? Math.Clamp(((p.X - q.X) * ex + (p.Y - q.Y) * ey) / length2, 0, 1) : 0;
                var projection = new VisionPoint2D(q.X + t * ex, q.Y + t * ey);
                var distance = MeasurementMath.Hypot(p.X - projection.X, p.Y - projection.Y);
                if (distance < direct) { direct = distance; best = projection; }
            }
            if (direct < min) { min = direct; closestA = p; closestB = best; }
            if (direct > max) max = direct;
            sum += direct;
        }
        var mean = sum / sa.Length;
        // A source sample is at most half a grid step from any omitted source point.
        var minSamplingErrorBoundPx = step / 2;
        var maxSamplingErrorBoundPx = step;
        var reported = mode == "gap" ? min : max;
        var within = limit <= 0 || reported <= limit;
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", sa.Length),
            ("mode", mode), ("minDistance", min), ("maxDistance", max), ("meanDistance", mean), ("withinTolerance", within),
            ("minSamplingErrorBoundPx", minSamplingErrorBoundPx), ("maxSamplingErrorBoundPx", maxSamplingErrorBoundPx),
            ("distanceDefinition", "source-samples-to-reference-segments"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(sa.Length),
                ["distance"] = VisionValue.Double(reported), ["minDistance"] = VisionValue.Double(min),
                ["maxDistance"] = VisionValue.Double(max), ["meanDistance"] = VisionValue.Double(mean),
                ["closestPointA"] = VisionValue.Point(closestA), ["closestPointB"] = VisionValue.Point(closestB),
                ["minSamplingErrorBoundPx"] = VisionValue.Double(minSamplingErrorBoundPx),
                ["maxSamplingErrorBoundPx"] = VisionValue.Double(maxSamplingErrorBoundPx),
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

