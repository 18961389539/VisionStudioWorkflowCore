namespace VisionStudio.Engine;

internal static class MetrologyCatalog
{
    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("measure.metrology", "Metrology Model", "Measurement",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image), new("pose", VisionDataType.Pose2D, Required: false)],
            [new("image", VisionDataType.Image), new("found", VisionDataType.Boolean), new("count", VisionDataType.Integer),
                new("foundCount", VisionDataType.Integer), new("measurements", VisionDataType.String), new("results", VisionDataType.MetrologyResults),
                new("residualRms", VisionDataType.Double, Required: false), new("residualMax", VisionDataType.Double, Required: false),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("debugImagePreviewOnly", "Input Image Preview", "boolean", false, Internal: true),
                new("objects", "Measurement Objects (JSON)", "textarea", "[]", Group: "Model", Description:
                    "JSON array of measurement objects. line: {type,rowBegin,columnBegin,rowEnd,columnEnd}; circle: {type,row,column,radius}; " +
                    "ellipse: {type,row,column,phiDeg,radius1,radius2}; rectangle2: {type,row,column,phiDeg,length1,length2}. " +
                    "Optional per object: name, measureLength1, measureLength2, sigma, threshold, transition, select, minScore."),
                new("alignMode", "Alignment", "select", "none", Group: "Alignment", Options:
                    [
                        new("None (geometry is already in image coordinates)", "none"),
                        new("Pose input (dynamic ROI)", "pose"),
                        new("Fixed row/column/angle", "fixed")
                    ], Description: "Rigidly moves every object before measuring. pose expects a Pose2D input (x/column, y/row, theta) such as a match.shape result; fixed uses the three parameters below."),
                new("alignRow", "Fixed Alignment Row", "number", 0, -100000, 100000, 0.1, Unit: "px", Group: "Alignment", VisibleWhen: new("alignMode", ["fixed"]), Description: "Row translation applied to all objects when Alignment = fixed."),
                new("alignColumn", "Fixed Alignment Column", "number", 0, -100000, 100000, 0.1, Unit: "px", Group: "Alignment", VisibleWhen: new("alignMode", ["fixed"]), Description: "Column translation applied to all objects when Alignment = fixed."),
                new("alignAngleDeg", "Fixed Alignment Angle", "number", 0, -180, 180, 0.1, Unit: "deg", Group: "Alignment", VisibleWhen: new("alignMode", ["fixed"]), Description: "Clockwise rotation in image coordinates applied to all objects (and their phi) when Alignment = fixed; pose alignment uses the same image-coordinate convention."),
                new("measureSigma", "Measure Sigma", "number", 1, 0.4, 100, 0.1, Group: "Measure", Description: "Gaussian smoothing sigma of the 1D edge profile. Larger values stabilise noisy edges but bias sharp corners."),
                new("measureThreshold", "Measure Threshold", "number", 30, 1, 255, 1, Group: "Measure", Description: "Minimum edge amplitude (0..255) inside each measure region."),
                new("measureLength1", "Measure Length 1", "number", 20, 1, 10000, 0.5, Unit: "px", Group: "Measure", Description: "Half length of the measure region perpendicular to the object boundary (search depth)."),
                new("measureLength2", "Measure Length 2", "number", 5, 0, 10000, 0.5, Unit: "px", Group: "Measure", Description: "Half width of the measure region along the boundary."),
                new("transition", "Edge Transition", "select", "all", Options:
                    [
                        new("All", "all"),
                        new("Dark to light", "positive"),
                        new("Light to dark", "negative")
                    ], Group: "Measure", Description: "Which edge polarity counts. Use positive/negative on parts with two nearby boundaries (rings, coatings) to avoid mixing them."),
                new("select", "Edge Selection", "select", "all", Options:
                    [
                        new("All", "all"),
                        new("First from start", "first"),
                        new("Last from start", "last")
                    ], Group: "Measure", Description: "Which detected edge inside each measure region feeds the fit."),
                new("minScore", "Minimum Edge Score", "number", 0.3, 0, 1, 0.01, Group: "Measure", Description: "Native minimum fuzzy score for a measure region to contribute."),
                new("requireAllFound", "Require All Objects", "boolean", true, Group: "Result", Description: "true = found requires every object to produce a result; false = found when at least one object succeeds. Per-object results are always returned in measurements.")
            ],
            "One JLVision metrology model with mixed line / circle / ellipse / rectangle2 measurement objects, applied once per image. " +
            "Objects can be rigidly aligned by a pose input (dynamic ROI) or fixed offsets, which is the intended way to measure at a located position. " +
            "Outputs one JSON row per object in image coordinates: circle -> row/column/radius; ellipse -> row/column/phiDeg/radius1/radius2; " +
            "rectangle2 -> row/column/phiDeg/length1/length2; line -> ny/nx/distancePx (Hesse normal form) plus normalAngleDeg. " +
            "Real limits of JLVisionLib 1.0.0: get_metrology_object_result returns a fixed value tuple per object type and no native score, " +
            "so quality is reported as residualPx computed from the native measure points for circle and line only; ellipse/rectangle2 carry no residual. " +
            "Alignment is applied to the object geometry before measuring (AlignMetrologyModel is not used because its pose semantics are undocumented in this wrapper). " +
            "No fuzzy-score map, no per-region rejection list, and no multi-instance (instance='all' only) in this version.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.metrologyResult", "Select Metrology Result", "Measurement",
            [new("exec", VisionDataType.Control), new("results", VisionDataType.MetrologyResults)],
            [new("found", VisionDataType.Boolean), new("count", VisionDataType.Integer), new("name", VisionDataType.String), new("kind", VisionDataType.String),
                new("center", VisionDataType.Point2D, Required: false), new("circle", VisionDataType.Circle, Required: false),
                new("line", VisionDataType.Line2D, Required: false), new("rectangle", VisionDataType.Rectangle2D, Required: false),
                new("pose", VisionDataType.Pose2D, Required: false), new("radius", VisionDataType.Double, Required: false),
                new("residual", VisionDataType.Double, Required: false), new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [new("name", "Object Name", "text", "", Description: "Select by unique authored object name. Empty uses the index."),
                new("index", "Index", "number", 0, 0, 511, 1, VisibleWhen: new("name", [""]), Description: "Zero-based authored object index; failed objects retain their position.")],
            "Select one typed metrology result by name or authored index. Connect circle/line/center/pose directly to downstream measurements. Missing or failed objects return found=false and no geometry. Reject duplicate object indices, lists over 512 objects, and invalid geometry or residuals in a successful selected result. Diagnostics record the selector, matched index/kind and object count.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false))
    ];
}
