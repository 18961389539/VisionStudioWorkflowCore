using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// GD&T（形位公差）2D 基础件：基准系、真实位置度、圆跳动/全跳动（2026-10-27）。
///
/// 现场现状：`measure.parallelism` / `measure.perpendicularity` / `measure.concentricity` 只回答
/// "两条线差多少度""两个圆偏多少"，但图纸上最常见的三个框格——位置度 ⌖、圆跳动 ↗、基准 A|B——
/// 无法表达。工程师只能把偏差抄到 Excel 里手算，既无法进流程判定，也无法留痕。
///
/// 本文件补三个纯几何节点（不动引擎、不依赖 OpenCV）：
/// - tolerance.datum：把"基准 A 线 + 基准 B 点"折算成基准坐标系（原点 + X 轴位姿）；
/// - tolerance.position：位置度，输出图纸上写的 Ø 偏差（2×径向），支持矩形公差带与 MMC 补偿；
/// - tolerance.runout：圆跳动 / 全跳动，在基准坐标系里取径向或轴向的 FIM（最大读数差）。
///
/// 口径与兄弟节点保持一致：
/// - 0 = 关闭门限（withinTolerance 恒为 true），负值/非有限值直接报错；
/// - 尺寸单位完全跟随输入坐标（未标定为像素，标定后为 mm），节点不做任何隐式换算；
/// - 输入几何非法（零长度线、负半径、坐标越界）硬失败；样本不足/覆盖不足返回 found=false。
/// </summary>
internal static class GdtMath
{
    private const double MaxCoordinate = 100_000_000;

    internal static void Finite(params double[] values)
    {
        if (values.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxCoordinate))
            throw new InvalidOperationException("GD&T coordinates and lengths must be finite within +/-100000000.");
    }

    /// <summary>归一化到 (-180, 180]，与 geometry.angle / coordinate.* 的口径一致。</summary>
    internal static double NormalizeAngle(double degrees)
    {
        var normalized = degrees % 360.0;
        if (normalized <= -180.0) normalized += 360.0;
        if (normalized > 180.0) normalized -= 360.0;
        return normalized;
    }

    /// <summary>图像坐标 → 基准坐标：绕基准原点逆旋转 theta。</summary>
    internal static VisionPoint2D ToDatum(VisionPose2D datum, VisionPoint2D point)
    {
        var radians = datum.ThetaDeg * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = point.X - datum.X;
        var dy = point.Y - datum.Y;
        return new VisionPoint2D(cos * dx + sin * dy, -sin * dx + cos * dy);
    }

    /// <summary>基准坐标 → 图像坐标，供叠加层还原图纸上的基本尺寸点。</summary>
    internal static VisionPoint2D FromDatum(VisionPose2D datum, VisionPoint2D point)
    {
        var radians = datum.ThetaDeg * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new VisionPoint2D(
            datum.X + cos * point.X - sin * point.Y,
            datum.Y + sin * point.X + cos * point.Y);
    }

    internal static VisionPose2D ReadDatum(NodeExecutionContext context, string port, string nodeType)
    {
        var datum = context.Require<VisionPose2D>(port);
        Finite(datum.X, datum.Y, datum.ThetaDeg);
        if (Math.Abs(datum.ThetaDeg) > 3600)
            throw new InvalidOperationException($"{nodeType} datum theta must be within +/-3600 degrees.");
        return datum;
    }

    internal static string RequireUnit(NodeDefinition node, string nodeType)
    {
        var unit = node.GetString("unit", "px").Trim();
        if (unit.Length is < 1 or > 16)
            throw new InvalidOperationException($"{nodeType} unit must be 1..16 characters (label only; no conversion is applied).");
        return unit;
    }
}

/// <summary>
/// 基准系（tolerance.datum）：基准 A（拟合直线）+ 可选基准 B（点）→ 原点 + X 轴位姿。
/// 原点取"基准 B 在该直线上的投影"，这是 2D 图纸里最常见的 A|B 组合；没有 B 时退化为直线中点。
/// </summary>
public sealed class DatumFrameNode : IVisionNodeExecutor
{
    public string Type => "tolerance.datum";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var unit = GdtMath.RequireUnit(node, Type);
        var datumName = node.GetString("datumName", "A").Trim();
        if (datumName.Length is < 1 or > 16)
            throw new InvalidOperationException("tolerance.datum datumName must be 1..16 characters.");
        var flipAxis = node.GetBool("flipAxis", false);

