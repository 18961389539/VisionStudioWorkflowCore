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

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        // ── 断链修复：轮廓 → 点集 ──────────────────────────────────────────
        new("contour.toPoints", "Contour to Point List", "Contour",
            [Exec, new("contours", VisionDataType.Contour)],
            [new("pointList", VisionDataType.PointList), Found, new("pointCount", VisionDataType.Integer),
             new("length", VisionDataType.Double, Required: false), new("stepUsed", VisionDataType.Double, Required: false),
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
            "Converts one contour into a PointList so tolerance.profile / tolerance.runout / geometry.fitFromPoints stop being limited to detection centres. Wire: contours -> pointList. resample=arcLength emits equidistant samples at sampleStep; uniform thins the existing points by index; everyPoint copies them all. Budget overflow fails hard. Units: pixels.", "builtin",
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
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("minCoverageDeg", "Minimum Arc Coverage", "number", 20, 0, 359, 1, Unit: "deg", Group: "Arc",
                    Description: "Reject arcs spanning less than this. Coverage is the angular span of the sampled points; below it returns found=false because the radius is not observable."),
                new("maxDeviation", "Max Radial Deviation (0=off)", "number", 0, 0, 1000000, 0.1, Unit: "px", Group: "Limits",
                    Description: "Optional form gate: withinTolerance reports (maxRadius-minRadius) <= limit. 0 disables the gate."),
            ],
            "Fits a circular arc to one contour and reports it in arc terms: sweepDeg, arcLength, chordLength and the start/end angles — what a fillet or a radius callout needs, and what measure.fitCircle (whole-circle least squares) does not give. Wire: contours -> circle/radius/sweepDeg/arcLength. Coverage below minCoverageDeg returns found=false. Units: pixels, angles in degrees CCW.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：最小区域 / 极限圆 ───────────────────────────────────────
        new("measure.mzCircle", "Minimum Zone Circle", "Measurement",
            [Exec, new("contours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("circle", VisionDataType.Circle),
             new("center", VisionDataType.Point2D), new("radius", VisionDataType.Double), new("zoneWidth", VisionDataType.Double),
             new("lsqRadius", VisionDataType.Double), new("centerShift", VisionDataType.Double),
             new("iterations", VisionDataType.Integer), new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
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
                new("maxIterations", "Max Iterations", "number", 400, 10, 10000, 10, Group: "Circle",
                    Description: "Hard iteration budget per radius. Exceeding it fails instead of returning an unrefined circle."),
                new("maxZoneWidth", "Max Zone Width (0=off)", "number", 0, 0, 1000000, .0001, Unit: "px", Group: "Limits",
                    Description: "Optional gate on zoneWidth. 0 disables it; a go/no-go ring gauge is exactly this gate."),
            ],
            "Extremal circle fits that a least-squares fit cannot give: minimum zone (ISO 1101), minimum circumscribed (shaft limit) and maximum inscribed (hole limit). Wire: contours -> circle/zoneWidth/lsqRadius. Starts from the least-squares centre and refines with a deterministic pattern search, so the result never depends on point order. withinTolerance gates zoneWidth. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P1：曲率 / 圆角定位 ────────────────────────────────────────
        new("measure.curvature", "Contour Curvature", "Measurement",
            [Exec, new("contours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("minRadius", VisionDataType.Double),
             new("minRadiusPoint", VisionDataType.Point2D), new("maxCurvature", VisionDataType.Double),
             new("meanCurvature", VisionDataType.Double), new("cornerCount", VisionDataType.Integer),
             new("cornerJson", VisionDataType.String), new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("fittingWindow", "Fitting Window", "number", 5, 1, 200, 1, Group: "Curvature",
                    Description: "Points on each side used for the local circle fit. Larger windows smooth noise and blur short fillets; smaller windows follow noise."),
                new("cornerRadius", "Corner Radius Threshold (0=off)", "number", 0, 0, 1000000, .01, Unit: "px", Group: "Curvature",
                    Description: "Local minima of the radius below this value count as corners. 0 counts none, so cornerCount stays 0 while minRadius is still reported."),
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
             new("length", VisionDataType.Double), new("removedPointCount", VisionDataType.Integer), Diagnostics, Next],
            [
                Index,
                new("cutoffWavelength", "Cutoff Wavelength (0=auto)", "number", 0, 0, 1000000, .1, Unit: "px", Group: "Filter",
                    Description: "ISO 16610-21 Gaussian cutoff. 0 derives cutoff = length/5, the usual evaluation-length rule."),
                new("sampleStep", "Sample Step", "number", 1, .01, 10000, .1, Unit: "px", Group: "Filter",
                    Description: "Arc-length resampling before filtering. The cutoff must stay well above this step or the filter cannot resolve the wavelength."),
                new("endEffect", "End Handling", "select", "reflect", Group: "Filter", Options:
                [
                    new("Reflect ends", "reflect"),
                    new("Discard ends", "discard")
                ], Description: "Gaussian filtering shortens the usable profile by half a cutoff at each end. Reflect keeps the full length; discard drops the ends honestly instead of inventing data."),
                Closed,
            ],
            "Splits a sampled profile into waviness and residual with an ISO 16610-21 Gaussian filter, emitting both as PointLists so downstream roughness or tolerance.profile can consume them. Wire: contours -> waviness/residual. The mean line is the least-squares axis, so the input must be a nominally straight edge; curved surfaces need a nominal first. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        new("measure.roughness", "Profile Roughness", "Measurement",
            [Exec, new("pointList", VisionDataType.PointList, Required: false), new("contours", VisionDataType.Contour, Required: false)],
            [Found, new("sampleCount", VisionDataType.Integer), new("evaluationLength", VisionDataType.Double),
             new("cutoff", VisionDataType.Double), new("ra", VisionDataType.Double), new("rq", VisionDataType.Double),
             new("rz", VisionDataType.Double), new("rp", VisionDataType.Double), new("rv", VisionDataType.Double),
             new("rmax", VisionDataType.Double), new("zeroCrossings", VisionDataType.Integer),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                new("index", "Zero-based Index", "number", 0, 0, 1999, 1,
                    Description: "Contour index used when the pointList port is disconnected. Ignored when points are wired directly."),
                new("cutoffWavelength", "Cutoff Wavelength (0=auto)", "number", 0, 0, 1000000, .1, Unit: "px", Group: "Filter",
                    Description: "Gaussian cutoff separating roughness from waviness. 0 derives length/5."),
                new("evaluationLength", "Evaluation Length (0=all)", "number", 0, 0, 1000000, .1, Unit: "px", Group: "Filter",
                    Description: "Trailing window actually evaluated; 0 uses the whole profile. Rz averages 5 sampling lengths, so shorter windows report fewer sub-lengths."),
                new("sampleStep", "Sample Step", "number", 1, .01, 10000, .1, Unit: "px", Group: "Filter",
                    Description: "Arc-length resampling before filtering. Must stay well below cutoffWavelength."),
                new("maxRa", "Max Ra (0=off)", "number", 0, 0, 1000000, .001, Unit: "px", Group: "Limits",
                    Description: "Optional gate: withinTolerance reports ra <= limit. 0 disables it."),
            ],
            "Amplitude roughness parameters of a sampled profile: Ra, Rq, Rz, Rp, Rv and Rmax after an ISO 16610-21 Gaussian mean-line removal. Wire: contours (or pointList) -> ra/rz/rmax. Rz averages the 5 sampling lengths required by ISO 4287; with a shorter window it averages what fits. Image-based values are only as good as the edge sampling, so report them as a trend, not a substitute for a stylus. Units follow the input coordinates.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：轮廓间距 ──────────────────────────────────────────────
        new("measure.contourDistance", "Contour Distance", "Measurement",
            [Exec, new("contours", VisionDataType.Contour), new("referenceContours", VisionDataType.Contour)],
            [Found, new("sampleCount", VisionDataType.Integer), new("distance", VisionDataType.Double),
             new("minDistance", VisionDataType.Double), new("maxDistance", VisionDataType.Double), new("meanDistance", VisionDataType.Double),
             new("closestPointA", VisionDataType.Point2D), new("closestPointB", VisionDataType.Point2D),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("referenceIndex", "Reference Contour Index", "number", 0, 0, 1999, 1,
                    Description: "Which contour in referenceContours to measure against (0-based)."),
                new("mode", "Reported Distance", "select", "gap", Group: "Distance", Options:
                [
                    new("Gap (nearest distance)", "gap"),
                    new("Deviation (farthest distance)", "deviation")
                ], Description: "gap reports the closest approach (a slit width); deviation reports the farthest stray (a form error). min/max/mean are always emitted regardless of mode."),
                new("sampleStep", "Sample Step", "number", 2, .01, 10000, .1, Unit: "px", Group: "Distance",
                    Description: "Arc-length spacing of the samples compared. Both polylines are resampled with it."),
                new("maxSamples", "Maximum Samples", "number", 1500, 2, 4000, 1, Group: "Distance",
                    Description: "Per-contour budget; the comparison is O(n*m), so overflow fails instead of stalling the run."),
                new("limit", "Limit (0=off)", "number", 0, 0, 1000000, .001, Unit: "px", Group: "Limits",
                    Description: "Optional gate. gap mode requires distance <= limit; deviation mode requires distance <= limit as well, so one parameter covers both."),
                new("closed", "Closed Contour", "boolean", true,
                    Description: "Treat both polylines as closed when sampling."),
            ],
            "Nearest and farthest distance between two polylines, with the actual closest point pair. Wire: contours + referenceContours -> distance/closestPointA/closestPointB. Fills the gap between point-to-line (measure.pointLineDistance) and circle-to-circle (measure.circleSpacing): an arbitrary edge against an arbitrary edge. Samples are equidistant, so features between samples can be missed. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：螺纹 ─────────────────────────────────────────────────
        new("measure.thread", "Thread Measurement", "Measurement",
            [Exec, new("contours", VisionDataType.Contour), new("axis", VisionDataType.Line2D)],
            [Found, new("crestCount", VisionDataType.Integer), new("sampleCount", VisionDataType.Integer),
             new("pitch", VisionDataType.Double), new("majorDiameter", VisionDataType.Double), new("minorDiameter", VisionDataType.Double),
             new("pitchDiameter", VisionDataType.Double), new("threadAngleDeg", VisionDataType.Double), new("threadDepth", VisionDataType.Double),
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
            ],
            "Image-based thread inspection against a supplied axis: pitch from crest spacing, major/minor diameter from the radial extrema, average thread angle from the flank slopes. Wire: contours + axis Line2D -> pitch/majorDiameter/threadAngleDeg. Optically this is a silhouette measurement: it cannot see the pitch diameter root, so pitchDiameter is the (major+minor)/2 estimate. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P2：齿轮（纯算量） ─────────────────────────────────────────
        new("measure.gear", "Spur Gear Geometry", "Measurement",
            [Exec],
            [Found, new("pitchDiameter", VisionDataType.Double), new("baseDiameter", VisionDataType.Double),
             new("outsideDiameter", VisionDataType.Double), new("rootDiameter", VisionDataType.Double),
             new("circularPitch", VisionDataType.Double), new("spanTeeth", VisionDataType.Integer),
             new("spanLength", VisionDataType.Double), new("pinDiameter", VisionDataType.Double),
             new("measurementOverPins", VisionDataType.Double), new("baseToothThickness", VisionDataType.Double), Diagnostics, Next],
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
            ],
            "Closed-form spur-gear geometry from module, tooth count and pressure angle: pitch/base/outside/root diameters, circular pitch, span length W_k with its k, and measurement over pins. This is arithmetic, not vision — no input ports, deterministic output, so it can drive a recipe or a report row directly. Helical gears are out of scope (helix angle assumed 0). All lengths in the module's unit.", "builtin",
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
             new("contributionsJson", VisionDataType.String), new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                new("contributionsJson", "Contributions JSON", "textarea",
                    """[{"name":"pin","nominal":10,"plus":0.02,"minus":0.02,"sign":1},{"name":"hole","nominal":10.05,"plus":0.03,"minus":0,"sign":-1}]""",
                    Group: "Chain",
                    Description: "Dimension chain: each entry is {name, nominal, plus, minus, sign}. plus/minus are magnitudes >= 0; sign is +1 to add the feature, -1 to subtract it. Ports a..f override nominal of entries 1..6."),
                new("method", "Method", "select", "worstCase", Group: "Chain", Options:
                [
                    new("Worst case (arithmetic)", "worstCase"),
                    new("Statistical (RSS)", "rss")
                ], Description: "Worst case adds every tolerance (100% interchangeability). RSS assumes independent +-3 sigma contributors and gives the statistical limit, which is what a process capability claim needs."),
                new("targetNominal", "Target Nominal (0=off)", "number", 0, -1000000, 1000000, .001, Group: "Limits",
                    Description: "Drawing nominal the chain should close at. The gate compares nominal against it; left at 0 with tolerance 0 the gate stays open."),
                new("tolerance", "Tolerance", "number", 0, 0, 1000000, .001, Unit: "px", Group: "Limits",
                    Description: "Allowed absolute gap between nominal and targetNominal. 0 disables the gate."),
                new("unit", "Unit Label", "text", "mm", Group: "Reporting",
                    Description: "Label only, no conversion (1..16 chars). Set it to whatever unit the contributions are in."),
            ],
            "Closes a 1D dimension chain: worst-case (arithmetic) or statistical (RSS, +-3 sigma) limits, the resulting upper/lower limit, closure margin against a target and per-contributor share. Wire: optional a..f to replace the first six nominals. Every existing tolerance.* node evaluates ONE feature against ONE tolerance; this node is the only place where several contributions are combined.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        // ── P2：多基准基准系 ─────────────────────────────────────────
        new("tolerance.drf", "Datum Reference Frame (A|B)", "Measurement",
            [Exec, new("primary", VisionDataType.Line2D), new("secondary", VisionDataType.Line2D),
             new("tertiary", VisionDataType.Point2D, Required: false)],
            [Found, new("pose", VisionDataType.Pose2D), new("origin", VisionDataType.Point2D),
             new("primaryAngleDeg", VisionDataType.Double), new("secondaryAngleDeg", VisionDataType.Double),
             new("orthogonalityErrorDeg", VisionDataType.Double), new("originOffset", VisionDataType.Double, Required: false),
             new("datumJson", VisionDataType.String), new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                new("datumName", "Datum Name", "text", "A|B", Group: "Datum",
                    Description: "Label written into datumJson and the overlay, e.g. A|B or A-B."),
                new("maxOrthogonalityErrorDeg", "Max Orthogonality Error (0=off)", "number", 0, 0, 90, .001, Unit: "deg", Group: "Limits",
                    Description: "Gate on how far the secondary datum is from 90 deg to the primary. 0 disables the gate."),
                new("flipAxis", "Flip Axis", "boolean", false, Group: "Datum",
                    Description: "Reverse the +X direction. Fitted lines carry no polarity; set this when the drawing's X points the other way."),
                new("unit", "Unit Label", "text", "px", Group: "Reporting",
                    Description: "Label only, no conversion (1..16 chars)."),
            ],
            "Two-datum reference frame: the origin is the A|B intersection (not a projected point), X follows datum A, and the orthogonality error of B is reported as the datum's own contribution. Wire: primary + secondary (secondary optional) -> pose/origin/orthogonalityErrorDeg. tolerance.datum stays the single-datum case; use this when the drawing names two datums and the origin has to be their intersection. Image-plane (2D) only.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: []),

        // ── P1：几何构造族 ───────────────────────────────────────────
        new("geometry.lineConstruct", "Construct Line", "Geometry",
            [Exec, new("line", VisionDataType.Line2D, Required: false), new("line2", VisionDataType.Line2D, Required: false),
             new("point", VisionDataType.Point2D, Required: false), new("circle", VisionDataType.Circle, Required: false)],
            [Found, new("line", VisionDataType.Line2D, Required: false), new("line2", VisionDataType.Line2D, Required: false),
             new("angleDeg", VisionDataType.Double, Required: false), new("length", VisionDataType.Double, Required: false),
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
                    Description: "x' = x + shear*y applied before rotation. Use it for skewed axes; it keeps the determinant at scaleX*scaleY."),
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
            "Affine (non-rigid) transform between named frames: scale, shear, rotation and translation, with the determinant exposed for downstream validation. coordinate.transformConstant is rigid-only and coordinate.transformCompose rejects non-rigid input, so anisotropic or skewed mapping had no entry point. Pixel-to-mm anisotropy is already covered by calibration.planar's homography; this node is for engineering-frame scaling and skew. A zero scale on either axis fails hard.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: [])
    ];
}
