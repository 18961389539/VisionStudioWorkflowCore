using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>
/// 第五批（2026-10-14 精密定位与测量）：轮廓（5A）→ 亚像素测量（5B）→ 形状定位与动态 ROI（5C）。
/// 命名实施前已核对无重复：既有 measure.caliper 为整数像素单点卡尺、measure.blobs 为
/// 外轮廓一步筛选、feature.circle 为霍夫整圆、match.template 为灰度模板匹配；
/// 本批分别为轮廓层级化、亚像素边缘/卡尺线/卡尺圆/椭圆拟合、签名式形状匹配与 ROI 刚体修正。
/// </summary>
public static class Batch5PrecisionCatalog
{
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        // ===== 5A 轮廓族 =====
        new(
            "contour.extract",
            "Contour Extract",
            "Contour",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image), new("roi", VisionDataType.Roi, Required: false)],
            [
                new("image", VisionDataType.Image),
                new("contours", VisionDataType.Contour),
                new("count", VisionDataType.Integer),
                new("found", VisionDataType.Boolean),
                new("next", VisionDataType.Control)
            ],
            [
                new("mode", "Retrieval", "select", "external", Options:
                [
                    new("External only", "external"),
                    new("External + holes", "all"),
                    new("Holes only", "holes")
                ], Group: "Retrieval"),
                new("binarize", "Binarize", "select", "auto", Options:
                [
                    new("Otsu (auto)", "auto"),
                    new("Fixed threshold", "fixed"),
                    new("Non-zero pixels", "direct")
                ], Group: "Binarize",
                    Description: "auto=Otsu; fixed uses Threshold; direct treats every non-zero pixel as foreground."),
                new("threshold", "Threshold", "number", 128, 0, 255, 1, Group: "Binarize", Description: "Used when Binarize = fixed threshold."),
                new("invert", "Dark Objects", "boolean", false, Group: "Binarize", Description: "Treat dark-on-light as foreground."),
                new("minArea", "Min Area", "number", 0, 0, 99999999, 1, Unit: "px²", Group: "Filter"),
                new("maxCount", "Max Contours", "number", 200, 1, 2000, 1, Group: "Filter"),
                new("maxPoints", "Point Budget", "number", 2000, 16, 20000, 1, Group: "Filter",
                    Description: "Contours are evenly subsampled to this budget so graph data stays bounded.")
            ],
            "Extract contours with hierarchy (external/holes). Returns a Contour list; an empty result is found=false, not a failure. Binarize before extraction or rely on Otsu.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "contour.filter",
            "Contour Filter",
            "Contour",
            [new("exec", VisionDataType.Control), new("contours", VisionDataType.Contour)],
            [
                new("contours", VisionDataType.Contour),
                new("count", VisionDataType.Integer),
                new("next", VisionDataType.Control)
            ],
            [
                new("minArea", "Min Area", "number", 0, 0, 99999999, 1, Unit: "px²", Group: "Geometry"),
                new("maxArea", "Max Area (0=unlimited)", "number", 0, 0, 99999999, 1, Unit: "px²", Group: "Geometry"),
                new("minCircularity", "Min Circularity", "number", 0, 0, 1, 0.01, Group: "Geometry"),
                new("maxCircularity", "Max Circularity", "number", 1, 0, 1, 0.01, Group: "Geometry"),
                new("hierarchy", "Hierarchy", "select", "any", Options:
                [
                    new("Any", "any"),
                    new("External only", "external"),
                    new("Holes only", "holes")
                ], Group: "Hierarchy"),
                new("windowX", "Window X (-1=off)", "number", -1, -1, 100000, 1, Unit: "px", Group: "Position"),
                new("windowY", "Window Y (-1=off)", "number", -1, -1, 100000, 1, Unit: "px", Group: "Position"),
                new("windowWidth", "Window Width", "number", 0, 0, 100000, 1, Unit: "px", Group: "Position"),
                new("windowHeight", "Window Height", "number", 0, 0, 100000, 1, Unit: "px", Group: "Position"),
                new("maxCount", "Max Contours", "number", 200, 1, 2000, 1, Group: "Limit")
            ],
            "Filter an existing Contour list by area, circularity, hierarchy and a position window (bounding-box center inside the window). Composable with contour.extract; no re-extraction.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "contour.properties",
            "Contour Properties",
            "Contour",
            [new("exec", VisionDataType.Control), new("contours", VisionDataType.Contour)],
            [
                new("properties", VisionDataType.ContourProperties),
                new("detections", VisionDataType.DetectionList),
                new("count", VisionDataType.Integer),
                new("selected", VisionDataType.Integer),
                new("center", VisionDataType.Point2D),
                new("circle", VisionDataType.Circle),
                new("rectangle", VisionDataType.Rectangle2D),
                new("pose", VisionDataType.Pose2D),
                new("area", VisionDataType.Double),
                new("circularity", VisionDataType.Double),
                new("next", VisionDataType.Control)
            ],
            [
                new("selectBy", "Select", "select", "largest", Options:
                [
                    new("Largest area", "largest"),
                    new("Smallest area", "smallest"),
                    new("First", "first"),
                    new("Last", "last"),
                    new("By index", "index")
                ], Group: "Selection"),
                new("index", "Index", "number", 0, 0, 100000, 1, Group: "Selection", Description: "Used when Select = by index (clamped).")
            ],
            "Compute derived contour geometry (centroid, min-area rotated rectangle, solidity, extent, aspect ratio, equivalent diameter) and emit a DetectionList for the collection/loop nodes. Empty input is an error.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),

        // ===== 5B 亚像素测量族 =====
        new(
            "feature.edgeSubpixel",
            "Subpixel Edge",
            "Feature",
            [
                new("exec", VisionDataType.Control),
                new("image", VisionDataType.Image),
                new("searchLine", VisionDataType.Line2D, Required: false),
                new("roi", VisionDataType.Roi, Required: false)
            ],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("point", VisionDataType.Point2D, Required: false),
                new("strength", VisionDataType.Double, Required: false),
                new("fitError", VisionDataType.Double, Required: false),
                new("sampleCount", VisionDataType.Integer, Required: false),
                new("searchLine", VisionDataType.Line2D),
                new("next", VisionDataType.Control)
            ],
            [
                new("startX", "Start X", "number", 120, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("startY", "Start Y", "number", 240, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("endX", "End X", "number", 520, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("endY", "End Y", "number", 240, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("sampleWidth", "Sample Width", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling"),
                new("minContrast", "Min Contrast", "number", 8, 0, 255, 1, Unit: "gray", Group: "Sampling"),
                new("polarity", "Polarity", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Sampling"),
                new("direction", "Direction", "select", "strongest", Options:
                [
                    new("Strongest", "strongest"),
                    new("First from start", "firstfromstart"),
                    new("First from end", "firstfromend")
                ], Group: "Sampling"),
                new("refine", "Refine", "select", "parabolic", Options:
                [
                    new("Parabolic (subpixel)", "parabolic"),
                    new("None (integer)", "none")
                ], Group: "Sampling")
            ],
            "Subpixel edge location along a search line: parabolic refinement on the gradient peak with fit error and valid sample count. No edge above the contrast threshold returns found=false, not a failure.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "measure.caliperLine",
            "Caliper Line",
            "Measurement",
            [
                new("exec", VisionDataType.Control),
                new("image", VisionDataType.Image),
                new("searchLine", VisionDataType.Line2D, Required: false),
                new("roi", VisionDataType.Roi, Required: false)
            ],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("point", VisionDataType.Point2D, Required: false),
                new("line", VisionDataType.Line2D, Required: false),
                new("strength", VisionDataType.Double, Required: false),
                new("fitError", VisionDataType.Double, Required: false),
                new("sampleCount", VisionDataType.Integer, Required: false),
                new("searchLine", VisionDataType.Line2D),
                new("next", VisionDataType.Control)
            ],
            [
                new("startX", "Start X", "number", 120, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("startY", "Start Y", "number", 240, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("endX", "End X", "number", 520, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("endY", "End Y", "number", 240, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("sampleWidth", "Sample Width", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling"),
                new("minContrast", "Min Contrast", "number", 8, 0, 255, 1, Unit: "gray", Group: "Sampling"),
                new("polarity", "Polarity", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Sampling"),
                new("direction", "Direction", "select", "strongest", Options:
                [
                    new("Strongest", "strongest"),
                    new("First from start", "firstfromstart"),
                    new("First from end", "firstfromend")
                ], Group: "Sampling"),
                new("refine", "Refine", "select", "parabolic", Options:
                [
                    new("Parabolic (subpixel)", "parabolic"),
                    new("None (integer)", "none")
                ], Group: "Sampling"),
                new("edgeLength", "Caliper Span (0=auto)", "number", 0, 0, 10000, 1, Unit: "px", Group: "Sampling", Description: "Spread of parallel profiles perpendicular to the search line; zero uses the search line length."),
                new("caliperCount", "Caliper Count", "number", 9, 3, 101, 2, Group: "Sampling", Description: "Number of parallel search profiles along the edge span; at least 3 valid edge points are required for a line fit.")
            ],
            "Caliper line measurement: detects edges on multiple parallel profiles across the configured edge span, then fits the measured edge points to a line with orthogonal RMS fit error. At least three valid points are required.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "measure.caliperCircle",
            "Caliper Circle",
            "Measurement",
            [
                new("exec", VisionDataType.Control),
                new("image", VisionDataType.Image),
                new("circle", VisionDataType.Circle, Required: false),
                new("roi", VisionDataType.Roi, Required: false)
            ],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("circle", VisionDataType.Circle, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("radius", VisionDataType.Double, Required: false),
                new("radiusMin", VisionDataType.Double, Required: false),
                new("radiusMax", VisionDataType.Double, Required: false),
                new("fitError", VisionDataType.Double, Required: false),
                new("sampleCount", VisionDataType.Integer, Required: false),
                new("next", VisionDataType.Control)
            ],
            [
                new("centerX", "Center X", "number", 320, 0, 10000, 1, Unit: "px", Group: "Nominal Circle"),
                new("centerY", "Center Y", "number", 240, 0, 10000, 1, Unit: "px", Group: "Nominal Circle"),
                new("radius", "Nominal Radius", "number", 100, 1, 5000, 1, Unit: "px", Group: "Nominal Circle"),
                new("angleStartDeg", "Angle Start", "number", -180, -360, 360, 1, Unit: "deg", Group: "Arc"),
                new("angleEndDeg", "Angle End", "number", 180, -360, 360, 1, Unit: "deg", Group: "Arc"),
                new("angleStepDeg", "Angle Step", "number", 5, 0.5, 45, 0.5, Unit: "deg", Group: "Arc"),
                new("radialBand", "Radial Band", "number", 8, 1, 500, 1, Unit: "px", Group: "Sampling"),
                new("minContrast", "Min Contrast", "number", 8, 0, 255, 1, Unit: "gray", Group: "Sampling"),
                new("polarity", "Polarity", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Sampling"),
                new("sampleWidth", "Sample Width", "number", 3, 1, 99, 2, Unit: "px", Group: "Sampling"),
                new("maxRadiusDeviation", "Max Radius Deviation (0=off)", "number", 0, 0, 5000, 1, Unit: "px", Group: "Sampling",
                    Description: "Reject spokes whose found edge deviates more than this from the nominal radius.")
            ],
            "Caliper circle measurement: radial caliper spokes around a nominal circle, least-squares circle fit over the valid edge points, with roundness (radius spread) and fit error. The optional circle input overrides the nominal parameters (use it with roi.transform for workpiece-following measurement).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "measure.fitEllipse",
            "Ellipse Fit",
            "Measurement",
            [new("exec", VisionDataType.Control), new("contours", VisionDataType.Contour)],
            [
                new("contours", VisionDataType.Contour),
                new("rectangle", VisionDataType.Rectangle2D),
                new("center", VisionDataType.Point2D),
                new("pose", VisionDataType.Pose2D),
                new("width", VisionDataType.Double),
                new("height", VisionDataType.Double),
                new("angle", VisionDataType.Double),
                new("count", VisionDataType.Integer),
                new("next", VisionDataType.Control)
            ],
            [
                new("selectBy", "Select", "select", "largest", Options:
                [
                    new("Largest area", "largest"),
                    new("Smallest area", "smallest"),
                    new("First", "first"),
                    new("Last", "last"),
                    new("By index", "index")
                ], Group: "Selection"),
                new("index", "Index", "number", 0, 0, 100000, 1, Group: "Selection", Description: "Used when Select = by index (clamped).")
            ],
            "Fit an ellipse to a contour (needs >= 5 non-collinear points). Output is the rotated rectangle of the ellipse: width = major axis, height = minor axis, angle = major-axis direction (degrees, CCW).",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),

        // ===== 5C 形状定位与动态 ROI =====
        new(
            "match.shape",
            "Shape Match",
            "Matching",
            [
                new("exec", VisionDataType.Control),
                new("image", VisionDataType.Image),
                new("template", VisionDataType.Image, Required: false)
            ],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("score", VisionDataType.Double),
                new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false),
                new("angle", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("pose", VisionDataType.Pose2D, Required: false),
                new("rectangle", VisionDataType.Rectangle2D, Required: false),
                new("detections", VisionDataType.DetectionList, Required: false),
                new("count", VisionDataType.Integer, Required: false),
                new("next", VisionDataType.Control)
            ],
            [
                new("templatePath", "Template Path", "text", "", Description: "Used only when the template port is not connected."),
                new("binarize", "Binarize", "select", "auto", Options:
                [
                    new("Otsu (auto)", "auto"),
                    new("Fixed threshold", "fixed"),
                    new("Non-zero pixels", "direct")
                ], Group: "Binarize"),
                new("threshold", "Threshold", "number", 128, 0, 255, 1, Group: "Binarize", Description: "Used when Binarize = fixed threshold."),
                new("invert", "Dark Objects", "boolean", false, Group: "Binarize"),
                new("minArea", "Min Area", "number", 200, 0, 99999999, 1, Unit: "px²", Group: "Candidates"),
                new("angleRangeDeg", "Angle Range", "number", 180, 0, 180, 1, Unit: "deg", Group: "Matching"),
                new("angleStepDeg", "Angle Step", "number", 1, 0.1, 30, 0.1, Unit: "deg", Group: "Matching"),
                new("minScore", "Min Score", "number", 0.6, 0, 1, 0.01, Group: "Matching"),
                new("signatureSamples", "Signature Samples", "number", 180, 16, 720, 4, Group: "Matching",
                    Description: "Angular bins of the polar radius signature; more bins = finer shape detail."),
                new("maxTargets", "Max Targets", "number", 5, 1, 100, 1, Group: "Multi-target"),
                new("minCenterDistance", "Min Center Distance", "number", 20, 0, 10000, 1, Unit: "px", Group: "Multi-target")
            ],
            "Rotation-and-translation shape matching over external contours using a polar radius signature (centroid-relative, rotation from a cyclic shift search). First version assumes a star-shaped target around its centroid and a fixed scale; a low score means the shape/scale does not match. angle = rotate the template by this amount (CCW) to align with the scene.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, EmitsOverlay: true)),
        new(
            "roi.transform",
            "ROI Transform",
            "Measurement",
            [
                new("exec", VisionDataType.Control),
                new("roi", VisionDataType.Roi, Required: false),
                new("currentPose", VisionDataType.Pose2D, Required: false)
            ],
            [
                new("roi", VisionDataType.Roi),
                new("rectangle", VisionDataType.Rectangle2D),
                new("circle", VisionDataType.Circle),
                new("center", VisionDataType.Point2D),
                new("pose", VisionDataType.Pose2D),
                new("transform", VisionDataType.Transform2D),
                new("next", VisionDataType.Control)
            ],
            [
                new("baseX", "Base X", "number", 0, -100000, 100000, 0.01, Unit: "px", Group: "Base Pose",
                    Description: "The pose at which the ROI parameter was authored."),
                new("baseY", "Base Y", "number", 0, -100000, 100000, 0.01, Unit: "px", Group: "Base Pose"),
                new("baseThetaDeg", "Base Theta", "number", 0, -3600, 3600, 0.01, Unit: "deg", Group: "Base Pose"),
                new("currentX", "Current X", "number", 0, -100000, 100000, 0.01, Unit: "px", Group: "Current Pose",
                    Description: "Used only when the currentPose port is not connected."),
                new("currentY", "Current Y", "number", 0, -100000, 100000, 0.01, Unit: "px", Group: "Current Pose"),
                new("currentThetaDeg", "Current Theta", "number", 0, -3600, 3600, 0.01, Unit: "deg", Group: "Current Pose")
            ],
            "Rigid ROI correction after locating: p' = R(dTheta)(p - base) + current. Rectangle ROIs stay rectangles for pure translation and become polygons when rotated; circle ROIs translate. Outputs a Roi value (feed it into the roi port of downstream measurement nodes), the bounding rectangle/circle and the base->current Transform2D.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true))
    ];
}
