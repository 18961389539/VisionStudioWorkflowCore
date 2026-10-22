namespace VisionStudio.Engine;

/// <summary>Image alignment, per-object region measurements and calibration correspondences.</summary>
public static class AlignmentRegionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);
    private static readonly PortDescriptor Count = new("count", VisionDataType.Integer);
    private static readonly PortDescriptor[] RegionOutputs = [Region, new("image", VisionDataType.Image), Count, new("area", VisionDataType.Double), Found, new("diagnostics", VisionDataType.String, Required: false), Next];
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.register", "Register Image", "Matching",
            [Exec, new("image", VisionDataType.Image), new("reference", VisionDataType.Image)],
            [Found, new("score", VisionDataType.Double), new("transform", VisionDataType.Transform2D, Required: false), new("angle", VisionDataType.Double, Required: false), new("center", VisionDataType.Point2D, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [new("x", "Reference Patch X", "number", 0, 0, 32768, 1, Group: "Reference", Description: "Reference patch left X in the reference image (pixels)."), new("y", "Reference Patch Y", "number", 0, 0, 32768, 1, Group: "Reference", Description: "Reference patch top Y in the reference image (pixels)."),
             new("width", "Reference Patch Width (0=full)", "number", 0, 0, 32768, 1, Group: "Reference", Description: "Reference patch width in pixels; 0 uses the rest of the reference width."), new("height", "Reference Patch Height (0=full)", "number", 0, 0, 32768, 1, Group: "Reference", Description: "Reference patch height in pixels; 0 uses the rest of the reference height."),
             new("angleRangeDeg", "Angle Range", "number", 15, 0, 180, 0.1, Group: "Search", Description: "Rotation search half-range in degrees around 0 (0..180)."), new("angleStepDeg", "Angle Step", "number", 1, 0.1, 30, 0.1, Group: "Search", Description: "Rotation search step in degrees (0.1..30)."), new("minScore", "Min Score", "number", 0.7, 0.01, 1, 0.01, Group: "Search", Description: "Minimum NCC score (0..1) for registration to be reported as found."),
             new("subpixelRefine", "Subpixel Refinement", "boolean", false, Group: "Refinement", Description: "Parabolically refine the match translation to subpixel precision. Applied only when the found angle is within 5 degrees (rotated matches keep the integer result)."),


                ],
            "JLVision NCC rigid registration at fixed scale (translation + rotation). Wire: current image + reference image; when found, connect transform to image.warpAffine or align before defect compare. Key params: distinctive reference patch x/y/width/height with search margin; angleRangeDeg/angleStepDeg; minScore; subpixelRefine (near 0 deg only). No continuous scale search. found=false omits transform — gate downstream on found.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("image.warpAffine", "Warp Affine Image", "Preprocess", [Exec, new("image", VisionDataType.Image), new("transform", VisionDataType.Transform2D)],
            [new("image", VisionDataType.Image), new("validRegion", VisionDataType.Region), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [new("width", "Output Width (0=input)", "number", 0, 0, 16384, 1, Description: "Output width in pixels; 0 keeps the input width."), new("height", "Output Height (0=input)", "number", 0, 0, 16384, 1, Description: "Output height in pixels; 0 keeps the input height."),
             new("interpolation", "Interpolation", "select", "linear", Options: [new("Nearest", "nearest"), new("Linear", "linear")], Description: "Resampling: nearest for masks/labels, linear for gray images."),

                ],
            "Apply a nonsingular pixel-to-pixel affine transform with zero border. validRegion excludes unsupported border pixels; intersect it with inspection regions before reference comparison.", "builtin"),
        new("region.morphology", "Region Morphology", "Region", [Exec, Region], RegionOutputs,
            [new("operation", "Operation", "select", "open", Options: [new("Erode", "erode"), new("Dilate", "dilate"), new("Open", "open"), new("Close", "close")],
                Description: "Rectangular morphology mode applied per object (or after unionFirst)."),
             new("kernelWidth", "Kernel Width", "number", 3, 1, 255, 2, Description: "Odd structuring-element width in 1..255."),
             new("kernelHeight", "Kernel Height", "number", 3, 1, 255, 2, Description: "Odd structuring-element height in 1..255."),
             new("unionFirst", "Union Objects First", "boolean", false, Description: "Merge all objects before morphology so the kernel can bridge gaps."),
                new("emptyPolicy", "Empty Region Policy", "select", "empty", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "Behavior when the input region is empty.")],
            "Rectangular morphology per region object, clipped to image bounds (erode/dilate/open/close). Key params: odd kernel sizes; unionFirst merges objects before morphology so the kernel can bridge gaps — then split components explicitly. Oversized kernels merge neighbors.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.properties", "Region Properties", "Region", [Exec, Region],
            [new("propertiesJson", VisionDataType.String), Count, Found, new("area", VisionDataType.Double, Required: false), new("center", VisionDataType.Point2D, Required: false),
             new("orientation", VisionDataType.Double, Required: false), new("perimeter", VisionDataType.Double, Required: false), new("circularity", VisionDataType.Double, Required: false),
             new("rectangularity", VisionDataType.Double, Required: false), new("rectangle", VisionDataType.Rectangle2D, Required: false),
             new("rectWidth", VisionDataType.Double, Required: false), new("rectHeight", VisionDataType.Double, Required: false),
             new("aspectRatio", VisionDataType.Double, Required: false), new("equivalentDiameter", VisionDataType.Double, Required: false),
             new("selectedIndex", VisionDataType.Integer, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [new("index", "Zero-based Index", "number", 0, 0, 9999, 1, Description: "Which region object the scalar outputs describe (0-based)."),
             new("selectBy", "Select By", "select", "index", Options: [new("Index", "index"), new("Largest Area", "largest")], Description: "Which region object the scalar outputs describe: largest area or the index parameter.")],
            "Measure each existing region object, preserving pixel area and holes. propertiesJson contains all objects; scalar ports select index or largest. Empty or out-of-range index returns found=false and omits scalar measurements. Orientation is degrees in image x-right/y-down coordinates. Emits diagnostics.", "builtin"),
        new("region.toDetections", "Region to Detections", "Region", [Exec, Region], [new("detections", VisionDataType.DetectionList), Count, Found, new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("emitted", VisionDataType.Integer), new("truncated", VisionDataType.Boolean), new("totalArea", VisionDataType.Double), Next],
            [new("maxCount", "Max Detections (0=all)", "number", 0, 0, 100000, 1, Description: "Optional cap on emitted detections after measurement order."),
                new("sortBy", "Sort By", "select", "area", Options: [new("Area", "area"), new("X", "x"), new("Y", "y")], Description: "Ordering of emitted detections."),
                new("emptyPolicy", "Empty Result Policy", "select", "empty", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "When the result region is empty: emit empty keeps outputs with found=false; fail stops the graph.")],
            "Convert each existing region object to one detection, preserving pixel area, centroid and bounding geometry. Does not split or reorder objects. Optional maxCount truncates the list. Emits diagnostics. Connect to collection nodes or flow.forEach.", "builtin"),
        new("region.transform", "Transform Region", "Region", [Exec, Region, new("transform", VisionDataType.Transform2D)], RegionOutputs,
            [new("width", "Output Width (0=input)", "number", 0, 0, 16384, 1, Description: "Output canvas width in pixels; 0 keeps the input canvas width."), new("height", "Output Height (0=input)", "number", 0, 0, 16384, 1, Description: "Output canvas height in pixels; 0 keeps the input canvas height.")],
            "Apply the same pixel-to-pixel affine convention as image.warpAffine using nearest-neighbor region rasterization. Preserve object boundaries and holes, clip to output bounds, and remove empty objects.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.lineSpacing", "Line Spacing", "Measurement", [Exec, new("a", VisionDataType.Line2D), new("b", VisionDataType.Line2D)],
            [Found, new("sampleCount", VisionDataType.Integer), new("distance", VisionDataType.Double, Required: false), new("min", VisionDataType.Double, Required: false),
             new("max", VisionDataType.Double, Required: false), new("stdDev", VisionDataType.Double, Required: false), new("deviation", VisionDataType.Double, Required: false),
             new("withinTolerance", VisionDataType.Boolean), new("samplesJson", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [new("sampleCount", "Spacing Samples", "number", 11, 2, 1000, 1, Description: "Number of rays sampled along segment A (each ray is intersected with segment B)."), new("direction", "Spacing Direction", "select", "normal", Options: [new("Normal to A", "normal"), new("Fixed Angle", "angle")], Description: "Ray direction strategy relative to segment A."),
             new("angleDeg", "Sampling Angle (image coordinates)", "number", 90, -180, 180, 0.1, Description: "Explicit ray angle in degrees when direction selects a fixed angle."),
             new("nominalDistance", "Nominal Distance (0=off)", "number", 0, 0, 1e9, 0.1, Unit: "px", Description: "When > 0, compare mean spacing to this nominal and set withinTolerance."),
             new("tolerance", "Tolerance", "number", 0, 0, 1e9, 0.1, Unit: "px", Description: "Allowed absolute deviation from nominalDistance when the gate is enabled."),
                ],
            "Sample segment A and intersect rays in both directions with segment B. Report mean/min/max/population deviation of absolute pixel distances, optional nominal/tolerance gate, overlays for A/B/samples, and diagnostics. Parallel sampling direction is invalid; no overlap returns found=false. Not an image edge detector.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("calibration.detectBoard", "Detect Calibration Board", "Calibration", [Exec, new("image", VisionDataType.Image), new("calibrationDataJson", VisionDataType.String, Required: false)],
            [Found, Count, new("image", VisionDataType.Image), new("viewJson", VisionDataType.String, Required: false), new("calibrationDataJson", VisionDataType.String),
             new("blurVariance", VisionDataType.Double), new("coverageX", VisionDataType.Double, Required: false), new("coverageY", VisionDataType.Double, Required: false), new("quality", VisionDataType.String, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [new("pattern", "Board Pattern", "select", "chessboard", Group: "Board", Options: [new("Chessboard", "chessboard"), new("Symmetric Circle Grid", "circles")], Description: "Calibration board pattern: chessboard inner corners or a symmetric circle grid."),
             new("columns", "Board Columns", "number", 7, 2, 30, 1, Group: "Board", Description: "Board columns (inner corners or circle centers)."), new("rows", "Board Rows", "number", 6, 2, 30, 1, Group: "Board", Description: "Board rows (inner corners or circle centers)."), new("spacingMm", "Board Spacing (mm)", "number", 10, 0.001, 10000, 0.1, Group: "Board", Description: "Physical spacing between adjacent board features in millimetres."),
             new("threshold", "Threshold", "number", 128, 0, 255, 1, Group: "Detection", Description: "Binarization threshold for circle-grid dots; chessboard detection does not use it.") { VisibleWhen = new("pattern", ["circles"]) },
             new("darkObjects", "Dark Objects", "boolean", true, Group: "Detection", Description: "True detects dark dots on a bright background; false detects bright dots on a dark background.") { VisibleWhen = new("pattern", ["circles"]) },
             new("minArea", "Minimum Dot Area", "number", 20, 1, 999999, 1, Group: "Detection", Description: "Minimum accepted dot area in pixels.") { VisibleWhen = new("pattern", ["circles"]) },
             new("maxArea", "Maximum Dot Area", "number", 10000, 1, 99999999, 1, Group: "Detection", Description: "Maximum accepted dot area in pixels; must be at least the minimum dot area.") { VisibleWhen = new("pattern", ["circles"]) },
             new("minCircularity", "Min Circularity", "number", 0.6, 0, 1, 0.01, Group: "Detection", Description: "Minimum circularity in 0..1.") { VisibleWhen = new("pattern", ["circles"]) },
             new("maxGridError", "Maximum Grid Error (px)", "number", 2, 0.1, 20, 0.1, Group: "Lattice", Description: "Maximum lattice fit error in pixels for either board pattern."),
             new("saddleThreshold", "Saddle Contrast", "number", 10, 0.1, 255, 0.1, Group: "Lattice", Description: "Saddle/corner response threshold; raise it to reject weak corners, lower it for low-contrast boards.") { VisibleWhen = new("pattern", ["chessboard"]) },
             new("minBlurVariance", "Min Blur Variance (0=off)", "number", 0, 0, 1e6, 1, Group: "Quality", VisibleWhen: new("rejectWeakViews", ["true"]), Description: "Laplacian variance below this value makes the view quality Fail; rejectWeakViews decides whether it is appended."), new("minCoverage", "Min Coverage Fraction (0=off)", "number", 0, 0, 1, 0.01, Group: "Quality", VisibleWhen: new("rejectWeakViews", ["true"]), Description: "Minimum board coverage fraction on both axes before the view quality becomes Fail; rejectWeakViews decides whether it is appended."),
             new("rejectWeakViews", "Reject Weak Views", "boolean", false, Group: "Quality", Description: "When true, views with a Fail quality verdict are not appended to calibrationDataJson."),
                ],
            "Extract a complete isolated chessboard inner-corner or symmetric circle grid with JLVision. Chessboard detection uses saddleThreshold; circle grids use threshold, darkObjects, area and circularity limits. Reports blurVariance (Laplacian), coverage, and a Pass/Warn/Fail quality verdict with guidance. minBlurVariance/minCoverage define failure; rejectWeakViews decides whether failed views are skipped. Append up to 100 views into calibrationDataJson for cameraIntrinsics or the Calibration Wizard.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
