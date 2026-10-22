using System.Globalization;
using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision / HALCON 计量模型（metrology model）：一次建模型，混合挂载 line / circle /
/// ellipse / rectangle2 测量对象，统一 Apply 后取几何结果。
/// <para>
/// 实测限制（JLVisionLib 1.0.0）：<c>GetMetrologyObjectResult</c> 只接受单个
/// <c>"all_param"</c> 名并返回固定元数；**不返回 score**，因此质量用测量点残差
/// （circle/line）补足；ellipse / rectangle2 不给残差。
/// </para>
/// </summary>
public sealed class MetrologyNode : IVisionNodeExecutor
{
    public string Type => "measure.metrology";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty())
            throw new InvalidOperationException("measure.metrology requires a non-empty image.");

        if (node.GetBool("debugImagePreviewOnly", false))
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source.CloneOwned()),
                    ["found"] = VisionValue.Boolean(false), ["count"] = VisionValue.Integer(0), ["foundCount"] = VisionValue.Integer(0),
                    ["measurements"] = VisionValue.String("[]"), ["results"] = VisionValue.MetrologyResults(Array.Empty<VisionMetrologyResult>()),
                    ["diagnostics"] = VisionValue.String("{\"previewOnly\":true}") },
                new Dictionary<string, object?> { ["previewOnly"] = true }, []));

        var specs = ParseObjects(node.GetString("objects", "[]"));
        if (specs.Count == 0)
            throw new InvalidOperationException("measure.metrology requires at least one measurement object in 'objects'.");

        var sigma = node.GetDouble("measureSigma", 1);
        var threshold = node.GetDouble("measureThreshold", 30);
        var length1 = node.GetDouble("measureLength1", 20);
        var length2 = node.GetDouble("measureLength2", 5);
        if (!double.IsFinite(sigma) || sigma is < 0.4 or > 100) throw new InvalidOperationException("measure.metrology measureSigma must be between 0.4 and 100.");
        if (!double.IsFinite(threshold) || threshold is < 1 or > 255) throw new InvalidOperationException("measure.metrology measureThreshold must be between 1 and 255.");
        if (!double.IsFinite(length1) || length1 is < 1 or > 10000) throw new InvalidOperationException("measure.metrology measureLength1 must be between 1 and 10000.");
        if (!double.IsFinite(length2) || length2 is < 0 or > 10000) throw new InvalidOperationException("measure.metrology measureLength2 must be between 0 and 10000.");
        var transition = Choice(node, "transition", "all", "all", "positive", "negative");
        var select = Choice(node, "select", "all", "all", "first", "last");
        var minScore = node.GetDouble("minScore", 0.3);
        if (!double.IsFinite(minScore) || minScore is < 0 or > 1) throw new InvalidOperationException("measure.metrology minScore must be finite in [0,1].");
        var requireAllFound = node.GetBool("requireAllFound", true);
        var alignment = ResolveAlignment(context, node);

        using var gray = ToGray(source.Mat);
        using var jlImage = JLVisionImages.FromMat(gray);
        using var model = new JlMetrologyModel();

        var indices = new List<(MetrologyObjectSpec Spec, int Index)>(specs.Count);
        var addFailures = new List<object>();
        foreach (var spec in specs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                indices.Add((spec, AddObject(model, spec, alignment, sigma, threshold, length1, length2, transition, select, minScore)));
            }
            catch (Exception ex) when (ex is JlOperatorException or InvalidOperationException)
            {
                addFailures.Add(new { name = spec.Name, type = spec.Type, error = ex.Message });
            }
        }

        if (indices.Count == 0)
        {
            var failed = CoordinateValidation.Diagnostics(Type,
                ("objects", specs.Count), ("added", 0), ("addFailures", addFailures.Count));
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(source.CloneOwned()),
                    ["found"] = VisionValue.Boolean(false),
                    ["count"] = VisionValue.Integer(0),
                    ["foundCount"] = VisionValue.Integer(0),
                    ["measurements"] = VisionValue.String("[]"),
                    ["results"] = VisionValue.MetrologyResults(specs.Select((spec, i) => new VisionMetrologyResult(i, spec.Name, spec.Type, false)).ToArray()),
                    ["diagnostics"] = VisionValue.String(failed)
                },
                new Dictionary<string, object?>
                {
                    ["diagnostics"] = failed,
                    ["found"] = false,
                    ["reason"] = "no-measurement-object-could-be-added",
                    ["addFailures"] = addFailures
                },
                []));
        }

        model.ApplyMetrologyModel(jlImage);

        using var resultNames = new JlTuple(new object[] { "all_param" });
        using var emptyValues = new JlTuple();
        var measurements = new List<Dictionary<string, object?>>(indices.Count);
        var overlays = new List<VisionOverlay>(indices.Count);
        var residuals = new List<double>();
        var missing = 0;

        foreach (var (spec, index) in indices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["index"] = index,
                ["objectIndex"] = specs.IndexOf(spec),
                ["name"] = spec.Name,
                ["type"] = spec.Type
            };

            JlTuple? result = null;
            try
            {
                result = model.GetMetrologyObjectResult(index, "all", resultNames, emptyValues);
                ReadGeometry(spec.Type, result, entry);
            }
            catch (Exception ex) when (ex is JlOperatorException or JlTupleAccessException)
            {
                entry["found"] = false;
                entry["error"] = ex.Message;
                missing++;
                measurements.Add(entry);
                continue;
            }
            finally
            {
                result?.Dispose();
            }

            var found = entry.TryGetValue("found", out var foundValue) && foundValue is true;
            if (!found) missing++;

            if (found)
            {
                var residual = TryResidual(model, index, spec.Type, entry);
                if (residual is not null)
                {
                    entry["residualPx"] = Math.Round(residual.Value, 4);
                    residuals.Add(residual.Value);
                }
                var overlay = BuildOverlay(spec, entry, alignment);
                if (overlay is not null) overlays.Add(overlay);
            }

            measurements.Add(entry);
        }

        var foundCount = measurements.Count(m => m.TryGetValue("found", out var f) && f is true);
        var foundOverall = requireAllFound ? foundCount == specs.Count : foundCount > 0;
        var residualRms = residuals.Count > 0 ? Math.Sqrt(residuals.Sum(r => r * r) / residuals.Count) : (double?)null;
        var residualMax = residuals.Count > 0 ? residuals.Max() : (double?)null;

        var json = JsonSerializer.Serialize(measurements, JsonOptions);
        var diag = CoordinateValidation.Diagnostics(Type,
            ("objects", specs.Count), ("foundCount", foundCount), ("missing", missing),
            ("requireAllFound", requireAllFound), ("residualRms", residualRms), ("residualMax", residualMax),
            ("transition", transition), ("select", select), ("minScore", minScore));

        var outputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(source.CloneOwned()),
            ["found"] = VisionValue.Boolean(foundOverall),
            ["count"] = VisionValue.Integer(measurements.Count),
            ["foundCount"] = VisionValue.Integer(foundCount),
            ["measurements"] = VisionValue.String(json),
            ["results"] = VisionValue.MetrologyResults(specs.Select((spec, i) => TypedResult(spec, i, measurements.FirstOrDefault(entry => Convert.ToInt32(entry["objectIndex"]) == i), alignment)).ToArray()),
            ["diagnostics"] = VisionValue.String(diag)
        };
        if (residualRms is not null) outputs["residualRms"] = VisionValue.Double(residualRms.Value);
        if (residualMax is not null) outputs["residualMax"] = VisionValue.Double(residualMax.Value);

        var summary = new Dictionary<string, object?>
        {
            ["diagnostics"] = diag,
            ["found"] = foundOverall,
            ["reason"] = foundOverall ? "measured" : "measurement-incomplete",
            ["objectCount"] = specs.Count,
            ["foundCount"] = foundCount,
            ["missingCount"] = missing,
            ["addFailures"] = addFailures,
            ["residualRms"] = residualRms,
            ["residualMax"] = residualMax,
            ["transition"] = transition,
            ["select"] = select,
            ["minScore"] = minScore,
            ["measureSigma"] = sigma,
            ["measureThreshold"] = threshold,
            ["measureLength1"] = length1,
            ["measureLength2"] = length2,
            ["alignmentMode"] = alignment.Mode,
            ["measurements"] = json
        };

        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
    }

    private static VisionMetrologyResult TypedResult(MetrologyObjectSpec spec, int index, Dictionary<string, object?>? entry, Alignment alignment)
    {
        if (entry is null || !entry.TryGetValue("found", out var found) || found is not true)
            return new(index, spec.Name, spec.Type, false);
        double Get(string key) => Convert.ToDouble(entry[key], CultureInfo.InvariantCulture);
        var residual = entry.TryGetValue("residualPx", out var r) ? Convert.ToDouble(r, CultureInfo.InvariantCulture) : (double?)null;
        if (spec.Type == "line")
        {
            var nx = Get("nx"); var ny = Get("ny"); var d = Get("distancePx");
            var start = alignment.Apply(spec.RowBegin!.Value, spec.ColumnBegin!.Value);
            var end = alignment.Apply(spec.RowEnd!.Value, spec.ColumnEnd!.Value);
            var cx = (start.Column + end.Column) / 2; var cy = (start.Row + end.Row) / 2;
            var norm = Math.Sqrt(nx * nx + ny * ny);
            if (norm < 1e-9) return new(index, spec.Name, spec.Type, false);
            var normalError = (nx * cx + ny * cy - d) / (norm * norm);
            cx -= nx * normalError; cy -= ny * normalError;
            var tx = -ny / norm; var ty = nx / norm;
            if (tx * (end.Column - start.Column) + ty * (end.Row - start.Row) < 0) { tx = -tx; ty = -ty; }
            var half = Math.Sqrt(Math.Pow(end.Column-start.Column, 2)+Math.Pow(end.Row-start.Row, 2))/2;
            var line = new VisionLine2D(new(cx-tx*half,cy-ty*half),new(cx+tx*half,cy+ty*half));
            return new(index, spec.Name, spec.Type, true, new(cx,cy), Line: line,
                Pose: new(cx,cy,line.AngleDeg), ResidualPx: residual);
        }
        var center = new VisionPoint2D(Get("column"),Get("row"));
        if (spec.Type == "circle") return new(index,spec.Name,spec.Type,true,center,new(center.X,center.Y,Get("radius")),
            Pose: new(center.X,center.Y,0), ResidualPx: residual);
        var width = 2*Get(spec.Type == "ellipse" ? "radius1" : "length1");
        var height = 2*Get(spec.Type == "ellipse" ? "radius2" : "length2");
        var angle = Get("phiDeg");
        return new(index,spec.Name,spec.Type,true,center,Rectangle:new(center.X,center.Y,width,height,angle),
            Pose:new(center.X,center.Y,angle),ResidualPx:residual);
    }

    private static int AddObject(
        JlMetrologyModel model, MetrologyObjectSpec spec, Alignment alignment,
        double sigmaDefault, double thresholdDefault, double length1Default, double length2Default,
        string transitionDefault, string selectDefault, double minScoreDefault)
    {
        using var names = new JlTuple(new object[] { "measure_transition", "measure_select", "min_score" });
        using var values = new JlTuple(new object[]
        {
            spec.Transition ?? transitionDefault,
            spec.Select ?? selectDefault,
            spec.MinScore ?? minScoreDefault
        });
        var sigma = spec.Sigma ?? sigmaDefault;
        var threshold = spec.Threshold ?? thresholdDefault;
        var length1 = spec.MeasureLength1 ?? length1Default;
        var length2 = spec.MeasureLength2 ?? length2Default;

        switch (spec.Type)
        {
            case "line":
            {
                var (r1, c1) = alignment.Apply(spec.RowBegin!.Value, spec.ColumnBegin!.Value);
                var (r2, c2) = alignment.Apply(spec.RowEnd!.Value, spec.ColumnEnd!.Value);
                return model.AddMetrologyObjectLineMeasure(r1, c1, r2, c2, length1, length2, sigma, threshold, names, values);
            }
            case "circle":
            {
                var (row, column) = alignment.Apply(spec.Row!.Value, spec.Column!.Value);
                return model.AddMetrologyObjectCircleMeasure(row, column, spec.Radius!.Value, length1, length2, sigma, threshold, names, values);
            }
            case "ellipse":
            {
                var (row, column) = alignment.Apply(spec.Row!.Value, spec.Column!.Value);
                var phi = (spec.PhiDeg ?? 0) * Math.PI / 180d + alignment.AngleRad;
                return model.AddMetrologyObjectEllipseMeasure(row, column, phi, spec.Radius1!.Value, spec.Radius2!.Value, length1, length2, sigma, threshold, names, values);
            }
            case "rectangle2":
            {
                var (row, column) = alignment.Apply(spec.Row!.Value, spec.Column!.Value);
                var phi = (spec.PhiDeg ?? 0) * Math.PI / 180d + alignment.AngleRad;
                return model.AddMetrologyObjectRectangle2Measure(row, column, phi, spec.Length1!.Value, spec.Length2!.Value, length1, length2, sigma, threshold, names, values);
            }
            default:
                throw new InvalidOperationException($"Unsupported metrology object type '{spec.Type}'.");
        }
    }

    /// <summary>
    /// 结果元数按对象类型固定（实测）：
    /// circle → (row, column, radius)；ellipse → (row, column, phi, radius1, radius2)；
    /// rectangle2 → (row, column, phi, length1, length2)；
    /// line → (Ny, Nx, distance)，即 Hesse 法式的单位法向与有符号距离。
    /// </summary>
    private static void ReadGeometry(string type, JlTuple result, Dictionary<string, object?> entry)
    {
        var values = new double[Math.Min(result.Length, 6)];
        for (var i = 0; i < values.Length; i++)
        {
            try { values[i] = result[i].D; }
            catch (JlTupleAccessException) { values[i] = double.NaN; }
        }

        if (values.Any(value => !double.IsFinite(value))) { entry["found"] = false; return; }
        switch (type)
        {
            case "circle" when values.Length >= 3 && values[2] > 0:
                entry["found"] = true;
                entry["row"] = Round(values[0]);
                entry["column"] = Round(values[1]);
                entry["radius"] = Round(values[2]);
                entry["x"] = Round(values[1]);
                entry["y"] = Round(values[0]);
                break;
            case "ellipse" when values.Length >= 5 && values[3] > 0 && values[4] > 0:
                entry["found"] = true;
                entry["row"] = Round(values[0]);
                entry["column"] = Round(values[1]);
                entry["phiDeg"] = Round(values[2] * 180d / Math.PI);
                entry["radius1"] = Round(values[3]);
                entry["radius2"] = Round(values[4]);
                entry["x"] = Round(values[1]);
                entry["y"] = Round(values[0]);
                break;
            case "rectangle2" when values.Length >= 5 && values[3] > 0 && values[4] > 0:
                entry["found"] = true;
                entry["row"] = Round(values[0]);
                entry["column"] = Round(values[1]);
                entry["phiDeg"] = Round(values[2] * 180d / Math.PI);
                entry["length1"] = Round(values[3]);
                entry["length2"] = Round(values[4]);
                entry["x"] = Round(values[1]);
                entry["y"] = Round(values[0]);
                break;
            case "line" when values.Length >= 3 && values[0]*values[0] + values[1]*values[1] > 1e-12:
                entry["found"] = true;
                entry["ny"] = Round(values[0]);
                entry["nx"] = Round(values[1]);
                entry["distancePx"] = Round(values[2]);
                entry["normalAngleDeg"] = Round(Math.Atan2(values[0], values[1]) * 180d / Math.PI);
                break;
            default:
                entry["found"] = false;
                break;
        }
    }

    /// <summary>用原生测量点算拟合残差（circle / line 才有；其余返回 null）。</summary>
    private static double? TryResidual(JlMetrologyModel model, int index, string type, Dictionary<string, object?> entry)
    {
        if (type is not ("circle" or "line")) return null;
        JlTuple? rows = null;
        JlTuple? columns = null;
        try
        {
            using var contour = model.GetMetrologyObjectMeasures(index, "all", out rows, out columns);
            var count = Math.Min(rows.Length, columns.Length);
            if (count == 0) return null;

            double sum = 0;
            if (type == "circle")
            {
                var cx = Convert.ToDouble(entry["column"], CultureInfo.InvariantCulture);
                var cy = Convert.ToDouble(entry["row"], CultureInfo.InvariantCulture);
                var radius = Convert.ToDouble(entry["radius"], CultureInfo.InvariantCulture);
                for (var i = 0; i < count; i++)
                {
                    var dx = columns[i].D - cx;
                    var dy = rows[i].D - cy;
                    var dr = Math.Sqrt(dx * dx + dy * dy) - radius;
                    sum += dr * dr;
                }
            }
            else
            {
                var ny = Convert.ToDouble(entry["ny"], CultureInfo.InvariantCulture);
                var nx = Convert.ToDouble(entry["nx"], CultureInfo.InvariantCulture);
                var distance = Convert.ToDouble(entry["distancePx"], CultureInfo.InvariantCulture);
                for (var i = 0; i < count; i++)
                {
                    var d = nx * columns[i].D + ny * rows[i].D - distance;
                    sum += d * d;
                }
            }
            return Math.Sqrt(sum / count);
        }
        catch (Exception ex) when (ex is JlOperatorException or JlTupleAccessException)
        {
            return null;
        }
        finally
        {
            rows?.Dispose();
            columns?.Dispose();
        }
    }

    private static VisionOverlay? BuildOverlay(MetrologyObjectSpec spec, Dictionary<string, object?> entry, Alignment alignment)
    {
        double Get(string key) => Convert.ToDouble(entry[key], CultureInfo.InvariantCulture);
        var label = string.IsNullOrWhiteSpace(spec.Name) ? spec.Type : spec.Name;
        return spec.Type switch
        {
            "circle" => new VisionOverlay($"metrology-{spec.Name}", VisionOverlayType.Circle,
                Label: label, X: Get("column"), Y: Get("row"), Radius: Get("radius"), Stroke: "#52c41a"),
            "rectangle2" => new VisionOverlay($"metrology-{spec.Name}", VisionOverlayType.Rectangle,
                Label: label, X: Get("column"), Y: Get("row"),
                Width: Get("length1") * 2, Height: Get("length2") * 2, AngleDeg: Get("phiDeg"), Stroke: "#52c41a"),
            "ellipse" => new VisionOverlay($"metrology-{spec.Name}", VisionOverlayType.Polygon,
                Label: label, Points: EllipsePoints(Get("column"), Get("row"), Get("radius1"), Get("radius2"), Get("phiDeg")),
                Stroke: "#52c41a"),
            "line" => LineOverlay(spec, entry, alignment, label),
            _ => null
        };
    }

    private static VisionOverlay? LineOverlay(MetrologyObjectSpec spec, Dictionary<string, object?> entry, Alignment alignment, string label)
    {
        var line = TypedResult(spec, 0, entry, alignment).Line;
        return line is not { } segment ? null : new VisionOverlay($"metrology-{spec.Name}", VisionOverlayType.Line,
            Label: label, X: segment.Start.X, Y: segment.Start.Y, X2: segment.End.X, Y2: segment.End.Y, Stroke: "#52c41a");
    }

    private static IReadOnlyList<VisionOverlayPoint> EllipsePoints(double cx, double cy, double r1, double r2, double phiDeg)
    {
        var phi = phiDeg * Math.PI / 180d;
        var cos = Math.Cos(phi);
        var sin = Math.Sin(phi);
        var points = new List<VisionOverlayPoint>(72);
        for (var i = 0; i < 72; i++)
        {
            var t = i * Math.PI * 2 / 72;
            var x = r1 * Math.Cos(t);
            var y = r2 * Math.Sin(t);
            points.Add(new VisionOverlayPoint(cx + x * cos - y * sin, cy + x * sin + y * cos));
        }
        return points;
    }

    private static Alignment ResolveAlignment(NodeExecutionContext context, NodeDefinition node)
    {
        var mode = Choice(node, "alignMode", "none", "none", "pose", "fixed");
        if (mode == "pose")
        {
            if (!context.TryGetValue("pose", out var poseValue) || poseValue is null)
                throw new InvalidOperationException("alignMode=pose requires a pose input.");
            var pose = poseValue.Require<VisionPose2D>("pose");
            CoordinateValidation.RequireFinite(pose.X, "pose.X"); CoordinateValidation.RequireFinite(pose.Y, "pose.Y"); CoordinateValidation.RequireFinite(pose.ThetaDeg, "pose.ThetaDeg");
            return new Alignment("pose", pose.Y, pose.X, pose.ThetaDeg * Math.PI / 180d);
        }
        if (mode == "fixed")
        {
            var row = node.GetDouble("alignRow", 0);
            var column = node.GetDouble("alignColumn", 0);
            var angle = node.GetDouble("alignAngleDeg", 0);
            CoordinateValidation.RequireFinite(row, "alignRow"); CoordinateValidation.RequireFinite(column, "alignColumn"); CoordinateValidation.RequireFinite(angle, "alignAngleDeg");
            return new Alignment("fixed", row, column, angle * Math.PI / 180d);
        }
        return new Alignment("none", 0, 0, 0);
    }

    private readonly record struct Alignment(string Mode, double Row, double Column, double AngleRad)
    {
        public (double Row, double Column) Apply(double row, double column)
        {
            if (Mode == "none") return (row, column);
            var cos = Math.Cos(AngleRad);
            var sin = Math.Sin(AngleRad);
            var x = column;
            var y = row;
            return (sin * x + cos * y + Row, cos * x - sin * y + Column);
        }
    }

    private static List<MetrologyObjectSpec> ParseObjects(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("measure.metrology 'objects' is empty.");

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidOperationException($"measure.metrology 'objects' is not valid JSON: {ex.Message}", ex); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("measure.metrology 'objects' must be a JSON array.");

            var specs = new List<MetrologyObjectSpec>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var type = (element.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null)?.Trim().ToLowerInvariant();
                type = type switch
                {
                    "line" or "circle" or "ellipse" or "rectangle2" => type,
                    "rect" or "rectangle" => "rectangle2",
                    _ => throw new InvalidOperationException($"metrology object #{index} has unsupported type '{type}' (line|circle|ellipse|rectangle2).")
                };

                double? Number(string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
                string? Text(string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

                var spec = new MetrologyObjectSpec(
                    type,
                    Text("name") ?? $"obj{index}",
                    Number("row"), Number("column"), Number("radius"),
                    Number("phiDeg"), Number("radius1"), Number("radius2"), Number("length1"), Number("length2"),
                    Number("rowBegin"), Number("columnBegin"), Number("rowEnd"), Number("columnEnd"),
                    Number("measureLength1"), Number("measureLength2"), Number("sigma"), Number("threshold"),
                    Text("transition"), Text("select"), Number("minScore"));

                spec.Validate(index);
                specs.Add(spec);
                index++;
            }
            if (specs.Count > 512) throw new InvalidOperationException("Metrology supports at most 512 objects.");
            if (specs.GroupBy(spec => spec.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new InvalidOperationException("Metrology object names must be unique.");
            return specs;
        }
    }

    private static Mat ToGray(Mat source)
    {
        if (source.Channels() == 1) return source.Clone();
        var gray = new Mat();
        JLVisionOperators.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static string Choice(NodeDefinition node, string key, string fallback, params string[] allowed)
    {
        var value = node.GetString(key, fallback).Trim().ToLowerInvariant();
        if (!allowed.Contains(value))
            throw new InvalidOperationException($"{key} must be one of {string.Join('|', allowed)} (got '{value}').");
        return value;
    }

    private static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 4) : value;

    private sealed record MetrologyObjectSpec(
        string Type, string Name,
        double? Row, double? Column, double? Radius,
        double? PhiDeg, double? Radius1, double? Radius2, double? Length1, double? Length2,
        double? RowBegin, double? ColumnBegin, double? RowEnd, double? ColumnEnd,
        double? MeasureLength1, double? MeasureLength2, double? Sigma, double? Threshold,
        string? Transition, string? Select, double? MinScore)
    {
        public void Validate(int index)
        {
            void Require(double? value, string name)
            {
                if (value is null || !double.IsFinite(value.Value))
                    throw new InvalidOperationException($"metrology object #{index} ({Type}) requires a finite '{name}'.");
            }

            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Name != Name.Trim())
                throw new InvalidOperationException($"metrology object #{index} requires a unique name of 1..128 characters without surrounding whitespace.");
            foreach (var value in new[] { PhiDeg, MeasureLength1, MeasureLength2, Sigma, Threshold, MinScore })
                if (value is { } number && !double.IsFinite(number))
                    throw new InvalidOperationException($"metrology object #{index} parameters must be finite.");
            if (MeasureLength2 is < 0 || Threshold is < 0)
                throw new InvalidOperationException($"metrology object #{index} measureLength2/threshold must be non-negative.");
            switch (Type)
            {
                case "line":
                    Require(RowBegin, "rowBegin");
                    Require(ColumnBegin, "columnBegin");
                    Require(RowEnd, "rowEnd");
                    Require(ColumnEnd, "columnEnd");
                    if (Math.Abs(RowEnd!.Value - RowBegin!.Value) < 1e-9 && Math.Abs(ColumnEnd!.Value - ColumnBegin!.Value) < 1e-9)
                        throw new InvalidOperationException($"metrology object #{index} (line) has zero length.");
                    break;
                case "circle":
                    Require(Row, "row");
                    Require(Column, "column");
                    Require(Radius, "radius");
                    if (Radius <= 0) throw new InvalidOperationException($"metrology object #{index} (circle) radius must be > 0.");
                    break;
                case "ellipse":
                    Require(Row, "row");
                    Require(Column, "column");
                    Require(Radius1, "radius1");
                    Require(Radius2, "radius2");
                    if (Radius1 <= 0 || Radius2 <= 0) throw new InvalidOperationException($"metrology object #{index} (ellipse) radii must be > 0.");
                    break;
                case "rectangle2":
                    Require(Row, "row");
                    Require(Column, "column");
                    Require(Length1, "length1");
                    Require(Length2, "length2");
                    if (Length1 <= 0 || Length2 <= 0) throw new InvalidOperationException($"metrology object #{index} (rectangle2) lengths must be > 0.");
                    break;
            }

            if (MeasureLength1 is <= 0) throw new InvalidOperationException($"metrology object #{index} measureLength1 must be > 0.");
            if (Sigma is <= 0) throw new InvalidOperationException($"metrology object #{index} sigma must be > 0.");
            if (Transition is not null && Transition is not ("all" or "positive" or "negative"))
                throw new InvalidOperationException($"metrology object #{index} transition must be all|positive|negative.");
            if (Select is not null && Select is not ("all" or "first" or "last"))
                throw new InvalidOperationException($"metrology object #{index} select must be all|first|last.");
            if (MinScore is < 0 or > 1)
                throw new InvalidOperationException($"metrology object #{index} minScore must be within 0..1.");
        }
    }
}
