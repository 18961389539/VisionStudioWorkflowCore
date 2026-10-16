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
                new("transform", VisionDataType.Transform2D, Required: false),
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
            "Subpixel edge location along a search line. Optional rigid px-to-px transform maps reference searchLine/endpoint parameters to the current image, including sampling direction. ROI inputs are already in current image coordinates. No edge above the contrast threshold returns found=false.",
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
                new("transform", VisionDataType.Transform2D, Required: false),
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
            "Caliper line measurement with orthogonal RMS fit error. Optional rigid px-to-px transform maps reference searchLine/endpoint parameters and the parallel sampling layout into the current image. ROI inputs are already in current image coordinates. At least three valid points are required.",
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
                new("transform", VisionDataType.Transform2D, Required: false),
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
            "Caliper circle measurement with least-squares fit and fit error. Optional rigid px-to-px transform maps the reference circle input/nominal center and arc angles into the current image; do not also supply an already corrected circle. ROI inputs are already in current image coordinates. Without transform the optional circle input is used directly in the current image.",
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
                new("scale", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("pose", VisionDataType.Pose2D, Required: false),
                new("rectangle", VisionDataType.Rectangle2D, Required: false),
                new("detections", VisionDataType.DetectionList, Required: false),
                new("count", VisionDataType.Integer, Required: false),
                new("next", VisionDataType.Control)
            ],
            [
                new("templatePath", "Template Path", "text", "", Description: "Used only when the template port is not connected."),
                new("modelMode", "Model Lifecycle", "select", "trained", Options:
                [
                    new("Trained model (recommended)", "trained"),
                    new("Legacy image template", "legacy")
                ], Group: "Training", Description: "New nodes require an explicitly trained model. Legacy workflows without this setting keep their previous template-image behavior."),
                new("binarize", "Binarize", "select", "auto", Options:
                [
                    new("Otsu (auto)", "auto"),
                    new("Fixed threshold", "fixed"),
                    new("Non-zero pixels", "direct")
                ], Group: "Binarize"),
                new("threshold", "Threshold", "number", 128, 0, 255, 1, Group: "Binarize", Description: "Used when Binarize = fixed threshold."),
                new("invert", "Dark Objects", "boolean", false, Group: "Binarize"),
                new("minArea", "Min Area", "number", 200, 0, 99999999, 1, Unit: "px²", Group: "Candidates"),
                new("angleRangeDeg", "Angle Range", "number", 180, 0, 180, 1, Unit: "deg", Group: "Training", Description: "Model training angle range; matching searches within this trained range."),
                new("angleStepDeg", "Angle Step", "number", 1, 0.1, 30, 0.1, Unit: "deg", Group: "Training", Description: "Model training angle step. Smaller steps improve angular coverage at additional model size."),
                new("scaleTolerancePercent", "Scale Tolerance", "number", 0, 0, 50, 1, Unit: "%", Group: "Training", Description: "Train and search a symmetric scale range around 100%. Zero keeps fixed-scale matching."),
                new("scaleStepPercent", "Scale Step", "number", 1, 0.1, 10, 0.1, Unit: "%", Group: "Training", Description: "Scale sampling step used when scale tolerance is enabled."),
                new("ignorePolarity", "Ignore Polarity", "boolean", false, Group: "Training", Description: "Match the same contour when its light/dark polarity is reversed."),
                new("minContrast", "Minimum Feature Contrast", "number", 5, 1, 255, 1, Group: "Training", Description: "Reject weak boundaries while training the shape model."),
                new("trainingOriginMode", "Training Origin", "select", "centroid", Options:
                [
                    new("Contour centroid", "centroid"),
                    new("Manual image coordinates", "manual")
                ], Group: "Training"),
                new("trainingOriginX", "Origin X", "number", 0, 0, 99999, 0.1, Unit: "px", Group: "Training", Description: "Used when Training Origin = manual; coordinates are relative to the full training image."),
                new("trainingOriginY", "Origin Y", "number", 0, 0, 99999, 0.1, Unit: "px", Group: "Training", Description: "Used when Training Origin = manual; coordinates are relative to the full training image."),
                new("minScore", "Min Score", "number", 0.6, 0, 1, 0.01, Group: "Matching"),
                new("maxTargets", "Max Targets", "number", 5, 1, 100, 1, Group: "Multi-target"),
                new("minCenterDistance", "Min Center Distance", "number", 20, 0, 10000, 1, Unit: "px", Group: "Multi-target")
            ],
            "Feature-based shape model matching with explicit model/search regions, subpixel pose, optional scale tolerance, polarity control and bounded multi-target suppression. angle rotates the trained template CCW to align with the scene; scale is relative to the trained size.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, EmitsOverlay: true, SupportsRoi: true)),
        new(
            "roi.transform",
            "ROI Transform",
            "Measurement",
            [
                new("exec", VisionDataType.Control),
                new("roi", VisionDataType.Roi, Required: false),
                new("currentPose", VisionDataType.Pose2D, Required: false),
                new("debugImage", VisionDataType.Image, Required: false)
            ],
            [
                new("roi", VisionDataType.Roi),
                new("image", VisionDataType.Image, Required: false),
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
                new("anglePeriodDeg", "Angle Period", "number", 360, 1, 360, 1, Unit: "deg", Group: "Base Pose",
                    Description: "Use 180 degrees for half-turn-symmetric locator models; use 360 degrees when orientation has a unique direction."),
                new("currentX", "Current X", "number", 0, -100000, 100000, 0.01, Unit: "px", Group: "Current Pose",
                    Description: "Used only when the currentPose port is not connected."),
                new("currentY", "Current Y", "number", 0, -100000, 100000, 0.01, Unit: "px", Group: "Current Pose"),
                new("currentThetaDeg", "Current Theta", "number", 0, -3600, 3600, 0.01, Unit: "deg", Group: "Current Pose")
            ],
            "Rigid ROI correction after locating: p' = R(dTheta)(p - base) + current. Angle Period controls orientation symmetry (180 degrees for half-turn-symmetric models, 360 for directional models). Capture a reference image and its actual pose together before drawing the ROI. Rectangle ROIs become polygons when rotated. Feed roi into downstream ROI inputs and transform into measurement transform inputs so reference search geometry follows the workpiece as well. Do not transform already corrected geometry twice. Connect debugImage for reference capture and result overlays.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true))
    ];
}