        var line = context.Require<VisionLine2D>("primary");
        GdtMath.Finite(line.Start.X, line.Start.Y, line.End.X, line.End.Y);
        var dx = line.End.X - line.Start.X;
        var dy = line.End.Y - line.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9)
            throw new InvalidOperationException("tolerance.datum primary datum line must have non-zero length.");

        var sign = flipAxis ? -1 : 1;
        var ux = sign * dx / length;
        var uy = sign * dy / length;

        VisionPoint2D origin;
        double? secondaryOffset = null;
        VisionPoint2D? secondaryPoint = null;
        if (context.TryGetValue("secondary", out var secondaryValue))
        {
            secondaryPoint = secondaryValue.Require<VisionPoint2D>("secondary");
            GdtMath.Finite(secondaryPoint.Value.X, secondaryPoint.Value.Y);
            var rx = secondaryPoint.Value.X - line.Start.X;
            var ry = secondaryPoint.Value.Y - line.Start.Y;
            var along = rx * ux + ry * uy;
            origin = new VisionPoint2D(line.Start.X + along * ux, line.Start.Y + along * uy);
            // 有符号垂距，正值 = 落在轴 +90° 法向一侧（右向轴时即图像下方，Y 向下）。
            // 这是基准要素自身形状误差给下游公差带来的固定偏置，必须可追溯。
            secondaryOffset = ry * ux - rx * uy;
        }
        else
        {
            origin = new VisionPoint2D((line.Start.X + line.End.X) / 2, (line.Start.Y + line.End.Y) / 2);
        }

        var angleDeg = GdtMath.NormalizeAngle(Math.Atan2(uy, ux) * 180.0 / Math.PI);
        var pose = new VisionPose2D(origin.X, origin.Y, angleDeg);

        var datumJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            name = datumName,
            unit,
            origin = new { x = origin.X, y = origin.Y },
            axis = new { x = ux, y = uy },
            angleDeg,
            length,
            secondary = secondaryPoint is null
                ? null
                : new { x = secondaryPoint.Value.X, y = secondaryPoint.Value.Y, offset = secondaryOffset }
        });

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("datum", datumName), ("unit", unit), ("originX", origin.X), ("originY", origin.Y),
            ("angleDeg", angleDeg), ("length", length), ("secondaryOffset", secondaryOffset), ("flipAxis", flipAxis));

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["found"] = VisionValue.Boolean(true),
            ["pose"] = VisionValue.Pose(pose),
            ["origin"] = VisionValue.Point(origin),
            ["axisAngleDeg"] = VisionValue.Double(angleDeg),
            ["length"] = VisionValue.Double(length),
            ["datumJson"] = VisionValue.String(datumJson),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (secondaryOffset is { } offset) outputs["secondaryOffset"] = VisionValue.Double(offset);

        var overlays = new List<VisionOverlay>
        {
            new($"datum-{datumName}", VisionOverlayType.Line, X: line.Start.X, Y: line.Start.Y,
                X2: line.End.X, Y2: line.End.Y, Label: $"datum {datumName}"),
            new("datum-origin", VisionOverlayType.Point, X: origin.X, Y: origin.Y, Label: "origin")
        };
        if (secondaryPoint is { } point)
            overlays.Add(new VisionOverlay("datum-secondary", VisionOverlayType.Point,
                X: point.X, Y: point.Y, Label: "datum B"));

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["datum"] = datumName, ["unit"] = unit, ["originX"] = origin.X, ["originY"] = origin.Y,
                ["angleDeg"] = angleDeg, ["length"] = length, ["secondaryOffset"] = secondaryOffset,
                ["flipAxis"] = flipAxis, ["diagnostics"] = diagnostics
            }, overlays));
    }
}

/// <summary>
/// 位置度（tolerance.position）：被测要素中心相对基准系里的基本尺寸，输出图纸标注的 Ø 偏差。
/// 支持 Ø 圆形公差带与 ±X/±Y 矩形公差带，以及 MMC 最大实体补偿（孔/轴两种方向）。
/// </summary>
public sealed class PositionToleranceNode : IVisionNodeExecutor
{
    public string Type => "tolerance.position";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var unit = GdtMath.RequireUnit(node, Type);
        var basicX = node.GetDouble("basicX", 0);
        var basicY = node.GetDouble("basicY", 0);
        GdtMath.Finite(basicX, basicY);

        var zoneType = node.GetString("zoneType", "diameter").Trim().ToLowerInvariant();
        if (zoneType is not ("diameter" or "rectangular"))
            throw new InvalidOperationException("tolerance.position zoneType must be diameter or rectangular.");

