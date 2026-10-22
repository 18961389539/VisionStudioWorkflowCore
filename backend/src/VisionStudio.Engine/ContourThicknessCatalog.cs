namespace VisionStudio.Engine;

internal static class ContourThicknessCatalog
{
    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("measure.contourThickness", "Contour Wall Thickness", "Measurement",
            [new("exec", VisionDataType.Control), new("innerContours", VisionDataType.Contour), new("outerContours", VisionDataType.Contour),
                new("image", VisionDataType.Image, Required: false)],
            [new("found", VisionDataType.Boolean), new("contained", VisionDataType.Boolean), new("sampleCount", VisionDataType.Integer),
                new("samplesJson", VisionDataType.String), new("withinTolerance", VisionDataType.Boolean, Required: false),
                new("minThickness", VisionDataType.Double, Required: false), new("maxThickness", VisionDataType.Double, Required: false),
                new("meanThickness", VisionDataType.Double, Required: false), new("stdDev", VisionDataType.Double, Required: false),
                new("variation", VisionDataType.Double, Required: false), new("thinSampleCount", VisionDataType.Integer, Required: false),
                new("minInnerPoint", VisionDataType.Point2D, Required: false), new("minOuterPoint", VisionDataType.Point2D, Required: false),
                new("minLine", VisionDataType.Line2D, Required: false), new("image", VisionDataType.Image, Required: false),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("next", VisionDataType.Control)],
            [new("innerIndex", "Inner Contour Index", "number", 0, 0, 1999, 1, Group: "Contours", Description: "Zero-based index into the innerContours list; out of range yields found=false."),
                new("outerIndex", "Outer Contour Index", "number", 0, 0, 1999, 1, Group: "Contours", Description: "Zero-based index into the outerContours list; must be a closed contour in the same pixel frame."),
                new("measurementMode", "Thickness Measurement Mode", "select", "shortest", Group: "Sampling", Options:
                    [new("Shortest contour distance", "shortest"), new("Inner contour normal", "innernormal")],
                    Description: "shortest = global min segment distance; innernormal = outward normals from the inner boundary."),
                new("sampleStep", "Thickness Sample Step", "number", 2, .1, 1000000, .1, Unit: "px", Group: "Sampling", Description: "Target arc spacing along the sampled boundary; original vertices are always kept."),
                new("maxSamplesPerBoundary", "Maximum Samples Per Boundary", "number", 4096, 8, 4096, 1, Group: "Sampling", Description: "Hard sample budget per measured boundary (integer 8..4096)."),
                new("minThickness", "Minimum Wall Thickness", "number", 0, 0, 100000000, .1, Unit: "px", Group: "Limits", Description: "withinTolerance=false when any sample falls below this; 0 disables the lower gate."),
                new("maxThickness", "Maximum Wall Thickness (0=unlimited)", "number", 0, 0, 100000000, .1, Unit: "px", Group: "Limits", Description: "Upper thickness gate; 0 disables. Must be >= minThickness when set.")],
            "Measure two simple closed subpixel contours in the same image pixel frame, with the inner boundary strictly inside the outer. Shortest mode samples the outer boundary to inner contour segments and provides an exact segment-to-segment global minimum over both complete contours. Normal mode samples outward inner-boundary normals (vertex bisectors at corners) and provides sampled extrema. Connections crossing the hole or leaving the outer shell are rejected. Every original vertex of the sampled boundary is retained; extra samples limit arc spacing. Means/std dev are arc-length weighted; maxima and thin counts are sampled. Missing/open/self-intersecting/touching/noncontained contours, unreliable pairs or sampling/work budget overflow return found=false and omit measurement values. Limits: 20000 points per contour, 4096 samples per measured boundary and 50 million geometry operations. Optional image passes through for overlay inspection. Red lines mark sampled tolerance failures. Units: px.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
