using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 5B-1 · feature.edgeSubpixel：沿搜索线做亚像素边缘定位。
/// 输出 found / point / strength / fitError / sampleCount / searchLine；
/// 未找到超过对比度阈值的边缘时 found=false（summary.reason 说明原因），不抛异常。
/// </summary>
public sealed class EdgeSubpixelNode : IVisionNodeExecutor
{
    public string Type => "feature.edgeSubpixel";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => CaliperNode.ExecuteShared(context, node, cancellationToken, "minContrast", 8, "parabolic");
}

/// <summary>
/// 5B-2 · measure.caliperLine：卡尺找线——沿搜索线定位最强（或指定方向）边缘，
/// 输出过边缘点且垂直于搜索方向的直线（搜索线横穿边缘时即工件边缘本身）。
/// </summary>
public sealed class CaliperLineNode : IVisionNodeExecutor
{
    public string Type => "measure.caliperLine";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Caliper line measurement requires a non-empty image.");

        var search = SubpixelEdgeCore.ResolveSearchLine(context, node);
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 5, 1, 99));
        var minContrast = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "minContrast", 8));
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "any", "any", "rising", "falling");
        var direction = SubpixelEdgeCore.ChoiceParameter(node, "direction", "strongest", "strongest", "firstfromstart", "firstfromend");
        var refine = SubpixelEdgeCore.ChoiceParameter(node, "refine", "parabolic", "parabolic", "none");
        var sigma = SubpixelEdgeCore.SigmaParameter(node);
        var edgeLength = Math.Clamp(SubpixelEdgeCore.RequireFiniteParameter(node, "edgeLength", 0), 0, 10000);
        var caliperCount = SubpixelEdgeCore.ClampedIntParameter(node, "caliperCount", 9, 3, 101);
        var minValidCalipers = SubpixelEdgeCore.ClampedIntParameter(node, "minValidCalipers", 3, 3, 101);
        var maxFitOutliers = SubpixelEdgeCore.ClampedIntParameter(node, "maxFitOutliers", 0, 0, 16);
        var fitOutlierThreshold = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "fitOutlierThreshold", 2));
        var maxFitError = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "maxFitError", 0));
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null)
                mask = roi.BuildMaskForMeasurement(sourceImage.Mat.Size(), cancellationToken);
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                var (start, end, baseClamped, baseIntersects) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
                var length = MeasurementMath.Hypot(search.End.X - search.Start.X, search.End.Y - search.Start.Y);
                if (length < 2)
                    throw new InvalidOperationException("The search line is too short (minimum 2 px).");
                var span = edgeLength > 0 ? edgeLength : length;
                var tangentX = -(search.End.Y - search.Start.Y) / length;
                var tangentY = (search.End.X - search.Start.X) / length;
                var points = new List<VisionPoint2D>(caliperCount);
                var strengths = new List<double>(caliperCount);
                var failures = 0;
                var outsideImage = 0;
                var shortAfterClip = 0;
                var profilesWithoutFiniteSamples = 0;
                var profilesWithInsufficientContiguousSamples = 0;
                var profilesClipped = 0;
                var anyProfileClipped = false;
                var overlays = new List<VisionOverlay>();
                if (baseIntersects)
                    overlays.Add(new("caliper-line-search", VisionOverlayType.Line, X: start.X, Y: start.Y, X2: end.X, Y2: end.Y, Stroke: "#597ef7", StrokeWidth: 1.5));
                for (var i = 0; i < caliperCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = caliperCount == 1 ? 0 : (i / (double)(caliperCount - 1) - 0.5) * span;
                    var offsetX = tangentX * offset;
                    var offsetY = tangentY * offset;
                    var requestedStart = new VisionPoint2D(search.Start.X + offsetX, search.Start.Y + offsetY);
                    var requestedEnd = new VisionPoint2D(search.End.X + offsetX, search.End.Y + offsetY);
                    if (SubpixelEdgeCore.SegmentBoundsOutsideImage(requestedStart, requestedEnd, gray.Cols, gray.Rows))
                    { failures++; outsideImage++; continue; }
                    var (profileStart, profileEnd, profileClamped, profileIntersects) =
                        SubpixelEdgeCore.ClampLine(requestedStart, requestedEnd, gray.Cols, gray.Rows, 10_005_000);
                    if (profileClamped) { profilesClipped++; anyProfileClipped = true; }
                    if (!profileIntersects) { failures++; outsideImage++; continue; }
                    if (MeasurementMath.Hypot(profileEnd.X - profileStart.X, profileEnd.Y - profileStart.Y) < 2)
                    { failures++; shortAfterClip++; continue; }
                    overlays.Add(new($"caliper-line-profile-{i}", VisionOverlayType.Line,
                        X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                        Stroke: "#91d5ff", StrokeWidth: 0.8));
                    var profile = SubpixelEdgeCore.SampleProfile(gray, mask, profileStart, profileEnd, sampleWidth, out _, out var lineSpacing, cancellationToken);
                    var edge = SubpixelEdgeCore.FindEdge(profile, lineSpacing, polarity, minContrast, direction, refine, sigma, cancellationToken);
                    if (!edge.Found)
                    {
                        failures++;
                        if (edge.Reason == "profile-has-no-finite-samples") profilesWithoutFiniteSamples++;
                        if (edge.Reason == "profile-has-insufficient-contiguous-samples") profilesWithInsufficientContiguousSamples++;
                        continue;
                    }
                    points.Add(SubpixelEdgeCore.Locate(profileStart, profileEnd, edge.Position));
                    strengths.Add(edge.Strength);
                }
                var fit = FitCaliperLine(points, maxFitOutliers, fitOutlierThreshold);
                var fitErrorExceedsLimit = fit is not null && maxFitError > 0 && fit.Error > maxFitError;
                var found = fit is not null && fit.UsedIndices.Length >= minValidCalipers && !fitErrorExceedsLimit;
                var reportedSearchLine = baseIntersects ? new VisionLine2D(start, end) : search;

                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(found),
                    ["searchLine"] = VisionValue.Line(reportedSearchLine),
                    ["sampleCount"] = VisionValue.Integer(fit?.UsedIndices.Length ?? points.Count),
                    ["ignoredCount"] = VisionValue.Integer(fit?.IgnoredIndices.Length ?? 0)
                };

                if (fit is not null)
                {
                    var usedPoints = fit.UsedIndices.Select(index => points[index]).ToArray();
                    var usedStrengths = fit.UsedIndices.Select(index => strengths[index]).ToArray();
                    // Keep the computed fit-quality metric available for downstream diagnostics
                    // even when the acceptance gate rejects the geometry.
                    outputs["fitError"] = VisionValue.Double(fit.Error);
                    if (found)
                    {
                        outputs["point"] = VisionValue.Point(fit.Point);
                        outputs["line"] = VisionValue.Line(fit.Line);
                        outputs["strength"] = VisionValue.Double(usedStrengths.Average());
                    }
                    overlays.AddRange(usedPoints.Select((point, index) => new VisionOverlay(
                        $"caliper-line-fit-point-{index}", VisionOverlayType.Point, X: point.X, Y: point.Y, Stroke: "#52c41a", StrokeWidth: 2.5)));
                    overlays.AddRange(fit.IgnoredIndices.Select((index, ignoredIndex) => new VisionOverlay(
                        $"caliper-line-ignored-point-{ignoredIndex}", VisionOverlayType.Point, X: points[index].X, Y: points[index].Y, Stroke: "#fa541c", StrokeWidth: 3)));
                    overlays.Add(new VisionOverlay("caliper-line-edge", VisionOverlayType.Line,
                        Label: $"N={usedPoints.Length} RMS={fit.Error:0.###}",
                        X: fit.Line.Start.X, Y: fit.Line.Start.Y, X2: fit.Line.End.X, Y2: fit.Line.End.Y,
                        Stroke: "#f5222d", StrokeWidth: 2.5));
                    overlays.Add(new VisionOverlay("caliper-line-point", VisionOverlayType.Point, X: fit.Point.X, Y: fit.Point.Y, Stroke: "#f5222d", StrokeWidth: 3));
                }
                else
                {
                    outputs["ignoredCount"] = VisionValue.Integer(0);
                    overlays.AddRange(points.Select((point, index) => new VisionOverlay(
                        $"caliper-line-candidate-point-{index}", VisionOverlayType.Point,
                        X: point.X, Y: point.Y, Stroke: "#faad14", StrokeWidth: 2.5)));
                }

                var reason = found ? "ok"
                    : points.Count > 0 && points.Count < 3 ? "too-few-edge-points"
                    : points.Count >= 3 && (points.Count < minValidCalipers
                        || fit is not null && fit.UsedIndices.Length < minValidCalipers) ? "insufficient-valid-calipers"
                    : fitErrorExceedsLimit ? "fit-error-exceeds-tolerance"
                    : points.Count > 0 ? "degenerate-line-fit"
                    : outsideImage == caliperCount ? "search-region-outside-image"
                    : outsideImage + shortAfterClip == caliperCount && shortAfterClip > 0 ? "search-region-clipped-too-short"
                    : profilesWithoutFiniteSamples == caliperCount - outsideImage - shortAfterClip && profilesWithoutFiniteSamples > 0
                        ? "profile-has-no-finite-samples"
                    : profilesWithoutFiniteSamples + profilesWithInsufficientContiguousSamples == caliperCount - outsideImage - shortAfterClip
                        && profilesWithInsufficientContiguousSamples > 0 ? "profile-has-insufficient-contiguous-samples"
                    : "no-edge-above-threshold";

                return ValueTask.FromResult(new NodeExecutorResult(
                    outputs,
                    new Dictionary<string, object?>
                    {
                        ["found"] = found,
                        ["reason"] = reason,
                        ["edgeX"] = fit is not null ? Math.Round(fit.Point.X, 3) : null,
                        ["edgeY"] = fit is not null ? Math.Round(fit.Point.Y, 3) : null,
                        ["strength"] = fit is not null ? Math.Round(fit.UsedIndices.Select(index => strengths[index]).Average(), 3) : null,
                        ["fitError"] = fit is not null ? Math.Round(fit.Error, 4) : null,
                        ["sampleCount"] = fit?.UsedIndices.Length ?? points.Count,
                        ["fitPoints"] = fit?.UsedIndices.Length ?? 0,
                        ["ignoredByFit"] = fit?.IgnoredIndices.Length ?? 0,
                        ["caliperCount"] = caliperCount,
                        ["minContrast"] = minContrast,
                        ["edgeLength"] = edgeLength,
                        ["effectiveCaliperSpan"] = span,
                        ["rejectedCalipers"] = failures,
                        ["maxFitOutliers"] = maxFitOutliers,
                        ["fitOutlierThreshold"] = fitOutlierThreshold,
                        ["maxFitError"] = maxFitError,
                        ["minValidCalipers"] = minValidCalipers,
                        ["sampleWidth"] = sampleWidth,
                        ["polarity"] = polarity,
                        ["direction"] = direction,
                        ["refine"] = refine,
                        ["sigma"] = sigma,
                        ["lineClampedToImage"] = baseClamped || anyProfileClipped,
                        ["profilesIntersectingImage"] = caliperCount - outsideImage,
                        ["profilesOutsideImage"] = outsideImage,
                        ["profilesClippedToImage"] = profilesClipped,
                        ["profilesTooShortAfterClip"] = shortAfterClip,
                        ["profilesWithoutFiniteSamples"] = profilesWithoutFiniteSamples,
                        ["profilesWithInsufficientContiguousSamples"] = profilesWithInsufficientContiguousSamples,
                        ["autoGray"] = autoGray,
                        ["roi"] = roi?.Type.ToString() ?? "FullImage"
                    },
                    overlays));
            }
            finally
            {
                owned?.Dispose();
            }
        }
        finally
        {
            mask?.Dispose();
        }
    }

    private sealed record CaliperLineFitResult(VisionPoint2D Point, VisionLine2D Line, double Error, int[] UsedIndices, int[] IgnoredIndices);

    private static CaliperLineFitResult? FitCaliperLine(IReadOnlyList<VisionPoint2D> points, int maxOutliers, double threshold)
    {
        var used = Enumerable.Range(0, points.Count).ToList();
        var ignored = new List<int>();
        var fit = FitCaliperLineCore(used.Select(index => points[index]).ToArray());
        if (fit is null) return null;
        var residuals = LineResiduals(used, points, fit.Value.Line);
        for (var i = 0; i < maxOutliers && used.Count > 3; i++)
        {
            var worst = Array.IndexOf(residuals, residuals.Max());
            if (residuals[worst] <= threshold) break;
            var removedIndex = used[worst];
            var candidateUsed = used.Where((_, index) => index != worst).ToList();
            var candidateFit = FitCaliperLineCore(candidateUsed.Select(index => points[index]).ToArray());
            if (candidateFit is null) break;
            used = candidateUsed;
            ignored.Add(removedIndex);
            fit = candidateFit;
            residuals = LineResiduals(used, points, fit.Value.Line);
        }
        return new(fit.Value.Point, fit.Value.Line, fit.Value.Error, used.ToArray(), ignored.ToArray());
    }

    private static (VisionPoint2D Point, VisionLine2D Line, double Error)? FitCaliperLineCore(IReadOnlyList<VisionPoint2D> points)
    {
        if (points.Count < 3) return null;
        var cx = points.Average(p => p.X);
        var cy = points.Average(p => p.Y);
        var xx = points.Sum(p => (p.X - cx) * (p.X - cx));
        var yy = points.Sum(p => (p.Y - cy) * (p.Y - cy));
        if (xx + yy < 1e-8) return null;
        var fitted = JLVisionPointFitting.Line(points.ToArray());
        var ux = (fitted.End.X - fitted.Start.X) / fitted.Length;
        var uy = (fitted.End.Y - fitted.Start.Y) / fitted.Length;
        var nx = -uy;
        var ny = ux;
        var residuals = points.Select(p => (p.X - cx) * nx + (p.Y - cy) * ny).ToArray();
        var error = Math.Sqrt(residuals.Sum(r => r * r) / residuals.Length);
        var half = (points.Max(p => (p.X - cx) * ux + (p.Y - cy) * uy) - points.Min(p => (p.X - cx) * ux + (p.Y - cy) * uy)) / 2;
        if (!double.IsFinite(error) || half < 1e-4) return null;
        return (new VisionPoint2D(cx, cy), new VisionLine2D(new(cx - ux * half, cy - uy * half), new(cx + ux * half, cy + uy * half)), error);
    }

    private static double[] LineResiduals(IReadOnlyList<int> indices, IReadOnlyList<VisionPoint2D> points, VisionLine2D line)
    {
        var length = line.Length;
        if (length < 1e-9) return Enumerable.Repeat(double.PositiveInfinity, indices.Count).ToArray();
        var nx = -(line.End.Y - line.Start.Y) / length;
        var ny = (line.End.X - line.Start.X) / length;
        return indices.Select(index => Math.Abs((points[index].X - line.Start.X) * nx + (points[index].Y - line.Start.Y) * ny)).ToArray();
    }
}