        var diameterTolerance = node.GetDouble("diameterTolerance", 0.2);
        var toleranceX = node.GetDouble("toleranceX", 0.1);
        var toleranceY = node.GetDouble("toleranceY", 0.1);
        foreach (var (name, value) in new[]
                 {
                     ("diameterTolerance", diameterTolerance), ("toleranceX", toleranceX), ("toleranceY", toleranceY)
                 })
        {
            if (!double.IsFinite(value) || value is < 0 or > 1_000_000)
                throw new InvalidOperationException($"tolerance.position {name} must be finite within 0..1000000 (0 disables).");
        }

        var featureType = node.GetString("featureType", "hole").Trim().ToLowerInvariant();
        if (featureType is not ("hole" or "pin"))
            throw new InvalidOperationException("tolerance.position featureType must be hole or pin.");
        var mmcBonus = node.GetBool("mmcBonus", false);
        var mmcSize = node.GetDouble("mmcSize", 0);
        if (mmcBonus)
        {
            if (!double.IsFinite(mmcSize) || mmcSize <= 0)
                throw new InvalidOperationException("tolerance.position mmcSize must be > 0 when MMC bonus is enabled.");
        }

        var hasPoint = context.TryGetValue("point", out var pointValue);
        var hasCircle = context.TryGetValue("circle", out var circleValue);
        if (hasPoint == hasCircle)
            throw new InvalidOperationException(
                "tolerance.position requires exactly one of point or circle (both are connected or both are missing).");

        VisionPoint2D feature;
        double? measuredSize = null;
        if (hasCircle)
        {
            var circle = circleValue.Require<VisionCircle>("circle");
            GdtMath.Finite(circle.X, circle.Y, circle.Radius);
            if (circle.Radius <= 0)
                throw new InvalidOperationException("tolerance.position circle radius must be positive.");
            feature = new VisionPoint2D(circle.X, circle.Y);
            measuredSize = 2 * circle.Radius;
        }
        else
        {
            feature = pointValue.Require<VisionPoint2D>("point");
            GdtMath.Finite(feature.X, feature.Y);
        }

        if (context.TryGetValue("size", out var sizeValue))
        {
            var explicitSize = sizeValue.Require<double>("size");
            GdtMath.Finite(explicitSize);
            if (explicitSize <= 0)
                throw new InvalidOperationException("tolerance.position size input must be positive.");
            measuredSize = explicitSize; // 显式 size 端口优先于圆的直径（后者只是几何推论）
        }

        var datum = context.TryGetValue("datum", out var datumValue)
            ? GdtMath.ReadDatum(context, "datum", Type)
            : new VisionPose2D(0, 0, 0);

        var measured = GdtMath.ToDatum(datum, feature);
        var deviationX = measured.X - basicX;
        var deviationY = measured.Y - basicY;
        var radialDeviation = Math.Sqrt(deviationX * deviationX + deviationY * deviationY);
        var diametralDeviation = 2 * radialDeviation;

        var bonus = 0d;
        if (mmcBonus)
        {
            if (measuredSize is not { } actual)
                throw new InvalidOperationException(
                    "tolerance.position needs a size to apply MMC bonus; connect size or a circle.");
            // 孔在最大实体 = 最小直径，孔径变大才有补偿；轴相反。
            bonus = featureType == "hole"
                ? Math.Max(0, actual - mmcSize)
                : Math.Max(0, mmcSize - actual);
        }

        double positionDeviation;
        double effectiveTolerance;
        double utilization;
        bool within;
        if (zoneType == "diameter")
        {
            effectiveTolerance = diameterTolerance <= 0 ? 0 : diameterTolerance + bonus;
            positionDeviation = diametralDeviation;
            utilization = effectiveTolerance <= 0 ? 0 : positionDeviation / effectiveTolerance;
            within = effectiveTolerance <= 0 || positionDeviation <= effectiveTolerance + 1e-9;
        }
        else
        {
            var effectiveX = toleranceX <= 0 ? 0 : toleranceX + bonus;
            var effectiveY = toleranceY <= 0 ? 0 : toleranceY + bonus;
            positionDeviation = Math.Max(Math.Abs(deviationX), Math.Abs(deviationY));
            var useX = effectiveX > 0;
            var useY = effectiveY > 0;
            utilization = !useX && !useY
                ? 0
                : Math.Max(useX ? Math.Abs(deviationX) / effectiveX : 0, useY ? Math.Abs(deviationY) / effectiveY : 0);
            within = (!useX || Math.Abs(deviationX) <= effectiveX + 1e-9)
                     && (!useY || Math.Abs(deviationY) <= effectiveY + 1e-9);
            effectiveTolerance = useX && useY ? Math.Min(effectiveX, effectiveY) : Math.Max(effectiveX, effectiveY);
        }

