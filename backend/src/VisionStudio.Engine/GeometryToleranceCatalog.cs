namespace VisionStudio.Engine;

public static class GeometryToleranceCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);
    private static readonly PortDescriptor Samples = new("sampleCount", VisionDataType.Integer);
    private static readonly ParameterDescriptor Index = new("index", "Zero-based Index", "number", 0, 0, 1999, 1,
        Description: "Which contour in the connected Contour list to measure (0-based). Out of range returns found=false.");
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("contour.fromPoints", "Contour from Points", "Contour", [new("exec", VisionDataType.Control, Required: false), new("pointsJson", VisionDataType.String, Required: false)],
            [new("contours", VisionDataType.Contour), Found, new("count", VisionDataType.Integer), Samples, new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), Next],
            [new("pointsJson", "Contour Points JSON", "textarea", "[[100,100],[200,100],[200,200],[100,200]]", Description: "Fallback [x,y] pairs when pointsJson input is disconnected. Max 2 MiB / 20000 points."),
             new("closed", "Closed Contour", "boolean", true, Description: "Treat the polyline as closed (repeat first point for length/area).")],
            "Create one subpixel contour from [x,y] pairs, preserving point order. Connected JSON overrides the parameter. Empty or singleton input returns found=false. Limits: 2 MiB JSON, 20000 points, finite coordinates within +/-100000000 px. Consecutive duplicates, and repeated endpoints for closed contours, are removed.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.wallThickness", "Circular Wall Thickness", "Measurement", [Exec, new("inner", VisionDataType.Circle), new("outer", VisionDataType.Circle)],
            [Found, new("contained", VisionDataType.Boolean), Samples, new("centerDistance", VisionDataType.Double), new("samplesJson", VisionDataType.String),
             new("minThickness", VisionDataType.Double, Required: false), new("maxThickness", VisionDataType.Double, Required: false), new("meanThickness", VisionDataType.Double, Required: false),
             new("stdDev", VisionDataType.Double, Required: false), new("variation", VisionDataType.Double, Required: false),
             new("cv", VisionDataType.Double, Required: false), new("uniformity", VisionDataType.Double, Required: false),
             new("withinTolerance", VisionDataType.Boolean, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [new("sampleCount", "Wall Samples", "number", 360, 8, 4096, 1, Description: "Number of radial rays from the inner centre (8..4096). Analytic min/max still use the closed-form extrema."),
             new("startAngleDeg", "Wall Start Angle", "number", 0, -3600, 3600, .1, Unit: "deg", Description: "Angle of the first sample ray in degrees (CCW from +X). Remaining rays are spaced evenly over 360°."),
             new("nominalThickness", "Nominal Thickness (0=off)", "number", 0, 0, 1e9, 0.1, Unit: "px", Description: "Expected mean wall thickness. 0 disables the withinTolerance gate."),
             new("tolerance", "Tolerance", "number", 0, 0, 1e9, 0.1, Unit: "px", Description: "Allowed absolute deviation of meanThickness from nominalThickness when the gate is enabled; must be >0 when nominal is set."),
                ],
            "Circular wall thickness from fitted inner/outer circles. Wire: inner + outer Circle → min/max/meanThickness/std/cv; optional nominal/tolerance gate. Key params: sampleCount, startAngleDeg, nominalThickness/tolerance. Inner disk must be contained by outer (else found=false, contained=false). Rejects non-positive outer radius or inner≥outer. Units: pixels. Non-circular walls: use measure.contourThickness.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.concentricity", "Circle Centre Offset", "Measurement", [Exec, new("circleA", VisionDataType.Circle), new("circleB", VisionDataType.Circle)],
            [new("centerDistance", VisionDataType.Double), new("diametralOffset", VisionDataType.Double), new("centerOffset", VisionDataType.Point2D), new("withinTolerance", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("deltaX", VisionDataType.Double), new("deltaY", VisionDataType.Double), new("pass", VisionDataType.Boolean), Next], [new("maxCenterDistance", "Max Centre Distance (0=off)", "number", 0, 0, 1000000, 0.1, Description: "Optional concentricity tolerance in px; withinTolerance reports centreDistance <= limit (always true when off)."),

                ],
            "Planar centre offset between two fitted circles. Wire: circleA + circleB → centerDistance/diametralOffset/centerOffset/withinTolerance. Key params: maxCenterDistance, reportMode. Radii do not affect centre distance. Not 3D coaxility. Empty geometry follows emptyPolicy; out of tolerance → withinTolerance=false. Units: pixels.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.roundness", "Sampled Roundness", "Measurement", [Exec, new("contours", VisionDataType.Contour)],
            [Found, Samples, new("coverageDeg", VisionDataType.Double, Required: false), new("circle", VisionDataType.Circle, Required: false), new("center", VisionDataType.Point2D, Required: false),
             new("radius", VisionDataType.Double, Required: false), new("minRadius", VisionDataType.Double, Required: false), new("maxRadius", VisionDataType.Double, Required: false),
             new("deviation", VisionDataType.Double, Required: false), new("rms", VisionDataType.Double, Required: false),
             new("outOfRoundPct", VisionDataType.Double, Required: false), new("relativeRms", VisionDataType.Double, Required: false),
             new("withinTolerance", VisionDataType.Boolean, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [Index, new("minCoverageDeg", "Minimum Angular Coverage", "number", 270, 0, 360, 1, Description: "Minimum angular span of samples (degrees). Coverage is 360 minus the largest gap; below this returns found=false."),
             new("maxDeviation", "Max Radial Range (0=off)", "number", 0, 0, 1000000, 0.1, Unit: "px", Description: "Optional roundness tolerance; withinTolerance reports (maxRadius-minRadius) <= limit. 0 disables the gate.")],
            "Sampled roundness: JLVision geometric least-squares circle on one contour (≥6 distinct points). Wire: contours → found/circle/radius/deviation/rms/outOfRoundPct; index selects contour; minCoverageDeg gates angular span; optional maxDeviation. Coverage is 360° minus the largest sample gap. Collinear/ill-conditioned samples fail; insufficient coverage → found=false. Depends on contour sampling. Units: pixels.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.straightness", "Sampled Straightness", "Measurement", [Exec, new("contours", VisionDataType.Contour)],
            [Found, Samples, new("line", VisionDataType.Line2D, Required: false), new("deviation", VisionDataType.Double, Required: false), new("rms", VisionDataType.Double, Required: false),
             new("maxAbsError", VisionDataType.Double, Required: false), new("relativeDeviation", VisionDataType.Double, Required: false),
             new("length", VisionDataType.Double, Required: false),
             new("angleDeg", VisionDataType.Double, Required: false),
             new("withinTolerance", VisionDataType.Boolean, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [Index, new("maxDeviation", "Max Straightness Range (0=off)", "number", 0, 0, 1000000, 0.1, Unit: "px", Description: "Optional straightness tolerance; withinTolerance reports residual peak-to-peak range <= limit. 0 disables the gate.")],
            "Sampled straightness: JLVision least-squares line fit on one contour (≥3 distinct points). Wire: contours → found/line/deviation/rms; index selects the contour; optional maxDeviation gate. deviation is peak-to-peak signed perpendicular residual range. Supply one measured edge, not a thick-band closed outline. Degenerate fits fail; too few points → found=false. Units: pixels.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.contourDeviation", "Sampled Contour Deviation", "Measurement", [Exec, new("contours", VisionDataType.Contour), new("referenceContours", VisionDataType.Contour)],
            [Found, Samples, new("samplesJson", VisionDataType.String), new("deviation", VisionDataType.Double, Required: false), new("mean", VisionDataType.Double, Required: false),
             new("rms", VisionDataType.Double, Required: false), new("forwardMax", VisionDataType.Double, Required: false), new("reverseMax", VisionDataType.Double, Required: false), new("withinTolerance", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [new("index", "Zero-based Index", "number", 0, 0, 1999, 1, Group: "Contours",
                 Description: "Which contour in the connected Contour list to measure (0-based). Out of range returns found=false."),
             new("referenceIndex", "Reference Contour Index", "number", 0, 0, 1999, 1, Group: "Contours", Description: "Which contour in referenceContours to compare against (0-based)."),
             new("closed", "Closed Contour", "boolean", true, Group: "Sampling", Description: "Treat both polylines as closed when resampling."),
             new("symmetric", "Symmetric Comparison", "boolean", true, Group: "Sampling", Description: "Also sample reference→current distances and pool them into mean/RMS/deviation."),
             new("sampleStep", "Deviation Sample Step", "number", 2, .01, 10000, .1, Unit: "px", Group: "Sampling", Description: "Arc-length spacing between samples along each polyline."),
             new("maxSamples", "Maximum Samples Per Direction", "number", 2000, 2, 10000, 1, Group: "Sampling", Description: "Hard budget per direction; overflow fails instead of truncating."),
             new("maxDeviation", "Maximum Allowed Deviation", "number", 0, 0, 1e6, .01, Unit: "px", Group: "Limits", Description: "Tolerance gate on max sampled distance; 0 disables. Sets withinTolerance."),
                ],
            "Compare already aligned polylines in the same pixel coordinates. Uniform arc-length samples are measured to reference segments with JLVision. deviation is the maximum sampled distance; mean/RMS pool both directions when symmetric=true. Sample budgets reject overflow rather than truncate. Missing/insufficient contours return found=false. Sampling can miss features between samples.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.parallelism", "Line Parallelism", "Measurement", [Exec, new("a", VisionDataType.Line2D), new("b", VisionDataType.Line2D)],
            [new("angleError", VisionDataType.Double), new("deviation", VisionDataType.Double), new("measuredLength", VisionDataType.Double), new("measuredAngleDeg", VisionDataType.Double), new("withinTolerance", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next], [new("maxAngleErrorDeg", "Max Angle Error deg (0=off)", "number", 0, 0, 90, 0.1, Description: "Optional parallelism tolerance; withinTolerance reports |angleError| <= limit."),
                ],
            "Parallelism of two fitted line segments. Wire: lines a + b → acute angleError from parallel, measured angle, endpoint drift of B perpendicular to A, withinTolerance. Key params: maxAngleErrorDeg. Drift scales with B length; reversing endpoints does not change results. Image-plane geometry only.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.perpendicularity", "Line Perpendicularity", "Measurement", [Exec, new("a", VisionDataType.Line2D), new("b", VisionDataType.Line2D)],
            [new("angleError", VisionDataType.Double), new("deviation", VisionDataType.Double), new("measuredLength", VisionDataType.Double), new("measuredAngleDeg", VisionDataType.Double), new("withinTolerance", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next], [new("maxAngleErrorDeg", "Max Angle Error deg (0=off)", "number", 0, 0, 90, 0.1, Description: "Optional perpendicularity tolerance; withinTolerance reports |angleError| <= limit."),
                ],
            "Perpendicularity of two fitted line segments. Wire: lines a + b → angleError from 90°, measured angle, endpoint drift of B along datum A, withinTolerance. Key params: maxAngleErrorDeg. Geometry-only on supplied fitted lines; reversing endpoints does not change results.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
