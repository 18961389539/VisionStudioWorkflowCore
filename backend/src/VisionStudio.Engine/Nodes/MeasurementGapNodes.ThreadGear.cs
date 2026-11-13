using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

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

