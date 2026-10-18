using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class CaliperWidthNode : IVisionNodeExecutor
{
    public string Type => "measure.caliperWidth";

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
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "bright", "bright", "dark", "any");
        var direction = SubpixelEdgeCore.ChoiceParameter(node, "direction", "strongest", "strongest", "firstfromstart", "firstfromend", "closestwidth");
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
            var measurement = !intersects
                ? new JLVisionPairMeasurement.Result([], 0, "search-line-outside-image")
                : MeasurementMath.Hypot(end.X - start.X, end.Y - start.Y) < 2
                    ? new JLVisionPairMeasurement.Result([], 0, "search-line-clipped-too-short")
                    : Measure();
            JLVisionPairMeasurement.Result Measure()
            {
                var profile = SubpixelEdgeCore.SampleProfile(gray, mask, start, end, sampleWidth, out _, out var spacing, cancellationToken);
                return JLVisionPairMeasurement.Find(profile, spacing, polarity, threshold, cancellationToken);
            }

            var qualified = measurement.Pairs.Where(pair => pair.Width >= minimum - 1e-9
                && (maximum == 0 || pair.Width <= maximum + 1e-9)).ToArray();
            var found = qualified.Length > 0;
            var selected = !found ? default : direction switch
            {
                "firstfromstart" => qualified[0],
                "firstfromend" => qualified[^1],
                "closestwidth" => qualified.OrderBy(pair => Math.Abs(pair.Width - nominal)).ThenByDescending(pair => pair.Strength).First(),
                _ => qualified.OrderByDescending(pair => pair.Strength).ThenBy(pair => pair.First).First()
            };
            var reason = found ? "ok" : measurement.Pairs.Count > 0 ? "no-pair-within-width-range" : measurement.Reason;
            var overlays = new List<VisionOverlay>();
            if (intersects) overlays.Add(VisionGeometryHelpers.OverlayLine("caliper-width-search", line, "#597ef7", 1.5));
            var outputs = new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(found),
                ["searchLine"] = VisionValue.Line(line),
                ["sampleCount"] = VisionValue.Integer(measurement.SampleCount),
                ["pairCount"] = VisionValue.Integer(qualified.Length)
            };
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
                overlays.Add(VisionGeometryHelpers.OverlayLine("caliper-width-result", new(first, second), "#52c41a", 3));
                overlays.Add(new("caliper-width-first", VisionOverlayType.Point, X: first.X, Y: first.Y, Stroke: "#f5222d", StrokeWidth: 4));
                overlays.Add(new("caliper-width-second", VisionOverlayType.Point, X: second.X, Y: second.Y, Stroke: "#f5222d", StrokeWidth: 4));
                overlays.Add(new("caliper-width-label", VisionOverlayType.Text, Label: $"W={selected.Width:0.000} px", X: center.Value.X + 7, Y: center.Value.Y - 10, Stroke: "#52c41a"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            outputs["image"] = VisionValue.Image(source.CloneOwned());
            return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
            {
                ["found"] = found, ["reason"] = reason, ["width"] = found ? selected.Width : null,
                ["centerX"] = center?.X, ["centerY"] = center?.Y,
                ["strength"] = found ? selected.Strength : null,
                ["firstAmplitude"] = found ? selected.FirstAmplitude : null,
                ["secondAmplitude"] = found ? selected.SecondAmplitude : null,
                ["candidatePairCount"] = measurement.Pairs.Count, ["pairCount"] = qualified.Length,
                ["rejectedByWidth"] = measurement.Pairs.Count - qualified.Length,
                ["sampleCount"] = measurement.SampleCount, ["sampleWidth"] = sampleWidth,
                ["polarity"] = polarity, ["direction"] = direction, ["edgeThreshold"] = threshold,
                ["minWidth"] = minimum, ["maxWidth"] = maximum, ["nominalWidth"] = nominal,
                ["lineClampedToImage"] = clamped, ["autoGray"] = autoGray,
                ["roi"] = roi?.Type.ToString() ?? "FullImage", ["unit"] = "px"
            }, overlays));
        }
        finally { owned?.Dispose(); }
    }
}