        var basicTarget = GdtMath.FromDatum(datum, new VisionPoint2D(basicX, basicY));
        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("unit", unit), ("zoneType", zoneType), ("featureType", featureType),
            ("deviationX", deviationX), ("deviationY", deviationY),
            ("radialDeviation", radialDeviation), ("diametralDeviation", diametralDeviation),
            ("positionDeviation", positionDeviation), ("effectiveTolerance", effectiveTolerance),
            ("bonus", bonus), ("utilization", utilization), ("measuredSize", measuredSize),
            ("withinTolerance", within), ("datumApplied", context.TryGetValue("datum", out _)));

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["found"] = VisionValue.Boolean(true),
            ["withinTolerance"] = VisionValue.Boolean(within),
            ["deviationX"] = VisionValue.Double(deviationX),
            ["deviationY"] = VisionValue.Double(deviationY),
            ["radialDeviation"] = VisionValue.Double(radialDeviation),
            ["diametralDeviation"] = VisionValue.Double(diametralDeviation),
            ["positionDeviation"] = VisionValue.Double(positionDeviation),
            ["effectiveTolerance"] = VisionValue.Double(effectiveTolerance),
            ["bonus"] = VisionValue.Double(bonus),
            ["utilization"] = VisionValue.Double(utilization),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };

        var overlays = new List<VisionOverlay>
        {
            new("position-basic", VisionOverlayType.Point, X: basicTarget.X, Y: basicTarget.Y, Label: "basic"),
            new("position-measured", VisionOverlayType.Point, X: feature.X, Y: feature.Y, Label: "measured"),
            new("position-deviation", VisionOverlayType.Line,
                X: basicTarget.X, Y: basicTarget.Y, X2: feature.X, Y2: feature.Y,
                Label: $"T={positionDeviation:0.###}")
        };
        if (zoneType == "diameter")
        {
            var zoneRadius = effectiveTolerance / 2;
            if (zoneRadius > 0)
                overlays.Add(new VisionOverlay("position-zone", VisionOverlayType.Circle,
                    X: basicTarget.X, Y: basicTarget.Y, Radius: zoneRadius, Label: $"Ø{effectiveTolerance:0.###}"));
        }
        else if (toleranceX > 0 || toleranceY > 0)
        {
            overlays.Add(new VisionOverlay("position-zone", VisionOverlayType.Rectangle,
                X: basicTarget.X - toleranceX, Y: basicTarget.Y - toleranceY,
                Width: 2 * toleranceX, Height: 2 * toleranceY, Label: $"±{toleranceX:0.###}/±{toleranceY:0.###}"));
        }

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["unit"] = unit, ["zoneType"] = zoneType, ["featureType"] = featureType,
                ["deviationX"] = deviationX, ["deviationY"] = deviationY,
                ["radialDeviation"] = radialDeviation, ["diametralDeviation"] = diametralDeviation,
                ["positionDeviation"] = positionDeviation, ["effectiveTolerance"] = effectiveTolerance,
                ["bonus"] = bonus, ["utilization"] = utilization, ["measuredSize"] = measuredSize,
                ["withinTolerance"] = within, ["datumApplied"] = context.TryGetValue("datum", out _),
                ["diagnostics"] = diagnostics
            }, overlays));
    }
}

/// <summary>
/// 圆跳动 / 全跳动（tolerance.runout）：在基准坐标系里取径向或轴向的读数极差（FIM）。
/// radial 用于圆柱面（到基准轴的距离），axial 用于端面（沿基准轴方向的坐标）。
/// </summary>
public sealed class RunoutToleranceNode : IVisionNodeExecutor
{
    public string Type => "tolerance.runout";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var unit = GdtMath.RequireUnit(node, Type);
        var mode = node.GetString("mode", "radial").Trim().ToLowerInvariant();
        if (mode is not ("radial" or "axial"))
            throw new InvalidOperationException("tolerance.runout mode must be radial or axial.");

        var tolerance = node.GetDouble("tolerance", 0);
        if (!double.IsFinite(tolerance) || tolerance is < 0 or > 1_000_000)
            throw new InvalidOperationException("tolerance.runout tolerance must be finite within 0..1000000 (0 disables).");

        var minCoverageDeg = node.GetDouble("minCoverageDeg", 0);
        if (!double.IsFinite(minCoverageDeg) || minCoverageDeg is < 0 or > 360)
            throw new InvalidOperationException("tolerance.runout minCoverageDeg must be finite within 0..360.");

        var datum = GdtMath.ReadDatum(context, "datum", Type);
        var samples = context.Require<IReadOnlyList<VisionPoint2D>>("samples");
        if (samples.Count > ToleranceGeometry.MaxPoints)
            throw new InvalidOperationException(
                $"tolerance.runout sample budget exceeded (max {ToleranceGeometry.MaxPoints} points).");

