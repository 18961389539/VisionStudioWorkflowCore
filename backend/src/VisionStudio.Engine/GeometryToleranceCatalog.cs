namespace VisionStudio.Engine;

public static class GeometryToleranceCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);
    private static readonly PortDescriptor Samples = new("sampleCount", VisionDataType.Integer);
    private static readonly ParameterDescriptor Index = new("index", "Zero-based Index", "number", 0, 0, 1999, 1);
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("contour.fromPoints", "Contour from Points", "Contour", [new("exec", VisionDataType.Control, Required: false), new("pointsJson", VisionDataType.String, Required: false)],
            [new("contours", VisionDataType.Contour), Found, new("count", VisionDataType.Integer), Samples, Next],
            [new("pointsJson", "Contour Points JSON", "textarea", "[[100,100],[200,100],[200,200],[100,200]]"), new("closed", "Closed Contour", "boolean", true)],
            "Create one subpixel contour from [x,y] pairs, preserving point order. Connected JSON overrides the parameter. Empty or singleton input returns found=false. Limits: 2 MiB JSON, 20000 points, finite coordinates within +/-100000000 px. Consecutive duplicates, and repeated endpoints for closed contours, are removed.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.wallThickness", "Circular Wall Thickness", "Measurement", [Exec, new("inner", VisionDataType.Circle), new("outer", VisionDataType.Circle)],
            [Found, new("contained", VisionDataType.Boolean), Samples, new("centerDistance", VisionDataType.Double), new("samplesJson", VisionDataType.String),
             new("minThickness", VisionDataType.Double, Required: false), new("maxThickness", VisionDataType.Double, Required: false), new("meanThickness", VisionDataType.Double, Required: false),
             new("stdDev", VisionDataType.Double, Required: false), new("variation", VisionDataType.Double, Required: false), Next],
            [new("sampleCount", "Wall Samples", "number", 360, 8, 4096, 1), new("startAngleDeg", "Wall Start Angle", "number", 0, -3600, 3600, .1)],
            "Circular wall model from fitted inner/outer circles. Sample radial rays from the inner centre; min/max are analytic extrema, mean/deviation use samples. The inner disk must be contained by the outer disk. Noncontainment returns found=false and omits thickness values. Units are pixels.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.concentricity", "Circle Centre Offset", "Measurement", [Exec, new("circleA", VisionDataType.Circle), new("circleB", VisionDataType.Circle)],
            [new("centerDistance", VisionDataType.Double), new("diametralOffset", VisionDataType.Double), new("centerOffset", VisionDataType.Point2D), Next], [],
            "Two-dimensional offset between fitted circle centres. Return centre distance, twice that distance and the A-to-B offset vector, all in image pixels. Radii do not affect the centre-offset calculation.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.roundness", "Sampled Roundness", "Measurement", [Exec, new("contours", VisionDataType.Contour)],
            [Found, Samples, new("coverageDeg", VisionDataType.Double, Required: false), new("circle", VisionDataType.Circle, Required: false), new("center", VisionDataType.Point2D, Required: false),
             new("radius", VisionDataType.Double, Required: false), new("minRadius", VisionDataType.Double, Required: false), new("maxRadius", VisionDataType.Double, Required: false),
             new("deviation", VisionDataType.Double, Required: false), new("rms", VisionDataType.Double, Required: false), Next],
            [Index, new("minCoverageDeg", "Minimum Angular Coverage", "number", 270, 0, 360, 1)],
            "Fit a geometric least-squares circle with JLVision to one contour (at least six distinct points). Return max-minus-min sampled radii and RMS radial residual. Angular coverage is 360 minus the largest sample gap. Insufficient points, missing index or coverage below the limit returns found=false. Results depend on supplied contour sampling.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.straightness", "Sampled Straightness", "Measurement", [Exec, new("contours", VisionDataType.Contour)],
            [Found, Samples, new("line", VisionDataType.Line2D, Required: false), new("deviation", VisionDataType.Double, Required: false), new("rms", VisionDataType.Double, Required: false),
             new("maxAbsError", VisionDataType.Double, Required: false), Next], [Index],
            "Fit a least-squares line with JLVision to one contour (at least three distinct points). deviation is the range of signed perpendicular residuals; rms and maxAbsError are relative to that fitted line. Supply one measured edge rather than the closed outline of a thick band. Units are pixels.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.contourDeviation", "Sampled Contour Deviation", "Measurement", [Exec, new("contours", VisionDataType.Contour), new("referenceContours", VisionDataType.Contour)],
            [Found, Samples, new("samplesJson", VisionDataType.String), new("deviation", VisionDataType.Double, Required: false), new("mean", VisionDataType.Double, Required: false),
             new("rms", VisionDataType.Double, Required: false), new("forwardMax", VisionDataType.Double, Required: false), new("reverseMax", VisionDataType.Double, Required: false), Next],
            [Index, new("referenceIndex", "Reference Contour Index", "number", 0, 0, 1999, 1), new("closed", "Closed Contour", "boolean", true),
             new("symmetric", "Symmetric Comparison", "boolean", true), new("sampleStep", "Deviation Sample Step", "number", 2, .01, 10000, .1), new("maxSamples", "Maximum Samples Per Direction", "number", 2000, 2, 10000, 1)],
            "Compare already aligned polylines in the same pixel coordinates. Uniform arc-length samples are measured to reference segments with JLVision. deviation is the maximum sampled distance; mean/RMS pool both directions when symmetric=true. Sample budgets reject overflow rather than truncate. Missing/insufficient contours return found=false. Sampling can miss features between samples.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.parallelism", "Line Parallelism", "Measurement", [Exec, new("a", VisionDataType.Line2D), new("b", VisionDataType.Line2D)],
            [new("angleError", VisionDataType.Double), new("deviation", VisionDataType.Double), new("measuredLength", VisionDataType.Double), Next], [],
            "For two fitted line segments, report the acute angular error from parallel (degrees) and the endpoint drift of B perpendicular to datum A (pixels). Drift depends on B length. Endpoint reversal does not change results.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.perpendicularity", "Line Perpendicularity", "Measurement", [Exec, new("a", VisionDataType.Line2D), new("b", VisionDataType.Line2D)],
            [new("angleError", VisionDataType.Double), new("deviation", VisionDataType.Double), new("measuredLength", VisionDataType.Double), Next], [],
            "For two fitted line segments, report angular error from 90 degrees and endpoint drift of B along datum A (pixels). Drift depends on B length. Endpoint reversal does not change results. Geometry-only indicators use the supplied fitted lines.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
