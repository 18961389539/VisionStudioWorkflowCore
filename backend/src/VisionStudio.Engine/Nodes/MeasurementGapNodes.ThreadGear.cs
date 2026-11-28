using System.Text.Json;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

public sealed class ThreadMeasurementNode : IVisionNodeExecutor
{
    private const string PitchDiameterMethod = "arithmeticMeanOfMajorAndMinor";

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
        var minValidIntervals = node.GetInt("minValidIntervals", 0);
        var expectedThreadAngleDeg = node.GetDouble("expectedThreadAngleDeg", 60);
        var threadAngleToleranceDeg = node.GetDouble("threadAngleToleranceDeg", 0);
        var minPolarityConfidence = node.GetDouble("minPolarityConfidence", 0);
        var certifyPitchDiameter = node.GetBool("certifyPitchDiameter", false);
        if (minValidIntervals is < 0 or > 500 ||
            !double.IsFinite(expectedThreadAngleDeg) || expectedThreadAngleDeg is < 1 or > 120 ||
            !double.IsFinite(threadAngleToleranceDeg) || threadAngleToleranceDeg is < 0 or > 120 ||
            !double.IsFinite(minPolarityConfidence) || minPolarityConfidence is < 0 or > 1)
            throw new InvalidOperationException("measure.thread interval/angle/polarity gates are invalid.");
        var requiredIntervals = minValidIntervals > 0 ? minValidIntervals : Math.Max(2, minCrests - 1);

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
        var maxPitchDeviation = pitches.Count > 0 ? pitches.Max(pitchValue => Math.Abs(pitchValue - pitch)) : 0;
        var crestPositionsJson = JsonSerializer.Serialize(crests.Select(index => Math.Round(u[index], 6)).ToArray());
        var pitchIntervalsJson = JsonSerializer.Serialize(pitches.Select(value => Math.Round(value, 6)).ToArray());
        var validIntervalCount = pitches.Count;
        if (validIntervalCount < requiredIntervals)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-pitch-intervals", new()
            {
                ["crestCount"] = VisionValue.Integer(crests.Count),
                ["validIntervalCount"] = VisionValue.Integer(validIntervalCount),
                ["requiredIntervals"] = VisionValue.Integer(requiredIntervals),
                ["crestPositionsJson"] = VisionValue.String(crestPositionsJson),
                ["pitchIntervalsJson"] = VisionValue.String(pitchIntervalsJson)
            }, nodeType: Type));

        // 极性不能靠肉眼 overlay 判断：C/V 交替性 + 峰谷显著性 + 牙型角合理性给出可复现的置信度。
        var orderedFeatures = crests.Select(index => (Index: index, Kind: "crest"))
            .Concat(valleys.Select(index => (Index: index, Kind: "valley")))
            .OrderBy(feature => feature.Index).ToArray();
        var flips = 0;
        for (var i = 1; i < orderedFeatures.Length; i++)
            if (orderedFeatures[i].Kind != orderedFeatures[i - 1].Kind) flips++;
        var alternationRatio = orderedFeatures.Length > 1 ? flips / (double)(orderedFeatures.Length - 1) : 0;
        var salienceSpan = Math.Max(1e-9, smooth.Max() - smooth.Min());
        var peakValleySalience = Math.Clamp((Median(crestZ) - Median(valleyZ)) / salienceSpan, 0, 1);
        var threadAnglePlausible = threadAngle > 0 &&
            (threadAngleToleranceDeg <= 0 || Math.Abs(threadAngle - expectedThreadAngleDeg) <= threadAngleToleranceDeg + 1e-9);
        var polarityConfidence = 0.4 * alternationRatio + 0.3 * peakValleySalience + 0.3 * (threadAnglePlausible ? 1 : 0);
        if (polarityConfidence + 1e-9 < minPolarityConfidence)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "thread-pattern-incomplete", new()
            {
                ["polarityConfidence"] = VisionValue.Double(polarityConfidence),
                ["crestValleyAlternation"] = VisionValue.Double(alternationRatio),
                ["peakValleySalience"] = VisionValue.Double(peakValleySalience),
                ["threadAnglePlausible"] = VisionValue.Boolean(threadAnglePlausible)
            }, nodeType: Type));

        // pitchTolerance 只检查“栅距是否一致”，与 pitchDiameter 是否可以放行是两件事。
        var pitchWithin = pitchTolerance <= 0 || pitches.All(pitchValue => Math.Abs(pitchValue - pitch) <= pitchTolerance);
        var within = pitchWithin;
        var certifiedUseAllowed = certifyPitchDiameter && validIntervalCount >= requiredIntervals && threadAnglePlausible;
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("crestCount", crests.Count),
            ("sampleCount", samples.Length), ("pitch", pitch), ("majorDiameter", major), ("minorDiameter", minor),
            ("pitchDiameter", pitchDiameter), ("pitchDiameterMethod", PitchDiameterMethod),
            ("pitchDiameterIsEstimate", true), ("certifiedUseAllowed", certifiedUseAllowed),
            ("maxPitchDeviation", maxPitchDeviation), ("validIntervalCount", validIntervalCount),
            ("crestValleyAlternation", alternationRatio), ("peakValleySalience", peakValleySalience),
            ("threadAnglePlausible", threadAnglePlausible), ("polarityConfidence", polarityConfidence),
            ("threadAngleDeg", threadAngle), ("axisLength", axisLength), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["crestCount"] = VisionValue.Integer(crests.Count),
                ["sampleCount"] = VisionValue.Integer(samples.Length), ["pitch"] = VisionValue.Double(pitch),
                ["majorDiameter"] = VisionValue.Double(major), ["minorDiameter"] = VisionValue.Double(minor),
                ["pitchDiameter"] = VisionValue.Double(pitchDiameter), ["pitchDiameterMethod"] = VisionValue.String(PitchDiameterMethod),
                ["pitchDiameterEstimate"] = VisionValue.Double(pitchDiameter), ["pitchDiameterIsEstimate"] = VisionValue.Boolean(true),
                ["certifiedUseAllowed"] = VisionValue.Boolean(certifiedUseAllowed),
                ["crestPositionsJson"] = VisionValue.String(crestPositionsJson),
                ["pitchIntervalsJson"] = VisionValue.String(pitchIntervalsJson),
                ["maxPitchDeviation"] = VisionValue.Double(maxPitchDeviation),
                ["validIntervalCount"] = VisionValue.Integer(validIntervalCount),
                ["crestValleyAlternation"] = VisionValue.Double(alternationRatio),
                ["peakValleySalience"] = VisionValue.Double(peakValleySalience),
                ["threadAnglePlausible"] = VisionValue.Boolean(threadAnglePlausible),
                ["polarityConfidence"] = VisionValue.Double(polarityConfidence),
                ["threadAngleDeg"] = VisionValue.Double(threadAngle),
                ["threadDepth"] = VisionValue.Double((major - minor) / 2), ["withinTolerance"] = VisionValue.Boolean(within),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["crestCount"] = crests.Count, ["pitch"] = pitch, ["majorDiameter"] = major,
                ["minorDiameter"] = minor, ["pitchDiameter"] = pitchDiameter, ["pitchDiameterMethod"] = PitchDiameterMethod,
                ["pitchDiameterIsEstimate"] = true, ["certifiedUseAllowed"] = certifiedUseAllowed,
                ["maxPitchDeviation"] = maxPitchDeviation, ["validIntervalCount"] = validIntervalCount,
                ["crestValleyAlternation"] = alternationRatio, ["peakValleySalience"] = peakValleySalience,
                ["threadAnglePlausible"] = threadAnglePlausible, ["polarityConfidence"] = polarityConfidence,
                ["threadAngleDeg"] = threadAngle,
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
        // module 与 pinDiameter 使用同一个 unit 契约，避免 mm 与 µm 混用造成 1000 倍量纲错误。
        var unit = (node.GetString("unit", "mm") ?? "mm").Trim();
        if (unit.Length is < 1 or > 16) throw new InvalidOperationException("measure.gear unit must be 1..16 characters.");
        var dimensionalUseAllowed = !string.Equals(unit, "px", StringComparison.OrdinalIgnoreCase);

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
        // 各参数单独合法不代表组合可加工：顶/根圆关系、跨齿数、反渐开线定义域都要先验证。
        if (df <= 0 || da <= d || df >= d || k < 1 || k > teeth ||
            !double.IsFinite(invPin) || invPin <= 0 || !double.IsFinite(span) || span <= 0 || !double.IsFinite(baseTooth))
            return ValueTask.FromResult(ToleranceGeometry.Missing(0, "invalid-gear-combination", new()
            {
                ["reason"] = VisionValue.String("invalid-gear-combination"),
                ["module"] = VisionValue.Double(module), ["teeth"] = VisionValue.Integer(teeth),
                ["profileShift"] = VisionValue.Double(shift), ["pressureAngleDeg"] = VisionValue.Double(alpha * 180 / Math.PI),
                ["pitchDiameter"] = VisionValue.Double(d), ["outsideDiameter"] = VisionValue.Double(da),
                ["rootDiameter"] = VisionValue.Double(df), ["spanTeeth"] = VisionValue.Integer(k),
                ["pinDiameter"] = VisionValue.Double(pin), ["invPin"] = VisionValue.Double(invPin),
                ["unit"] = VisionValue.String(unit)
            }, nodeType: Type));
        var alphaPin = InverseInvolute(invPin);
        var overPins = d * Math.Cos(alpha) / Math.Cos(alphaPin) + pin;
        if (teeth % 2 == 1) overPins = d * Math.Cos(alpha) / Math.Cos(alphaPin) * Math.Cos(Math.PI / (2 * teeth)) + pin;

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("module", module), ("teeth", teeth),
            ("pressureAngleDeg", alpha * 180 / Math.PI), ("profileShift", shift), ("spanTeeth", k),
            ("pitchDiameter", d), ("baseDiameter", db), ("outsideDiameter", da), ("rootDiameter", df),
            ("pinDiameter", pin), ("pinDiameterSource", pinParam > 0 ? "parameter" : "auto"),
            ("unit", unit), ("dimensionalUseAllowed", dimensionalUseAllowed));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["pitchDiameter"] = VisionValue.Double(d),
                ["baseDiameter"] = VisionValue.Double(db), ["outsideDiameter"] = VisionValue.Double(da),
                ["rootDiameter"] = VisionValue.Double(df), ["circularPitch"] = VisionValue.Double(circularPitch),
                ["spanTeeth"] = VisionValue.Integer(k), ["spanLength"] = VisionValue.Double(span),
                ["pinDiameter"] = VisionValue.Double(pin), ["measurementOverPins"] = VisionValue.Double(overPins),
                ["baseToothThickness"] = VisionValue.Double(baseTooth),
                ["autoPinDiameter"] = VisionValue.Double(1.728 * module),
                ["pinDiameterSource"] = VisionValue.String(pinParam > 0 ? "parameter" : "auto"),
                ["unit"] = VisionValue.String(unit), ["dimensionalUseAllowed"] = VisionValue.Boolean(dimensionalUseAllowed),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["pitchDiameter"] = d, ["baseDiameter"] = db, ["outsideDiameter"] = da,
                ["rootDiameter"] = df, ["circularPitch"] = circularPitch, ["spanTeeth"] = k, ["spanLength"] = span,
                ["pinDiameter"] = pin, ["measurementOverPins"] = overPins, ["baseToothThickness"] = baseTooth,
                ["autoPinDiameter"] = 1.728 * module, ["pinDiameterSource"] = pinParam > 0 ? "parameter" : "auto",
                ["unit"] = unit, ["dimensionalUseAllowed"] = dimensionalUseAllowed,
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
        var gateMode = node.GetString("gateMode", "nominal").Trim().ToLowerInvariant();
        if (gateMode is not ("nominal" or "interval"))
            throw new InvalidOperationException("tolerance.stackup gateMode must be nominal or interval.");
        var unit = node.GetString("unit", "mm").Trim();
        if (unit.Length > 16) throw new InvalidOperationException("tolerance.stackup unit must be at most 16 characters.");
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
        if (contributions.Any(item => item is null)) throw new InvalidOperationException("tolerance.stackup contributions must be objects.");

        // a..f 覆盖前六个贡献的名义值：把上游实测值接进尺寸链，而不必改 JSON
        var ports = new[] { "a", "b", "c", "d", "e", "f" };
        var wiredPorts = new bool[ports.Length];
        var connectionMask = new List<string>(ports.Length);
        for (var i = 0; i < ports.Length && i < contributions.Count; i++)
        {
            wiredPorts[i] = context.TryGetValue(ports[i], out _);
            if (wiredPorts[i]) contributions[i].Nominal = context.RequireNumber(ports[i]);
            connectionMask.Add($"{ports[i]}={(wiredPorts[i] ? "port" : "json")}");
        }
        var wiredCount = wiredPorts.Count(wired => wired);
        var nominalSource = wiredCount == 0 ? "json"
            : wiredCount == Math.Min(ports.Length, contributions.Count) ? "ports"
            : "mixed";
        foreach (var c in contributions)
        {
            if (!double.IsFinite(c.Nominal) || !double.IsFinite(c.Plus) || !double.IsFinite(c.Minus) || c.Plus < 0 || c.Minus < 0)
                throw new InvalidOperationException($"tolerance.stackup contribution '{c.Name}' has non-finite values or negative tolerances.");
            if (c.Sign is not (1 or -1)) throw new InvalidOperationException($"tolerance.stackup contribution '{c.Name}' sign must be +1 or -1.");
        }

        var nominal = contributions.Sum(c => c.Sign * c.Nominal);
        var upper = 0.0; var lower = 0.0; var sigmaTotal = 0.0;
        foreach (var c in contributions)
        {
            var plus = c.Sign > 0 ? c.Plus : c.Minus;
            var minus = c.Sign > 0 ? c.Minus : c.Plus;
            upper += c.Sign * c.Nominal + plus;
            lower += c.Sign * c.Nominal - minus;
            sigmaTotal = double.Hypot(sigmaTotal, c.Plus / 6 + c.Minus / 6);
        }
        var rssTolerance = 6 * sigmaTotal;
        var useRss = method == "rss";
        // RSS 把每个全公差带当成六西格玛、且假设各环独立；这两个前提不声明就不该用于放行。
        var assumeIndependent = node.GetBool("assumeIndependent", false);
        var assumeSixSigma = node.GetBool("assumeSixSigma", false);
        var assumptionsDeclared = !useRss || (assumeIndependent && assumeSixSigma);
        var limitModel = useRss ? "independentSymmetricThreeSigma" : "worstCase";
        var reportedUpper = useRss ? nominal + 3 * sigmaTotal : upper;
        var reportedLower = useRss ? nominal - 3 * sigmaTotal : lower;
        var gap = gateMode == "interval"
            ? Math.Max(Math.Abs(reportedLower - target), Math.Abs(reportedUpper - target))
            : Math.Abs(nominal - target);
        var gateEnabled = tolerance > 0;
        var margin = gateEnabled ? tolerance - gap : 0;
        var statisticalPass = gateEnabled && gap <= tolerance;
        var within = statisticalPass && assumptionsDeclared;
        var utilization = gateEnabled ? gap / tolerance : 0;
        var evaluationState = !gateEnabled || !assumptionsDeclared ? "not-evaluated" : within ? "pass" : "fail";
        var reason = !gateEnabled ? "tolerance-disabled"
            : !assumptionsDeclared ? "assumptions-undeclared"
            : within ? "ok" : "exceeded";
        var assumptionWarnings = assumptionsDeclared
            ? null
            : "RSS treats each full tolerance width as six sigma and assumes independent contributors; set assumeIndependent=true and assumeSixSigma=true before using this limit for release.";
        if (!new[] { nominal, upper, lower, rssTolerance, reportedUpper, reportedLower, gap, margin, utilization }.All(double.IsFinite))
            throw new InvalidOperationException("tolerance.stackup calculation exceeded the supported numeric range.");

        var shares = contributions.Select((c, index) =>
        {
            var sigma = c.Plus / 6 + c.Minus / 6;
            var item = new Dictionary<string, object?>
            {
                ["name"] = c.Name, ["sign"] = c.Sign, ["nominal"] = Math.Round(c.Sign * c.Nominal, 6),
                ["upper"] = Math.Round(c.Sign > 0 ? c.Plus : c.Minus, 6),
                ["lower"] = Math.Round(c.Sign > 0 ? c.Minus : c.Plus, 6),
                ["sigma"] = Math.Round(sigma, 6),
                ["nominalSource"] = index < wiredPorts.Length && wiredPorts[index] ? "port" : "json"
            };
            if (!useRss) return item;
            var ratio = sigmaTotal > 0 ? sigma / sigmaTotal : 0;
            item["varianceShare"] = Math.Round(ratio * ratio, 6);
            return item;
        }).ToArray();
        var contributorSigmasJson = JsonSerializer.Serialize(contributions.Select((c, index) => new
        {
            name = c.Name,
            sigma = Math.Round(c.Plus / 6 + c.Minus / 6, 6),
            nominalSource = index < wiredPorts.Length && wiredPorts[index] ? "port" : "json"
        }));

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("method", method),
            ("contributorCount", contributions.Count), ("nominal", nominal), ("upper", reportedUpper),
            ("lower", reportedLower), ("rssTolerance", rssTolerance), ("margin", margin), ("withinTolerance", within),
            ("limitModel", limitModel), ("gateMode", gateMode), ("gateEnabled", gateEnabled), ("evaluatedDeviation", gap),
            ("unit", unit), ("assumptionsDeclared", assumptionsDeclared), ("assumeIndependent", assumeIndependent),
            ("assumeSixSigma", assumeSixSigma), ("nominalSource", nominalSource), ("evaluationState", evaluationState),
            ("reason", reason));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["contributorCount"] = VisionValue.Integer(contributions.Count),
                ["nominal"] = VisionValue.Double(nominal), ["worstCaseMax"] = VisionValue.Double(upper),
                ["worstCaseMin"] = VisionValue.Double(lower), ["rssTolerance"] = VisionValue.Double(rssTolerance),
                ["upperLimit"] = VisionValue.Double(reportedUpper), ["lowerLimit"] = VisionValue.Double(reportedLower),
                ["margin"] = VisionValue.Double(margin), ["utilization"] = VisionValue.Double(utilization),
                ["limitModel"] = VisionValue.String(limitModel), ["gateMode"] = VisionValue.String(gateMode),
                ["gateEnabled"] = VisionValue.Boolean(gateEnabled), ["evaluatedDeviation"] = VisionValue.Double(gap),
                ["unit"] = VisionValue.String(unit),
                ["assumptionsDeclared"] = VisionValue.Boolean(assumptionsDeclared),
                ["nominalSource"] = VisionValue.String(nominalSource),
                ["connectionMask"] = VisionValue.String(string.Join(",", connectionMask)),
                ["contributorSigmasJson"] = VisionValue.String(contributorSigmasJson),
                ["evaluationState"] = VisionValue.String(evaluationState),
                ["reason"] = VisionValue.String(reason),
                ["contributionsJson"] = VisionValue.String(JsonSerializer.Serialize(shares)),
                ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["method"] = method, ["nominal"] = nominal, ["worstCaseMax"] = upper,
                ["worstCaseMin"] = lower, ["rssTolerance"] = rssTolerance, ["margin"] = margin,
                ["lowerLimit"] = reportedLower, ["upperLimit"] = reportedUpper,
                ["limitModel"] = limitModel, ["gateMode"] = gateMode, ["gateEnabled"] = gateEnabled,
                ["evaluatedDeviation"] = gap, ["unit"] = unit,
                ["assumptionsDeclared"] = assumptionsDeclared, ["assumptionWarnings"] = assumptionWarnings,
                ["nominalSource"] = nominalSource, ["connectionMask"] = string.Join(",", connectionMask),
                ["evaluationState"] = evaluationState, ["reason"] = reason,
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

        // 两条近似平行的线交点对方向误差极敏感：条件数 = 1/|sinθ| 给出放大倍率，
        // 再配上“交点到两条实测线段的距离”，才够判断这个原点是否还能实用。
        var maxConditionNumber = node.GetDouble("maxConditionNumber", 0);
        var maxIntersectionDistance = node.GetDouble("maxIntersectionDistance", 0);
        if (!double.IsFinite(maxConditionNumber) || maxConditionNumber < 0 ||
            !double.IsFinite(maxIntersectionDistance) || maxIntersectionDistance < 0)
            throw new InvalidOperationException(
                "tolerance.drf maxConditionNumber and maxIntersectionDistance must be finite and >= 0 (0 disables).");

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

        var conditionNumber = 1d / Math.Abs(cross);
        var intersectionDistanceA = PointToSegmentDistance(origin, primary.Line);
        var intersectionDistanceB = PointToSegmentDistance(origin, secondary.Line);
        var intersectionDistanceToSegments = Math.Max(intersectionDistanceA, intersectionDistanceB);
        var conditionExceeded = maxConditionNumber > 0 && conditionNumber > maxConditionNumber;
        var distanceExceeded = maxIntersectionDistance > 0 && intersectionDistanceToSegments > maxIntersectionDistance;
        var originUnreliable = conditionExceeded || distanceExceeded;
        // 几何可构造 ≠ 基准可用：交点飘到线段之外时位姿仍能算出来，但不能再拿去放行。
        var datumValid = within && !originUnreliable;

        var payload = JsonSerializer.Serialize(new
        {
            name, origin = new { x = Math.Round(origin.X, 6), y = Math.Round(origin.Y, 6) },
            primaryAngleDeg = Math.Round(angle, 6), secondaryAngleDeg = Math.Round(secondaryAngle, 6),
            orthogonalityErrorDeg = Math.Round(orthogonality, 6), unit, flipped = flip,
            conditionNumber = Math.Round(conditionNumber, 6),
            intersectionDistanceToSegments = Math.Round(intersectionDistanceToSegments, 6),
            intersectionDistanceA = Math.Round(intersectionDistanceA, 6),
            intersectionDistanceB = Math.Round(intersectionDistanceB, 6),
            originUnreliable, datumValid
        });
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("datum", name),
            ("orthogonalityErrorDeg", orthogonality), ("primaryAngleDeg", angle), ("secondaryAngleDeg", secondaryAngle),
            ("withinTolerance", within), ("unit", unit), ("conditionNumber", conditionNumber),
            ("intersectionDistanceToSegments", intersectionDistanceToSegments),
            ("originUnreliable", originUnreliable), ("datumValid", datumValid),
            ("maxConditionNumber", maxConditionNumber), ("maxIntersectionDistance", maxIntersectionDistance));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["pose"] = VisionValue.Pose(pose),
                ["origin"] = VisionValue.Point(origin), ["primaryAngleDeg"] = VisionValue.Double(angle),
                ["secondaryAngleDeg"] = VisionValue.Double(secondaryAngle), ["orthogonalityErrorDeg"] = VisionValue.Double(orthogonality),
                ["originOffset"] = originOffset is { } offset ? VisionValue.Double(offset) : VisionValue.Double(0),
                ["conditionNumber"] = VisionValue.Double(conditionNumber),
                ["intersectionDistanceToSegments"] = VisionValue.Double(intersectionDistanceToSegments),
                ["originUnreliable"] = VisionValue.Boolean(originUnreliable),
                ["datumValid"] = VisionValue.Boolean(datumValid),
                ["datumJson"] = VisionValue.String(payload), ["withinTolerance"] = VisionValue.Boolean(within),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["datum"] = name, ["origin"] = origin, ["orthogonalityErrorDeg"] = orthogonality,
                ["primaryAngleDeg"] = angle, ["secondaryAngleDeg"] = secondaryAngle, ["originOffset"] = originOffset,
                ["withinTolerance"] = within, ["unit"] = unit, ["conditionNumber"] = conditionNumber,
                ["intersectionDistanceToSegments"] = intersectionDistanceToSegments,
                ["originUnreliable"] = originUnreliable, ["datumValid"] = datumValid,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            [ToleranceGeometry.Segment("datum-a", primary.Line, "A"), ToleranceGeometry.Segment("datum-b", secondary.Line, "B"),
             new("datum-origin", VisionOverlayType.Point, X: origin.X, Y: origin.Y, Label: name)]));
    }

    /// <summary>点到线段的最近距离；投影落在延长线外时取端点距离。</summary>
    private static double PointToSegmentDistance(VisionPoint2D point, VisionLine2D line)
    {
        var dx = line.End.X - line.Start.X;
        var dy = line.End.Y - line.Start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-18) return MeasurementMath.Hypot(point.X - line.Start.X, point.Y - line.Start.Y);
        var t = Math.Clamp(((point.X - line.Start.X) * dx + (point.Y - line.Start.Y) * dy) / lengthSquared, 0, 1);
        return MeasurementMath.Hypot(point.X - (line.Start.X + t * dx), point.Y - (line.Start.Y + t * dy));
    }
}

