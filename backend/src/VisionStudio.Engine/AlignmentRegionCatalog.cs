namespace VisionStudio.Engine;

/// <summary>Image alignment, per-object region measurements and calibration correspondences.</summary>
public static class AlignmentRegionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);
    private static readonly PortDescriptor Count = new("count", VisionDataType.Integer);
    private static readonly PortDescriptor[] RegionOutputs = [Region, new("image", VisionDataType.Image), Count, new("area", VisionDataType.Double), Found, Next];
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.register", "Register Image", "Matching",
            [Exec, new("image", VisionDataType.Image), new("reference", VisionDataType.Image)],
            [Found, new("score", VisionDataType.Double), new("transform", VisionDataType.Transform2D, Required: false), new("angle", VisionDataType.Double, Required: false), new("center", VisionDataType.Point2D, Required: false), Next],
            [new("x", "Reference Patch X", "number", 0, 0, 32768, 1), new("y", "Reference Patch Y", "number", 0, 0, 32768, 1),
             new("width", "Reference Patch Width (0=full)", "number", 0, 0, 32768, 1), new("height", "Reference Patch Height (0=full)", "number", 0, 0, 32768, 1),
             new("angleRangeDeg", "Angle Range", "number", 15, 0, 180, 0.1), new("angleStepDeg", "Angle Step", "number", 1, 0.1, 30, 0.1), new("minScore", "Min Score", "number", 0.7, 0.01, 1, 0.01),
             new("subpixelRefine", "Subpixel Refinement", "boolean", false, Description: "Parabolically refine the match translation to subpixel precision. Applied only when the found angle is within 5 degrees (rotated matches keep the integer result).")],
            "NCC rigid registration at fixed scale. Select a distinctive reference patch with margin in the scene. Outputs current-pixel to reference-pixel transform, match angle and patch centre only when found; gate downstream warp on found. Optional subpixel refinement improves translation for near-axis-aligned matches.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("image.warpAffine", "Warp Affine Image", "Preprocess", [Exec, new("image", VisionDataType.Image), new("transform", VisionDataType.Transform2D)],
            [new("image", VisionDataType.Image), new("validRegion", VisionDataType.Region), Next],
            [new("width", "Output Width (0=input)", "number", 0, 0, 16384, 1), new("height", "Output Height (0=input)", "number", 0, 0, 16384, 1),
             new("interpolation", "Interpolation", "select", "linear", Options: [new("Nearest", "nearest"), new("Linear", "linear")])],
            "Apply a nonsingular pixel-to-pixel affine transform with zero border. validRegion excludes unsupported border pixels; intersect it with inspection regions before reference comparison.", "builtin"),
        new("region.morphology", "Region Morphology", "Region", [Exec, Region], RegionOutputs,
            [new("operation", "Operation", "select", "open", Options: [new("Erode", "erode"), new("Dilate", "dilate"), new("Open", "open"), new("Close", "close")]),
             new("kernelWidth", "Kernel Width", "number", 3, 1, 255, 2), new("kernelHeight", "Kernel Height", "number", 3, 1, 255, 2), new("unionFirst", "Union Objects First", "boolean", false)],
            "Rectangular morphology per object, clipped to image bounds. Enable unionFirst to bridge different objects, then split connected components explicitly. Kernel dimensions must be odd.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.properties", "Region Properties", "Region", [Exec, Region],
            [new("propertiesJson", VisionDataType.String), Count, Found, new("area", VisionDataType.Double, Required: false), new("center", VisionDataType.Point2D, Required: false),
             new("orientation", VisionDataType.Double, Required: false), new("perimeter", VisionDataType.Double, Required: false), new("circularity", VisionDataType.Double, Required: false),
             new("rectangularity", VisionDataType.Double, Required: false), new("rectangle", VisionDataType.Rectangle2D, Required: false), Next],
            [new("index", "Zero-based Index", "number", 0, 0, 9999, 1)],
            "Measure each existing region object, preserving pixel area and holes. propertiesJson contains all objects; scalar ports select index. Empty or out-of-range index returns found=false and omits scalar measurements. Orientation is degrees in image x-right/y-down coordinates.", "builtin"),
        new("region.toDetections", "Region to Detections", "Region", [Exec, Region], [new("detections", VisionDataType.DetectionList), Count, Found, Next], [],
            "Convert each existing region object to one detection, preserving pixel area, centroid and bounding geometry. Does not split or reorder objects. Connect to collection nodes or flow.forEach.", "builtin"),
        new("region.transform", "Transform Region", "Region", [Exec, Region, new("transform", VisionDataType.Transform2D)], RegionOutputs,
            [new("width", "Output Width (0=input)", "number", 0, 0, 16384, 1), new("height", "Output Height (0=input)", "number", 0, 0, 16384, 1)],
            "Apply the same pixel-to-pixel affine convention as image.warpAffine using nearest-neighbor region rasterization. Preserve object boundaries and holes, clip to output bounds, and remove empty objects.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.lineSpacing", "Line Spacing", "Measurement", [Exec, new("a", VisionDataType.Line2D), new("b", VisionDataType.Line2D)],
            [Found, new("sampleCount", VisionDataType.Integer), new("distance", VisionDataType.Double, Required: false), new("min", VisionDataType.Double, Required: false),
             new("max", VisionDataType.Double, Required: false), new("stdDev", VisionDataType.Double, Required: false), new("samplesJson", VisionDataType.String), Next],
            [new("sampleCount", "Spacing Samples", "number", 11, 2, 1000, 1), new("direction", "Spacing Direction", "select", "normal", Options: [new("Normal to A", "normal"), new("Fixed Angle", "angle")]),
             new("angleDeg", "Sampling Angle (image coordinates)", "number", 90, -180, 180, 0.1)],
            "Sample segment A and intersect rays in both directions with segment B. Report mean/min/max/population deviation of absolute pixel distances. Parallel sampling direction is invalid; no overlap returns found=false. Not an image edge detector.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("calibration.detectBoard", "Detect Calibration Board", "Calibration", [Exec, new("image", VisionDataType.Image), new("calibrationDataJson", VisionDataType.String, Required: false)],
            [Found, Count, new("image", VisionDataType.Image), new("viewJson", VisionDataType.String, Required: false), new("calibrationDataJson", VisionDataType.String), Next],
            [new("pattern", "Board Pattern", "select", "chessboard", Options: [new("Chessboard", "chessboard"), new("Symmetric Circle Grid", "circles")]),
             new("columns", "Board Columns", "number", 7, 2, 30, 1), new("rows", "Board Rows", "number", 6, 2, 30, 1), new("spacingMm", "Board Spacing (mm)", "number", 10, 0.001, 10000, 0.1),
             new("threshold", "Threshold", "number", 128, 0, 255, 1), new("darkObjects", "Dark Objects", "boolean", true), new("minArea", "Minimum Area", "number", 20, 1, 999999, 1),
             new("maxArea", "Maximum Dot Area", "number", 10000, 1, 99999999, 1), new("minCircularity", "Min Circularity", "number", 0.6, 0, 1, 0.01),
             new("maxGridError", "Maximum Grid Error (px)", "number", 2, 0.1, 20, 0.1), new("saddleThreshold", "Saddle Contrast", "number", 10, 0.1, 255, 0.1)],
            "Extract a complete isolated chessboard inner-corner or symmetric circle grid with JLVision. Require exactly rows*columns candidates and a consistent projective lattice. Append a view to optional calibrationDataJson (maximum 100 views), then connect it to cameraIntrinsics. Use varied views; missing boards return found=false without appending.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
