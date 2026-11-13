namespace VisionStudio.Engine;

/// <summary>
/// 分割 · 轮廓 · 特征专项补全（node-gap-analysis.md 第十节）。
///
/// 这一域算法层已经够厚（regionOp 77 / contourOp 32 / MSER + 分水岭 / featureExtract 8），
/// 缺的是**数据形态**：Roi 只有出没有进、Region 与掩膜图单向、任意轮廓回不了区域。
/// 所以这批节点几乎都不引入新算法，只做格式转换与一个凸性度量。
/// </summary>
public static class RegionGapCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);
    private static readonly ParameterDescriptor Index = new("index", "Zero-based Index", "number", 0, 0, 1999, 1,
        Description: "Which object in the connected list to use (0-based). Out of range returns found=false.");

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        // ── Roi 的生产者（此前只有 roi.transform / image.crop 两个）────────────
        new("roi.fromGeometry", "ROI from Geometry", "Region",
            [Exec, new("rectangle", VisionDataType.Rectangle2D, Required: false), new("circle", VisionDataType.Circle, Required: false),
             new("pointList", VisionDataType.PointList, Required: false)],
            [new("roi", VisionDataType.Roi), new("shape", VisionDataType.String),
             new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("width", VisionDataType.Double),
             new("height", VisionDataType.Double), new("radius", VisionDataType.Double), new("pointCount", VisionDataType.Integer),
             Diagnostics, Next],
            [
                new("shape", "Shape", "select", "rectangle", Group: "Geometry", Options:
                [
                    new("Rectangle", "rectangle"),
                    new("Circle", "circle"),
                    new("Polygon", "polygon")
                ], Description: "Which ROI to build. A connected port of the same shape overrides the numeric fallbacks below, so a located rectangle or fitted circle can drive the ROI directly."),
                new("x", "X", "number", 0, -1000000, 1000000, .5, Unit: "px", Group: "Geometry",
                    VisibleWhen: new("shape", ["rectangle"]), Description: "Left edge of the fallback rectangle, in image pixels."),
                new("y", "Y", "number", 0, -1000000, 1000000, .5, Unit: "px", Group: "Geometry",
                    VisibleWhen: new("shape", ["rectangle"]), Description: "Top edge of the fallback rectangle."),
                new("width", "Width", "number", 100, .5, 1000000, .5, Unit: "px", Group: "Geometry",
                    VisibleWhen: new("shape", ["rectangle"]), Description: "Rectangle width; must be positive."),
                new("height", "Height", "number", 100, .5, 1000000, .5, Unit: "px", Group: "Geometry",
                    VisibleWhen: new("shape", ["rectangle"]), Description: "Rectangle height; must be positive."),
                new("rectangleAnchor", "Rectangle Anchor", "select", "topLeft", Group: "Geometry", Options:
                [
                    new("Top-left (X/Y is the corner)", "topLeft"),
                    new("Center (X/Y is the middle)", "center")
                ], VisibleWhen: new("shape", ["rectangle"]),
                    Description: "Where the rectangle's X/Y points. The catalog is not consistent here: box outputs from matching/aruco are top-left, rotated-rectangle outputs are the centre. Pick the one that matches the producer you wired."),
                new("centerX", "Center X", "number", 0, -1000000, 1000000, .5, Unit: "px", Group: "Geometry",
                    VisibleWhen: new("shape", ["circle"]), Description: "Center of the fallback circle."),
                new("centerY", "Center Y", "number", 0, -1000000, 1000000, .5, Unit: "px", Group: "Geometry",
                    VisibleWhen: new("shape", ["circle"]), Description: "Center of the fallback circle."),
                new("radius", "Radius", "number", 50, .5, 1000000, .5, Unit: "px", Group: "Geometry",
                    VisibleWhen: new("shape", ["circle"]), Description: "Circle radius; must be positive."),
                new("pointsJson", "Polygon Points JSON", "textarea", "[[20,20],[120,20],[120,120]]", Group: "Geometry",
                    VisibleWhen: new("shape", ["polygon"]),
                    Description: "Fallback [x,y] pairs when no pointList is wired. At least three points, at most 20000, finite and within +/-100000000 px."),
                new("expand", "Expand (px)", "number", 0, -1000, 1000, .5, Unit: "px", Group: "Adjust",
                    Description: "Grows the ROI on every side; negative values shrink it. Use it to leave margin for a following caliper search, or to tighten a located box."),
            ],
            "Builds a runtime ROI from geometry. Until now Roi had only two producers (roi.transform, image.crop) while 15 nodes consume it, so a located box or a fitted circle could not become a search region. Wire: rectangle / circle / pointList (any one) -> roi, then into the calipers, match.template, region.fromRoi or image.crop. ROI rectangles are axis-aligned; a rotated rectangle input is reduced to its axis-aligned bounds and the rotation is reported in diagnostics.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("roi.fromDetection", "ROI from Detection", "Region",
            [Exec, new("detections", VisionDataType.DetectionList)],
            [new("roi", VisionDataType.Roi), new("index", VisionDataType.Integer), new("x", VisionDataType.Double),
             new("y", VisionDataType.Double), new("width", VisionDataType.Double), new("height", VisionDataType.Double),
             new("radius", VisionDataType.Double), Diagnostics, Next],
            [
                Index,
                new("shape", "Shape", "select", "rectangle", Group: "Geometry", Options:
                [
                    new("Rectangle (bounding box)", "rectangle"),
                    new("Circle (fitted circle)", "circle")
                ], Description: "Rectangle uses the detection bounding box; circle uses the fitted circle and falls back to half the larger box side when the detection carries no radius."),
                new("expand", "Expand (px)", "number", 0, -1000, 1000, .5, Unit: "px", Group: "Adjust",
                    Description: "Grows the ROI on every side (negative shrinks). Leaves margin for the caliper search that follows a detection."),
            ],
            "Turns one detection into a runtime ROI. This is the piece that makes per-object measurement possible before flow.forEach is generalised: region.toDetections -> flow.forEach -> roi.fromDetection -> region.fromRoi -> measure.regionIntensity runs once per object instead of duplicating the measurement chain. Detection X/Y is the centroid, so the rectangle is centred on it. Out-of-range index returns found=false.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        // ── 轮廓 → 区域（regionOp 只收标量，点集进不去）──────────────────
        new("region.fromContour", "Region from Contour", "Region",
            [Exec, new("contours", VisionDataType.Contour), new("image", VisionDataType.Image, Required: false)],
            [new("region", VisionDataType.Region), new("image", VisionDataType.Image), Found,
             new("count", VisionDataType.Integer), new("area", VisionDataType.Integer), Diagnostics, Next],
            [
                Index,
                new("allContours", "Fill All Contours", "boolean", false, Group: "Selection",
                    Description: "Fill every contour in the list into one region instead of only index. Use it when the measured feature is made of several segments (an interrupted edge)."),
                new("expand", "Expand (px)", "number", 0, 0, 50, 1, Unit: "px", Group: "Raster",
                    Description: "Dilates the filled polygon by an elliptical kernel of this radius, which compensates half-pixel contour sampling and hairline gaps."),
                new("invert", "Invert", "boolean", false, Group: "Raster",
                    Description: "Fill everything outside the polygon instead of inside. Useful to build a background region for region.boolean."),
            ],
            "Rasterizes one contour (or all of them) into a pixel region. jlvision.regionOp only accepts 11 scalar parameters, so genRegionPolygon can never receive a fitted contour; region.fromMask needs a mask image and contour.fromMask goes the other way. This closes contour -> area / IoU / morphology / convexity. Wire: contours (+ optional image for the canvas size) -> region + mask image. Components are split by 8-connectivity, so region.properties and region.filter see real objects.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        // ── 区域 → 掩膜图（此前没有产出方）──────────────────────────────
        new("region.toMask", "Region to Mask", "Region",
            [Exec, new("region", VisionDataType.Region)],
            [new("image", VisionDataType.Image), new("width", VisionDataType.Integer), new("height", VisionDataType.Integer),
             new("area", VisionDataType.Integer), new("count", VisionDataType.Integer), Diagnostics, Next],
            [
                new("foreground", "Foreground Value", "number", 255, 0, 255, 1, Group: "Values",
                    Description: "Gray value written inside the region. 255 is what region.fromMask and contour.fromMask expect; 1 keeps it usable as a weight map."),
                new("background", "Background Value", "number", 0, 0, 255, 1, Group: "Values",
                    Description: "Gray value outside the region."),
            ],
            "Renders a pixel region into an 8-bit mask image. Every mask consumer in the catalog (region.fromMask, contour.fromMask, image.reduceDomain, inference) had no producer, so a region could not be fed back as a mask, exported, or handed to a model. Wire: region -> image. Region dimensions are carried by the region itself, so no canvas image is needed. algorithmBackend=managed.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        // ── 凸性缺陷（XLD 凸包差）────────────────────────────────────
        new("contour.convexity", "Contour Convexity", "Contour",
            [Exec, new("contours", VisionDataType.Contour)],
            [Found, new("pointCount", VisionDataType.Integer), new("hullPointCount", VisionDataType.Integer),
             new("area", VisionDataType.Double), new("hullArea", VisionDataType.Double), new("convexity", VisionDataType.Double),
             new("maxDefectDepth", VisionDataType.Double), new("maxDefectPoint", VisionDataType.Point2D),
             new("defectCount", VisionDataType.Integer), new("defectsJson", VisionDataType.String),
             new("withinTolerance", VisionDataType.Boolean), Diagnostics, Next],
            [
                Index,
                new("closed", "Closed Contour", "boolean", true,
                    Description: "Treat the polyline as closed. An open contour has no interior, so convexity is meaningless and the node returns found=false."),
                new("minDefectDepth", "Minimum Defect Depth", "number", .5, 0, 1000000, .1, Unit: "px", Group: "Limits",
                    Description: "Depressions shallower than this are ignored when counting defects. Below roughly one pixel you are counting contour sampling noise."),
                new("maxDefectDepth", "Max Defect Depth (0=off)", "number", 0, 0, 1000000, .1, Unit: "px", Group: "Limits",
                    Description: "Optional gate: withinTolerance reports maxDefectDepth <= limit. 0 disables it."),
            ],
            "Convexity of one closed contour: area / hullArea, the deepest dent and how many dents there are. A dent (chipped edge, notch, burr pocket) is a stretch of contour that sits inside the convex hull; the node measures each sample's distance to the hull boundary and groups consecutive samples above minDefectDepth into one defect. jlvision.contourOp has no convexity operator, and building the same result from regions required region.fromContour first. Units: pixels.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: [])
    ];
}
