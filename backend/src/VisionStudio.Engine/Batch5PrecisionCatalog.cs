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
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("next", VisionDataType.Control)
            ],
            [
                new("mode", "Retrieval", "select", "external", Options:
                [
                    new("External only", "external"),
                    new("External + holes", "all"),
                    new("Holes only", "holes")
                ], Group: "Retrieval", Description: "Retrieve external contours, external contours plus holes, or holes only. Unknown modes fail."),
                new("binarize", "Binarize", "select", "auto", Options:
                [
                    new("Otsu (auto)", "auto"),
                    new("Fixed threshold", "fixed"),
                    new("Non-zero pixels", "direct")
                ], Group: "Binarize",
                    Description: "auto=Otsu; fixed uses Threshold; direct treats every non-zero pixel as foreground."),
                new("threshold", "Threshold", "number", 128, 0, 255, 1, Group: "Binarize", VisibleWhen: new("binarize", ["fixed"]), Description: "Used only when Binarize = fixed threshold; must be finite in 0..255."),
                new("invert", "Dark Objects", "boolean", false, Group: "Binarize", Description: "Treat dark-on-light as foreground."),
                new("minArea", "Min Area", "number", 0, 0, 99999999, 1, Unit: "px²", Group: "Filter", Description: "Minimum contour area in pixels."),
                new("maxCount", "Max Contours", "number", 200, 1, 2000, 1, Group: "Filter", Description: "Keep the first 1..2000 contours that pass the area filter; additional qualifying contours are truncated."),
                new("maxPoints", "Point Budget", "number", 2000, 16, 20000, 1, Group: "Filter",
                    Description: "Contours are evenly subsampled to this budget so graph data stays bounded.")
            ],
            "Extract contours with hierarchy (external/holes). Wire: binary or Otsu-capable image → Contour list; empty → found=false (not a hard fail). Key params: hierarchy mode, approximation. Chain to contour.filter, thickness, roundness/straightness. Binarize first or rely on built-in Otsu.",
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
                new("inputCount", VisionDataType.Integer),
                new("droppedCount", VisionDataType.Integer),
                new("truncated", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false),
                new("next", VisionDataType.Control)
            ],
            [
                new("minArea", "Min Area", "number", 0, 0, 99999999, 1, Unit: "px²", Group: "Geometry", Description: "Keep contours whose area is at least this value (px²)."),
                new("maxArea", "Max Area (0=unlimited)", "number", 0, 0, 99999999, 1, Unit: "px²", Group: "Geometry", Description: "Keep contours whose area is at most this value; 0 disables the upper bound."),
                new("minCircularity", "Min Circularity", "number", 0, 0, 1, 0.01, Group: "Geometry", Description: "Lower circularity bound in [0,1]; must be <= maxCircularity."),
                new("maxCircularity", "Max Circularity", "number", 1, 0, 1, 0.01, Group: "Geometry", Description: "Upper circularity bound in [0,1]; must be >= minCircularity."),
                new("hierarchy", "Hierarchy", "select", "any", Options:
                [
                    new("Any", "any"),
                    new("External only", "external"),
                    new("Holes only", "holes")
                ], Group: "Hierarchy", Description: "Retain any, external-only, or hole-only contours."),
                new("windowX", "Window X (-1=off)", "number", -1, -1, 100000, 1, Unit: "px", Group: "Position", Description: "Position-window origin X; window is off when X/Y < 0 or width/height is 0."),
                new("windowY", "Window Y (-1=off)", "number", -1, -1, 100000, 1, Unit: "px", Group: "Position", Description: "Position-window origin Y; bounding-box center must fall inside the window."),
                new("windowWidth", "Window Width", "number", 0, 0, 100000, 1, Unit: "px", Group: "Position", Description: "Position-window width in pixels; 0 disables the window."),
                new("windowHeight", "Window Height", "number", 0, 0, 100000, 1, Unit: "px", Group: "Position", Description: "Position-window height in pixels; 0 disables the window."),
                new("maxCount", "Max Contours", "number", 200, 1, 2000, 1, Group: "Limit", Description: "Hard cap on kept contours (1..2000). truncated is true only when qualifying contours exceed this cap; ordinary filtering does not count as truncation.")
            ],
            "Filter an existing Contour list by area, circularity, hierarchy and a position window (bbox center inside the window). Wire: contours → filtered contours; compose after contour.extract (no re-extraction). Over-strict gates clear the list; window must share the image coordinate frame.",
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
                new("perimeter", VisionDataType.Double),
                new("solidity", VisionDataType.Double),
                new("extent", VisionDataType.Double),
                new("aspectRatio", VisionDataType.Double),
                new("equivalentDiameter", VisionDataType.Double),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false),
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
                ], Group: "Selection", Description: "Which contour the scalar outputs describe: largest area or the index parameter."),
                new("index", "Index", "number", 0, 0, 100000, 1, Group: "Selection", VisibleWhen: new("selectBy", ["index"]), Description: "Zero-based position in the input list, used only when Select = by index; out of range fails.")
            ],
            "Compute derived contour geometry (centroid, min-area rotated rectangle, solidity, extent, aspect ratio, equivalent diameter) with strict selectBy/index checks, scalar ports for the selected contour, overlays and diagnostics. Empty input or contours with fewer than 3 points fail.",
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
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("next", VisionDataType.Control)
            ],
            [
                new("startX", "Start X", "number", 120, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start X in pixels."),
                new("startY", "Start Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start Y in pixels."),
                new("endX", "End X", "number", 520, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end X in pixels."),
                new("endY", "End Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end Y in pixels."),
                new("sampleWidth", "Projection Length", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling",
                    Description: "The projection width must be odd; even values are rounded up to the next odd value (for example, 4 becomes 5). Length is averaged along the expected edge; larger values smooth local noise but may merge nearby features."),
                new("minContrast", "Min Contrast", "number", 8, 0, 255, 1, Unit: "gray", Group: "Sampling", Description: "Minimum contrast required for an edge."),
                new("polarity", "Polarity", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Sampling", Description: "Edge polarity (dark-to-light, light-to-dark, or either)."),
                new("direction", "Direction", "select", "strongest", Options:
                [
                    new("Strongest", "strongest"),
                    new("First from start", "firstfromstart"),
                    new("First from end", "firstfromend")
                ], Group: "Sampling", Description: "Scan direction along the search geometry."),
                new("refine", "Refine", "select", "parabolic", Options:
                [
                    new("Parabolic (subpixel)", "parabolic"),
                    new("No parabolic refinement", "none")
                ], Group: "Sampling", Description: "Enable subpixel refinement of the edge location.")
            ,

                ],
            "Subpixel edge location along a search line. Measurements use 8-bit grayscale; UInt16 input is scaled linearly from its full range to 0..255. Optional rigid px-to-px transform maps reference searchLine/endpoint parameters to the current image, including sampling direction. ROI inputs are already in current image coordinates. No edge above the contrast threshold returns found=false.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true), RoiImageBindings: [new("roi", "image")]),
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
                new("ignoredCount", VisionDataType.Integer, Required: false),
                new("searchLine", VisionDataType.Line2D),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("next", VisionDataType.Control)
            ],
            [
                new("startX", "Start X", "number", 120, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start X in image pixels before the optional rigid transform."),
                new("startY", "Start Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start Y in image pixels before the optional rigid transform."),
                new("endX", "End X", "number", 520, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end X in image pixels before the optional rigid transform."),
                new("endY", "End Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end Y in image pixels before the optional rigid transform."),
                new("sampleWidth", "Sample Width", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling",
                    Description: "The projection width must be odd; even values are rounded up to the next odd value (for example, 4 becomes 5)."),
                new("minContrast", "Min Contrast", "number", 8, 0, 255, 1, Unit: "gray", Group: "Sampling", Description: "Minimum gray-level step along each profile for an edge to be accepted."),
                new("polarity", "Polarity", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Sampling", Description: "Accept only rising (dark-to-light), falling (light-to-dark), or any edge along each profile."),
                new("direction", "Direction", "select", "strongest", Options:
                [
                    new("Strongest", "strongest"),
                    new("First from start", "firstfromstart"),
                    new("First from end", "firstfromend")
                ], Group: "Sampling", Description: "Which accepted edge to keep when a profile contains several: strongest, first from the start, or first from the end."),
                new("refine", "Refine", "select", "parabolic", Options:
                [
                    new("Parabolic (subpixel)", "parabolic"),
                    new("No parabolic refinement", "none")
                ], Group: "Sampling", Description: "Parabolic interpolation around the strongest profile sample for subpixel edge position, or whole-pixel only."),
                new("edgeLength", "Caliper Span (0=auto)", "number", 0, 0, 10000, 1, Unit: "px", Group: "Sampling", Description: "Spread of parallel profiles along the expected edge; zero uses the search-line length."),
                new("caliperCount", "Caliper Count", "number", 9, 3, 101, 1, Group: "Sampling", Description: "Number of parallel search profiles along the edge span."),
                new("minValidCalipers", "Minimum Valid Calipers", "number", 3, 3, 101, 1, Group: "Fit",
                    Description: "Require at least this many edge points to remain after outlier rejection; the default preserves the three-point fit minimum."),
                new("maxFitOutliers", "Max Fit Outliers", "number", 1, 0, 16, 1, Group: "Fit",
                    Description: "Ignore up to this many edge points when their orthogonal residual exceeds the threshold."),
                new("fitOutlierThreshold", "Fit Outlier Threshold", "number", 2, 0, 100, 0.1, Unit: "px", Group: "Fit",
                    Description: "A point can be excluded when its distance from the fitted line exceeds this value."),
                new("maxFitError", "Max Fit Error (0=off)", "number", 0, 0, 100, 0.1, Unit: "px", Group: "Fit",
                    Description: "Return found=false when the final orthogonal RMS fit error exceeds this value; zero disables this quality gate.")
            ,
                ],
            "Caliper line measurement with orthogonal RMS fit error, bounded outlier rejection, and an optional maximum fit-error quality gate. Measurements use 8-bit grayscale; UInt16 input is scaled linearly from its full range to 0..255. Out-of-image profiles are clipped without changing their direction and the visible profile lines are shown in the result. Green points were used for the fit; orange points were ignored. Optional rigid px-to-px transform maps reference searchLine/endpoint parameters and the parallel sampling layout into the current image. ROI inputs are already in current image coordinates. The minimum valid caliper gate is checked after outlier rejection.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true), RoiImageBindings: [new("roi", "image")]),
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
                new("radiusSpread", VisionDataType.Double, Required: false),
                new("fitError", VisionDataType.Double, Required: false),
                new("sampleCount", VisionDataType.Integer, Required: false),
                new("ignoredCount", VisionDataType.Integer, Required: false),
                new("diagnostics", VisionDataType.String),
                new("overlay", VisionDataType.Image, Required: false),
                new("next", VisionDataType.Control)
            ],
            [
                new("centerX", "Center X", "number", 320, -10000000, 10000000, 1, Unit: "px", Group: "Nominal Circle", Description: "Nominal circle center X in image pixels before optional rigid transform."),
                new("centerY", "Center Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Nominal Circle", Description: "Nominal circle center Y in image pixels before optional rigid transform."),
                new("radius", "Nominal Radius", "number", 100, 1, 10000000, 1, Unit: "px", Group: "Nominal Circle", Description: "Expected circle radius in pixels used as the radial search mid-line."),
                new("angleStartDeg", "Angle Start", "number", -180, -360, 360, 1, Unit: "deg", Group: "Arc", Description: "Arc start angle in degrees; full circles typically use -180..180."),
                new("angleEndDeg", "Angle End", "number", 180, -360, 360, 1, Unit: "deg", Group: "Arc", Description: "Arc end angle in degrees; must differ from angleStartDeg for a measurable arc."),
                new("samplingMode", "Sampling Mode", "select", "count", Options:
                [
                    new("Caliper Count", "count"),
                    new("Angle Step (Legacy)", "step")
                ], Group: "Arc", Description: "Distribute radial calipers by count or by fixed angular step."),
                new("caliperCount", "Caliper Count", "number", 72, 3, 361, 1, Unit: "", Group: "Arc",
                    Description: "Number of radial calipers. Full circles distribute them without duplicating the end angle; partial arcs include both ends.",
                    VisibleWhen: new("samplingMode", ["count"])),
                new("angleStepDeg", "Angle Step (Legacy)", "number", 5, 0.5, 45, 0.5, Unit: "deg", Group: "Arc",
                    Description: "Fixed angular interval. Full circles omit the duplicate end ray; partial arcs sample from the start by this interval and may stop before the end.",
                    VisibleWhen: new("samplingMode", ["step"])),
                new("radialBand", "Radial Search Half-Width", "number", 8, 1, 500, 1, Unit: "px", Group: "Sampling",
                    Description: "Searches from max(0, nominal radius minus this value) to nominal radius plus this value; the radial search never crosses the center."),
                new("minContrast", "Min Contrast", "number", 8, 0, 255, 1, Unit: "gray", Group: "Sampling", Description: "Minimum edge contrast in gray levels required to accept a spoke sample."),
                new("polarity", "Polarity", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Sampling", Description: "Edge polarity filter applied along each radial profile."),
                new("searchDirection", "Search Direction", "select", "strongest", Options:
                [
                    new("Strongest Edge", "strongest"),
                    new("First from Start", "firstfromstart"),
                    new("First from End", "firstfromend")
                ], Group: "Sampling", Description: "Which edge along the radial band is retained for the fit."),
                new("sampleWidth", "Projection Length", "number", 3, 1, 99, 2, Unit: "px", Group: "Sampling",
                    Description: "The projection width must be odd; even values are rounded up to the next odd value (for example, 4 becomes 5). Length is averaged along the circle tangent; larger values smooth local noise but may merge nearby edges."),
                new("maxRadiusDeviation", "Max Radius Deviation (0=off)", "number", 0, 0, 5000, 1, Unit: "px", Group: "Sampling",
                    Description: "Reject each spoke before fitting when its edge radius is farther than this from the nominal radius."),
                new("minValidCalipers", "Minimum Valid Calipers", "number", 3, 3, 361, 1, Group: "Fit",
                    Description: "Require at least this many edge points to remain after outlier rejection; increase it to prevent sparse arcs from producing a measurement."),
                new("maxFitOutliers", "Max Fit Outliers", "number", 2, 0, 16, 1, Group: "Fit",
                    Description: "After an initial fit, discard up to this many points whose radial residual exceeds the threshold."),
                new("fitOutlierThreshold", "Fit Outlier Threshold", "number", 2, 0, 100, 0.1, Unit: "px", Group: "Fit",
                    Description: "Maximum allowed point-to-circle residual before a point can be excluded from the refit."),
                new("fitRadiusTolerance", "Fit Radius Tolerance (0=off)", "number", 0, 0, 5000, 0.1, Unit: "px", Group: "Fit",
                    Description: "Reject the fitted circle when its radius differs from the nominal radius by more than this amount."),
                new("maxFitError", "Max Fit Error (0=off)", "number", 0, 0, 100, 0.1, Unit: "px", Group: "Fit",
                    Description: "Return found=false when the final radial RMS fit error exceeds this value; zero disables this quality gate.")
            ],
            "Caliper circle measurement with selectable radial sampling, edge direction, bounded outlier rejection, and an optional maximum fit-error quality gate. Measurements use 8-bit grayscale; UInt16 input is scaled linearly from its full range to 0..255. Each radial segment is clipped to the image while preserving its direction. Optional rigid px-to-px transform maps the reference circle input/nominal center and arc angles into the current image; do not also supply an already corrected circle. ROI inputs are already in current image coordinates. Without transform the optional circle input is used directly in the current image. The minimum valid caliper gate is checked after outlier rejection.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true), RoiImageBindings: [new("roi", "image")]),
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
                new("axisRatio", VisionDataType.Double),
                new("count", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false),
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
                ], Group: "Selection", Description: "Which contour the ellipse outputs describe: largest area or the index parameter."),
                new("index", "Index", "number", 0, 0, 100000, 1, Group: "Selection", Description: "Used when Select = by index; out of range fails.")
            ],
            "Fit an ellipse to a contour (needs >= 5 non-collinear points). Output is the rotated rectangle of the ellipse: width = major axis, height = minor axis, angle = major-axis direction (degrees, CCW), plus axisRatio and diagnostics. Strict selectBy/index validation.",
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
                new("scaleX", VisionDataType.Double, Required: false),
                new("scaleY", VisionDataType.Double, Required: false),
                new("clutterScore", VisionDataType.Double, Required: false),
                new("modelPointCount", VisionDataType.Integer, Required: false),
                new("orientedModelPointCount", VisionDataType.Integer, Required: false),
                new("polarityMode", VisionDataType.String, Required: false),
                new("polarityMatchFraction", VisionDataType.Double, Required: false),
                new("coverageScore", VisionDataType.Double, Required: false),
                new("distanceScore", VisionDataType.Double, Required: false),
                new("nativeScore", VisionDataType.Double, Required: false),
                new("contrast", VisionDataType.Double, Required: false),
                new("contrastFraction", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("pose", VisionDataType.Pose2D, Required: false),
                new("rectangle", VisionDataType.Rectangle2D, Required: false),
                new("detections", VisionDataType.DetectionList, Required: false),
                new("count", VisionDataType.Integer, Required: false),
                new("pyramidLevels", VisionDataType.Double, Required: false),
                new("maxDeformationPx", VisionDataType.Double, Required: false),
                new("angleRefineDeltaDeg", VisionDataType.Double, Required: false),
                new("scaleRefineDelta", VisionDataType.Double, Required: false),
                new("xldSnapDeltaPx", VisionDataType.Double, Required: false),
                new("modelMeanRadiusPx", VisionDataType.Double, Required: false),
                new("modelMeanNormalMagnitude", VisionDataType.Double, Required: false),
                new("anisotropicScale", VisionDataType.Boolean, Required: false),
                new("subpixelMethod", VisionDataType.String, Required: false),
                new("clutterEnabled", VisionDataType.Boolean, Required: false),
                new("modelCacheHit", VisionDataType.Boolean, Required: false),
                new("diagnostics", VisionDataType.String),
                new("next", VisionDataType.Control)
            ],
            [
                new("templatePath", "Template Path", "text", "", Group: "Model", Description: "Used only when the template port is not connected."),
                new("modelMode", "Model Lifecycle", "select", "trained", Options:
                [
                    new("Trained model (recommended)", "trained"),
                    new("Legacy image template", "legacy")
                ], Group: "Training", Description: "New nodes require an explicitly trained model. Legacy workflows without this setting keep their previous template-image behavior."),
                new("binarize", "Origin Estimation", "select", "auto", Options:
                [
                    new("Otsu (auto)", "auto"),
                    new("Fixed threshold", "fixed"),
                    new("Non-zero pixels", "direct")
                ], Group: "Origin Estimation", Description: "Estimates the default origin only. Model features are trained from the original gray image."),
                new("threshold", "Origin Threshold", "number", 128, 0, 255, 1, Group: "Origin Estimation", Description: "Used when origin estimation uses a fixed threshold; this does not binarize the shape model."),
                new("invert", "Dark Foreground", "boolean", false, Group: "Origin Estimation", Description: "Invert only the automatic foreground used to estimate the origin."),
                new("minArea", "Min Area", "number", 200, 0, 99999999, 1, Unit: "px²", Group: "Candidates", Description: "Minimum candidate area (px²) for shape-match candidates."),
                new("angleRangeDeg", "Legacy Angle Range", "number", 180, 0, 180, 1, Unit: "deg", Group: "Search", Description: "Compatibility setting for older shape workflows that use an image template directly. Template Match defaults the same key to 0 (no rotation search); here the default is the full 180° coverage."),
                new("trainingAngleRangeDeg", "Training Angle Coverage", "number", 180, 0, 180, 1, Unit: "deg", Group: "Training", Description: "Maximum symmetric angle range represented by the trained model. Changing this requires retraining."),
                new("searchAngleStartDeg", "Search Start Angle", "number", -180, -180, 180, 1, Unit: "deg", Group: "Search", Description: "Lower angle bound for this search run. It is clipped to the trained model coverage and does not require retraining."),
                new("searchAngleEndDeg", "Search End Angle", "number", 180, -180, 180, 1, Unit: "deg", Group: "Search", Description: "Upper angle bound for this search run. It is clipped to the trained model coverage and does not require retraining."),
                new("searchScaleMinPercent", "Search Minimum Scale", "number", 50, 50, 150, 1, Unit: "%", Group: "Search", Description: "Minimum scale for this search run, as a percentage of the trained object size. It is clipped to the trained model coverage."),
                new("searchScaleMaxPercent", "Search Maximum Scale", "number", 150, 50, 150, 1, Unit: "%", Group: "Search", Description: "Maximum scale for this search run, as a percentage of the trained object size. It is clipped to the trained model coverage."),
                new("angleStepDeg", "Training Angle Step", "number", 1, 0.1, 30, 0.1, Unit: "deg", Group: "Training", Description: "Model training angle step. Smaller steps improve angular coverage at additional model size. Note: Template Match's same-named key is a search step (default 5°), not a training step."),
                new("pyramidLevels", "Pyramid Levels", "number", 0, 0, 8, 1, Group: "Training", Description: "0 selects automatic pyramid depth; 1–8 fixes the coarse-to-fine levels used for training and search. More levels can speed up large-image search but may miss small features."),
                new("parameterSource", "Parameter Source", "select", "manual", Options:
                [
                    new("Manual (engineer-set values)", "manual"),
                    new("Automatic (JLVision determine at training)", "auto")
                ], Group: "Auto Tune", Description: "manual keeps every existing recipe bit-identical. auto calls JLVision determine_shape_model_params on the training ROI and overrides Pyramid Levels, Angle Step and Minimum Feature Contrast at training time; the resolved values are reported in the training summary. The run-time search never calls determine."),
                new("autoTuneProfile", "Auto Tune Profile", "select", "balanced", Options:
                [
                    new("Balanced", "balanced"),
                    new("Speed (one level less, coarser step)", "speed"),
                    new("Accuracy (one level more, finer step)", "accuracy")
                ], Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Post-adjusts the native recommendation inside the pyramid-level budget. Balanced applies the recommendation unchanged."),
                new("autoTuneLockedParams", "Locked Parameters", "text", "", Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Comma-separated parameter names the auto tune must not override: num_levels, angle_step, min_contrast. Locked values keep the node's current setting."),
                new("scaleTolerancePercent", "Scale Tolerance", "number", 0, 0, 50, 1, Unit: "%", Group: "Training", Description: "Train and search a symmetric scale range around 100%. Zero keeps fixed-scale matching."),
                new("scaleToleranceXPercent", "X Scale Tolerance", "number", 0, 0, 50, 1, Unit: "%", Group: "Training", Description: "Independent horizontal scale tolerance for anisotropic shape matching. If either X/Y tolerance is non-zero, these axis values take precedence over uniform Scale Tolerance."),
                new("scaleToleranceYPercent", "Y Scale Tolerance", "number", 0, 0, 50, 1, Unit: "%", Group: "Training", Description: "Independent vertical scale tolerance for anisotropic shape matching. If either X/Y tolerance is non-zero, these axis values take precedence over uniform Scale Tolerance."),
                new("scaleStepPercent", "Training Scale Step", "number", 1, 0.1, 10, 0.1, Unit: "%", Group: "Training", Description: "Scale sampling step used when scale tolerance is enabled. Note: Template Match's same-named key is a search resampling step (default 10%)."),
                new("trainingMinSampleFraction", "Minimum Sample Fraction", "number", 0.75, 0.5, 1, 0.05, Group: "Training", Description: "Minimum fraction of aligned samples that must contain a contour feature for it to enter the composite model."),
                new("trainingFeatureTolerancePx", "Feature Alignment Tolerance", "number", 2, 0.5, 10, 0.5, Unit: "px", Group: "Training", Description: "Maximum point distance used to treat contour features from different samples as the same feature."),
                new("ignorePolarity", "Ignore Polarity", "boolean", false, Group: "Training", Description: "Match the same contour when its light/dark polarity is reversed."),
                new("minContrast", "Minimum Feature Contrast", "number", 5, 1, 255, 1, Group: "Training", Description: "Reject weak boundaries while training the shape model."),
                new("clutterContrast", "Clutter Contrast", "number", 5, 1, 255, 1, Group: "Training", Description: "Minimum edge contrast counted inside the optional Clutter ROI; must be at least Minimum Feature Contrast."),
                new("trainingOriginMode", "Training Origin", "select", "centroid", Options:
                [
                    new("Automatic object centroid", "centroid"),
                    new("Manual image coordinates", "manual")
                ], Group: "Training", Description: "Use the dominant thresholded component centroid when available; fall back to the selected model region centroid. Model features still use the original grayscale image."),
                new("trainingOriginX", "Origin X", "number", 0, 0, 99999, 0.1, Unit: "px", Group: "Training", VisibleWhen: new("trainingOriginMode", ["manual"]), Description: "Used when Training Origin = manual; coordinates are relative to the full training image."),
                new("trainingOriginY", "Origin Y", "number", 0, 0, 99999, 0.1, Unit: "px", Group: "Training", VisibleWhen: new("trainingOriginMode", ["manual"]), Description: "Used when Training Origin = manual; coordinates are relative to the full training image."),
                new("minScore", "Min Score", "number", 0.6, 0, 1, 0.01, Group: "Matching", Description: "Acceptance threshold for a candidate pose. Template Match defaults the same key to 0.5, so switching node types changes the effective threshold unless it is set explicitly."),
                new("maxDeformationPx", "Maximum Local Deformation", "number", 0, 0, 32, 1, Unit: "px", Group: "Matching", Description: "Allow each model point to move locally by up to this many pixels during pose refinement. Uses least-squares adjustment and does not require retraining; 0 disables deformation tolerance."),
                new("maxClutterScore", "Maximum Clutter Score", "number", 0.2, 0, 1, 0.01, Group: "Matching", Description: "When a Clutter ROI is trained, reject candidates whose clutter-region edge score exceeds this value."),
                new("scoreUsingClutter", "Score Using Clutter", "boolean", false, Group: "Matching", Description: "When a Clutter ROI is trained, multiply the native match score by (1 - clutter score). The unadjusted score is also available as nativeScore."),
                new("runtimeContrastThreshold", "Runtime Contrast Threshold", "number", 0, 0, 255, 1, Group: "Matching", Description: "Minimum average gradient strength (0–255) at matched model features. Zero disables this run-time filter; it does not require retraining."),
                new("contrastFractionThreshold", "Contrast Fraction Threshold", "number", 0, 0, 1, 0.01, Group: "Matching", Description: "Minimum run-time feature contrast divided by the trained-image feature contrast, capped at 1. Zero disables this check; it needs the lossless original training image."),
                new("maxTargets", "Max Targets", "number", 5, 1, 100, 1, Group: "Multi-target", Description: "Accepted matches per run. Template Match defaults the same key to 1 (single result), so a switched-in shape node returns up to 5 unless this is set explicitly."),
                new("minCenterDistance", "Min Center Distance", "number", 20, 0, 10000, 1, Unit: "px", Group: "Multi-target", Description: "Candidates closer than this to a better match are dropped. Template Match defaults the same key to 0 (keep every accepted center)."),
                new("engine", "Shape Engine", "select", "native", Options:
                [
                    new("Native JLVision shape model", "native"),
                    new("Managed chamfer (cross-platform)", "managedChamfer"),
                    new("Auto (= native JLVision only)", "auto")
                ], Group: "Managed Chamfer", Description: "Default/product path is native JLVision. managedChamfer builds a contour model from the template image (templateRoi crop, centre origin) and searches a chamfer distance transform; it ignores trained native models. auto is an alias of native and never silently falls back to chamfer — set managedChamfer explicitly for the lab path."),
                new("chamferPolarityCosine", "Chamfer Polarity Cosine", "number", 0, -1, 1, 0.05, Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Managed engine only. When Ignore Polarity is off, require scene Sobel direction · rotated model normal >= this cosine (0 ≈ hemisphere agreement)."),
                new("chamferTolerancePx", "Chamfer Tolerance", "number", 2.5, 0.5, 10, 0.5, Unit: "px", Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Edge distance (px) that still counts as covered model contour. Higher tolerates more occlusion/blur but admits more clutter."),
                new("chamferAngleStepDeg", "Chamfer Angle Step", "number", 5, 0.5, 30, 0.5, Unit: "deg", Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Coarse angular sampling for the managed engine; refinement halves the step locally. Angle remains discrete, not continuous."),
                new("chamferScaleStepPercent", "Chamfer Scale Step", "number", 10, 1, 50, 1, Unit: "%", Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Scale sampling between Search Minimum/Maximum Scale for the managed engine."),
                new("chamferCannyLow", "Chamfer Canny Low", "number", 50, 0, 255, 1, Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Canny low threshold for managed chamfer edge extraction."),
                new("chamferCannyHigh", "Chamfer Canny High", "number", 150, 0, 255, 1, Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Canny high threshold for managed chamfer edge extraction; should be >= low."),
                new("chamferMaxModelPoints", "Chamfer Max Model Points", "number", 240, 16, 2000, 1, Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Contour points kept after subsampling the template edges; more points are slower but describe finer shape detail.")
            ,
                new("chamferContinuousRefine", "Chamfer Continuous Pose Refine", "boolean", true, Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Managed engine: parabolic continuous angle+scale refine after discrete search (not full HALCON XLD least-squares)."),
                new("chamferXldSnap", "Chamfer XLD-ish Contour Snap", "boolean", true, Group: "Managed Chamfer", VisibleWhen: new("engine", ["managedChamfer"]), Description: "Managed engine: snap pose along model normals toward the chamfer zero-set (+/-1 px). Approximate XLD refine, not PatMax."),
                new("diagnosticsLevel", "Diagnostics Level", "select", "standard", Group: "Diagnostics", Options: [new("Minimal", "minimal"), new("Standard", "standard"), new("Verbose", "verbose")], Description: "Controls how many fields are included in the diagnostics JSON payload."),
                ],
            "JLVision gray-edge shape matching with explicit model/search regions, train-up-to-four equal-size samples, rotation, uniform or aniso scale, polarity, multi-target and clutter ROI. Wire: train first on new nodes, then run on scene image; pose/scale outputs feed roi.transform. Key params: model/search regions, minScore, train/search angle and scale ranges, pyramidLevels, contrast gates, clutterRoi. Default matchingBackend=jlvision; engine=auto is native only and never silently switches to managedChamfer (explicit engine=managedChamfer required for the managed distance matcher). Remaining gap vs PatMax/full HALCON: no PatMax feature vectors, no trained clutter model, no full XLD least-squares deformation. found=false: not trained, weak contrast, outside angle/scale window, or clutter reject.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, EmitsOverlay: true, SupportsRoi: true), TransformFrames: new(NodeTransformFramePolicy.TemplateToImage, ImagePort: "image", TemplatePort: "template")),
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
                new("roi", VisionDataType.Roi, Required: false),
                new("image", VisionDataType.Image, Required: false),
                new("rectangle", VisionDataType.Rectangle2D, Required: false),
                new("circle", VisionDataType.Circle, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("pose", VisionDataType.Pose2D, Required: false),
                new("transform", VisionDataType.Transform2D, Required: false),
                new("diagnostics", VisionDataType.String),
                new("overlay", VisionDataType.Image, Required: false),
                new("preview", VisionDataType.Image, Required: false),
                new("next", VisionDataType.Control)
            ],
            [
                new("baseX", "Base X", "number", 0, -10000000, 10000000, 0.01, Unit: "px", Group: "Base Pose",
                    Description: "Baseline pose X in pixels when the ROI parameter was authored (frozen with reference capture)."),
                new("baseY", "Base Y", "number", 0, -10000000, 10000000, 0.01, Unit: "px", Group: "Base Pose",
                    Description: "Baseline pose Y in pixels when the ROI parameter was authored (frozen with reference capture)."),
                new("baseThetaDeg", "Base Theta", "number", 0, -3600, 3600, 0.01, Unit: "deg", Group: "Base Pose",
                    Description: "Baseline pose angle in degrees when the ROI was authored; interpreted with Angle Period for orientation symmetry."),
                new("anglePeriodDeg", "Angle Period", "number", 360, 1, 360, 1, Unit: "deg", Group: "Base Pose",
                    Description: "Use 180 degrees for half-turn-symmetric locator models; use 360 degrees when orientation has a unique direction."),
                new("currentX", "Current X", "number", 0, -10000000, 10000000, 0.01, Unit: "px", Group: "Current Pose",
                    Description: "Used only when the currentPose port is not connected."),
                new("currentY", "Current Y", "number", 0, -10000000, 10000000, 0.01, Unit: "px", Group: "Current Pose",
                    Description: "Used only when the currentPose port is not connected."),
                new("currentThetaDeg", "Current Theta", "number", 0, -3600, 3600, 0.01, Unit: "deg", Group: "Current Pose",
                    Description: "Used only when the currentPose port is not connected."),
                // 基准图像资产与一次性动作由画布/宿主写入，不是可编辑表单字段；必须在目录中声明。
                new("captureReference", "Capture Reference", "boolean", false, Group: "Reference Capture", Internal: true,
                    Description: "One-shot action written by the reference-capture command: freeze the connected debugImage and the current pose as the new baseline."),
                new("referenceImage", "Reference Image (base64 PNG)", "textarea", null, Group: "Reference Capture", Internal: true,
                    Description: "Captured baseline image asset. Kept on the node so the ROI can be re-anchored after the scene changes."),
                new("referenceWidth", "Reference Width", "number", null, Group: "Reference Capture", Internal: true,
                    Description: "Width in pixels of the captured reference image asset; written by reference capture, not edited by hand."),
                new("referenceHeight", "Reference Height", "number", null, Group: "Reference Capture", Internal: true,
                    Description: "Height in pixels of the captured reference image asset; written by reference capture, not edited by hand."),
                new("debugImagePreviewOnly", "Debug Preview Only", "boolean", false, Group: "Reference Capture", Internal: true,
                    Description: "Validation-mode flag: return the debug image without requiring a ROI yet.")
            ,

                ],
            "Rigid ROI follow after locate: p' = R(dTheta)(p − base) + current. Capture reference image + pose before drawing the ROI. Wire: currentPose (or currentX/Y/Theta) from matching; roi → measurement ROI ports; transform → measurement transform; debugImage for capture/overlay. Output transform maps ReferenceImage→CurrentImage; compose with image.crop transform (A=this, B=crop) for CropImage coordinates. anglePeriodDeg 180 for half-turn-symmetric models, 360 when orientation is unique. Rotated rectangles become polygons. Do not transform already-corrected geometry twice.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true), Frames: [new(NodeImageFramePolicy.InputFrame, InputPort: "debugImage", FallbackFrame: "CurrentImage", Transform: NodeTransformFramePolicy.RoiReference)], OutputFrames: [new(NodeOutputFrameSource.ImageInput, OutputPort: "roi", InputPort: "debugImage")], RoiImageBindings: [new("roi", "debugImage")])
    ];
}
