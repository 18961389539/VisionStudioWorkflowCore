using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class ContourThicknessNode : IVisionNodeExecutor
{
    public string Type => "measure.contourThickness";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = SubpixelEdgeCore.ChoiceParameter(node, "measurementMode", "shortest", "shortest", "innernormal");
        var step = SubpixelEdgeCore.RequireFiniteParameter(node, "sampleStep", 2);
        var budgetValue = SubpixelEdgeCore.RequireFiniteParameter(node, "maxSamplesPerBoundary", 4096);
        var lower = SubpixelEdgeCore.RequireFiniteParameter(node, "minThickness", 0);
        var upper = SubpixelEdgeCore.RequireFiniteParameter(node, "maxThickness", 0);
        if (step is < .1 or > 1_000_000) throw new InvalidOperationException("Thickness sampleStep must be within 0.1..1000000 px.");
        if (budgetValue is < 8 or > 4096 || budgetValue != Math.Truncate(budgetValue))
            throw new InvalidOperationException("Thickness maxSamplesPerBoundary must be an integer within 8..4096.");
        if (lower < 0 || upper < 0 || upper > 0 && upper < lower)
            throw new InvalidOperationException("Thickness limits must satisfy 0 <= minThickness <= maxThickness; maxThickness=0 disables the upper limit.");
        var innerContour = Select("innerContours", "innerIndex");
        var outerContour = Select("outerContours", "outerIndex");
        var inner = innerContour is null ? [] : ToleranceGeometry.Points(innerContour.Points);
        var outer = outerContour is null ? [] : ToleranceGeometry.Points(outerContour.Points);
        var result = inner.Length < 3 || outer.Length < 3
            ? new ContourThicknessMeasurement.Result("missing-or-insufficient-contour", false, [], 0, 0, 0, null)
            : innerContour!.Closed && outerContour!.Closed
                ? ContourThicknessMeasurement.Measure(inner, outer, mode, step, (int)budgetValue, cancellationToken)
                : new ContourThicknessMeasurement.Result("open-contour", false, [], 0, 0, 0, null);
        var found = result.Reason == "ok";
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(found), ["contained"] = VisionValue.Boolean(result.Contained),
            ["sampleCount"] = VisionValue.Integer(result.Samples.Count),
            ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(result.Samples.Select(sample => new
            {
                sample.Boundary, sample.Index, sample.ArcLength, sample.Weight, sample.Inner, sample.Outer, sample.Thickness,
                WithinTolerance = sample.Thickness >= lower && (upper == 0 || sample.Thickness <= upper)
            })))
        };
        var summary = new Dictionary<string, object?>
        {
            ["found"] = found, ["reason"] = result.Reason, ["contained"] = result.Contained,
            ["measurementMode"] = mode, ["sampleCount"] = result.Samples.Count,
            ["requestedSamples"] = result.RequestedSamples, ["invalidSamples"] = result.InvalidSamples,
            ["sampleStep"] = step, ["maxSamplesPerBoundary"] = budgetValue,
            ["lowerThicknessLimit"] = lower, ["upperThicknessLimit"] = upper,
            ["unit"] = "px", ["algorithmBackend"] = "JLVision"
        };
        var overlays = new List<VisionOverlay>();
        AddBoundary(inner, "thickness-inner", "#597ef7", innerContour?.Closed == true);
        AddBoundary(outer, "thickness-outer", "#13c2c2", outerContour?.Closed == true);
        if (found)
        {
            var totalWeight = result.Samples.Sum(sample => sample.Weight);
            var mean = result.Samples.Sum(sample => sample.Thickness * sample.Weight) / totalWeight;
            var deviation = Math.Sqrt(result.Samples.Sum(sample => sample.Weight * Math.Pow(sample.Thickness - mean, 2)) / totalWeight);
            var maximum = result.Samples.Max(sample => sample.Thickness);
            var thin = result.Samples.Count(sample => sample.Thickness < lower);
            var outside = result.Samples.Count(sample => sample.Thickness < lower || upper > 0 && sample.Thickness > upper);
            var pass = result.Minimum >= lower && (upper == 0 || maximum <= upper);
            var minimumLine = result.MinimumLine!.Value;
            outputs["minThickness"] = VisionValue.Double(result.Minimum);
            outputs["maxThickness"] = VisionValue.Double(maximum);
            outputs["meanThickness"] = VisionValue.Double(mean);
            outputs["stdDev"] = VisionValue.Double(deviation);
            outputs["variation"] = VisionValue.Double(maximum - result.Minimum);
            outputs["withinTolerance"] = VisionValue.Boolean(pass);
            outputs["thinSampleCount"] = VisionValue.Integer(thin);
            outputs["minInnerPoint"] = VisionValue.Point(minimumLine.Start);
            outputs["minOuterPoint"] = VisionValue.Point(minimumLine.End);
            outputs["minLine"] = VisionValue.Line(minimumLine);
            summary["minThickness"] = result.Minimum; summary["maxThickness"] = maximum;
            summary["meanThickness"] = mean; summary["stdDev"] = deviation;
            summary["withinTolerance"] = pass; summary["thinSampleCount"] = thin;
            summary["outOfToleranceCount"] = outside;
            // Spread the preview across the perimeter; always include the minimum line.
            var stride = Math.Max(1, (int)Math.Ceiling(result.Samples.Count / 160d));
            var selected = result.Samples.Where((_, index) => index % stride == 0).ToList();
            selected.AddRange(result.Samples.Where(sample => sample.Thickness < lower).OrderBy(sample => sample.Thickness).Take(40));
            foreach (var sample in selected.Distinct())
            {
                var failed = sample.Thickness < lower || upper > 0 && sample.Thickness > upper;
                overlays.Add(new($"thickness-{sample.Boundary}-{sample.Index}", VisionOverlayType.Line,
                    X: sample.Inner.X, Y: sample.Inner.Y, X2: sample.Outer.X, Y2: sample.Outer.Y,
                    Stroke: failed ? "#f5222d" : "#52c41a", StrokeWidth: failed ? 2.5 : 1));
            }
            overlays.Add(new("thickness-minimum", VisionOverlayType.Line,
                X: minimumLine.Start.X, Y: minimumLine.Start.Y, X2: minimumLine.End.X, Y2: minimumLine.End.Y,
                Stroke: pass ? "#faad14" : "#f5222d", StrokeWidth: 4, Label: $"min={result.Minimum:0.###} px"));
            summary["overlaySamples"] = selected.Distinct().Count();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (context.TryGetValue("image", out _)) outputs["image"] = VisionValue.Image(context.RequireImage("image").CloneOwned());
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));

        VisionContour? Select(string port, string parameter)
        {
            var contours = context.Require<IReadOnlyList<VisionContour>>(port);
            var index = node.GetInt(parameter, 0);
            if (index is < 0 or > 1999 || contours.Count > 2000) throw new InvalidOperationException("Contour index must be 0..1999 and list budget is 2000 contours.");
            if (index >= contours.Count) return null;
            var contour = contours[index];
            if (contour?.Points is null) throw new InvalidOperationException("Contour and its points must be non-null.");
            return contour;
        }
        void AddBoundary(VisionPoint2D[] points, string id, string color, bool closed)
        {
            if (points.Length >= 2) overlays.Add(new(id, VisionOverlayType.Contour, Stroke: color,
                Points: (closed ? points.Append(points[0]) : points).Select(point => new VisionOverlayPoint(point.X, point.Y)).ToArray()));
        }
    }
}
