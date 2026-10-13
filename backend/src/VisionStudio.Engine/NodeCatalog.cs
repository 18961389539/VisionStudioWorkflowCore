namespace VisionStudio.Engine;

public static class BuiltInNodeCatalog
{
    private static readonly PortDescriptor ExecIn = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);

    private static readonly IReadOnlyList<NodeCatalogItem> RawItems =
    [
        new(
            "image.acquire",
            "Acquire Image",
            "Acquisition",
            [ExecIn],
            [
                new("image", VisionDataType.Image),
                new("sequence", VisionDataType.Integer),
                new("timestamp", VisionDataType.String),
                new("cameraId", VisionDataType.String),
                Next
            ],
            [
                new("cameraId", "Camera ID", "text", "virtual-1"),
                new("frameMode", "Frame Mode", "select", "Latest", Options:
                [
                    new("Latest", "Latest"),
                    new("Next", "Next")
                ]),
                new("timeoutMs", "Timeout (ms)", "number", 1500, 50, 10000, 50),
                new("autoStart", "Auto Start", "boolean", true),
                new("triggerBeforeGrab", "Trigger Before Grab", "boolean", false)
            ],
            "Acquire a frame lease from the continuous CameraFrameHub. Latest reuses the newest frame; Next waits for a newer sequence.",
            "builtin",
            // R02：triggerBeforeGrab=true 时节点会主动触发相机（物理动作）；纯取图（false/缺省）保持只读语义。
            Actions: [new(NodeActionKind.CameraTrigger, NodeActionCompletion.Synchronous, WhenParameter: "triggerBeforeGrab", WhenValue: "true")],
            Resources: [new("camera", "cameraId")]),
        new(
            "camera.syncCapture",
            "Synchronized Capture",
            "Acquisition",
            [ExecIn],
            [
                new("frameSet", VisionDataType.FrameSet),
                new("skewUs", VisionDataType.Double),
                new("timestampBasis", VisionDataType.String),
                new("withinTolerance", VisionDataType.Boolean),
                new("cameraCount", VisionDataType.Integer),
                Next
            ],
            [
                new("groupId", "Synchronization Group", "text", "sync-group-1", Group: "Synchronization"),
                new("scheduled", "Scheduled Action", "boolean", false, Group: "Synchronization"),
                new("leadTimeMs", "Scheduled Lead Time", "number", 100, 10, 60000, 10, Unit: "ms", Group: "Synchronization"),
                new("frameTimeoutMs", "Frame Timeout", "number", 3000, 100, 30000, 100, Unit: "ms", Group: "Acquisition")
            ],
            "Issue one synchronization-group Action Command, collect one new frame from every member camera, and output a workflow-owned FrameSet with measured skew.",
            "builtin",
            new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore),
            // R02：同步采集总会下发设备端 Action Command（真实物理触发），恒为设备动作。
            Actions: [new(NodeActionKind.CameraTrigger, NodeActionCompletion.Synchronous)],
            Resources: [new("cameraSyncGroup", "groupId")]),
        new(
            "frameset.image",
            "FrameSet Image",
            "Acquisition",
            [ExecIn, new("frameSet", VisionDataType.FrameSet)],
            [
                new("image", VisionDataType.Image),
                new("sequence", VisionDataType.Integer),
                new("timestamp", VisionDataType.String),
                new("deviceTimestampNs", VisionDataType.Integer, Required: false),
                new("triggerId", VisionDataType.Integer, Required: false),
                new("cameraId", VisionDataType.String),
                Next
            ],
            [new("cameraId", "Camera ID", "text", "camera-1")],
            "Select one camera image from a synchronized FrameSet without cloning the underlying frame.",
            "builtin",
            Resources: [new("camera", "cameraId")]),
        new(
            "image.synthetic",
            "Synthetic Image",
            "Acquisition",
            [],
            [new("image", VisionDataType.Image), Next],
            [
                new("width", "Width", "number", 640, 160, 1920, 1),
                new("height", "Height", "number", 480, 120, 1080, 1),
                new("centerX", "Circle X", "number", 320, 0, 1920, 1),
                new("centerY", "Circle Y", "number", 240, 0, 1080, 1),
                new("radius", "Radius", "number", 80, 5, 400, 1)
            ],
            "Generate a deterministic test image for MVP validation.",
            "builtin"),
        new(
            "image.threshold",
            "Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), Next],
            [new("threshold", "Threshold", "number", 128, 0, 255, 1)],
            "Binary threshold using OpenCvSharp.",
            "builtin"),
        // ===== 批次一（2026-10-13 节点补齐）：图像预处理族 =====
        new(
            "image.grayscale",
            "Grayscale",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), Next],
            [
                new("mode", "Mode", "select", "luminance", Options:
                [
                    new("Luminance (BT.601)", "luminance"),
                    new("Average", "average"),
                    new("Max", "max")
                ])
            ],
            "Convert a color image to single-channel gray for thresholding/measurement (single-channel input passes through).",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "image.channel",
            "Channel Extract",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), Next],
            [
                new("channel", "Channel", "select", "gray", Options:
                [
                    new("Gray", "gray"),
                    new("Blue", "blue"),
                    new("Green", "green"),
                    new("Red", "red"),
                    new("Hue", "hue"),
                    new("Saturation", "saturation"),
                    new("Value", "value")
                ])
            ],
            "Extract one channel (gray/B/G/R or HSV H/S/V) as a single-channel image; color channels require a color input.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "image.blur.gaussian",
            "Gaussian Blur",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), Next],
            [
                new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2),
                new("sigmaX", "Sigma X", "number", 0, 0, 20, 0.5, Description: "0 derives sigma from the kernel size."),
                new("sigmaY", "Sigma Y", "number", 0, 0, 20, 0.5, Description: "0 mirrors sigmaX.")
            ],
            "Gaussian blur for noise suppression before edge/blob analysis; kernel size is coerced to an odd value.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.blur.median",
            "Median Blur",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), Next],
            [new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2)],
            "Median filter: removes salt-and-pepper noise while preserving edges (odd kernel required).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.morphology",
            "Morphology",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), Next],
            [
                new("operation", "Operation", "select", "open", Options:
                [
                    new("Erode", "erode"),
                    new("Dilate", "dilate"),
                    new("Open (erode then dilate)", "open"),
                    new("Close (dilate then erode)", "close")
                ]),
                new("kernelSize", "Kernel Size", "number", 3, 1, 31, 2),
                new("iterations", "Iterations", "number", 1, 1, 10, 1),
                new("shape", "Kernel Shape", "select", "rect", Options:
                [
                    new("Rectangle", "rect"),
                    new("Ellipse", "ellipse"),
                    new("Cross", "cross")
                ])
            ],
            "Morphological erode/dilate/open/close: remove burrs, fill pinholes and stabilize blobs.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.threshold.adaptive",
            "Adaptive Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), Next],
            [
                new("blockSize", "Block Size", "number", 31, 3, 99, 2, Description: "Local window size (coerced to odd)."),
                new("c", "C Constant", "number", 5, 0, 50, 1),
                new("method", "Method", "select", "mean", Options:
                [
                    new("Mean", "mean"),
                    new("Gaussian", "gaussian")
                ]),
                new("invert", "Invert", "boolean", false)
            ],
            "Local adaptive threshold for uneven illumination (color input is auto-converted to gray).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.threshold.otsu",
            "Otsu Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("threshold", VisionDataType.Double), Next],
            [new("invert", "Invert", "boolean", false)],
            "Automatic global threshold (Otsu) with the computed threshold exposed for traceability and illumination drift monitoring.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "measure.blobs",
            "Multi Blob",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("count", VisionDataType.Integer),
                new("detections", VisionDataType.DetectionList),
                new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false),
                new("area", VisionDataType.Double, Required: false),
                new("radius", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("circle", VisionDataType.Circle, Required: false),
                Next
            ],
            [
                new("minArea", "Min Area", "number", 100, 0, 99999999, 10),
                new("maxArea", "Max Area", "number", 0, 0, 99999999, 10, Description: "0 disables the upper bound."),
                new("minCircularity", "Min Circularity", "number", 0, 0, 1, 0.05, Description: "4*pi*A/P^2, 1 = perfect circle."),
                new("maxCount", "Max Count", "number", 20, 1, 200, 1),
                new("sortBy", "Sort By", "select", "area", Options:
                [
                    new("Area", "area"),
                    new("X (left to right)", "x"),
                    new("Y (top to bottom)", "y")
                ]),
                new("descending", "Descending", "boolean", true)
            ],
            "Detect/filter/sort/count multiple blobs; outputs a DetectionList plus the first (sorted) target's geometry. Zero blobs is a normal result (count=0).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "match.template",
            "Template Match",
            "Matching",
            [ExecIn, new("image", VisionDataType.Image), new("template", VisionDataType.Image, Required: false)],
            [
                new("found", VisionDataType.Boolean),
                new("score", VisionDataType.Double),
                new("angle", VisionDataType.Double, Required: false),
                new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("rectangle", VisionDataType.Rectangle2D, Required: false),
                Next
            ],
            [
                new("templatePath", "Template File", "text", string.Empty, Description: "Used when the template input port is not connected (absolute path, or relative to the host working directory)."),
                new("angleRangeDeg", "Angle Range (deg)", "number", 0, 0, 180, 1, Description: "0 = no rotation search."),
                new("angleStepDeg", "Angle Step (deg)", "number", 5, 0.5, 30, 0.5),
                new("minScore", "Min Score", "number", 0.5, 0, 1, 0.01),
                new("method", "Method", "select", "ccoeff-normed", Options:
                [
                    new("Normalized cross-correlation", "ccoeff-normed"),
                    new("Normalized squared difference", "sqdiff-normed")
                ])
            ],
            "Locate a template in the image (optional coarse rotation search); outputs position, angle and score. angle = rotation applied to the template to align with the image (positive = counter-clockwise).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: true)),
        new(
            "measure.blob",
            "Largest Blob",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("x", VisionDataType.Double),
                new("y", VisionDataType.Double),
                new("area", VisionDataType.Double),
                new("radius", VisionDataType.Double),
                new("center", VisionDataType.Point2D),
                new("circle", VisionDataType.Circle),
                Next
            ],
            [new("minArea", "Min Area", "number", 1000, 1, 999999, 10)],
            "Find the largest contour and return typed geometry.",
            "builtin"),
        new(
            "feature.edge",
            "Edge / Canny",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("edgeCount", VisionDataType.Integer), Next],
            [
                new("lowThreshold", "Low Threshold", "number", 50, 0, 255, 1, Unit: "gray", Group: "Edge"),
                new("highThreshold", "High Threshold", "number", 150, 0, 255, 1, Unit: "gray", Group: "Edge")
            ],
            "Canny edge extraction. Supports image-space ROI and returns a binary edge image.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "feature.line",
            "Line Finder",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("line", VisionDataType.Line2D),
                new("length", VisionDataType.Double),
                new("angle", VisionDataType.Double),
                Next
            ],
            [
                new("houghThreshold", "Hough Threshold", "number", 45, 1, 500, 1, Group: "Detection"),
                new("minLength", "Min Length", "number", 60, 1, 4000, 1, Unit: "px", Group: "Detection"),
                new("maxGap", "Max Gap", "number", 12, 0, 1000, 1, Unit: "px", Group: "Detection")
            ],
            "Find the longest Hough line and return a strongly typed Line2D.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "feature.circle",
            "Circle Finder",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("circle", VisionDataType.Circle),
                new("center", VisionDataType.Point2D),
                new("radius", VisionDataType.Double),
                Next
            ],
            [
                new("minRadius", "Min Radius", "number", 15, 1, 5000, 1, Unit: "px", Group: "Geometry"),
                new("maxRadius", "Max Radius", "number", 180, 1, 5000, 1, Unit: "px", Group: "Geometry"),
                new("minDist", "Min Center Distance", "number", 40, 1, 5000, 1, Unit: "px", Group: "Detection"),
                new("edgeThreshold", "Edge Threshold", "number", 100, 1, 500, 1, Group: "Detection"),
                new("centerThreshold", "Center Threshold", "number", 24, 1, 500, 1, Group: "Detection")
            ],
            "Hough circle finder returning Circle + Point2D geometry.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "measure.caliper",
            "Caliper Edge",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("point", VisionDataType.Point2D),
                new("strength", VisionDataType.Double),
                new("searchLine", VisionDataType.Line2D),
                Next
            ],
            [
                new("startX", "Start X", "number", 120, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("startY", "Start Y", "number", 240, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("endX", "End X", "number", 520, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("endY", "End Y", "number", 240, 0, 10000, 1, Unit: "px", Group: "Search Line"),
                new("sampleWidth", "Sample Width", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling"),
                new("edgeThreshold", "Edge Threshold", "number", 12, 0, 255, 1, Unit: "gray", Group: "Sampling"),
                new("polarity", "Polarity", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Sampling")
            ],
            "1D grayscale caliper along a configurable search line. Returns the strongest edge point.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "measure.rotatedRect",
            "Rotated Rectangle",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("rectangle", VisionDataType.Rectangle2D),
                new("center", VisionDataType.Point2D),
                new("pose", VisionDataType.Pose2D),
                new("angle", VisionDataType.Double),
                Next
            ],
            [new("minArea", "Min Area", "number", 500, 1, 99999999, 10, Unit: "px²", Group: "Contour")],
            "Minimum-area rotated rectangle around the largest accepted contour.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "geometry.intersection",
            "Line Intersection",
            "Geometry",
            [ExecIn, new("lineA", VisionDataType.Line2D), new("lineB", VisionDataType.Line2D)],
            [new("point", VisionDataType.Point2D), Next],
            [],
            "Intersect two infinite Line2D values and return Point2D.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "geometry.distance",
            "Point Distance",
            "Geometry",
            [ExecIn, new("pointA", VisionDataType.Point2D), new("pointB", VisionDataType.Point2D)],
            [new("distance", VisionDataType.Double), Next],
            [],
            "Euclidean distance between two Point2D values.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "geometry.angle",
            "Line Angle",
            "Geometry",
            [ExecIn, new("lineA", VisionDataType.Line2D), new("lineB", VisionDataType.Line2D)],
            [new("angle", VisionDataType.Double), Next],
            [],
            "Acute angle between two Line2D values in degrees.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "calibration.planar",
            "Planar Calibration",
            "Calibration",
            [ExecIn],
            [
                new("transform", VisionDataType.Transform2D),
                new("rmse", VisionDataType.Double),
                new("maxError", VisionDataType.Double),
                Next
            ],
            [
                new("sourceFrame", "Source Frame", "text", "ImagePixel", Group: "Frames"),
                new("targetFrame", "Target Frame", "text", "Workpiece", Group: "Frames"),
                new("targetUnit", "Target Unit", "select", "mm", Options:
                [
                    new("mm", "mm"),
                    new("cm", "cm"),
                    new("m", "m")
                ], Group: "Frames"),
                new("assetId", "Calibration Asset ID", "text", "", Group: "Provenance", Description: "Optional immutable calibration asset provenance."),
                new("assetVersion", "Calibration Asset Version", "number", 0, 0, 999999, 1, Group: "Provenance"),
                new("pairsJson", "Calibration Pairs JSON", "textarea",
                    "[{\"imageX\":100,\"imageY\":100,\"worldX\":0,\"worldY\":0},{\"imageX\":320,\"imageY\":100,\"worldX\":22,\"worldY\":0},{\"imageX\":540,\"imageY\":100,\"worldX\":44,\"worldY\":0},{\"imageX\":100,\"imageY\":240,\"worldX\":0,\"worldY\":14},{\"imageX\":320,\"imageY\":240,\"worldX\":22,\"worldY\":14},{\"imageX\":540,\"imageY\":240,\"worldX\":44,\"worldY\":14},{\"imageX\":100,\"imageY\":380,\"worldX\":0,\"worldY\":28},{\"imageX\":320,\"imageY\":380,\"worldX\":22,\"worldY\":28},{\"imageX\":540,\"imageY\":380,\"worldX\":44,\"worldY\":28}]",
                    Group: "Calibration",
                    Description: "Four or more image/world point pairs. Nine points are recommended for MVP validation.")
            ],
            "Least-squares planar homography from pixel coordinates to a named target coordinate frame.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "coordinate.transformPoint",
            "Transform Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.Point2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), Next],
            [],
            "Convert an image-space Point2D into a frame/unit-aware coordinate point.",
            "builtin"),
        new(
            "coordinate.transformLine",
            "Transform Line",
            "Coordinate",
            [ExecIn, new("line", VisionDataType.Line2D), new("transform", VisionDataType.Transform2D)],
            [new("line", VisionDataType.CoordinateLine2D), new("length", VisionDataType.Measurement), new("lengthValue", VisionDataType.Double), Next],
            [],
            "Transform both endpoints of a pixel Line2D into a coordinate-aware line.",
            "builtin"),
        new(
            "coordinate.transformPose",
            "Transform Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.Pose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), Next],
            [],
            "Transform X/Y and local orientation into a named target frame.",
            "builtin"),
        new(
            "coordinate.point",
            "Coordinate Point",
            "Coordinate",
            [ExecIn],
            [new("point", VisionDataType.CoordinatePoint2D), Next],
            [
                new("x", "X", "number", 20, -1000000, 1000000, 0.001, Unit: "target", Group: "Point"),
                new("y", "Y", "number", 10, -1000000, 1000000, 0.001, Unit: "target", Group: "Point"),
                new("frame", "Frame", "text", "Workpiece", Group: "Point"),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Point")
            ],
            "Create a constant coordinate-aware reference point.",
            "builtin"),
        new(
            "coordinate.distance",
            "Coordinate Distance",
            "Coordinate",
            [ExecIn, new("pointA", VisionDataType.CoordinatePoint2D), new("pointB", VisionDataType.CoordinatePoint2D)],
            [new("measurement", VisionDataType.Measurement), new("distance", VisionDataType.Double), Next],
            [],
            "Distance between two points only when frame and unit match.",
            "builtin"),
        new(
            "coordinate.transformConstant",
            "Constant Transform",
            "Coordinate",
            [ExecIn],
            [new("transform", VisionDataType.Transform2D), Next],
            [
                new("sourceFrame", "Source Frame", "text", "Workpiece", Group: "Frames"),
                new("targetFrame", "Target Frame", "text", "RobotBase", Group: "Frames"),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Frames"),
                new("x", "X Translation", "number", 0, -1000000, 1000000, 0.001, Unit: "unit", Group: "Rigid Transform"),
                new("y", "Y Translation", "number", 0, -1000000, 1000000, 0.001, Unit: "unit", Group: "Rigid Transform"),
                new("angleDeg", "Rotation", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Rigid Transform")
            ],
            "Create a rigid 2D transform between named engineering coordinate frames.",
            "builtin"),
        new(
            "coordinate.transformInverse",
            "Inverse Transform",
            "Coordinate",
            [ExecIn, new("transform", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), Next],
            [],
            "Invert a Transform2D and swap source/target frame and units.",
            "builtin"),
        new(
            "coordinate.transformCompose",
            "Compose Transform",
            "Coordinate",
            [ExecIn, new("transformA", VisionDataType.Transform2D), new("transformB", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), Next],
            [],
            "Compose A:S→M with B:M→T to produce S→T, enforcing frame/unit compatibility.",
            "builtin"),
        new(
            "coordinate.frameTree",
            "Frame Tree",
            "Coordinate",
            [ExecIn],
            [new("tree", VisionDataType.FrameTree2D), Next],
            [
                new("transformsJson", "Transforms JSON", "textarea",
                    "[{\"sourceFrame\":\"Workpiece\",\"targetFrame\":\"Fixture\",\"unit\":\"mm\",\"x\":100,\"y\":50,\"angleDeg\":5},{\"sourceFrame\":\"Fixture\",\"targetFrame\":\"RobotBase\",\"unit\":\"mm\",\"x\":500,\"y\":200,\"angleDeg\":0}]",
                    Group: "Frame Tree",
                    Description: "Static rigid transforms. Runtime dynamic transforms can still be composed as normal workflow data.")
            ],
            "Build a bidirectional frame graph and resolve transforms by named frame path.",
            "builtin"),
        new(
            "coordinate.frameResolve",
            "Resolve Frame Transform",
            "Coordinate",
            [ExecIn, new("tree", VisionDataType.FrameTree2D)],
            [new("transform", VisionDataType.Transform2D), new("path", VisionDataType.String), Next],
            [
                new("sourceFrame", "Source Frame", "text", "Workpiece", Group: "Resolve"),
                new("targetFrame", "Target Frame", "text", "RobotBase", Group: "Resolve")
            ],
            "Resolve the shortest transform path in a FrameTree2D, automatically using inverse edges where required.",
            "builtin"),
        new(
            "coordinate.pose",
            "Coordinate Pose",
            "Coordinate",
            [ExecIn],
            [new("pose", VisionDataType.CoordinatePose2D), Next],
            [
                new("x", "X", "number", 500, -1000000, 1000000, 0.001, Group: "Pose"),
                new("y", "Y", "number", 200, -1000000, 1000000, 0.001, Group: "Pose"),
                new("thetaDeg", "Theta", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pose"),
                new("frame", "Frame", "text", "RobotBase", Group: "Pose"),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Pose")
            ],
            "Create a constant frame-aware 2D pose.",
            "builtin"),
        new(
            "coordinate.poseToTransform",
            "Pose to Transform",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D)],
            [new("transform", VisionDataType.Transform2D), Next],
            [new("sourceFrame", "Source Frame", "text", "Tool", Group: "Frames")],
            "Interpret a pose expressed in its parent frame as a rigid transform from Source Frame to that parent frame. Useful for dynamic Tool→RobotBase transforms.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePoint",
            "Transform Coordinate Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.CoordinatePoint2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), Next],
            [],
            "Transform a frame-aware point through another engineering-frame Transform2D with frame/unit validation.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePose",
            "Transform Coordinate Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), Next],
            [],
            "Transform a frame-aware pose through another engineering-frame Transform2D with frame/unit validation.",
            "builtin"),
        new(
            "device.readTag",
            "Read Device Tag",
            "Device",
            [ExecIn],
            [
                new("value", VisionDataType.DeviceTagValue),
                new("boolValue", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device"),
                new("tagId", "Tag ID", "text", "trigger", Group: "Tag"),
                new("fresh", "Fresh Read", "boolean", false, Group: "Read"),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device")
            ],
            "Read a typed tag through DeviceManager. Returns value, quality and timestamp without exposing protocol SDK objects.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false),
            Resources: [new("device", "deviceId")]),
        new(
            "device.writeTag",
            "Write Device Tag",
            "Device",
            [ExecIn, new("value", VisionDataType.Any, Required: false)],
            [
                new("value", VisionDataType.DeviceTagValue),
                new("boolValue", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device"),
                new("tagId", "Tag ID", "text", "statusText", Group: "Tag"),
                new("valueType", "Fallback Type", "select", "String", Options: [new("Boolean", "Boolean"), new("Integer", "Integer"), new("Double", "Double"), new("String", "String")], Group: "Fallback"),
                new("fallbackValue", "Fallback Value", "text", "Vision OK", Group: "Fallback", Description: "Used only when the optional Any input is not connected."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device")
            ],
            "Write a generic device tag. Connected input wins; otherwise the typed fallback parameter is used.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "device.waitTag",
            "Wait Device Tag",
            "Device",
            [ExecIn],
            [
                new("value", VisionDataType.DeviceTagValue),
                new("boolValue", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                new("matched", VisionDataType.Boolean),
                new("waitMs", VisionDataType.Double),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device"),
                new("tagId", "Tag ID", "text", "trigger", Group: "Tag"),
                new("operator", "Condition", "select", "True", Options: [new("True", "True"), new("False", "False"), new("==", "=="), new("!=", "!="), new(">", ">"), new(">=", ">="), new("<", "<"), new("<=", "<=")], Group: "Condition"),
                new("compareValue", "Compare Value", "text", "1", Group: "Condition"),
                new("timeoutMs", "Timeout", "number", 5000, 50, 120000, 50, Unit: "ms", Group: "Wait"),
                new("pollMs", "Poll Interval", "number", 25, 10, 5000, 5, Unit: "ms", Group: "Wait"),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device")
            ],
            "Block the workflow until a tag condition becomes true or the timeout expires. Useful for PLC trigger/ready handshakes.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false),
            Resources: [new("device", "deviceId")]),
        new(
            "device.writeVisionResult",
            "Write Vision Result",
            "Device",
            [ExecIn, new("pass", VisionDataType.Boolean), new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("r", VisionDataType.Double, Required: false)],
            [new("written", VisionDataType.Boolean), new("pass", VisionDataType.Boolean), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), Next],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device"),
                new("resultReadyTag", "Result Ready Tag", "text", "resultReady", Group: "Mapping"),
                new("resultOkTag", "Result OK Tag", "text", "resultOk", Group: "Mapping"),
                new("xTag", "X Tag", "text", "resultX", Group: "Mapping"),
                new("yTag", "Y Tag", "text", "resultY", Group: "Mapping"),
                new("rTag", "R Tag", "text", "resultR", Group: "Mapping"),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device")
            ],
            "Write OK/NG + optional X/Y/R as an ordered PLC result transaction: Ready=0, payload, Ready=1.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "robot.currentPose",
            "Robot Current Pose",
            "Robot",
            [ExecIn],
            [
                new("pose", VisionDataType.CoordinatePose2D),
                new("busy", VisionDataType.Boolean),
                new("inPosition", VisionDataType.Boolean),
                new("targetReady", VisionDataType.Boolean),
                new("execute", VisionDataType.Boolean),
                new("complete", VisionDataType.Boolean),
                new("handshakeError", VisionDataType.Boolean),
                new("ack", VisionDataType.Boolean),
                new("state", VisionDataType.String),
                new("connection", VisionDataType.String),
                new("error", VisionDataType.String),
                Next
            ],
            [
                new("robotId", "Robot ID", "text", "virtual-abb-1", Group: "Robot"),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Robot")
            ],
            "Read the current planar robot pose and neutral handshake state from RobotManager. Vendor protocol objects remain outside Workflow Core.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false),
            Resources: [new("robot", "robotId")]),
        new(
            "robot.executeTarget",
            "Execute Robot Target",
            "Robot",
            [ExecIn, new("target", VisionDataType.RobotTarget2D)],
            [
                new("target", VisionDataType.RobotTarget2D),
                new("currentPose", VisionDataType.CoordinatePose2D),
                new("commandId", VisionDataType.Integer),
                new("inPosition", VisionDataType.Boolean),
                new("state", VisionDataType.String),
                new("traceId", VisionDataType.String),
                new("attempts", VisionDataType.Integer),
                Next
            ],
            [
                new("robotId", "Robot ID", "text", "virtual-abb-1", Group: "Robot"),
                new("action", "Action", "select", "Handshake", Options: [new("Industrial Handshake", "Handshake"), new("Move", "Move"), new("Send Target Only", "SendTarget")], Group: "Command"),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Command"),
                new("waitForInPosition", "Wait For Complete", "boolean", true, Group: "Handshake"),
                new("timeoutMs", "Timeout", "number", 5000, 50, 120000, 50, Unit: "ms", Group: "Handshake"),
                new("maxRetries", "Max Retries", "number", 1, 0, 10, 1, Group: "Handshake"),
                new("retryDelayMs", "Retry Delay", "number", 100, 0, 10000, 10, Unit: "ms", Group: "Handshake"),
                new("autoAck", "Auto Ack", "boolean", true, Group: "Handshake")
            ],
            "Execute a vendor-neutral RobotTarget2D using SendTarget/Move or the standard TargetReady→Execute→Busy→Complete/Error→Ack industrial handshake with timeout/retry policy.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.RobotCommand, NodeActionCompletion.ProtocolConfirmed)],
            Resources: [new("robot", "robotId")]),
        new(
            "robot.guidance2d",
            "Robot Guidance 2D",
            "Robot",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D)],
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), Next],
            [
                new("robot", "Robot", "text", "ABB", Group: "Target"),
                new("guidanceMode", "Guidance Mode", "select", "EyeToHand", Options: [new("Eye-to-Hand", "EyeToHand"), new("Eye-in-Hand", "EyeInHand"), new("Manual", "Manual")], Group: "Target"),
                new("expectedFrame", "Expected Frame", "text", "RobotBase", Group: "Target"),
                new("offsetX", "Pick Offset X", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset"),
                new("offsetY", "Pick Offset Y", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset"),
                new("angleOffsetDeg", "Angle Offset", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pick Offset")
            ],
            "Create a vendor-neutral RobotTarget2D from a pose already resolved into RobotBase (or another configured robot frame).",
            "builtin"),
        new(
            "robot.j4TcpCompensation",
            "J4 / TCP Compensation",
            "Robot",
            [ExecIn, new("target", VisionDataType.RobotTarget2D)],
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), Next],
            [
                new("tcpOffsetX", "TCP Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("tcpOffsetY", "TCP Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("j4PivotOffsetX", "J4 Pivot Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("j4PivotOffsetY", "J4 Pivot Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("angleZeroOffsetDeg", "J4 Angle Zero Offset", "number", 0, -360, 360, 0.001, Unit: "deg", Group: "Rotation")
            ],
            "Planar compensation hook for J4 rotation-center/TCP eccentricity. Outputs a compensated robot target but does not send it to hardware.",
            "builtin"),
        // ===== 批次一（2026-10-13 节点补齐）：通用数值 / 布尔判定族 =====
        new(
            "value.number",
            "Number Constant",
            "Value",
            [ExecIn],
            [new("value", VisionDataType.Double), new("integer", VisionDataType.Integer), Next],
            [new("value", "Value", "number", 0, -1000000000, 1000000000, 1)],
            "Emit a constant number (with an integer variant) for comparisons, arithmetic and thresholds.",
            "builtin"),
        new(
            "value.arithmetic",
            "Arithmetic",
            "Value",
            [ExecIn, new("a", VisionDataType.Double), new("b", VisionDataType.Double)],
            [new("result", VisionDataType.Double), Next],
            [
                new("op", "Operation", "select", "add", Options:
                [
                    new("Add (a + b)", "add"),
                    new("Subtract (a - b)", "subtract"),
                    new("Multiply (a * b)", "multiply"),
                    new("Divide (a / b)", "divide")
                ])
            ],
            "Combine two measurements with one of the four basic operations; division by zero fails explicitly instead of producing NaN/Infinity.",
            "builtin"),
        new(
            "value.range",
            "Range Check",
            "Value",
            [ExecIn, new("value", VisionDataType.Double)],
            [new("pass", VisionDataType.Boolean), Next],
            [
                new("min", "Min", "number", 0, -1000000000, 1000000000, 1),
                new("max", "Max", "number", 100, -1000000000, 1000000000, 1),
                new("invert", "Invert", "boolean", false)
            ],
            "Inclusive range check (min/max are swapped when configured in reverse); invert for exclusive windows.",
            "builtin"),
        new(
            "logic.boolean",
            "Boolean Logic",
            "Logic",
            [ExecIn, new("a", VisionDataType.Boolean), new("b", VisionDataType.Boolean, Required: false)],
            [new("result", VisionDataType.Boolean), Next],
            [
                new("op", "Operation", "select", "and", Options:
                [
                    new("AND", "and"),
                    new("OR", "or"),
                    new("XOR", "xor"),
                    new("NOT (a)", "not")
                ])
            ],
            "Combine boolean results (NOT uses 'a' only); feeds flow.result / flow.if for the final OK/NG disposition.",
            "builtin"),
        new(
            "flow.if",
            "If",
            "Flow",
            [ExecIn, new("value", VisionDataType.Double)],
            [new("true", VisionDataType.Control), new("false", VisionDataType.Control), new("condition", VisionDataType.Boolean)],
            [
                new(
                    "operator",
                    "Operator",
                    "select",
                    ">",
                    Options:
                    [
                        new(">", ">"),
                        new(">=", ">="),
                        new("<", "<"),
                        new("<=", "<="),
                        new("==", "=="),
                        new("!=", "!=")
                    ]),
                new("threshold", "Compare Value", "number", 15000, -99999999, 99999999, 1)
            ],
            "Workflow Core conditional marker.",
            "builtin"),
        new(
            "flow.result",
            "Result / Disposition",
            "Flow",
            [ExecIn, new("pass", VisionDataType.Boolean)],
            [new("pass", VisionDataType.Boolean), new("disposition", VisionDataType.String), Next],
            [],
            "Set the automatic inspection disposition (OK/NG) for traceability. Inside Parallel branches the value is scoped to its branch and merged at Join with any-NG semantics.",
            "builtin"),
        new(
            "flow.parallel",
            "Parallel",
            "Flow",
            [ExecIn],
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control)],
            [],
            "Start two Workflow Core parallel paths.",
            "builtin"),
        .. Batch2ImageCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch2UtilityCatalog.Items,
        .. Batch3GeometryCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch3RecognitionCatalog.Items,
        .. Batch4IterationCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        new(
            "flow.join",
            "Join",
            "Flow",
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control)],
            [Next],
            [],
            "Structured branch join marker. Merges Parallel branch dispositions with any-NG semantics (NG wins) into the run disposition.",
            "builtin")
    ];

    // Compute-only built-ins explicitly declare absence of device actions, including for Try validation.
    // File-backed template matching stays outside automatic retry because its file can change between attempts.
    public static readonly IReadOnlyList<NodeCatalogItem> Items = RawItems.Select(item =>
        item.Type == "match.template"
            ? item with { Capabilities = (item.Capabilities ?? new VisionToolCapabilities()) with { Deterministic = false } }
            : item.Actions is null && Runtime.VisionExecutionPolicy.IsPipelineEligible(item)
                ? item with { Actions = [] }
                : item).ToArray();

    public static NodeCatalogItem Require(string type) =>
        Items.FirstOrDefault(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Built-in catalog item '{type}' was not found.");
}