/// <summary>
/// 5B-3 · measure.caliperCircle：卡尺找圆——沿一圈径向采样条带定位边缘，
/// 对有效边缘点做最小二乘圆拟合，输出圆/半径/圆度（半径极差）与拟合误差。
/// 有效边缘点少于 3 个时 found=false（退化拟合不抛异常，由 reason 说明）。
/// </summary>
public sealed class CaliperCircleNode : IVisionNodeExecutor
{
    public string Type => "measure.caliperCircle";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Caliper circle measurement requires a non-empty image.");

        // circle 输入端口（可来自 roi.transform 的动态圆）优先于参数。
        var center = new VisionPoint2D(node.GetDouble("centerX", 320), node.GetDouble("centerY", 240));
        var nominalRadius = node.GetDouble("radius", 100);
        if (context.TryGetValue("circle", out var circleValue))
        {
            var circle = circleValue.Require<VisionCircle>("circle");
            if (!double.IsFinite(circle.X) || !double.IsFinite(circle.Y) || !double.IsFinite(circle.Radius) || circle.Radius <= 0)
                throw new InvalidOperationException("The connected circle input must have a finite center and positive radius.");
            center = new VisionPoint2D(circle.X, circle.Y);
            nominalRadius = circle.Radius;
        }
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(nominalRadius) || nominalRadius <= 0)
            throw new InvalidOperationException("Caliper circle requires a finite center and a positive radius.");
        if (Math.Abs(center.X) > 10_000_000 || Math.Abs(center.Y) > 10_000_000 || nominalRadius > 10_000_000)
            throw new InvalidOperationException("Caliper circle coordinates and radius must be within 10000000 pixels.");

        var angleStart = SubpixelEdgeCore.RequireFiniteParameter(node, "angleStartDeg", -180);
        var angleEnd = SubpixelEdgeCore.RequireFiniteParameter(node, "angleEndDeg", 180);
        if (Math.Abs(angleEnd - angleStart) < 1e-9)
            throw new InvalidOperationException("Caliper circle start and end angles are equal, which creates a single radial caliper and cannot fit a circle. Increase the search arc angle range.");
        var fixture = MeasurementFixture.Resolve(context);
        if (fixture is not null)
        {
            center = fixture.Apply(center);
            // Arc angles use image axes (positive clockwise). Preserve the unwrapped span,
            // including a full 360-degree circle, when rotating both ends.
            var rotation = Math.Atan2(fixture.H10, fixture.H00) * 180 / Math.PI;
            angleStart += rotation;
            angleEnd += rotation;
        }
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)
            || Math.Abs(center.X) > 10_000_000 || Math.Abs(center.Y) > 10_000_000)
            throw new InvalidOperationException("Caliper circle center after coordinate correction must be finite and within +/-10000000 pixels.");
        var requestedMode = node.GetString("samplingMode", "").Trim().ToLowerInvariant();
        // Old workflows only have angleStepDeg. Keep their fixed-step spacing until the user
        // explicitly switches to count mode; new nodes use a direct caliper count.
        var samplingMode = requestedMode switch
        {
            "count" or "step" => requestedMode,
            "" => node.Parameters?.ContainsKey("angleStepDeg") == true ? "step" : "count",
            _ => throw new InvalidOperationException("Parameter 'samplingMode' must be one of [count, step].")
        };
        var angleStep = Math.Clamp(SubpixelEdgeCore.RequireFiniteParameter(node, "angleStepDeg", 5), 0.5, 45);
        var caliperCount = SubpixelEdgeCore.ClampedIntParameter(node, "caliperCount", 72, 3, 361);
        var minValidCalipers = SubpixelEdgeCore.ClampedIntParameter(node, "minValidCalipers", 3, 3, 361);
        var band = Math.Clamp(SubpixelEdgeCore.RequireFiniteParameter(node, "radialBand", 8), 1, 500);
        var minContrast = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "minContrast", 8));
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "any", "any", "rising", "falling");
        var searchDirection = SubpixelEdgeCore.ChoiceParameter(node, "searchDirection", "strongest", "strongest", "firstfromstart", "firstfromend");
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 3, 1, 99));
        var sigma = SubpixelEdgeCore.SigmaParameter(node);
        var maxDeviation = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "maxRadiusDeviation", 0));
        var maxFitOutliers = SubpixelEdgeCore.ClampedIntParameter(node, "maxFitOutliers", 0, 0, 16);
        var fitOutlierThreshold = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "fitOutlierThreshold", 2));
        var fitRadiusTolerance = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "fitRadiusTolerance", 0));
        var maxFitError = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "maxFitError", 0));
        var innerRadius = Math.Max(0, nominalRadius - band);
        var outerRadius = nominalRadius + band;
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null)
                mask = roi.BuildMaskForMeasurement(sourceImage.Mat.Size(), cancellationToken);
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                var angles = samplingMode == "count"
                    ? BuildAnglesByCount(angleStart, angleEnd, caliperCount, out var effectiveStep)
                    : BuildAnglesByStep(angleStart, angleEnd, angleStep, out effectiveStep);
                var edgePoints = new List<VisionPoint2D>();
                var rejected = 0;
                var rejectedByDeviation = 0;
                var spokesOutsideImage = 0;
                var spokesTooShortAfterClip = 0;
                var spokesWithoutFiniteSamples = 0;
                var spokesWithInsufficientContiguousSamples = 0;
                var spokesWithEdgeCandidate = 0;
                var spokesClipped = 0;
                var spokeOverlays = new List<VisionOverlay>(angles.Length);
                var diagnosticSpokes = CaliperCircleDiagnosticCapture.Enabled ? new List<CaliperSpokeDiagnostic>(angles.Length) : null;
                var edgeSpokeIndices = diagnosticSpokes is not null ? new List<int>() : null;
                for (var spokeIndex = 0; spokeIndex < angles.Length; spokeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var angleDeg = angles[spokeIndex];
                    var radians = angleDeg * Math.PI / 180.0;
                    var dirX = Math.Cos(radians);
                    var dirY = Math.Sin(radians);
                    // A radial search cannot continue through a negative radius and emerge on the far side.
                    // Clip the inner limit at the center so first-from-start always searches outward.
                    var start = new VisionPoint2D(center.X + dirX * innerRadius, center.Y + dirY * innerRadius);
                    var end = new VisionPoint2D(center.X + dirX * outerRadius, center.Y + dirY * outerRadius);
                    var diagnostic = diagnosticSpokes is not null ? new CaliperSpokeDiagnostic(spokeIndex, angleDeg, start, end) : null;
                    if (diagnostic is not null) diagnosticSpokes!.Add(diagnostic);
                    if (SubpixelEdgeCore.SegmentBoundsOutsideImage(start, end, gray.Cols, gray.Rows))
                    { rejected++; spokesOutsideImage++; continue; }
                    var (profileStart, profileEnd, spokeClipped, spokeIntersects) =
                        SubpixelEdgeCore.ClampLine(start, end, gray.Cols, gray.Rows, 20_000_500);
                    if (spokeClipped) spokesClipped++;
                    if (!spokeIntersects) { rejected++; spokesOutsideImage++; continue; }
                    var clippedStartRadius = (profileStart.X - center.X) * dirX + (profileStart.Y - center.Y) * dirY;
                    if (diagnostic is not null)
                    {
                        diagnostic.ProfileStart = profileStart;
                        diagnostic.ProfileEnd = profileEnd;
                        diagnostic.Clipped = spokeClipped;
                        diagnostic.StartRadius = clippedStartRadius;
                        diagnostic.Status = "clipped-too-short";
                    }
                    if (MeasurementMath.Hypot(profileEnd.X - profileStart.X, profileEnd.Y - profileStart.Y) < 2)
                    { rejected++; spokesTooShortAfterClip++; continue; }
                    var profile = SubpixelEdgeCore.SampleProfile(gray, mask, profileStart, profileEnd, sampleWidth, out _, out var spokeSpacing, cancellationToken);
                    var edge = SubpixelEdgeCore.FindEdge(profile, spokeSpacing, polarity, minContrast, searchDirection, "parabolic", sigma, cancellationToken,
                        diagnostic?.Candidates);
                    if (diagnostic is not null)
                    {
                        diagnostic.Profile = profile;
                        diagnostic.Spacing = spokeSpacing;
                        diagnostic.Status = edge.Reason;
                    }
                    if (!edge.Found)
                    {
                        spokeOverlays.Add(new($"caliper-circle-search-{spokeIndex}", VisionOverlayType.Line,
                            X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                            Stroke: "#bfbfbf", StrokeWidth: 0.7));
                        rejected++;
                        if (edge.Reason == "profile-has-no-finite-samples") spokesWithoutFiniteSamples++;
                        if (edge.Reason == "profile-has-insufficient-contiguous-samples") spokesWithInsufficientContiguousSamples++;
                        continue;
                    }
                    spokesWithEdgeCandidate++;
                    var edgeRadius = clippedStartRadius + edge.Position;
                    if (diagnostic is not null)
                    {
                        diagnostic.EdgeRadius = edgeRadius;
                        diagnostic.EdgeStrength = edge.Strength;
                        diagnostic.EdgePoint = new VisionPoint2D(center.X + dirX * edgeRadius, center.Y + dirY * edgeRadius);
                        diagnostic.Status = "radius-deviation";
                    }
                    if (maxDeviation > 0 && Math.Abs(edgeRadius - nominalRadius) > maxDeviation)
                    {
                        var candidate = new VisionPoint2D(center.X + dirX * edgeRadius, center.Y + dirY * edgeRadius);
                        spokeOverlays.Add(new($"caliper-circle-search-{spokeIndex}", VisionOverlayType.Line,
                            X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                            Stroke: "#faad14", StrokeWidth: 1));
                        spokeOverlays.Add(new($"caliper-circle-radius-rejected-{spokeIndex}", VisionOverlayType.Point,
                            Label: $"ΔR={edgeRadius - nominalRadius:+0.##;-0.##;0}", X: candidate.X, Y: candidate.Y,
                            Stroke: "#faad14", StrokeWidth: 3));
                        rejected++;
                        rejectedByDeviation++;
                        continue;
                    }
                    spokeOverlays.Add(new($"caliper-circle-search-{spokeIndex}", VisionOverlayType.Line,
                        X: profileStart.X, Y: profileStart.Y, X2: profileEnd.X, Y2: profileEnd.Y,
                        Stroke: "#91d5ff", StrokeWidth: 0.7));
                    edgePoints.Add(new VisionPoint2D(center.X + dirX * edgeRadius, center.Y + dirY * edgeRadius));
                    edgeSpokeIndices?.Add(spokeIndex);
                }

                var hasEnoughPoints = edgePoints.Count >= 3;
                var fit = hasEnoughPoints ? FitCircleWithOutlierRejection(edgePoints, maxFitOutliers, fitOutlierThreshold, cancellationToken) : null;
                var radiusOutsideTolerance = fit is not null && fitRadiusTolerance > 0
                    && Math.Abs(fit.Circle.Radius - nominalRadius) > fitRadiusTolerance;
                var fitErrorExceedsLimit = fit is not null && maxFitError > 0 && fit.Rmse > maxFitError;
                var hasMinimumCalipers = fit is not null
                    ? fit.UsedIndices.Length >= minValidCalipers
                    : edgePoints.Count >= minValidCalipers;
                var found = fit is not null && hasMinimumCalipers && !radiusOutsideTolerance && !fitErrorExceedsLimit;
                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(found),
                    ["sampleCount"] = VisionValue.Integer(fit?.UsedIndices.Length ?? edgePoints.Count),
                    ["ignoredCount"] = VisionValue.Integer(fit?.IgnoredIndices.Length ?? 0)
                };
                var overlays = new List<VisionOverlay>
                {
                    new("caliper-circle-nominal", VisionOverlayType.Circle, X: center.X, Y: center.Y, Radius: nominalRadius, Stroke: "#597ef7", StrokeWidth: 1)
                };
                overlays.AddRange(spokeOverlays);
                var reason = found ? "ok"
                    : edgePoints.Count >= 3 && !hasMinimumCalipers ? "insufficient-valid-calipers"
                    : radiusOutsideTolerance ? "fitted-radius-outside-tolerance"
                    : fitErrorExceedsLimit ? "fit-error-exceeds-tolerance"
                    : edgePoints.Count == 0 && rejectedByDeviation > 0 && rejectedByDeviation == spokesWithEdgeCandidate
                        ? "all-spokes-exceed-max-radius-deviation"
                    : edgePoints.Count == 0 && spokesOutsideImage == angles.Length ? "search-region-outside-image"
                    : edgePoints.Count == 0 && spokesOutsideImage + spokesTooShortAfterClip == angles.Length && spokesTooShortAfterClip > 0
                        ? "search-region-clipped-too-short"
                    : edgePoints.Count == 0 && spokesWithoutFiniteSamples == angles.Length - spokesOutsideImage - spokesTooShortAfterClip
                        && spokesWithoutFiniteSamples > 0 ? "profile-has-no-finite-samples"
                    : edgePoints.Count == 0 && spokesWithoutFiniteSamples + spokesWithInsufficientContiguousSamples == angles.Length - spokesOutsideImage - spokesTooShortAfterClip
                        && spokesWithInsufficientContiguousSamples > 0 ? "profile-has-insufficient-contiguous-samples"
                    : edgePoints.Count == 0 ? "no-edge-above-threshold"
                    : edgePoints.Count < 3 ? "too-few-edge-points"
                    : "degenerate-circle-fit";
                var summary = new Dictionary<string, object?>
                {
                    ["found"] = found,
                    ["reason"] = reason,
                    ["spokes"] = angles.Length,
                    ["sampleCount"] = fit?.UsedIndices.Length ?? edgePoints.Count,
                    ["edgePoints"] = edgePoints.Count,
                    ["rejectedSpokes"] = rejected,
                    ["rejectedByDeviation"] = rejectedByDeviation,
                    ["spokesWithEdgeCandidate"] = spokesWithEdgeCandidate,
                    ["spokesOutsideImage"] = spokesOutsideImage,
                    ["spokesTooShortAfterClip"] = spokesTooShortAfterClip,
                    ["spokesWithoutFiniteSamples"] = spokesWithoutFiniteSamples,
                    ["spokesWithInsufficientContiguousSamples"] = spokesWithInsufficientContiguousSamples,
                    ["spokesClippedToImage"] = spokesClipped,
                    ["centerX"] = Math.Round(center.X, 4),
                    ["centerY"] = Math.Round(center.Y, 4),
                    ["nominalRadius"] = Math.Round(nominalRadius, 3),
                    ["angleStartDeg"] = Math.Round(angleStart, 4),
                    ["angleEndDeg"] = Math.Round(angleEnd, 4),
                    ["samplingMode"] = samplingMode,
                    ["caliperCount"] = angles.Length,
                    ["angleStepDeg"] = effectiveStep,
                    ["radialBand"] = band,
                    ["radialSearchMinRadius"] = innerRadius,
                    ["radialSearchMaxRadius"] = outerRadius,
                    ["minContrast"] = minContrast,
                    ["sampleWidth"] = sampleWidth,
                    ["maxRadiusDeviation"] = maxDeviation,
                    ["fitRadiusTolerance"] = fitRadiusTolerance,
                    ["maxFitError"] = maxFitError,
                    ["minValidCalipers"] = minValidCalipers,
                    ["maxFitOutliers"] = maxFitOutliers,
                    ["fitOutlierThreshold"] = fitOutlierThreshold,
                    ["polarity"] = polarity,
                    ["searchDirection"] = searchDirection,
                    ["refine"] = "parabolic",
                    ["sigma"] = sigma,
                    ["autoGray"] = autoGray,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage"
                };

                if (fit is not null)
                {
                    var usedIndices = fit.UsedIndices;
                    var usedPoints = usedIndices.Select(i => edgePoints[i]).ToArray();
                    var circle = fit.Circle;
                    // Roundness/radius extrema must be measured around the fitted center;
                    // using the nominal center would turn a small center offset into false runout.
                    var usedRadii = usedPoints.Select(point => MeasurementMath.Hypot(point.X - circle.X, point.Y - circle.Y)).ToArray();
                    var radiusSpread = usedRadii.Max() - usedRadii.Min();
                    var rmse = fit.Rmse;
                    outputs["sampleCount"] = VisionValue.Integer(usedPoints.Length);
                    outputs["ignoredCount"] = VisionValue.Integer(fit.IgnoredIndices.Length);
                    summary["fittedRadius"] = Math.Round(circle.Radius, 3);
                    summary["radiusDelta"] = Math.Round(circle.Radius - nominalRadius, 3);
                    summary["radiusSpread"] = Math.Round(radiusSpread, 3);
                    summary["fitError"] = Math.Round(rmse, 4);
                    summary["fitPoints"] = usedPoints.Length;
                    summary["ignoredByFit"] = fit.IgnoredIndices.Length;
                    // These quality metrics describe the available fit and remain useful when
                    // a radius or fit-error gate rejects its geometry.
                    outputs["radiusSpread"] = VisionValue.Double(radiusSpread);
                    outputs["fitError"] = VisionValue.Double(rmse);
                    if (found)
                    {
                        outputs["circle"] = VisionValue.Circle(circle);
                        outputs["center"] = VisionValue.Point(new VisionPoint2D(circle.X, circle.Y));
                        outputs["radius"] = VisionValue.Double(circle.Radius);
                        outputs["radiusMin"] = VisionValue.Double(usedRadii.Min());
                        outputs["radiusMax"] = VisionValue.Double(usedRadii.Max());
                    }
                    overlays.Add(new VisionOverlay("caliper-circle-fit", VisionOverlayType.Circle,
                        Label: $"R={circle.Radius:0.###}",
                        X: circle.X, Y: circle.Y, Radius: circle.Radius, Stroke: found ? "#52c41a" : "#fa541c", StrokeWidth: 2));
                    overlays.AddRange(usedPoints.Select((point, i) => new VisionOverlay(
                        $"caliper-circle-point-{i}", VisionOverlayType.Point, X: point.X, Y: point.Y, Stroke: "#52c41a", StrokeWidth: 2.5)));
                    overlays.AddRange(fit.IgnoredIndices.Select((index, i) => new VisionOverlay(
                        $"caliper-circle-ignored-{i}", VisionOverlayType.Point, X: edgePoints[index].X, Y: edgePoints[index].Y, Stroke: "#fa541c", StrokeWidth: 3)));
                }
                else
                {
                    outputs["ignoredCount"] = VisionValue.Integer(0);
                    overlays.AddRange(edgePoints.Select((point, index) => new VisionOverlay(
                        $"caliper-circle-candidate-point-{index}", VisionOverlayType.Point,
                        X: point.X, Y: point.Y, Stroke: "#faad14", StrokeWidth: 2.5)));
                }

                if (diagnosticSpokes is not null)
                {
                    var ignored = fit?.IgnoredIndices.ToHashSet();
                    for (var index = 0; index < edgeSpokeIndices!.Count; index++)
                        diagnosticSpokes[edgeSpokeIndices[index]].Status = fit is null ? "fit-unavailable"
                            : ignored!.Contains(index) ? "fit-outlier" : "used";
                    CaliperCircleDiagnosticCapture.Publish(new(gray.Cols, gray.Rows, sourceImage.CoordinateFrame, center,
                        nominalRadius, band, minContrast, sampleWidth, polarity, searchDirection, found, reason, diagnosticSpokes));
                }
                return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
            }
            finally
            {
                owned?.Dispose();
            }
        }
        finally
        {
            mask?.Dispose();
        }
    }

    private sealed record CircleFitResult(VisionCircle Circle, int[] UsedIndices, int[] IgnoredIndices, double Rmse);

    private static CircleFitResult? FitCircleWithOutlierRejection(IReadOnlyList<VisionPoint2D> points, int maxOutliers, double threshold, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var used = Enumerable.Range(0, points.Count).ToList();
        var ignored = new List<int>();
        var fittedCircle = TryFitCircle(used.Select(i => points[i]).ToArray(), cancellationToken);
        if (fittedCircle is null) return null;
        var circle = fittedCircle.Value;
        var residuals = Residuals(used, points, circle);
        for (var i = 0; i < maxOutliers && used.Count > 3; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worst = Array.IndexOf(residuals, residuals.Max());
            if (residuals[worst] <= threshold) break;
            var removedIndex = used[worst];
            var candidateUsed = used.Where((_, index) => index != worst).ToList();
            var candidateFit = TryFitCircle(candidateUsed.Select(index => points[index]).ToArray(), cancellationToken);
            if (candidateFit is null) break;
            used = candidateUsed;
            ignored.Add(removedIndex);
            circle = candidateFit.Value;
            residuals = Residuals(used, points, circle);
        }
        return new(circle, used.ToArray(), ignored.ToArray(), Math.Sqrt(residuals.Sum(x => x * x) / residuals.Length));
    }

    private static VisionCircle? TryFitCircle(IReadOnlyList<VisionPoint2D> points, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (points.Count < 3) return null;
        var centerX = points.Average(point => point.X);
        var centerY = points.Average(point => point.Y);
        var xx = points.Sum(point => (point.X - centerX) * (point.X - centerX));
        var yy = points.Sum(point => (point.Y - centerY) * (point.Y - centerY));
        var xy = points.Sum(point => (point.X - centerX) * (point.Y - centerY));
        var spread = xx + yy;
        var determinant = xx * yy - xy * xy;
        // A very short arc can look almost like a straight line and yield a finite but wildly
        // incorrect circle. Reject poorly conditioned point clouds instead of reporting that
        // unstable radius as a successful measurement.
        const double minimumNormalizedSpread = 2e-3;
        if (!double.IsFinite(spread) || !double.IsFinite(determinant) || spread < 1e-8
            || determinant <= spread * spread * minimumNormalizedSpread)
            return null;
        try { return MultiPointFit.Circle(points.ToArray(), cancellationToken); }
        catch (InvalidOperationException) { return null; }
    }

    private static double[] Residuals(IReadOnlyList<int> indices, IReadOnlyList<VisionPoint2D> points, VisionCircle circle) =>
        indices.Select(i => Math.Abs(MeasurementMath.Hypot(points[i].X - circle.X, points[i].Y - circle.Y) - circle.Radius)).ToArray();

    private static (double Span, bool FullCircle) ArcSpan(double start, double end)
    {
        if (start == end) return (0, false);
        var raw = end - start;
        if (double.IsFinite(raw) && Math.Abs(raw) < 1e-9) return (0, false);
        // Reduce each endpoint before subtracting if the difference overflows. The
        // modulo of the endpoints preserves the clockwise arc while keeping it finite.
        var span = double.IsFinite(raw)
            ? raw % 360
            : ((end % 360) - (start % 360)) % 360;
        if (Math.Abs(span) < 1e-9) return (360, true);
        if (span < 0) span += 360;
        return (span, false);
    }

    private static double NormalizeAngle(double angle, double samplingStep)
    {
        // Preserve the existing trigonometric path for ordinary angles. Normalize only
        // when floating-point spacing would noticeably distort adjacent caliper directions.
        var ulp = Math.Abs(Math.BitIncrement(angle) - angle);
        if (double.IsFinite(ulp) && ulp <= Math.Abs(samplingStep) * 0.01) return angle;
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static double[] BuildAnglesByCount(double start, double end, int count, out double effectiveStep)
    {
        var (span, fullCircle) = ArcSpan(start, end);
        if (span == 0)
        {
            effectiveStep = 0;
            return [start];
        }
        count = Math.Clamp(count, 3, 361);
        effectiveStep = fullCircle ? span / count : span / (count - 1);
        var angles = new double[count];
        var normalizedStart = NormalizeAngle(start, effectiveStep);
        for (var i = 0; i < count; i++) angles[i] = normalizedStart + i * effectiveStep;
        return angles;
    }

    private static double[] BuildAnglesByStep(double start, double end, double step, out double effectiveStep)
    {
        var (span, fullCircle) = ArcSpan(start, end);
        effectiveStep = step;
        if (span == 0) return [start];

        var intervalCount = span / effectiveStep;
        var nearestIntervalCount = (int)Math.Round(intervalCount);
        var reachesEnd = nearestIntervalCount > 0
            && Math.Abs(nearestIntervalCount * effectiveStep - span) <= 1e-9;
        var count = fullCircle && reachesEnd
            ? nearestIntervalCount
            : (int)Math.Floor(intervalCount) + 1;
        var maximumCount = fullCircle ? 360 : 361;
        if (count > maximumCount)
        {
            effectiveStep = span / (fullCircle ? maximumCount : maximumCount - 1);
            count = maximumCount;
        }
        var angles = new double[count];
        var normalizedStart = NormalizeAngle(start, effectiveStep);
        for (var i = 0; i < count; i++) angles[i] = normalizedStart + i * effectiveStep;
        return angles;
    }
}

