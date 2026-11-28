namespace VisionStudio.Engine;

/// <summary>
/// 测量与几何专项补全（node-gap-analysis.md 第八节）。
///
/// 这批节点的共同前提：都能在**单帧 2D 影像**内给出可追溯数值，不依赖 3D 深度源，
/// 也不引入新的端口类型。刻意保持"纯几何 / 纯算量"——全部声明 <c>Actions: []</c>，
/// 因而可以进 pipeline 执行（见 VisionExecutionPolicy）。
///
/// 与既有节点的边界：
/// - <c>measure.arcFit</c> 与 <c>measure.fitCircle</c> 的区别是"弧段"语义：给弧长、圆心角、
///   径向偏差，而不是整圆最小二乘；
/// - <c>measure.mzCircle</c> 是通止规口径的极限圆（MZ/MCCI/MIC），不吃最小二乘残差；
/// - <c>measure.roughness</c> 的输入必须是**已采样轮廓**或点集，不做去毛刺、不做分层。
/// </summary>
public static class MeasurementGapCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static readonly ParameterDescriptor Index = new("index", "Zero-based Index", "number", 0, 0, 1999, 1,
        Description: "Which contour in the connected Contour list to measure (0-based). Out of range returns found=false.");
    private static readonly ParameterDescriptor Closed = new("closed", "Closed Contour", "boolean", true,
        Description: "Treat the polyline as closed when sampling (repeats the first point implicitly).");
    // A profile filter expects a nominally straight, open edge (see the node description); unlike the
    // contour-wide measurement nodes above, its executor fallback has always been false.
    private static readonly ParameterDescriptor OpenProfile = new("closed", "Closed Contour", "boolean", false,
        Description: "Treat the polyline as closed when sampling (repeats the first point implicitly).");
    private static readonly ParameterDescriptor MaxInputGap = new("maxInputGap", "Maximum Input Gap (0=auto 3 steps)", "number", 0, 0, 1000000, .1,
        Unit: "px", Group: "Filter", Description: "Reject a profile when adjacent projected input points are farther apart than this. 0 now means 'auto': three sample steps, because bridging a larger hole fabricates waviness. Set an explicit larger value to allow crossing a known gap.");
    private static readonly ParameterDescriptor MinUsableLength = new("minUsableLength", "Min Usable Length (0=off)", "number", 0, 0, 1000000, .1,
        Unit: "px", Group: "Filter", Description: "Reject the result when the retained (fully supported) span is shorter than this.");
    private static readonly ParameterDescriptor MinRetainedPoints = new("minRetainedPoints", "Min Retained Points (0=off)", "number", 0, 0, 10000000, 1,
        Group: "Filter", Description: "Reject the result when fewer samples survive the end handling.");
    private static readonly ParameterDescriptor AllowTruncatedKernel = new("allowTruncatedKernel", "Allow Truncated Kernel", "boolean", false,
        Group: "Filter", Description: "Discard mode only: permit an edge-depleted Gaussian kernel. Off by default, because a truncated kernel means the end support is insufficient.");
    private static readonly ParameterDescriptor ProfileEndEffect = new("endEffect", "End Handling", "select", "reflect", Group: "Filter",
        Options: [new("Reflect ends", "reflect"), new("Discard ends", "discard")],
        Description: "Reflect keeps the full profile using mirrored samples at the ends. Discard uses only samples with full Gaussian kernel support.");

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        // ── 断链修复：轮廓 → 点集 ──────────────────────────────────────────
        new("contour.toPoints", "Contour to Point List", "Contour",
            [Exec, new("contours", VisionDataType.Contour)],
            [new("pointList", VisionDataType.PointList), Found, new("pointCount", VisionDataType.Integer),
             new("length", VisionDataType.Double, Required: false), new("stepUsed", VisionDataType.Double, Required: false),
             new("sourceClosed", VisionDataType.Boolean), new("closurePolicy", VisionDataType.String),
             Diagnostics, Next],
            [
                Index,
                new("resample", "Resample Mode", "select", "arcLength", Group: "Sampling", Options:
                [
                    new("Arc-length spacing", "arcLength"),
                    new("Uniform index spacing", "uniform"),
                    new("Every contour point", "everyPoint")
                ], Description: "arcLength gives equidistant samples (what profile/runout need); uniform keeps the source point order but thins by index; everyPoint copies the contour verbatim."),
                new("sampleStep", "Sample Step", "number", 2, .01, 10000, .1, Unit: "px", Group: "Sampling",
                    VisibleWhen: new("resample", ["arcLength"]),
                    Description: "Arc-length spacing between samples. Ignored by uniform and everyPoint."),
                new("maxPoints", "Maximum Samples", "number", 5000, 2, 20000, 1, Group: "Sampling",
                    Description: "Hard budget. Exceeding it fails instead of silently truncating the profile."),
                Closed,
            ],
            "Converts one contour into a PointList. The explicit closed parameter overrides the source contour state. arcLength excludes the duplicate closing endpoint; everyPoint repeats the first point when closed; uniform preserves source indices. A pre-sampling budget check reports estimatedCount, maxPoints, length and suggestedStep before allocation. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P0：圆弧拟合 ──────────────────────────────────────────────
        new("measure.arcFit", "Arc Fit", "Measurement",
            [Exec, new("contours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("coverageDeg", VisionDataType.Double),
             new("circle", VisionDataType.Circle), new("center", VisionDataType.Point2D), new("radius", VisionDataType.Double),
             new("startAngleDeg", VisionDataType.Double), new("endAngleDeg", VisionDataType.Double), new("sweepDeg", VisionDataType.Double),
             new("arcLength", VisionDataType.Double), new("chordLength", VisionDataType.Double),
             new("minRadius", VisionDataType.Double, Required: false), new("maxRadius", VisionDataType.Double, Required: false),
             new("deviation", VisionDataType.Double, Required: false), new("rms", VisionDataType.Double, Required: false),
             new("conditionNumber", VisionDataType.Double, Required: false), new("radiusUncertainty", VisionDataType.Double, Required: false),
             new("observabilityOk", VisionDataType.Boolean, Required: false),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("minCoverageDeg", "Minimum Arc Coverage", "number", 20, 0, 359, 1, Unit: "deg", Group: "Arc",
                    Description: "Reject arcs spanning less than this. Coverage is the angular span of the sampled points; below it returns found=false because the radius is not observable."),
                new("maxConditionNumber", "Max Condition Number (0=off)", "number", 0, 0, 1e9, 1, Group: "Arc",
                    Description: "Upper bound on 1/(1-cos(sweep/2)). Short arcs make the radius ill-conditioned: exceeding the bound returns found=false / reason=arc-observability-weak instead of a numerically stable but meaningless radius."),
                new("maxRadiusUncertainty", "Max Radius Uncertainty (0=off)", "number", 0, 0, 1e9, 0.001, Unit: "px", Group: "Arc",
                    Description: "Upper bound on rms/(1-cos(sweep/2)), i.e. how much the fitted radius is amplified by the short arc. 0 disables this bound."),
                new("maxDeviation", "Max Radial Deviation (0=off)", "number", 0, 0, 1000000, 0.1, Unit: "px", Group: "Limits",
                    Description: "Optional form gate: withinTolerance reports (maxRadius-minRadius) <= limit. 0 disables the gate."),
            ],
            "Fits a circular arc to one contour and reports it in arc terms: sweepDeg, arcLength, chordLength and the start/end angles — what a fillet or a radius callout needs, and what measure.fitCircle (whole-circle least squares) does not give. conditionNumber = 1/(1-cos(sweep/2)) and radiusUncertainty = rms/(1-cos(sweep/2)) expose how strongly a short arc amplifies noise into the radius; exceeding either configured bound returns found=false / reason=arc-observability-weak. Input is canonically angle-ordered; separated arcs or discontinuous point groups return found=false. start/end angles and chordLength use the two points bounding the largest angular gap.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：最小区域 / 极限圆 ───────────────────────────────────────
        new("measure.mzCircle", "Minimum Zone Circle", "Measurement",
            [Exec, new("contours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("circle", VisionDataType.Circle),
             new("center", VisionDataType.Point2D), new("radius", VisionDataType.Double), new("zoneWidth", VisionDataType.Double),
             new("lsqRadius", VisionDataType.Double), new("centerShift", VisionDataType.Double),
             new("iterations", VisionDataType.Integer), new("startCount", VisionDataType.Integer),
             new("converged", VisionDataType.Boolean), new("optimalityGap", VisionDataType.Double),
             new("gateMetric", VisionDataType.String), new("evaluationState", VisionDataType.String),
             new("reason", VisionDataType.String),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("mode", "Extremum Mode", "select", "minZone", Group: "Circle", Options:
                [
                    new("Minimum zone (MZ)", "minZone"),
                    new("Minimum circumscribed (MCCI)", "minimumCircumscribed"),
                    new("Maximum inscribed (MIC)", "maximumInscribed")
                ], Description: "MZ minimizes the radial zone (ISO 1101 roundness), MCCI is the smallest circle containing the points (shaft), MIC the largest circle inside them (hole)."),
                new("precision", "Search Precision", "number", .0001, .000001, 1, .0001, Unit: "px", Group: "Circle",
                    Description: "Pattern-search step floor. Smaller values cost more iterations and give no more digits than the input resolution."),
                new("maxIterations", "Max Iterations (per start)", "number", 400, 10, 10000, 10, Group: "Circle",
                    Description: "Hard step budget for each deterministic start. Exhausting it marks the run not-converged (found=false, reason=not-converged) instead of returning an unrefined circle."),
                new("maxZoneWidth", "Max Zone Width (0=off)", "number", 0, 0, 1000000, .0001, Unit: "px", Group: "Limits",
                    Description: "Gate on zoneWidth for minZone, and the legacy fallback gate for MCCI/MIC when no nominal radius is given. 0 disables it."),
                new("nominalRadius", "Nominal Radius (0=off)", "number", 0, 0, 1000000, .0001, Unit: "px", Group: "Limits",
                    Description: "Drawing radius for MCCI/MIC. When set, those modes judge |radius - nominalRadius| instead of zoneWidth, because a shaft/hole limit is a radius callout."),
                new("radiusTolerance", "Radius Tolerance", "number", 0, 0, 1000000, .0001, Unit: "px", Group: "Limits",
                    Description: "Allowed absolute radius deviation; required when nominalRadius is set on MCCI/MIC."),
            ],
            "Extremal circle fits that a least-squares fit cannot give: minimum zone (ISO 1101), minimum circumscribed (shaft limit) and maximum inscribed (hole limit). Wire: contours -> circle/zoneWidth/lsqRadius. Runs five deterministic pattern searches (least-squares centre plus four +/-10%-radius probes) and reports converged/optimalityGap/startCount, so a local optimum cannot pass as the metrology limit; exhausting maxIterations yields found=false with reason=not-converged. Gates follow the drawing: minZone compares zoneWidth with maxZoneWidth, while MCCI/MIC compare radius with nominalRadius +/- radiusTolerance (falling back to maxZoneWidth when no nominal radius is set); gateMetric/gateValue/gateLimit echo the quantity actually judged. The search never depends on point order; withinTolerance is false when no gate is configured, and evaluationState marks that case as not-evaluated. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P1：曲率 / 圆角定位 ────────────────────────────────────────
        new("measure.curvature", "Contour Curvature", "Measurement",
            [Exec, new("contours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("minRadius", VisionDataType.Double),
             new("minRadiusPoint", VisionDataType.Point2D), new("maxCurvature", VisionDataType.Double),
             new("meanCurvature", VisionDataType.Double), new("cornerCount", VisionDataType.Integer),
             new("cornerJson", VisionDataType.String), new("withinTolerance", VisionDataType.Boolean),
             new("windowMode", VisionDataType.String, Required: false), new("windowLength", VisionDataType.Double, Required: false),
             new("actualWindowLength", VisionDataType.Double, Required: false), new("windowLengthCv", VisionDataType.Double, Required: false),
             new("windowUniformityOk", VisionDataType.Boolean, Required: false),
             new("toleranceEnabled", VisionDataType.Boolean, Required: false), new("evaluationState", VisionDataType.String, Required: false), Diagnostics, Next],
            [
                Index,
                new("fittingWindow", "Fitting Window", "number", 5, 1, 200, 1, Group: "Curvature",
                    Description: "Points on each side used for the local circle fit. Larger windows smooth noise and blur short fillets; smaller windows follow noise."),
                new("windowLength", "Arc-Length Window (0=off)", "number", 0, 0, 1000000, .01, Unit: "px", Group: "Curvature",
                    Description: "When set, the contour is first resampled so 2*fittingWindow+1 points span exactly this arc length. Use it when point density is non-uniform and the same point count would mean different physical spans."),
                new("cornerRadius", "Corner Radius Threshold (0=off)", "number", 0, 0, 1000000, .01, Unit: "px", Group: "Curvature",
                    Description: "Local minima of the radius below this value count as corners. 0 counts none, so cornerCount stays 0 while minRadius is still reported."),
                new("minRadius", "Minimum Radius", "number", 0, 0, 1000000, .01, Unit: "px", Group: "Limits",
                    Description: "Require global minimum radius >= this value. 0 disables; mutually exclusive with maxCurvature."),
                new("maxCurvature", "Maximum Curvature", "number", 0, 0, 1000000, .0001, Unit: "1/px", Group: "Limits",
                    Description: "Require global maximum curvature <= this value. 0 disables; mutually exclusive with minRadius."),
                Closed,
            ],
            "Sliding local circle fit along one contour: radius series, global minimum radius and its location, plus corners wherever the radius dips below cornerRadius. Wire: contours -> minRadius/minRadiusPoint/cornerCount. This is how a fillet radius or a bend line is located; segmentContours only splits the contour and never reports radii. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P1：轮廓滤波 / 粗糙度 ─────────────────────────────────────
        new("measure.profileFilter", "Profile Filter", "Measurement",
            [Exec, new("contours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("cutoff", VisionDataType.Double),
             new("waviness", VisionDataType.PointList), new("residual", VisionDataType.PointList),
             new("residualPeak", VisionDataType.Double, Required: false), new("wavinessPeak", VisionDataType.Double, Required: false),
             new("residualRms", VisionDataType.Double, Required: false),
             new("actualSampleStep", VisionDataType.Double, Required: false), new("maxObservedInputGap", VisionDataType.Double, Required: false),
             new("usableLength", VisionDataType.Double, Required: false), new("kernelTruncated", VisionDataType.Boolean, Required: false),
             new("retainedPointCount", VisionDataType.Integer, Required: false), new("droppedHeadLength", VisionDataType.Double, Required: false),
             new("droppedTailLength", VisionDataType.Double, Required: false), new("usableLengthRatio", VisionDataType.Double, Required: false),
             new("length", VisionDataType.Double), new("removedPointCount", VisionDataType.Integer), Diagnostics, Next],
            [
                Index,
                new("cutoffWavelength", "Cutoff Wavelength (0=auto)", "number", 0, 0, 1000000, .1, Unit: "px", Group: "Filter",
                    Description: "ISO 16610-21 Gaussian cutoff. 0 derives cutoff = length/5, the usual evaluation-length rule."),
                new("sampleStep", "Sample Step", "number", 1, .01, 10000, .1, Unit: "px", Group: "Filter",
                    Description: "Maximum axial spacing for the uniform resampling grid. The actual spacing is reported in actualSampleStep."),
                MaxInputGap, ProfileEndEffect,
                MinUsableLength, MinRetainedPoints, AllowTruncatedKernel,
                OpenProfile,
            ],
            "Splits a nominally straight sampled edge into waviness and residual using a Gaussian filter on an axial grid. maxInputGap=0 is an automatic 3-step limit, not 'off': larger holes are rejected with the gap indices so a linear bridge cannot fabricate waviness. Discard mode rejects a truncated kernel unless allowTruncatedKernel=true, and minUsableLength/minRetainedPoints guard against a result whose ends carry no support. Reports retainedPointCount, droppedHeadLength, droppedTailLength and usableLengthRatio alongside the usable pixel span, actual sample spacing and largest input gap.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        new("measure.roughness", "Profile Roughness", "Measurement",
            [Exec, new("pointList", VisionDataType.PointList, Required: false), new("contours", VisionDataType.Contour, Required: false)],
            [Found, new("sampleCount", VisionDataType.Integer), new("evaluationLength", VisionDataType.Double),
             new("cutoff", VisionDataType.Double), new("ra", VisionDataType.Double), new("rq", VisionDataType.Double),
             new("rz", VisionDataType.Double), new("rp", VisionDataType.Double), new("rv", VisionDataType.Double),
             new("rmax", VisionDataType.Double), new("zeroCrossings", VisionDataType.Integer),
             new("actualSampleStep", VisionDataType.Double, Required: false), new("maxObservedInputGap", VisionDataType.Double, Required: false),
             new("rzSamplingLengths", VisionDataType.Integer, Required: false), new("kernelTruncated", VisionDataType.Boolean, Required: false),
             new("rzWholeWindow", VisionDataType.Double, Required: false), new("rzValidForRating", VisionDataType.Boolean, Required: false),
             new("evaluationStartIndex", VisionDataType.Integer, Required: false), new("evaluationEndIndex", VisionDataType.Integer, Required: false),
             new("evaluationStartU", VisionDataType.Double, Required: false), new("evaluationEndU", VisionDataType.Double, Required: false),
             new("availableStartU", VisionDataType.Double, Required: false), new("evaluationStartRequestedPx", VisionDataType.Double, Required: false),
             new("evaluationStartTruncated", VisionDataType.Boolean, Required: false), new("evaluationSpanRequested", VisionDataType.Double, Required: false),
             new("reason", VisionDataType.String, Required: false),
             new("gateEnabled", VisionDataType.Boolean, Required: false), new("unit", VisionDataType.String, Required: false),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                new("index", "Zero-based Index", "number", 0, 0, 1999, 1,
                    Description: "Contour index used when the pointList port is disconnected. Ignored when points are wired directly."),
                new("cutoffWavelength", "Cutoff Wavelength (0=auto)", "number", 0, 0, 1000000, .1, Unit: "px", Group: "Filter",
                    Description: "Gaussian cutoff separating roughness from waviness. 0 derives length/5."),
                new("evaluationLength", "Evaluation Length (0=all)", "number", 0, 0, 1000000, .1, Unit: "px", Group: "Filter",
                    Description: "Maximum length of the evaluation window. 0 uses the full usable span."),
                new("evaluationStartPx", "Fixed Evaluation Start (-1=trailing)", "number", -1, -1, 1000000, .1, Unit: "px", Group: "Filter",
                    Description: "Axial coordinate of the first evaluated sample. -1 keeps the existing trailing window. Use a fixed value to compare several windows from the same origin."),
                new("sampleStep", "Sample Step", "number", 1, .01, 10000, .1, Unit: "px", Group: "Filter",
                    Description: "Maximum axial spacing for uniform resampling before filtering."),
                MaxInputGap, ProfileEndEffect,
                new("maxRa", "Max Ra (0=off)", "number", 0, 0, 1000000, .001, Unit: "px", Group: "Limits",
                    Description: "Optional gate: withinTolerance reports ra <= limit. 0 disables it."),
                new("maxRz", "Max Rz (0=off)", "number", 0, 0, 1000000, .001, Unit: "px", Group: "Limits",
                    Description: "Optional gate on the multi-sampling-length Rz. When fewer than three sampling lengths are available, rzValidForRating=false and the gate refuses to pass.")
            ],
            "Estimates roughness trends in pixels from a nominally straight sampled image edge. This image-based estimate does not replace a calibrated contact profilometer or a certified roughness measurement. rz is the average of the sampling lengths only when rzValidForRating=true; otherwise rzWholeWindow carries the whole-window peak-to-valley range and an Rz gate refuses to pass. evaluationStartPx fixes the window origin, and evaluationStartIndex/evaluationEndIndex/evaluationStartU/evaluationEndU report exactly which samples were evaluated so several windows can be compared.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：轮廓间距 ──────────────────────────────────────────────
        new("measure.contourDistance", "Contour Distance", "Measurement",
            [Exec, new("contours", VisionDataType.Contour), new("referenceContours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("distance", VisionDataType.Double),
             new("minDistance", VisionDataType.Double), new("maxDistance", VisionDataType.Double), new("meanDistance", VisionDataType.Double),
             new("closestPointA", VisionDataType.Point2D), new("closestPointB", VisionDataType.Point2D),
             new("direction", VisionDataType.String, Required: false), new("directedAToB", VisionDataType.Double, Required: false),
             new("directedBToA", VisionDataType.Double, Required: false), new("symmetricDistance", VisionDataType.Double, Required: false),
             new("symmetricGap", VisionDataType.Double, Required: false),
             new("minSamplingErrorBoundPx", VisionDataType.Double), new("maxSamplingErrorBoundPx", VisionDataType.Double),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("referenceIndex", "Reference Contour Index", "number", 0, 0, 1999, 1,
                    Description: "Which contour in referenceContours to measure against (0-based)."),
                new("mode", "Reported Distance", "select", "gap", Group: "Distance", Options:
                [
                    new("Gap (nearest distance)", "gap"),
                    new("Deviation (farthest distance)", "deviation")
                ], Description: "gap reports the closest approach (a slit width); deviation reports the farthest stray (a form error). min/max/mean follow the selected direction; both directions are always emitted."),
                new("direction", "Comparison Direction", "select", "aToB", Group: "Distance", Options:
                [
                    new("A → B (contours to reference)", "aToB"),
                    new("B → A (reference to contours)", "bToA"),
                    new("Symmetric (worst of both)", "symmetric")
                ], Description: "Which sampling direction decides distance/min/max/mean. A one-way comparison ignores features that exist only on the reference side; directedAToB/directedBToA/symmetricDistance/symmetricGap are always reported so the asymmetry stays visible."),
                new("sampleStep", "Sample Step", "number", 2, .01, 10000, .1, Unit: "px", Group: "Distance",
                    Description: "Arc-length spacing of the samples compared. Both polylines are resampled with it."),
                new("maxSamples", "Maximum Samples", "number", 1500, 2, 4000, 1, Group: "Distance",
                    Description: "Per-contour budget; the comparison is O(n*m), so overflow fails instead of stalling the run."),
                new("limit", "Limit (0=off)", "number", 0, 0, 1000000, .001, Unit: "px", Group: "Limits",
                    Description: "Optional gate. gap mode requires distance <= limit; deviation mode requires distance <= limit as well, so one parameter covers both."),
                new("closed", "Closed Contour", "boolean", true,
                    Description: "Treat both polylines as closed when sampling."),
            ],
            "Nearest and farthest distance between two polylines, with the actual closest point pair. Wire: contours + referenceContours -> distance/closestPointA/closestPointB. direction selects A→B, B→A or the symmetric worst case; directedAToB, directedBToA, symmetricDistance and symmetricGap are always emitted, because a one-way sweep silently ignores features that exist only on the reference contour. Distances use exact point-to-segment projection against every reference segment. min/maxSamplingErrorBoundPx bound source-sampling approximation; sampleStep controls accuracy, not geometry. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：螺纹 ─────────────────────────────────────────────────
        new("measure.thread", "Thread Measurement", "Measurement",
            [Exec, new("contours", VisionDataType.Contour), new("axis", VisionDataType.Line2D)],
            [Found, new("crestCount", VisionDataType.Integer), new("sampleCount", VisionDataType.Integer),
             new("pitch", VisionDataType.Double), new("majorDiameter", VisionDataType.Double), new("minorDiameter", VisionDataType.Double),
             new("pitchDiameter", VisionDataType.Double), new("pitchDiameterMethod", VisionDataType.String, Required: false),
             new("pitchDiameterEstimate", VisionDataType.Double, Required: false), new("pitchDiameterIsEstimate", VisionDataType.Boolean, Required: false),
             new("certifiedUseAllowed", VisionDataType.Boolean, Required: false),
             new("crestPositionsJson", VisionDataType.String, Required: false), new("pitchIntervalsJson", VisionDataType.String, Required: false),
             new("maxPitchDeviation", VisionDataType.Double, Required: false), new("validIntervalCount", VisionDataType.Integer, Required: false),
             new("crestValleyAlternation", VisionDataType.Double, Required: false), new("peakValleySalience", VisionDataType.Double, Required: false),
             new("threadAnglePlausible", VisionDataType.Boolean, Required: false), new("polarityConfidence", VisionDataType.Double, Required: false),
             new("threadAngleDeg", VisionDataType.Double), new("threadDepth", VisionDataType.Double),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("polarity", "Crest Polarity", "select", "profile", Group: "Thread",
                    Options: [new("Crest on the input side", "profile"), new("Crest on the opposite side", "inverted")],
                    Description: "Which side of the axis the crests sit on. Wrong polarity turns peaks into valleys and the pitch count collapses; check with the overlay."),
                new("pitchTolerance", "Pitch Tolerance (0=off)", "number", 0, 0, 1000000, .001, Unit: "px", Group: "Limits",
                    Description: "Optional gate on the measured pitch. 0 disables the gate."),
                new("minCrests", "Minimum Crests", "number", 3, 2, 500, 1, Group: "Thread",
                    Description: "Fewer detected crests than this returns found=false: two crests give one pitch, which is not a measurement."),
                new("sampleStep", "Sample Step", "number", 1, .01, 10000, .1, Unit: "px", Group: "Thread",
                    Description: "Arc-length resampling along the profile before crest detection."),
                new("minValidIntervals", "Min Valid Pitch Intervals (0=auto)", "number", 0, 0, 500, 1, Group: "Limits",
                    Description: "Minimum number of adjacent crest-to-crest intervals. Defaults to max(2, minCrests-1); fewer intervals returns found=false / reason=insufficient-pitch-intervals."),
                new("expectedThreadAngleDeg", "Expected Thread Angle", "number", 60, 1, 120, 1, Unit: "deg", Group: "Limits",
                    Description: "Nominal included flank angle used for the plausibility part of polarity confidence."),
                new("threadAngleToleranceDeg", "Thread Angle Tolerance (0=off)", "number", 0, 0, 120, 1, Unit: "deg", Group: "Limits",
                    Description: "Allowed deviation from expectedThreadAngleDeg. 0 disables the angle check."),
                new("minPolarityConfidence", "Min Polarity Confidence (0=off)", "number", 0, 0, 1, .05, Group: "Limits",
                    Description: "Minimum confidence from crest/valley alternation, peak-valley salience and flank-angle plausibility. Exceeding it rejects an incomplete thread pattern instead of trusting the overlay."),
                new("certifyPitchDiameter", "Certify Pitch Diameter", "boolean", false, Group: "Limits",
                    Description: "Explicit opt-in for using pitchDiameter as a certified result. Off by default because the silhouette only yields an arithmetic estimate.")
            ],
            "Measure thread pitch, major and minor diameters, flank angle and depth from a silhouette and a supplied axis. The silhouette does not measure the pitch diameter directly: pitchDiameterEstimate/pitchDiameterIsEstimate mark the arithmetic mean, certifiedUseAllowed stays false until certifyPitchDiameter is explicitly enabled with a plausible pattern. crestPositionsJson and pitchIntervalsJson expose every crest and adjacent pitch, maxPitchDeviation locates a single bad interval, and polarityConfidence combines crest/valley alternation, salience and flank-angle plausibility. pitchTolerance applies only to pitch consistency. The tolerance flag is not a thread-gauge acceptance decision.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：齿轮（纯算量） ─────────────────────────────────────────
        new("measure.gear", "Spur Gear Geometry", "Measurement",
            [Exec],
            [Found, new("pitchDiameter", VisionDataType.Double), new("baseDiameter", VisionDataType.Double),
             new("outsideDiameter", VisionDataType.Double), new("rootDiameter", VisionDataType.Double),
             new("circularPitch", VisionDataType.Double), new("spanTeeth", VisionDataType.Integer),
             new("spanLength", VisionDataType.Double), new("pinDiameter", VisionDataType.Double),
             new("measurementOverPins", VisionDataType.Double), new("baseToothThickness", VisionDataType.Double),
             new("autoPinDiameter", VisionDataType.Double, Required: false), new("pinDiameterSource", VisionDataType.String, Required: false),
             new("unit", VisionDataType.String, Required: false), new("dimensionalUseAllowed", VisionDataType.Boolean, Required: false),
             Diagnostics, Next],
            [
                new("module", "Module", "number", 1, .001, 1000, .001, Unit: "mm",
                    Description: "Gear module m. All diameters come out in the same unit."),
                new("teeth", "Tooth Count", "number", 20, 3, 2000, 1, Group: "Gear",
                    Description: "Number of teeth z. Must be a whole number >= 3.") { Integer = true },
                new("pressureAngleDeg", "Pressure Angle", "number", 20, 10, 35, .5, Unit: "deg", Group: "Gear",
                    Description: "Reference pressure angle: 20 deg is standard, 14.5 deg is legacy. Drives base diameter and span length."),
                new("profileShift", "Profile Shift Coefficient", "number", 0, -2, 2, .01, Group: "Gear",
                    Description: "x coefficient for corrected gears. 0 is an uncorrected gear; it shifts outside/root diameters and the span length."),
                new("pinDiameter", "Pin Diameter (0=auto)", "number", 0, 0, 1000, .001, Unit: "mm", Group: "Gear",
                    Description: "Measuring pin diameter. 0 derives the usual 1.728*m. Only used for measurement over pins."),
                new("unit", "Unit Label", "text", "mm", Group: "Reporting",
                    Description: "One unit contract for module and pinDiameter; no conversion is applied. unit=px sets dimensionalUseAllowed=false."),
            ],
            "Closed-form spur-gear geometry from module, tooth count and pressure angle: pitch/base/outside/root diameters, circular pitch, span length W_k with its k, and measurement over pins. Combinations are validated before evaluation (tip/root relation, span teeth, involute domain and derived pin diameter); unusable combinations return found=false / reason=invalid-gear-combination with the offending values. module and pinDiameter share the unit label echoed on the result. This is arithmetic, not vision — no input ports, deterministic output. Helical gears are out of scope (helix angle assumed 0).", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        // ── P1：公差叠加 ─────────────────────────────────────────────
        new("tolerance.stackup", "Tolerance Stack-Up", "Measurement",
            [Exec, new("a", VisionDataType.Double, Required: false), new("b", VisionDataType.Double, Required: false),
             new("c", VisionDataType.Double, Required: false), new("d", VisionDataType.Double, Required: false),
             new("e", VisionDataType.Double, Required: false), new("f", VisionDataType.Double, Required: false)],
            [Found, new("contributorCount", VisionDataType.Integer), new("nominal", VisionDataType.Double),
             new("worstCaseMax", VisionDataType.Double), new("worstCaseMin", VisionDataType.Double),
             new("rssTolerance", VisionDataType.Double), new("upperLimit", VisionDataType.Double), new("lowerLimit", VisionDataType.Double),
             new("margin", VisionDataType.Double), new("utilization", VisionDataType.Double),
             new("gateMode", VisionDataType.String), new("gateEnabled", VisionDataType.Boolean),
             new("evaluatedDeviation", VisionDataType.Double), new("limitModel", VisionDataType.String), new("unit", VisionDataType.String),
             new("assumptionsDeclared", VisionDataType.Boolean), new("nominalSource", VisionDataType.String),
             new("connectionMask", VisionDataType.String), new("contributorSigmasJson", VisionDataType.String),
             new("evaluationState", VisionDataType.String), new("reason", VisionDataType.String),
             new("contributionsJson", VisionDataType.String), new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                new("contributionsJson", "Contributions JSON", "textarea",
                    "[]",
                    Group: "Chain",
                    Description: "Dimension chain: each entry is {name, nominal, plus, minus, sign}, e.g. [{\"name\":\"pin\",\"nominal\":10,\"plus\":0.02,\"minus\":0.02,\"sign\":1},{\"name\":\"hole\",\"nominal\":10.05,\"plus\":0.03,\"minus\":0,\"sign\":-1}]. plus/minus are magnitudes >= 0; sign is +1 to add the feature, -1 to subtract it. Ports a..f override nominal of entries 1..6."),
                new("method", "Method", "select", "worstCase", Group: "Chain", Options:
                [
                    new("Worst case (arithmetic)", "worstCase"),
                    new("Statistical (RSS)", "rss")
                ], Description: "Worst case combines all signed tolerance bounds. RSS estimates symmetric limits around nominal assuming independent contributors with each full tolerance width treated as six sigma."),
                new("gateMode", "Acceptance Basis", "select", "nominal", Group: "Limits",
                    Options: [new("Nominal Only", "nominal"), new("Full Calculated Interval", "interval")],
                    Description: "Check either the chain nominal or both calculated bounds against the target tolerance."),
                new("targetNominal", "Target Nominal", "number", 0, -1000000, 1000000, .001, Group: "Limits",
                    Description: "Center of the acceptance interval. Zero is a valid target."),
                new("tolerance", "Tolerance", "number", 0, 0, 1000000, .001, Group: "Limits",
                    Description: "Allowed absolute deviation from the target in the chain's unit. 0 disables the gate."),
                new("unit", "Unit Label", "text", "mm", Group: "Reporting",
                    Description: "Report the unit used by the inputs. Up to 16 characters. Values are used as supplied."),
                new("assumeIndependent", "Assume Independent (RSS)", "boolean", false, Group: "Statistical",
                    Description: "Declares that the contributors are statistically independent. Required for an RSS result to be usable for release."),
                new("assumeSixSigma", "Assume Six Sigma (RSS)", "boolean", false, Group: "Statistical",
                    Description: "Declares that each full tolerance width equals six sigma (three per side). Required for an RSS result to be usable for release."),
            ],
            "Combines a one-dimensional tolerance chain using worst-case or RSS limits. Reports the limit model, which values were checked against the target, the resulting margin and the input unit. Optional inputs a through f replace the first six nominal values; nominalSource (json|ports|mixed) and connectionMask record where each nominal came from, and contributorSigmasJson publishes the per-contributor sigma conversion. RSS treats every full width as six sigma and assumes independent contributors: unless assumeIndependent and assumeSixSigma are both declared, withinTolerance stays false with evaluationState=not-evaluated and reason=assumptions-undeclared, so the numbers are reported but never silently released.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        // ── P2：多基准基准系 ─────────────────────────────────────────
        new("tolerance.drf", "Datum Frame (A|B intersection)", "Measurement",
            [Exec, new("primary", VisionDataType.Line2D), new("secondary", VisionDataType.Line2D),
             new("tertiary", VisionDataType.Point2D, Required: false)],
            [Found, new("pose", VisionDataType.Pose2D), new("origin", VisionDataType.Point2D),
             new("primaryAngleDeg", VisionDataType.Double), new("secondaryAngleDeg", VisionDataType.Double),
             new("orthogonalityErrorDeg", VisionDataType.Double), new("originOffset", VisionDataType.Double, Required: false),
             new("conditionNumber", VisionDataType.Double, Required: false), new("intersectionDistanceToSegments", VisionDataType.Double, Required: false),
             new("originUnreliable", VisionDataType.Boolean, Required: false), new("datumValid", VisionDataType.Boolean, Required: false),
             new("datumJson", VisionDataType.String), new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                new("datumName", "Datum Name", "text", "A|B", Group: "Datum",
                    Description: "Label written into datumJson and the overlay, e.g. A|B or A-B."),
                new("maxOrthogonalityErrorDeg", "Max Orthogonality Error (0=off)", "number", 0, 0, 90, .001, Unit: "deg", Group: "Limits",
                    Description: "Gate on how far the secondary datum is from 90 deg to the primary. 0 disables the gate."),
                new("maxConditionNumber", "Max Condition Number (0=off)", "number", 0, 0, 1e6, .1, Group: "Limits",
                    Description: "Condition number of the A|B intersection (1/|sin theta|). Near-parallel datums amplify direction noise into the origin; exceeding the limit sets originUnreliable=true and datumValid=false."),
                new("maxIntersectionDistance", "Max Intersection Distance (0=off)", "number", 0, 0, 1e6, .1, Unit: "px", Group: "Limits",
                    Description: "Allowed distance from the computed intersection to the nearest measured datum segment. Exceeding it means the origin is an extrapolation and sets originUnreliable=true."),
                new("flipAxis", "Flip Axis", "boolean", false, Group: "Datum",
                    Description: "Reverse the +X direction. Fitted lines carry no polarity; set this when the drawing's X points the other way."),
                new("unit", "Unit Label", "text", "px", Group: "Reporting",
                    Description: "Label only, no conversion (1..16 chars)."),
            ],
            "Two-datum reference frame: the origin is the A|B intersection (not a projected point), X follows datum A, and the orthogonality error of B is reported as the datum's own contribution. conditionNumber and intersectionDistanceToSegments expose how far the intersection is extrapolated; either limit sets originUnreliable=true. datumValid = withinTolerance && !originUnreliable, so a geometrically constructible but unusable datum is visible to downstream tolerance nodes instead of looking like a pass. Image-plane (2D) only.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P1：几何构造族 ───────────────────────────────────────────
        new("geometry.lineConstruct", "Construct Line", "Geometry",
            [Exec, new("line", VisionDataType.Line2D, Required: false), new("line2", VisionDataType.Line2D, Required: false),
             new("point", VisionDataType.Point2D, Required: false), new("circle", VisionDataType.Circle, Required: false)],
            [Found, new("line", VisionDataType.Line2D, Required: false), new("line2", VisionDataType.Line2D, Required: false),
             new("line2Valid", VisionDataType.Boolean), new("angleDeg", VisionDataType.Double, Required: false), new("length", VisionDataType.Double, Required: false),
             new("offset", VisionDataType.Double, Required: false), Diagnostics, Next],
            [
                new("operation", "Operation", "select", "perpendicular", Group: "Construction", Options:
                [
                    new("Perpendicular through point", "perpendicular"),
                    new("Parallel offset", "parallel"),
                    new("Midline", "midline"),
                    new("Angle bisector", "bisector"),
                    new("Tangent from point", "tangent")
                ], Description: "perpendicular: line + point. parallel: line + point, or line + offset. midline/bisector: line + line2. tangent: circle + point, emits both tangents."),
                new("offset", "Offset", "number", 0, -1000000, 1000000, .001, Unit: "px", Group: "Construction",
                    VisibleWhen: new("operation", ["parallel"]),
                    Description: "Signed normal offset for the parallel operation. Positive offsets to the left of the source line. Ignored when a point is wired."),
                new("spanScale", "Length Scale", "number", 1, .01, 100, .01, Group: "Construction",
                    Description: "Length multiplier for the constructed segment relative to the source line, so the result is visible and overlappable. Does not change its direction."),
            ],
            "Builds a Line2D from existing geometry: perpendicular through a point, parallel offset, midline, angle bisector, or the two tangents from a point to a circle. Without it, parallel/perpendicular relationships can only be measured, never constructed — genParallelContour offsets a contour but never yields a Line2D. Missing inputs for the selected operation fail hard instead of emitting a degenerate line.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：非刚体坐标变换 ───────────────────────────────────────
        new("coordinate.transformAffine", "Affine Transform", "Coordinate",
            [Exec, new("scaleX", VisionDataType.Double, Required: false), new("scaleY", VisionDataType.Double, Required: false),
             new("rotationDeg", VisionDataType.Double, Required: false), new("translateX", VisionDataType.Double, Required: false),
             new("translateY", VisionDataType.Double, Required: false), new("shear", VisionDataType.Double, Required: false)],
            [new("transform", VisionDataType.Transform2D), new("determinant", VisionDataType.Double),
             new("scaleX", VisionDataType.Double), new("scaleY", VisionDataType.Double), new("rotationDeg", VisionDataType.Double),
             new("translateX", VisionDataType.Double), new("translateY", VisionDataType.Double), new("shear", VisionDataType.Double),
             new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String),
             new("unit", VisionDataType.String), Diagnostics, Next],
            [
                new("scaleX", "Scale X", "number", 1, -1000, 1000, .0001, Group: "Transform",
                    Description: "Fallback X scale when the port is disconnected. 0 is rejected: a collapsed axis is not a transform."),
                new("scaleY", "Scale Y", "number", 1, -1000, 1000, .0001, Group: "Transform",
                    Description: "Fallback Y scale when the port is disconnected."),
                new("rotationDeg", "Rotation", "number", 0, -3600, 3600, .01, Unit: "deg", Group: "Transform",
                    Description: "CCW rotation applied before translation."),
                new("shear", "Shear", "number", 0, -100, 100, .0001, Group: "Transform",
                    Description: "x' = x + shear*y applied before scale and rotation. Use it for skewed axes; it keeps the determinant at scaleX*scaleY."),
                new("translateX", "Translate X", "number", 0, -1000000, 1000000, .001, Group: "Transform",
                    Description: "Translation along the source X axis, in target units."),
                new("translateY", "Translate Y", "number", 0, -1000000, 1000000, .001, Group: "Transform",
                    Description: "Translation along the source Y axis, in target units."),
                new("sourceFrame", "Source Frame", "text", "Image", Group: "Frames",
                    Description: "Name of the frame this transform maps FROM (non-empty, max 64 chars)."),
                new("targetFrame", "Target Frame", "text", "World", Group: "Frames",
                    Description: "Name of the frame this transform maps TO."),
                new("sourceUnit", "Source Unit", "text", "px", Group: "Frames",
                    Description: "Unit label of the source coordinates."),
                new("targetUnit", "Target Unit", "text", "mm", Group: "Frames",
                    Description: "Unit label of the target coordinates."),
            ],
            "Affine (non-rigid) transform between named frames. Column-vector convention: p'=R*S*[[1,shear],[0,1]]*p+t; translation is in the target frame. Connected scalar ports override constants independently and diagnostics record each final source/value plus the actual 2x3 matrix. Zero/non-finite values and empty frames fail.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: [])
    ];
}
