using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

public sealed class CaliperWidthNode : IVisionNodeExecutor, IVisionNodeDefinitionValidator
{
    public string Type => "measure.caliperWidth";

    void IVisionNodeDefinitionValidator.ValidateDefinition(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputs)
    {
        MeasurementDefinitionValidation.SearchLine(node, catalog, connectedInputs);
        MeasurementDefinitionValidation.WidthLimits(node, catalog);
    }


    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty()) throw new InvalidOperationException("Caliper width measurement requires a non-empty image.");
        var search = SubpixelEdgeCore.ResolveSearchLine(context, node);
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 5, 1, 99));
        var threshold = SubpixelEdgeCore.RequireFiniteParameter(node, "edgeThreshold", 12);
        var minimum = SubpixelEdgeCore.RequireFiniteParameter(node, "minWidth", 0);
        var maximum = SubpixelEdgeCore.RequireFiniteParameter(node, "maxWidth", 0);
        var nominal = SubpixelEdgeCore.RequireFiniteParameter(node, "nominalWidth", 20);
        // 产品公差与候选筛选分离：minWidth/maxWidth 只筛候选边缘对，尺寸判定必须走独立公差，
        // 否则超差会表现成 found=false，NG 与“未测到”在下游无法区分。
        var lowerTolerance = SubpixelEdgeCore.RequireFiniteParameter(node, "lowerTolerance", 0);
        var upperTolerance = SubpixelEdgeCore.RequireFiniteParameter(node, "upperTolerance", 0);
        if (lowerTolerance < 0 || upperTolerance < 0)
            throw new InvalidOperationException("Width tolerances must be non-negative; 0/0 disables the tolerance gate.");
        var toleranceEnabled = lowerTolerance > 0 || upperTolerance > 0;
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "bright", "bright", "dark", "any");
        var direction = SubpixelEdgeCore.ChoiceParameter(node, "direction", "strongest", "strongest", "firstfromstart", "firstfromend", "closestwidth");
        var minEdgeSegmentSamples = SubpixelEdgeCore.ClampedIntParameter(node, "minEdgeSegmentSamples", 0, 0, 100000);
        if (threshold is < 0 or > 255) throw new InvalidOperationException("Edge threshold must be within 0..255.");
        if (minimum < 0 || maximum < 0 || maximum > 0 && maximum < minimum)
            throw new InvalidOperationException("Width limits must satisfy 0 <= minWidth <= maxWidth; maxWidth=0 disables the upper limit.");
        if (nominal < 0 || direction == "closestwidth" && nominal <= 0)
            throw new InvalidOperationException("Nominal width must be positive when selecting the closest width.");
        if (MeasurementMath.Hypot(search.End.X - search.Start.X, search.End.Y - search.Start.Y) < 2)
            throw new InvalidOperationException("The search line is too short (minimum 2 px).");

        var roi = ContourNodesHelper.ResolveRoi(context, node);
        using var mask = roi?.BuildMaskForMeasurement(source.Mat.Size(), cancellationToken);
        var gray = PreprocessImageHelpers.ToGray8(source.Mat, out var autoGray, out var owned);
        try
        {
            var (start, end, clamped, intersects) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
            var line = intersects ? new VisionLine2D(start, end) : search;
            double[]? profile = null;
            var measurement = !intersects
                ? new JLVisionPairMeasurement.Result([], 0, "search-line-outside-image")
                : MeasurementMath.Hypot(end.X - start.X, end.Y - start.Y) < 2
                    ? new JLVisionPairMeasurement.Result([], 0, "search-line-clipped-too-short")
                    : Measure();
            JLVisionPairMeasurement.Result Measure()
            {
                profile = SubpixelEdgeCore.SampleProfile(gray, mask, start, end, sampleWidth, out _, out var spacing, cancellationToken);
                return JLVisionPairMeasurement.Find(profile, spacing, polarity, threshold, cancellationToken);
            }

            // ROI 空洞会把剖面拆成多段：边缘对跨洞时“宽度”没有物理意义，必须拒绝。
            var valid = profile?.Select(value => double.IsFinite(value)).ToArray() ?? [];
            var segments = new List<(int Start, int End)>();
            var maskBreaks = new List<int>();
            for (var index = 0; index < valid.Length;)
            {
                if (!valid[index]) { maskBreaks.Add(index); index++; continue; }
                var segmentStart = index;
                while (index < valid.Length && valid[index]) index++;
                segments.Add((segmentStart, index - 1));
            }
            var longestContinuousSamples = segments.Count == 0 ? 0 : segments.Max(segment => segment.End - segment.Start + 1);
            var continuityRejected = 0;
            var qualified = measurement.Pairs.Where(pair => pair.Width >= minimum - 1e-9
                && (maximum == 0 || pair.Width <= maximum + 1e-9)).ToArray();
            var qualifiedPairs = qualified.Where(pair =>
            {
                if (maskBreaks.Count > 0)
                {
                    var low = (int)Math.Floor(Math.Min(pair.First, pair.Second));
                    var high = (int)Math.Ceiling(Math.Max(pair.First, pair.Second));
                    if (maskBreaks.Any(index => index >= low && index <= high)) { continuityRejected++; return false; }
                }
                if (minEdgeSegmentSamples > 0)
                {
                    if (SegmentLengthAt(pair.First) is not { } firstLength || firstLength < minEdgeSegmentSamples ||
                        SegmentLengthAt(pair.Second) is not { } secondLength || secondLength < minEdgeSegmentSamples)
                    { continuityRejected++; return false; }
                }
                return true;
            }).ToArray();
            var found = qualifiedPairs.Length > 0;
            var selected = !found ? default : direction switch
            {
                "firstfromstart" => qualifiedPairs[0],
                "firstfromend" => qualifiedPairs[^1],
                "closestwidth" => qualifiedPairs.OrderBy(pair => Math.Abs(pair.Width - nominal)).ThenByDescending(pair => pair.Strength).First(),
                _ => qualifiedPairs.OrderByDescending(pair => pair.Strength).ThenBy(pair => pair.First).First()
            };
            var reason = found ? "ok"
                : qualified.Length > 0 ? "edge-pair-spans-mask-break"
                : measurement.Pairs.Count > 0 ? "no-pair-within-width-range"
                : measurement.Reason;

            int? SegmentLengthAt(double position)
            {
                var index = (int)Math.Round(position);
                foreach (var segment in segments)
                    if (index >= segment.Start && index <= segment.End) return segment.End - segment.Start + 1;
                return null;
            }
            var overlays = new List<VisionOverlay>();
            if (intersects) overlays.Add(VisionGeometryHelpers.OverlayLine("caliper-width-search", line, "#597ef7", 1.5));
            var outputs = new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(found),
                ["searchLine"] = VisionValue.Line(line),
                ["sampleCount"] = VisionValue.Integer(measurement.SampleCount),
                ["pairCount"] = VisionValue.Integer(qualified.Length),
                ["continuousPairCount"] = VisionValue.Integer(qualifiedPairs.Length),
                ["continuousSegmentCount"] = VisionValue.Integer(segments.Count),
                ["longestContinuousSamples"] = VisionValue.Integer(longestContinuousSamples),
                ["maskBreakCount"] = VisionValue.Integer(maskBreaks.Count),
                ["maskBreakIndicesJson"] = VisionValue.String(System.Text.Json.JsonSerializer.Serialize(maskBreaks)),
                ["continuityRejectedCount"] = VisionValue.Integer(continuityRejected),
                ["minEdgeSegmentSamples"] = VisionValue.Integer(minEdgeSegmentSamples),
                ["toleranceEnabled"] = VisionValue.Boolean(toleranceEnabled)
            };
            var widthDeviation = 0d;
            var withinTolerance = false;
            var widthOutOfTolerance = false;
            VisionPoint2D? center = null;
            if (found)
            {
                var first = SubpixelEdgeCore.Locate(start, end, selected.First);
                var second = SubpixelEdgeCore.Locate(start, end, selected.Second);
                center = new VisionPoint2D((first.X + second.X) / 2, (first.Y + second.Y) / 2);
                outputs["point1"] = VisionValue.Point(first);
                outputs["point2"] = VisionValue.Point(second);
                outputs["center"] = VisionValue.Point(center.Value);
                outputs["width"] = VisionValue.Double(selected.Width);
                outputs["strength"] = VisionValue.Double(selected.Strength);
                widthDeviation = selected.Width - nominal;
                withinTolerance = toleranceEnabled && widthDeviation >= -lowerTolerance - 1e-9 && widthDeviation <= upperTolerance + 1e-9;
                widthOutOfTolerance = toleranceEnabled && !withinTolerance;
                outputs["widthDeviation"] = VisionValue.Double(widthDeviation);
                outputs["widthOutOfTolerance"] = VisionValue.Boolean(widthOutOfTolerance);
                overlays.Add(VisionGeometryHelpers.OverlayLine("caliper-width-result", new(first, second), "#52c41a", 3));
                overlays.Add(new("caliper-width-first", VisionOverlayType.Point, X: first.X, Y: first.Y, Stroke: "#f5222d", StrokeWidth: 4));
                overlays.Add(new("caliper-width-second", VisionOverlayType.Point, X: second.X, Y: second.Y, Stroke: "#f5222d", StrokeWidth: 4));
                overlays.Add(new("caliper-width-label", VisionOverlayType.Text, Label: $"W={selected.Width:0.000} px", X: center.Value.X + 7, Y: center.Value.Y - 10, Stroke: "#52c41a"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            // 公差关闭时 withinTolerance 恒为 false，并显式标记 not-evaluated；
            // 任何消费者都不能把“未评价”当成 PASS。
            var evaluationState = !found ? "not-measured" : !toleranceEnabled ? "not-evaluated" : withinTolerance ? "pass" : "fail";
            outputs["withinTolerance"] = VisionValue.Boolean(withinTolerance);
            outputs["evaluationState"] = VisionValue.String(evaluationState);
            outputs["image"] = VisionValue.Image(source.CloneOwned());
            var diag = CoordinateValidation.Diagnostics(Type,
                ("found", found), ("reason", reason), ("pairCount", qualified.Length),
                ("sampleCount", measurement.SampleCount), ("polarity", polarity), ("direction", direction),
                ("minWidth", minimum), ("maxWidth", maximum), ("nominalWidth", nominal),
                ("lowerTolerance", lowerTolerance), ("upperTolerance", upperTolerance),
                ("continuousSegmentCount", segments.Count), ("longestContinuousSamples", longestContinuousSamples),
                ("maskBreakCount", maskBreaks.Count), ("continuityRejectedCount", continuityRejected),
                ("minEdgeSegmentSamples", minEdgeSegmentSamples),
                ("widthDeviation", found ? widthDeviation : null), ("toleranceEnabled", toleranceEnabled),
                ("withinTolerance", withinTolerance), ("evaluationState", evaluationState),
                ("roi", roi?.Type.ToString() ?? "FullImage"));
            outputs["diagnostics"] = VisionValue.String(diag);
            return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
            {
                ["found"] = found, ["reason"] = reason, ["width"] = found ? selected.Width : null,
                ["centerX"] = center?.X, ["centerY"] = center?.Y,
                ["strength"] = found ? selected.Strength : null,
                ["firstAmplitude"] = found ? selected.FirstAmplitude : null,
                ["secondAmplitude"] = found ? selected.SecondAmplitude : null,
                ["candidatePairCount"] = measurement.Pairs.Count, ["pairCount"] = qualified.Length,
                ["rejectedByWidth"] = measurement.Pairs.Count - qualified.Length,
                ["continuousPairCount"] = qualifiedPairs.Length,
                ["continuousSegmentCount"] = segments.Count, ["longestContinuousSamples"] = longestContinuousSamples,
                ["maskBreakCount"] = maskBreaks.Count, ["maskBreakIndices"] = maskBreaks,
                ["continuityRejectedCount"] = continuityRejected, ["minEdgeSegmentSamples"] = minEdgeSegmentSamples,
                ["sampleCount"] = measurement.SampleCount, ["sampleWidth"] = sampleWidth,
                ["polarity"] = polarity, ["direction"] = direction, ["edgeThreshold"] = threshold,
                ["minWidth"] = minimum, ["maxWidth"] = maximum, ["nominalWidth"] = nominal,
                ["lowerTolerance"] = lowerTolerance, ["upperTolerance"] = upperTolerance,
                ["widthDeviation"] = found ? widthDeviation : null, ["toleranceEnabled"] = toleranceEnabled,
                ["withinTolerance"] = withinTolerance, ["widthOutOfTolerance"] = widthOutOfTolerance,
                ["evaluationState"] = evaluationState,
                ["lineClampedToImage"] = clamped, ["autoGray"] = autoGray,
                ["roi"] = roi?.Type.ToString() ?? "FullImage", ["unit"] = "px", ["diagnostics"] = diag
            }, overlays));
        }
        finally { owned?.Dispose(); }
    }
}
