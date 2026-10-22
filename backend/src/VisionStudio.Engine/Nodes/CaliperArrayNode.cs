using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Cognex-style multi-caliper width array (鍗″昂闃靛垪): place N edge-pair calipers
/// along a guide parallel to the measured boundaries, each searching along the
/// same profile direction as <c>measure.caliperWidth</c>.
/// Aggregates mean/min/max/std/cv width plus per-caliper JSON (optional profile samples).
/// Honest limits vs Cognex Caliper Tool / HALCON measure_pairs arrays: no circular
/// station layout here (use <c>measure.caliperCircle</c>), no interactive score-curve
/// editor, no multi-pair ranking beyond the shared direction selector, and no
/// fixture-trained expected-width model.
/// </summary>
public sealed class CaliperArrayNode : IVisionNodeExecutor
{
    public string Type => "measure.caliperArray";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty()) throw new InvalidOperationException("Caliper array measurement requires a non-empty image.");

        var search = SubpixelEdgeCore.ResolveSearchLine(context, node);
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 5, 1, 99));
        var threshold = SubpixelEdgeCore.RequireFiniteParameter(node, "edgeThreshold", 12);
        var minimum = SubpixelEdgeCore.RequireFiniteParameter(node, "minWidth", 0);
        var maximum = SubpixelEdgeCore.RequireFiniteParameter(node, "maxWidth", 0);
        var nominal = SubpixelEdgeCore.RequireFiniteParameter(node, "nominalWidth", 20);
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "bright", "bright", "dark", "any");
        var direction = SubpixelEdgeCore.ChoiceParameter(node, "direction", "strongest", "strongest", "firstfromstart", "firstfromend", "closestwidth");
        var caliperCount = SubpixelEdgeCore.ClampedIntParameter(node, "caliperCount", 9, 1, 101);
        var minValidCalipers = SubpixelEdgeCore.ClampedIntParameter(node, "minValidCalipers", 1, 1, 101);
        var edgeLength = SubpixelEdgeCore.RequireFiniteParameter(node, "edgeLength", 0);
        var maxWidthStd = SubpixelEdgeCore.RequireFiniteParameter(node, "maxWidthStd", 0);
        if (edgeLength is < 0 or > 10000) throw new InvalidOperationException("edgeLength must be between 0 and 10000.");
        if (maxWidthStd < 0) throw new InvalidOperationException("maxWidthStd must be >= 0 (0 disables the gate).");
        var includeProfiles = node.GetBool("includeProfiles", false);
        var profileSampleCap = SubpixelEdgeCore.ClampedIntParameter(node, "profileSampleCap", 64, 8, 256);

        if (threshold is < 0 or > 255) throw new InvalidOperationException("Edge threshold must be within 0..255.");
        if (minimum < 0 || maximum < 0 || maximum > 0 && maximum < minimum)
            throw new InvalidOperationException("Width limits must satisfy 0 <= minWidth <= maxWidth; maxWidth=0 disables the upper limit.");
        if (nominal < 0 || direction == "closestwidth" && nominal <= 0)
            throw new InvalidOperationException("Nominal width must be positive when selecting the closest width.");
        if (minValidCalipers > caliperCount)
            throw new InvalidOperationException("minValidCalipers cannot exceed caliperCount.");

        var length = MeasurementMath.Hypot(search.End.X - search.Start.X, search.End.Y - search.Start.Y);
        if (length < 2)
            throw new InvalidOperationException("The search line is too short (minimum 2 px).");

        var roi = ContourNodesHelper.ResolveRoi(context, node);
        using var mask = roi?.BuildMaskForMeasurement(source.Mat.Size(), cancellationToken);
        var gray = PreprocessImageHelpers.ToGray8(source.Mat, out var autoGray, out var owned);
        try
        {
            var tangentX = -(search.End.Y - search.Start.Y) / length;
            var tangentY = (search.End.X - search.Start.X) / length;
            var span = edgeLength > 0 ? edgeLength : length;
            var stations = new List<CaliperStation>(caliperCount);
            var overlays = new List<VisionOverlay>();
            var (baseStart, baseEnd, baseClamped, baseIntersects) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
            var reportedSearch = baseIntersects ? new VisionLine2D(baseStart, baseEnd) : search;
            if (baseIntersects)
                overlays.Add(VisionGeometryHelpers.OverlayLine("caliper-array-search", reportedSearch, "#597ef7", 1.5));

            var outsideImage = 0;
            var shortAfterClip = 0;
            var noPair = 0;
            var rejectedByWidth = 0;

            for (var i = 0; i < caliperCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = caliperCount == 1 ? 0 : (i / (double)(caliperCount - 1) - 0.5) * span;
                var requestedStart = new VisionPoint2D(search.Start.X + tangentX * offset, search.Start.Y + tangentY * offset);
                var requestedEnd = new VisionPoint2D(search.End.X + tangentX * offset, search.End.Y + tangentY * offset);
                if (SubpixelEdgeCore.SegmentBoundsOutsideImage(requestedStart, requestedEnd, gray.Cols, gray.Rows))
                {
                    outsideImage++;
                    stations.Add(CaliperStation.Miss(i, "search-region-outside-image"));
                    continue;
                }

                var (profileStart, profileEnd, profileClamped, profileIntersects) =
                    SubpixelEdgeCore.ClampLine(requestedStart, requestedEnd, gray.Cols, gray.Rows, 10_005_000);
                if (!profileIntersects)
                {
                    outsideImage++;
                    stations.Add(CaliperStation.Miss(i, "search-region-outside-image", profileClamped));
                    continue;
                }
                if (MeasurementMath.Hypot(profileEnd.X - profileStart.X, profileEnd.Y - profileStart.Y) < 2)
                {
                    shortAfterClip++;
                    stations.Add(CaliperStation.Miss(i, "search-line-clipped-too-short", profileClamped));
                    continue;
                }

                overlays.Add(new($"caliper-array-profile-{i}", VisionOverlayType.Line,
                    X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                    Stroke: "#91d5ff", StrokeWidth: 0.8));

                var profile = SubpixelEdgeCore.SampleProfile(gray, mask, profileStart, profileEnd, sampleWidth, out _, out var spacing, cancellationToken);
                var measurement = JLVisionPairMeasurement.Find(profile, spacing, polarity, threshold, cancellationToken);
                var qualified = measurement.Pairs.Where(pair => pair.Width >= minimum - 1e-9
                    && (maximum == 0 || pair.Width <= maximum + 1e-9)).ToArray();
                if (qualified.Length == 0)
                {
                    if (measurement.Pairs.Count > 0) rejectedByWidth++;
                    else noPair++;
                    var missReason = measurement.Pairs.Count > 0 ? "no-pair-within-width-range" : measurement.Reason;
                    stations.Add(CaliperStation.Miss(i, missReason, profileClamped, measurement.SampleCount, measurement.Pairs.Count,
                        includeProfiles ? DownsampleProfile(profile, profileSampleCap) : null));
                    continue;
                }

                var selected = direction switch
                {
                    "firstfromstart" => qualified[0],
                    "firstfromend" => qualified[^1],
                    "closestwidth" => qualified.OrderBy(pair => Math.Abs(pair.Width - nominal)).ThenByDescending(pair => pair.Strength).First(),
                    _ => qualified.OrderByDescending(pair => pair.Strength).ThenBy(pair => pair.First).First()
                };
                var first = SubpixelEdgeCore.Locate(profileStart, profileEnd, selected.First);
                var second = SubpixelEdgeCore.Locate(profileStart, profileEnd, selected.Second);
                var center = new VisionPoint2D((first.X + second.X) / 2, (first.Y + second.Y) / 2);
                stations.Add(new CaliperStation(i, true, "ok", selected.Width, selected.Strength, first, second, center,
                    selected.FirstAmplitude, selected.SecondAmplitude, measurement.SampleCount, qualified.Length,
                    measurement.Pairs.Count - qualified.Length, profileClamped,
                    includeProfiles ? DownsampleProfile(profile, profileSampleCap) : null));

                overlays.Add(VisionGeometryHelpers.OverlayLine($"caliper-array-width-{i}", new(first, second), "#52c41a", 2));
                overlays.Add(new($"caliper-array-center-{i}", VisionOverlayType.Point, X: center.X, Y: center.Y, Stroke: "#f5222d", StrokeWidth: 2.5));
            }

            var valid = stations.Where(s => s.Found).ToArray();
            var widths = valid.Select(s => s.Width!.Value).ToArray();
            double? widthMean = widths.Length > 0 ? widths.Average() : null;
            double? widthMin = widths.Length > 0 ? widths.Min() : null;
            double? widthMax = widths.Length > 0 ? widths.Max() : null;
            double? widthStd = widths.Length > 1
                ? Math.Sqrt(widths.Sum(w => (w - widthMean!.Value) * (w - widthMean.Value)) / widths.Length)
                : widths.Length == 1 ? 0 : null;
            double? widthCv = widthMean is > 1e-9 && widthStd is not null ? widthStd.Value / widthMean.Value : widthMean is 0 ? 0 : null;
            var stdGateFailed = maxWidthStd > 0 && widthStd is not null && widthStd.Value > maxWidthStd;
            var found = valid.Length >= minValidCalipers && !stdGateFailed;
            var reason = found ? "ok"
                : stdGateFailed ? "width-std-exceeds-tolerance"
                : valid.Length > 0 ? "insufficient-valid-calipers"
                : outsideImage == caliperCount ? "search-region-outside-image"
                : outsideImage + shortAfterClip == caliperCount && shortAfterClip > 0 ? "search-region-clipped-too-short"
                : rejectedByWidth == caliperCount - outsideImage - shortAfterClip && rejectedByWidth > 0 ? "no-pair-within-width-range"
                : "no-edge-pair-above-threshold";

            if (found && widthMean is not null)
            {
                var labelCenter = valid.Length > 0
                    ? new VisionPoint2D(valid.Average(s => s.Center!.Value.X), valid.Average(s => s.Center!.Value.Y))
                    : new VisionPoint2D((search.Start.X + search.End.X) / 2, (search.Start.Y + search.End.Y) / 2);
                overlays.Add(new("caliper-array-label", VisionOverlayType.Text,
                    Label: $"N={valid.Length} mean={widthMean:0.000} std={widthStd ?? 0:0.000}",
                    X: labelCenter.X + 7, Y: labelCenter.Y - 10, Stroke: "#52c41a"));
            }

            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(source.CloneOwned()),
                ["found"] = VisionValue.Boolean(found),
                ["searchLine"] = VisionValue.Line(reportedSearch),
                ["caliperCount"] = VisionValue.Integer(caliperCount),
                ["validCount"] = VisionValue.Integer(valid.Length),
                ["calipersJson"] = VisionValue.String(JsonSerializer.Serialize(stations.Select(s => s.ToJsonObject())))
            };
            if (widthMean is not null) outputs["widthMean"] = VisionValue.Double(widthMean.Value);
            if (widthMin is not null) outputs["widthMin"] = VisionValue.Double(widthMin.Value);
            if (widthMax is not null) outputs["widthMax"] = VisionValue.Double(widthMax.Value);
            if (widthStd is not null) outputs["widthStd"] = VisionValue.Double(widthStd.Value);
            if (widthCv is not null) outputs["widthCv"] = VisionValue.Double(widthCv.Value);
            if (valid.Length > 0)
            {
                outputs["center"] = VisionValue.Point(new VisionPoint2D(valid.Average(s => s.Center!.Value.X), valid.Average(s => s.Center!.Value.Y)));
                outputs["strength"] = VisionValue.Double(valid.Average(s => s.Strength!.Value));
            }

            var diag = CoordinateValidation.Diagnostics(Type,
                ("found", found), ("reason", reason), ("caliperCount", caliperCount),
                ("validCount", valid.Length), ("widthMean", widthMean), ("widthStd", widthStd),
                ("maxWidthStd", maxWidthStd), ("polarity", polarity), ("direction", direction));
            outputs["diagnostics"] = VisionValue.String(diag);
            return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
            {
                ["diagnostics"] = diag,
                ["found"] = found,
                ["reason"] = reason,
                ["caliperCount"] = caliperCount,
                ["validCount"] = valid.Length,
                ["failedCount"] = caliperCount - valid.Length,
                ["outsideImage"] = outsideImage,
                ["shortAfterClip"] = shortAfterClip,
                ["noPair"] = noPair,
                ["rejectedByWidth"] = rejectedByWidth,
                ["widthMean"] = widthMean,
                ["widthMin"] = widthMin,
                ["widthMax"] = widthMax,
                ["widthStd"] = widthStd,
                ["widthCv"] = widthCv,
                ["stdGateFailed"] = stdGateFailed,
                ["maxWidthStd"] = maxWidthStd,
                ["sampleWidth"] = sampleWidth,
                ["polarity"] = polarity,
                ["direction"] = direction,
                ["edgeThreshold"] = threshold,
                ["minWidth"] = minimum,
                ["maxWidth"] = maximum,
                ["nominalWidth"] = nominal,
                ["edgeLength"] = span,
                ["minValidCalipers"] = minValidCalipers,
                ["includeProfiles"] = includeProfiles,
                ["lineClampedToImage"] = baseClamped,
                ["autoGray"] = autoGray,
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["unit"] = "px",
                ["limits"] = "No circular station layout; no interactive edge-score curve editor; pair selection matches measure.caliperWidth only."
            }, overlays));
        }
        finally { owned?.Dispose(); }
    }

    private static double[]? DownsampleProfile(double[] profile, int cap)
    {
        if (profile.Length == 0) return [];
        if (profile.Length <= cap) return profile.ToArray();
        var step = (profile.Length - 1) / (double)(cap - 1);
        var samples = new double[cap];
        for (var i = 0; i < cap; i++)
        {
            var index = (int)Math.Round(i * step);
            samples[i] = profile[Math.Clamp(index, 0, profile.Length - 1)];
        }
        return samples;
    }

    private readonly record struct CaliperStation(
        int Index,
        bool Found,
        string Reason,
        double? Width,
        double? Strength,
        VisionPoint2D? Point1,
        VisionPoint2D? Point2,
        VisionPoint2D? Center,
        double? FirstAmplitude,
        double? SecondAmplitude,
        int SampleCount,
        int PairCount,
        int RejectedByWidth,
        bool ProfileClipped,
        double[]? Profile)
    {
        public static CaliperStation Miss(int index, string reason, bool clipped = false, int sampleCount = 0, int candidatePairs = 0, double[]? profile = null)
            => new(index, false, reason, null, null, null, null, null, null, null, sampleCount, 0, candidatePairs, clipped, profile);

        public Dictionary<string, object?> ToJsonObject()
        {
            var map = new Dictionary<string, object?>
            {
                ["index"] = Index,
                ["found"] = Found,
                ["reason"] = Reason,
                ["sampleCount"] = SampleCount,
                ["pairCount"] = PairCount,
                ["rejectedByWidth"] = RejectedByWidth,
                ["profileClipped"] = ProfileClipped
            };
            if (Width is not null) map["width"] = Round(Width.Value);
            if (Strength is not null) map["strength"] = Round(Strength.Value);
            if (Point1 is not null) map["point1"] = Point(Point1.Value);
            if (Point2 is not null) map["point2"] = Point(Point2.Value);
            if (Center is not null) map["center"] = Point(Center.Value);
            if (FirstAmplitude is not null) map["firstAmplitude"] = Round(FirstAmplitude.Value);
            if (SecondAmplitude is not null) map["secondAmplitude"] = Round(SecondAmplitude.Value);
            if (Profile is not null) map["profile"] = Profile.Select(Round).ToArray();
            return map;
        }

        private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
        private static object Point(VisionPoint2D p) => new { x = Round(p.X), y = Round(p.Y) };
    }
}