        if (samples.Count < 3)
        {
            return ValueTask.FromResult(ToleranceGeometry.Missing(samples.Count,
                $"runout needs at least 3 samples, got {samples.Count}", nodeType: Type));
        }

        var values = new double[samples.Count];
        var angles = new double[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            var point = samples[index];
            GdtMath.Finite(point.X, point.Y);
            var local = GdtMath.ToDatum(datum, point);
            values[index] = mode == "radial"
                ? Math.Sqrt(local.X * local.X + local.Y * local.Y)
                : local.X;
            angles[index] = GdtMath.NormalizeAngle(Math.Atan2(local.Y, local.X) * 180.0 / Math.PI);
        }

        var coverageDeg = mode == "radial" ? Coverage(angles) : 360.0;
        if (mode == "radial" && minCoverageDeg > 0 && coverageDeg < minCoverageDeg - 1e-9)
        {
            return ValueTask.FromResult(ToleranceGeometry.Missing(samples.Count,
                $"radial coverage {coverageDeg:0.###} deg is below minCoverageDeg={minCoverageDeg:0.###}",
                nodeType: Type));
        }

        var maxIndex = 0;
        var minIndex = 0;
        var sum = 0d;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] > values[maxIndex]) maxIndex = index;
            if (values[index] < values[minIndex]) minIndex = index;
            sum += values[index];
        }

        var runout = values[maxIndex] - values[minIndex];
        var mean = sum / values.Length;
        var within = tolerance <= 0 || runout <= tolerance + 1e-9;

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("unit", unit), ("mode", mode), ("sampleCount", samples.Count),
            ("runout", runout), ("minValue", values[minIndex]), ("maxValue", values[maxIndex]),
            ("meanValue", mean), ("coverageDeg", coverageDeg), ("tolerance", tolerance),
            ("withinTolerance", within));

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["found"] = VisionValue.Boolean(true),
            ["sampleCount"] = VisionValue.Integer(samples.Count),
            ["runout"] = VisionValue.Double(runout),
            ["minValue"] = VisionValue.Double(values[minIndex]),
            ["maxValue"] = VisionValue.Double(values[maxIndex]),
            ["meanValue"] = VisionValue.Double(mean),
            ["withinTolerance"] = VisionValue.Boolean(within),
            ["highPoint"] = VisionValue.Point(samples[maxIndex]),
            ["lowPoint"] = VisionValue.Point(samples[minIndex]),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (mode == "radial")
        {
            outputs["coverageDeg"] = VisionValue.Double(coverageDeg);
            outputs["highAngleDeg"] = VisionValue.Double(angles[maxIndex]);
            outputs["lowAngleDeg"] = VisionValue.Double(angles[minIndex]);
        }

        var pointOverlays = new List<VisionOverlay>
        {
            new("runout-high", VisionOverlayType.Point,
                X: samples[maxIndex].X, Y: samples[maxIndex].Y, Label: $"max {values[maxIndex]:0.###}"),
            new("runout-low", VisionOverlayType.Point,
                X: samples[minIndex].X, Y: samples[minIndex].Y, Label: $"min {values[minIndex]:0.###}")
        };
        if (mode == "radial" && mean > 0)
        {
            pointOverlays.Add(new VisionOverlay("runout-nominal", VisionOverlayType.Circle,
                X: datum.X, Y: datum.Y, Radius: mean, Label: $"FIM {runout:0.###}"));
        }

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["unit"] = unit, ["mode"] = mode, ["sampleCount"] = samples.Count,
                ["runout"] = runout, ["minValue"] = values[minIndex], ["maxValue"] = values[maxIndex],
                ["meanValue"] = mean, ["coverageDeg"] = coverageDeg, ["tolerance"] = tolerance,
                ["withinTolerance"] = within, ["datumApplied"] = true, ["diagnostics"] = diagnostics
            }, pointOverlays));
    }

    /// <summary>覆盖角 = 360° − 相邻样本的最大角度缺口（与 measure.roundness 同口径）。</summary>
    private static double Coverage(double[] angles)
    {
        var sorted = angles.OrderBy(angle => angle).ToArray();
        var largestGap = 0d;
        for (var index = 0; index < sorted.Length; index++)
        {
            var next = index + 1 < sorted.Length ? sorted[index + 1] : sorted[0] + 360.0;
            largestGap = Math.Max(largestGap, next - sorted[index]);
        }
        return Math.Max(0, 360.0 - largestGap);
    }
}
