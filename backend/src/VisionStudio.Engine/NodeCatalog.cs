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
                new("width", VisionDataType.Integer),
                new("height", VisionDataType.Integer),
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
                new("triggerBeforeGrab", "Trigger Before Grab", "boolean", false),
                new("exposureUs", "Exposure (us, 0=leave)", "number", 0, 0, 10000000, 100, Description: "Applied through the camera device layer before grabbing; 0 leaves the current exposure unchanged. Cameras without exposure support fail deterministically."),
                new("gainDb", "Gain (dB, -1=leave)", "number", -1, -1, 48, 0.5, Description: "Applied through the camera device layer before grabbing; -1 leaves the current gain unchanged."),
                // 兼容/宿主注入参数：autoOpen 是 Auto Start 的旧键名；__replay* 只允许离线回放宿主写入。
                new("autoOpen", "Auto Open (legacy)", "boolean", true, Group: "Compatibility", Internal: true,
                    Description: "Legacy alias of Auto Start. Read only when Auto Start is absent."),
                new("__replayInputPath", "Replay Input Path", "text", null, Group: "Offline Replay", Internal: true,
                    Description: "Host-injected offline replay image path. Authorization is enforced by the host replay resolver; workflow documents cannot enable it."),
                new("__replaySequence", "Replay Sequence", "number", null, Group: "Offline Replay", Internal: true),
                new("__replayTimestamp", "Replay Timestamp", "text", null, Group: "Offline Replay", Internal: true),
                new("__replayCameraId", "Replay Camera ID", "text", null, Group: "Offline Replay", Internal: true),
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
                new("width", "Width", "number", 1280, 160, 1920, 1),
                new("height", "Height", "number", 960, 120, 1080, 1),
                new("centerX", "Circle X", "number", 640, 0, 1920, 1),
                new("centerY", "Circle Y", "number", 480, 0, 1080, 1),
                new("radius", "Radius", "number", 80, 5, 400, 1),
                new("placementMode", "Placement Mode", "select", "fixed", Options:
                [new("Fixed", "fixed"), new("Sequence", "sequence"), new("Seeded random", "seededRandom"), new("Random each run", "randomEachRun")]),
                new("sampleIndex", "Sample Index", "number", 0, 0, 10000, 1,
                    Description: "Zero-based sample identifier. The same mode, settings and index reproduce the same image.",
                    VisibleWhen: new("placementMode", ["sequence", "seededRandom"])),
                new("offsetStepX", "X Shift per Sample", "number", 20, -500, 500, 1,
                    Description: "Horizontal translation per sample index in sequence mode.", VisibleWhen: new("placementMode", ["sequence"])),
                new("offsetStepY", "Y Shift per Sample", "number", 0, -500, 500, 1,
                    Description: "Vertical translation per sample index in sequence mode.", VisibleWhen: new("placementMode", ["sequence"])),
                new("angleStepDeg", "Angle Step", "number", 5, -180, 180, 1,
                    Description: "Rotation added per sample index in sequence mode.", VisibleWhen: new("placementMode", ["sequence"])),
                new("randomSeed", "Random Seed", "number", 1, 0, 2147483647, 1,
                    Description: "Fixed seed for repeatable pseudo-random positions.", VisibleWhen: new("placementMode", ["seededRandom"])),
                new("maxOffsetX", "Maximum X Shift", "number", 160, 0, 500, 1,
                    Description: "Random horizontal shift is selected from this symmetric range.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"])),
                new("maxOffsetY", "Maximum Y Shift", "number", 100, 0, 400, 1,
                    Description: "Random vertical shift is selected from this symmetric range.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"])),
                new("maxAngleDeg", "Maximum Angle", "number", 15, 0, 180, 1,
                    Description: "Random angle is selected from the symmetric range -value..+value.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"]))
            ],
            "Generate a large test image with a fixed-size pattern. The pattern can stay fixed, follow a sample-index sequence, use repeatable seeded random offsets, or choose a new valid random placement on every run. Sequence and seeded placements are minimally translated into the image when needed; random-each-run placements are resampled instead.",
            "builtin"),
        new(
            "image.threshold",
            "Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("threshold", VisionDataType.Double, Required: false), Next],
            [
                new("mode", "Threshold Mode", "select", "manual", Options:
                [new("Manual", "manual"), new("Adaptive", "adaptive"), new("Otsu", "otsu")]),
                new("threshold", "Threshold", "number", 128, 0, 255, 1,
                    VisibleWhen: new("mode", ["manual"])),
                new("blockSize", "Block Size", "number", 31, 3, 99, 2,
                    Description: "Local window size (coerced to odd).", VisibleWhen: new("mode", ["adaptive"])),
                new("c", "C Constant", "number", 5, 0, 50, 1,
                    VisibleWhen: new("mode", ["adaptive"])),
                new("method", "Adaptive Method", "select", "mean", Options:
                [new("Mean", "mean"), new("Gaussian", "gaussian")], VisibleWhen: new("mode", ["adaptive"])),
                new("invert", "Invert", "boolean", false,
                    VisibleWhen: new("mode", ["manual", "adaptive", "otsu"]))
            ],
            "Create a binary image with a manual, local adaptive, or Otsu threshold. Adaptive and Otsu modes convert color input to gray; the threshold output is present for manual and Otsu modes.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        // ===== 批次一（2026-10-13 节点补齐）：图像预处理族 =====
        new(
            "image.grayscale",
            "Grayscale",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("diagnostics", VisionDataType.String), Next],
            [
                new("mode", "Mode", "select", "luminance", Description: "Conversion mode: BT.601 luminance, channel average, or per-pixel channel maximum. Unknown modes fail instead of silently using luminance.", Options:
                [
                    new("Luminance (BT.601)", "luminance"),
                    new("Average", "average"),
                    new("Max", "max")
                ])
            ],
            "Convert a color image to single-channel gray for thresholding/measurement (single-channel input passes through unchanged). Strictly validates the mode and emits JSON diagnostics with input/output channels and size.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "image.channel",
            "Channel Extract",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("diagnostics", VisionDataType.String), Next],
            [
                new("channel", "Channel", "select", "gray", Description: "Channel to extract: gray/B/G/R, HSV hue/saturation/value, or average/max gray. Color/HSV channels require a color input; unknown values fail.", Options:
                [
                    new("Gray", "gray"),
                    new("Average Gray", "average"),
                    new("Maximum Gray", "max"),
                    new("Blue", "blue"),
                    new("Green", "green"),
                    new("Red", "red"),
                    new("Hue", "hue"),
                    new("Saturation", "saturation"),
                    new("Value", "value")
                ])
            ],
            "Extract one channel (gray/B/G/R or HSV H/S/V, plus average/max gray) as a single-channel image; color channels require a color input and unknown channels fail deterministically. Emits JSON diagnostics with channel and size.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "image.filter",
            "Image Filter",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("diagnostics", VisionDataType.String, Required: false), Next],
            [
                new("method", "Filter Method", "select", "gaussian", Options:
                [
                    new("Gaussian", "gaussian"),
                    new("Median", "median")
                ]),
                new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2),
                new("sigmaX", "Sigma X", "number", 0, 0, 20, 0.5,
                    Description: "0 derives sigma from the kernel size.", VisibleWhen: new("method", ["gaussian"])),
                new("sigmaY", "Sigma Y", "number", 0, 0, 20, 0.5,
                    Description: "0 mirrors sigmaX.", VisibleWhen: new("method", ["gaussian"]))
            ],
            "Gaussian filtering suppresses general noise; median filtering removes salt-and-pepper noise while preserving edges. Kernel size must be odd.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.blur.gaussian",
            "Gaussian Blur",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("diagnostics", VisionDataType.String), Next],
            [
                new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2, Description: "Gaussian aperture (coerced to an odd value in 1-31 and reported in diagnostics)."),
                new("sigmaX", "Sigma X", "number", 0, 0, 20, 0.5, Description: "0 derives sigma from the kernel size."),
                new("sigmaY", "Sigma Y", "number", 0, 0, 20, 0.5, Description: "0 mirrors sigmaX.")
            ],
            "Gaussian blur for noise suppression before edge/blob analysis; kernel size is coerced to an odd value and reported. Validates finite, non-negative sigma values and emits JSON diagnostics with effective kernel, size and channels.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.blur.median",
            "Median Blur",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("diagnostics", VisionDataType.String), Next],
            [new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2, Description: "Median aperture (coerced to an odd value in 3-31 and reported in diagnostics); larger kernels remove stronger impulse noise.")],
            "Median filter: removes salt-and-pepper noise while preserving edges (odd kernel required). Emits JSON diagnostics with the effective kernel, image size and channel count.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.morphology",
            "Morphology",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("diagnostics", VisionDataType.String, Required: false), Next],
            [
                new("operation", "Operation", "select", "open", Options:
                [
                    new("Erode", "erode"),
                    new("Dilate", "dilate"),
                    new("Open (erode then dilate)", "open"),
                    new("Close (dilate then erode)", "close"),
                    new("White Top Hat (bright details)", "whiteTopHat"),
                    new("Black Bottom Hat (dark details)", "blackBottomHat"),
                    new("Gradient (local gray range)", "gradient")
                ]),
                new("kernelSize", "Kernel Size", "number", 3, 1, 31, 2,
                    VisibleWhen: new("operation", ["erode", "dilate", "open", "close"])),
                new("iterations", "Iterations", "number", 1, 1, 10, 1,
                    VisibleWhen: new("operation", ["erode", "dilate", "open", "close"])),
                new("shape", "Kernel Shape", "select", "rect", Options:
                [
                    new("Rectangle", "rect"),
                    new("Ellipse", "ellipse"),
                    new("Cross", "cross")
                ], VisibleWhen: new("operation", ["erode", "dilate", "open", "close"])),
                new("kernelWidth", "Kernel Width", "number", 15, 1, 255, 2,
                    VisibleWhen: new("operation", ["whiteTopHat", "blackBottomHat", "gradient"])),
                new("kernelHeight", "Kernel Height", "number", 15, 1, 255, 2,
                    VisibleWhen: new("operation", ["whiteTopHat", "blackBottomHat", "gradient"]))
            ],
            "Choose erode/dilate/open/close for binary-shape cleanup, or white top hat, black bottom hat and gradient for grayscale feature extraction. Grayscale modes convert color input to gray and use a rectangular odd-sized kernel.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.threshold.adaptive",
            "Adaptive Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("foregroundRatio", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [
                new("blockSize", "Block Size", "number", 31, 3, 99, 2, Description: "Local window size (coerced to odd)."),
                new("c", "C Constant", "number", 5, 0, 50, 1, Description: "Finite constant subtracted from the local threshold; larger values reduce foreground."),
                new("method", "Method", "select", "mean", Options:
                [
                    new("Mean", "mean"),
                    new("Gaussian", "gaussian")
                ], Description: "Local statistic: plain mean or Gaussian-weighted mean. Unknown methods fail."),
                new("invert", "Invert", "boolean", false)
            ],
            "Local adaptive threshold for uneven illumination (color input is auto-converted to gray). Strictly validates method and C, and emits the foreground pixel ratio plus JSON diagnostics so binarization drift is visible without opening the image.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.threshold.otsu",
            "Otsu Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("threshold", VisionDataType.Double), new("foregroundRatio", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [new("invert", "Invert", "boolean", false, Description: "Invert the binary output (dark objects become foreground).")],
            "Automatic global threshold (Otsu) with the computed threshold exposed for traceability and illumination drift monitoring. Also emits the foreground pixel ratio and JSON diagnostics (auto-gray flag, threshold) for run-to-run comparison.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "measure.blobs",
            "Multi Blob",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
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
                new("mode", "Blob Mode", "select", "all", Options:
                [new("All Blobs", "all"), new("Largest Blob", "largest")]),
                new("minArea", "Min Area", "number", 100, 0, 99999999, 10),
                new("maxArea", "Max Area", "number", 0, 0, 99999999, 10, Description: "0 disables the upper bound.", VisibleWhen: new("mode", ["all"])),
                new("minCircularity", "Min Circularity", "number", 0, 0, 1, 0.05, Description: "4*pi*A/P^2, 1 = perfect circle.", VisibleWhen: new("mode", ["all"])),
                new("minWidth", "Min Width", "number", 0, 0, 99999999, 1, Unit: "px", Description: "0 disables the lower bound.", VisibleWhen: new("mode", ["all"])),
                new("maxWidth", "Max Width", "number", 0, 0, 99999999, 1, Unit: "px", Description: "0 disables the upper bound.", VisibleWhen: new("mode", ["all"])),
                new("minHeight", "Min Height", "number", 0, 0, 99999999, 1, Unit: "px", Description: "0 disables the lower bound.", VisibleWhen: new("mode", ["all"])),
                new("maxHeight", "Max Height", "number", 0, 0, 99999999, 1, Unit: "px", Description: "0 disables the upper bound.", VisibleWhen: new("mode", ["all"])),
                new("minRectangularity", "Min Rectangularity", "number", 0, 0, 1, 0.05, Description: "Area / bounding-box area, 1 = perfect rectangle.", VisibleWhen: new("mode", ["all"])),
                new("maxCount", "Max Count", "number", 20, 1, 200, 1, VisibleWhen: new("mode", ["all"])),
                new("sortBy", "Sort By", "select", "area", Options:
                [
                    new("Area", "area"),
                    new("X (left to right)", "x"),
                    new("Y (top to bottom)", "y"),
                    new("Circularity", "circularity")
                ], VisibleWhen: new("mode", ["all"])),
                new("descending", "Descending", "boolean", true, VisibleWhen: new("mode", ["all"]))
            ],
            "Analyze either all filtered/sorted blobs or only the largest one. Filters combine area, circularity, bounding-box width/height and rectangularity (HALCON select_shape style); sorting supports area, X, Y and circularity. Outputs a DetectionList, the selected target geometry and source image; zero blobs is a normal result (count=0).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "match.template",
            "Template Match",
            "Matching",
            [ExecIn, new("image", VisionDataType.Image), new("template", VisionDataType.Image, Required: false), new("searchRoi", VisionDataType.Roi, Required: false), new("templateRoi", VisionDataType.Roi, Required: false)],
            [
                new("found", VisionDataType.Boolean),
                new("score", VisionDataType.Double),
                new("angle", VisionDataType.Double, Required: false),
                new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("rectangle", VisionDataType.Rectangle2D, Required: false),
                new("scale", VisionDataType.Double, Required: false),
                new("count", VisionDataType.Integer),
                new("detections", VisionDataType.DetectionList),
                Next
            ],
            [
                new("templatePath", "Template File", "text", string.Empty, Description: "Used when the template input port is not connected (absolute path, or relative to the host working directory)."),
                new("angleRangeDeg", "Angle Range (deg)", "number", 0, 0, 180, 1, Description: "0 = no rotation search."),
                new("angleStepDeg", "Angle Step (deg)", "number", 5, 0.5, 30, 0.5),
                new("pyramidLevels", "Pyramid Levels (0=auto)", "number", 0, 0, 8, 1, Description: "0 derives a coarse-to-fine pyramid from the template size. 1 forces single-level search; models are cached by pixel content and levels."),
                new("minScore", "Min Score", "number", 0.5, 0, 1, 0.01),
                new("method", "Method", "select", "ccoeff-normed", Options:
                [
                    new("Normalized cross-correlation", "ccoeff-normed"),
                    new("Normalized squared difference", "sqdiff-normed")
                ]),
                new("maxTargets", "Max Targets", "number", 1, 1, 50, 1, Group: "Search", Description: "Accepted matches. found/score/x/y/angle stay the best match. Default 1 keeps older single-result workflows."),
                new("minCenterDistance", "Min Center Distance", "number", 0, 0, 10000, 1, Unit: "px", Group: "Search", Description: "0 keeps every accepted center. A positive distance drops a later match whose center is closer than this to a better one."),
                new("duplicateOverlapThreshold", "Duplicate Box IoU Threshold (0=off)", "number", 0.3, 0, 1, 0.01, Group: "Search", Description: "Suppress later template matches whose axis-aligned box IoU reaches this threshold; 0 disables overlap suppression."),
                new("scaleMinPercent", "Scale Min", "number", 100, 50, 200, 1, Unit: "%", Group: "Scale", Description: "Uniform scale is a discrete resampled NCC search, not a continuous scaled model. 100 with Scale Max 100 keeps the original single-scale search. Use match.shape for trained scale."),
                new("scaleMaxPercent", "Scale Max", "number", 100, 50, 200, 1, Unit: "%", Group: "Scale", Description: "Largest uniform scale to resample. Must be greater than or equal to Scale Min."),
                new("scaleStepPercent", "Scale Step (%)", "number", 10, 1, 50, 1, Unit: "%", Group: "Scale", Description: "Ignored when min and max are both 100. At most 41 samples; widen the step or narrow the range if the search is rejected.")
            ],
            "JLVision cached NCC pyramid matching with subpixel translation and rotation. Multiple matches use score-ordered box-IoU suppression by default; a search ROI, template mask, and discrete uniform scale range are optional. Defaults stay single-scale and single-result. This is not shape-based matching: there is no polarity ignore, clutter, deformation, or continuous scaled model. Use match.shape for those. Squared-difference mode verifies native NCC proposals using native SSD arithmetic. angle aligns the template with the image (positive = counter-clockwise).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: true)),
        new(
            "measure.blob",
            "Largest Blob",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false),
                new("area", VisionDataType.Double, Required: false),
                new("radius", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("circle", VisionDataType.Circle, Required: false),
                new("count", VisionDataType.Integer),
                new("circularity", VisionDataType.Double, Required: false),
                new("width", VisionDataType.Double, Required: false),
                new("height", VisionDataType.Double, Required: false),
                new("blobs", VisionDataType.String),
                Next
            ],
            [new("minArea", "Min Area", "number", 1000, 1, 999999, 10)],
            "Find the largest blob and return typed geometry plus shape features (circularity, bounding-box size). blobs JSON lists every blob at or above Min Area, largest first; no qualifying blob returns found=false instead of failing.",
            "builtin"),
        new(
            "feature.edge",
            "Edge / Canny",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("edgeCount", VisionDataType.Integer), new("found", VisionDataType.Boolean), new("chainCount", VisionDataType.Integer), new("longestChain", VisionDataType.Integer), new("chains", VisionDataType.String), Next],
            [
                new("lowThreshold", "Low Threshold", "number", 50, 0, 255, 1, Unit: "gray", Group: "Edge"),
                new("highThreshold", "High Threshold", "number", 150, 0, 255, 1, Unit: "gray", Group: "Edge"),
                new("blurSize", "Blur Kernel (0=off)", "number", 0, 0, 7, 2, Unit: "px", Group: "Edge", Description: "Optional Gaussian pre-blur; must be 0, 3, 5, or 7."),
                new("subpixel", "Subpixel Chain Points", "boolean", false, Group: "Edge", Description: "Refine chain points along the local gradient (parabolic peak interpolation) and include them in the chains JSON, stride-sampled to at most 200 points per chain.")
            ],
            "Canny edge extraction with chain analysis. Supports image-space ROI; returns the binary edge image plus per-chain lengths and bounding boxes (chains JSON, longest first), optionally with subpixel-refined chain points. Thresholds must satisfy 0 <= low <= high <= 255.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "feature.line",
            "Line Finder",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("line", VisionDataType.Line2D, Required: false),
                new("length", VisionDataType.Double, Required: false),
                new("angle", VisionDataType.Double, Required: false),
                new("rmsError", VisionDataType.Double, Required: false),
                new("confidence", VisionDataType.Double, Required: false),
                new("count", VisionDataType.Integer),
                new("lines", VisionDataType.String),
                Next
            ],
            [
                new("houghThreshold", "Minimum Edge Evidence", "number", 45, 1, 500, 1, Group: "Detection"),
                new("minLength", "Min Length", "number", 60, 1, 4000, 1, Unit: "px", Group: "Detection"),
                new("maxGap", "Max Gap", "number", 12, 0, 1000, 1, Unit: "px", Group: "Detection"),
                new("cannyLow", "Canny Low Threshold", "number", 50, 0, 255, 1, Unit: "gray", Group: "Edge"),
                new("cannyHigh", "Canny High Threshold", "number", 150, 0, 255, 1, Unit: "gray", Group: "Edge"),
                new("maxLines", "Max Lines", "number", 10, 1, 100, 1, Group: "Detection"),
                new("angleToleranceDeg", "Collinear Angle Tolerance", "number", 5, 0.5, 45, 0.5, Unit: "deg", Group: "Merging"),
                new("distanceTolerance", "Collinear Distance Tolerance", "number", 6, 0, 1000, 1, Unit: "px", Group: "Merging")
            ],
            "Detect multiple lines: collinear segments are clustered and refit with robust total least squares. Returns found=false (instead of failing) when no line qualifies; lines JSON lists every line with length, angle, RMS/max fit error and inlier support. The primary line/length/angle ports carry the longest line.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "feature.circle",
            "Circle Finder",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("circle", VisionDataType.Circle, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("radius", VisionDataType.Double, Required: false),
                new("rmsError", VisionDataType.Double, Required: false),
                new("coverageDeg", VisionDataType.Double, Required: false),
                new("confidence", VisionDataType.Double, Required: false),
                new("count", VisionDataType.Integer),
                new("circles", VisionDataType.String),
                Next
            ],
            [
                new("minRadius", "Min Radius", "number", 15, 1, 5000, 1, Unit: "px", Group: "Geometry"),
                new("maxRadius", "Max Radius", "number", 180, 1, 5000, 1, Unit: "px", Group: "Geometry"),
                new("minDist", "Min Center Distance", "number", 40, 1, 5000, 1, Unit: "px", Group: "Detection"),
                new("edgeThreshold", "Edge Threshold", "number", 100, 1, 500, 1, Group: "Detection"),
                new("centerThreshold", "Center Threshold", "number", 24, 1, 500, 1, Group: "Detection"),
                new("blurSize", "Blur Kernel", "number", 9, 3, 15, 2, Unit: "px", Group: "Detection", Description: "Gaussian pre-blur kernel; must be odd (3-15)."),
                new("refineBand", "Refinement Band", "number", 3, 0.5, 25, 0.5, Unit: "px", Group: "Refinement", Description: "Edge points within this radial distance of a candidate are used for the geometric circle refit."),
                new("maxCircles", "Max Circles", "number", 10, 1, 100, 1, Group: "Detection")
            ],
            "Detect multiple circles: candidates are refined against Canny edge points with a robust geometric circle fit and ranked by inlier support (not largest radius). Returns found=false when no circle qualifies; circles JSON lists center, radius, RMS error and inliers per circle.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "measure.caliper",
            "Caliper Edge",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image), new("searchLine", VisionDataType.Line2D, Required: false), new("transform", VisionDataType.Transform2D, Required: false), new("roi", VisionDataType.Roi, Required: false)],
            [
                new("image", VisionDataType.Image),
                new("found", VisionDataType.Boolean),
                new("point", VisionDataType.Point2D, Required: false),
                new("strength", VisionDataType.Double, Required: false),
                new("fitError", VisionDataType.Double, Required: false),
                new("sampleCount", VisionDataType.Integer, Required: false),
                new("searchLine", VisionDataType.Line2D),
                Next
            ],
            [
                new("startX", "Start X", "number", 120, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("startY", "Start Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("endX", "End X", "number", 520, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("endY", "End Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("sampleWidth", "Projection Length", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling",
                    Description: "The projection width must be odd; even values are rounded up to the next odd value (for example, 4 becomes 5)."),
                new("edgeThreshold", "Edge Threshold", "number", 12, 0, 255, 1, Unit: "gray", Group: "Sampling"),
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
                    new("No parabolic refinement", "none")
                ], Group: "Sampling")
            ],
            "Single-caliper grayscale edge location along a configurable search line. Measurements use 8-bit grayscale; UInt16 input is scaled linearly from its full range to 0..255. Select edge polarity and search order, refine to subpixel, and optionally mask with ROI. No qualified edge returns found=false. Optional rigid px-to-px transform maps reference searchLine/endpoint parameters and sampling direction into the current image.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
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
                new("width", VisionDataType.Double),
                new("height", VisionDataType.Double),
                new("area", VisionDataType.Double),
                new("aspectRatio", VisionDataType.Double),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [new("minArea", "Min Area", "number", 500, 1, 99999999, 10, Unit: "px²", Group: "Contour", Description: "Minimum contour area in px^2; contours below it are ignored and no accepted contour fails deterministically.")],
            "Minimum-area rotated rectangle around the largest accepted contour. Emits rectangle, centre, pose, angle, width/height, contour area, aspect ratio and JSON diagnostics; validates minArea and reports how many external contours were considered.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new(
            "geometry.intersection",
            "Line Intersection",
            "Geometry",
            [ExecIn, new("lineA", VisionDataType.Line2D), new("lineB", VisionDataType.Line2D)],
            [new("point", VisionDataType.Point2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("withinSegments", VisionDataType.Boolean), new("angleDeg", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [new("parallelTolerance", "Parallel Tolerance", "number", 0.000000001, 0, 0.1, 0.000000001, Group: "Validation", Description: "Minimum |determinant| below which lines are rejected as parallel/coincident instead of returning an unstable point."), new("requireWithinSegments", "Require Within Segments", "boolean", false, Group: "Validation", Description: "When true, reject intersections that lie outside either finite segment; when false, intersect the infinite lines.")],
            "Intersect two Line2D values with strict parallel/degenerate validation. Reports the point, segment parameters (withinSegments), inter-line angle and a JSON diagnostics payload; optionally requires the crossing to lie inside both finite segments.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "geometry.distance",
            "Point Distance",
            "Geometry",
            [ExecIn, new("pointA", VisionDataType.Point2D), new("pointB", VisionDataType.Point2D)],
            [new("distance", VisionDataType.Double), new("dx", VisionDataType.Double), new("dy", VisionDataType.Double), new("midX", VisionDataType.Double), new("midY", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [],
            "Euclidean distance between two finite Point2D values, with signed dx/dy, midpoint, direction angle and JSON diagnostics. Non-finite inputs are rejected deterministically instead of propagating NaN downstream.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "geometry.angle",
            "Line Angle",
            "Geometry",
            [ExecIn, new("lineA", VisionDataType.Line2D), new("lineB", VisionDataType.Line2D)],
            [new("angle", VisionDataType.Double), new("signedAngleDeg", VisionDataType.Double), new("lineALength", VisionDataType.Double), new("lineBLength", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [],
            "Acute (0-90 deg) and signed (-180..180 deg) angle between two non-degenerate Line2D values, with both line lengths, parallel/perpendicular flags in JSON diagnostics, and overlay lines for visual debugging.",
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
                new("pointCount", VisionDataType.Integer),
                new("worstPairIndex", VisionDataType.Integer),
                new("residualsJson", VisionDataType.String),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("sourceFrame", "Source Frame", "text", "ImagePixel", Group: "Frames"),
                new("targetFrame", "Target Frame", "text", "Workpiece", Group: "Frames"),
                new("targetUnit", "Target Unit", "select", "mm", Options:
                [
                    new("mm", "mm"),
                    new("cm", "cm"),
                    new("m", "m"),
                    new("px", "px")
                ], Group: "Frames"),
                new("assetId", "Calibration Asset ID", "text", "", Group: "Provenance", Description: "Optional immutable calibration asset provenance."),
                new("assetVersion", "Calibration Asset Version", "number", 0, 0, 999999, 1, Group: "Provenance"),
                new("maxRmse", "Maximum Calibration RMSE (0=off)", "number", 0, 0, 1000000000, 0.001, Unit: "target", Group: "Quality Gate", Description: "Fail the node when the fit RMSE exceeds this value; 0 disables the gate."),
                new("maxPointError", "Maximum Point Error (0=off)", "number", 0, 0, 1000000000, 0.001, Unit: "target", Group: "Quality Gate", Description: "Fail the node when any calibration pair residual exceeds this value; 0 disables the gate."),
                new("pairsJson", "Calibration Pairs JSON", "textarea",
                    "[{\"imageX\":100,\"imageY\":100,\"worldX\":0,\"worldY\":0},{\"imageX\":320,\"imageY\":100,\"worldX\":22,\"worldY\":0},{\"imageX\":540,\"imageY\":100,\"worldX\":44,\"worldY\":0},{\"imageX\":100,\"imageY\":240,\"worldX\":0,\"worldY\":14},{\"imageX\":320,\"imageY\":240,\"worldX\":22,\"worldY\":14},{\"imageX\":540,\"imageY\":240,\"worldX\":44,\"worldY\":14},{\"imageX\":100,\"imageY\":380,\"worldX\":0,\"worldY\":28},{\"imageX\":320,\"imageY\":380,\"worldX\":22,\"worldY\":28},{\"imageX\":540,\"imageY\":380,\"worldX\":44,\"worldY\":28}]",
                    Group: "Calibration",
                    Description: "Four or more image/world point pairs. Nine points are recommended for MVP validation.")
            ],
            "Coordinate-normalized, rank-revealing least-squares planar homography from image coordinates to a named target frame. Requires at least four finite, spatially distributed pairs; reports per-pair residuals and supports optional RMSE and maximum-point-error gates. Singular or poorly conditioned geometry fails deterministically.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "coordinate.transformPoint",
            "Transform Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.Point2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Convert an image-space Point2D into a frame/unit-aware coordinate point through a validated Transform2D. Rejects non-finite points and singular transforms; emits frame, unit and JSON diagnostics alongside the point.",
            "builtin"),
        new(
            "coordinate.transformPoint3D",
            "Transform 3D Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.CoordinatePoint3D), new("transform", VisionDataType.Transform3D)],
            [new("point", VisionDataType.CoordinatePoint3D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("z", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Transform a frame-aware 3D point through a rigid row-major Transform3D. Requires exact source-frame and translation-unit matches, validates the 4x4 rigid transform, and emits target coordinates plus frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformLine",
            "Transform Line",
            "Coordinate",
            [ExecIn, new("line", VisionDataType.Line2D), new("transform", VisionDataType.Transform2D)],
            [new("line", VisionDataType.CoordinateLine2D), new("length", VisionDataType.Measurement), new("lengthValue", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Transform both endpoints of a pixel Line2D into a coordinate-aware line. Validates line degeneracy and transform singularity; emits length, direction angle, frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformPose",
            "Transform Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.Pose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Transform X/Y and local orientation into a named target frame through a validated Transform2D. Rejects non-finite pose values and singular transforms; emits scalars, frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.point",
            "Coordinate Point",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("tree", VisionDataType.FrameTree2D, Required: false)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("x", "X", "number", 20, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Used when the X input is not connected."),
                new("y", "Y", "number", 10, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Used when the Y input is not connected."),
                new("frame", "Frame", "text", "Workpiece", Group: "Point", Description: "Must be non-empty. When a frame tree is connected, the name must exist on that tree. There is no global frame registry."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Point")
            ],
            "Create a coordinate-aware reference point. Optional X/Y inputs override the constants so a match or detection can drive the point. Values must be finite. The frame must be non-empty; a connected frame tree checks it against known frames.",
            "builtin"),
        new(
            "coordinate.point3D",
            "Coordinate Point 3D",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("z", VisionDataType.Double, Required: false)],
            [new("point", VisionDataType.CoordinatePoint3D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("z", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("x", "X", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point"),
                new("y", "Y", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point"),
                new("z", "Z", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point"),
                new("frame", "Frame", "text", "Workpiece", Group: "Point"),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Point")
            ],
            "Create a frame-aware 3D coordinate point. Optional X/Y/Z inputs override constants, so upstream measurements can drive the point. Coordinates must be finite and bounded; frame must be non-empty.",
            "builtin"),
        new(
            "coordinate.distance",
            "Coordinate Distance",
            "Coordinate",
            [ExecIn, new("pointA", VisionDataType.CoordinatePoint2D), new("pointB", VisionDataType.CoordinatePoint2D)],
            [new("measurement", VisionDataType.Measurement), new("distance", VisionDataType.Double), new("dx", VisionDataType.Double), new("dy", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [],
            "Distance between two frame-aware points, only when frame and unit match exactly. Emits a Measurement, scalar distance, signed dx/dy and JSON diagnostics; mismatched or non-finite points fail deterministically.",
            "builtin"),
        new(
            "coordinate.transformConstant",
            "Constant Transform",
            "Coordinate",
            [ExecIn],
            [new("transform", VisionDataType.Transform2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("sourceFrame", "Source Frame", "text", "Workpiece", Group: "Frames", Description: "Frame the transform maps from; must be non-empty."),
                new("targetFrame", "Target Frame", "text", "RobotBase", Group: "Frames", Description: "Frame the transform maps to; must be non-empty."),
                new("unit", "Unit", "select", "mm", Options:
                [
                    new("Pixels (px)", "px"), new("mm", "mm"), new("cm", "cm"), new("m", "m")
                ], Group: "Frames", Description: "Use px for image-space transforms used by image.warpAffine or caliper measurement; use mm/cm/m for engineering coordinates."),
                new("x", "X Translation", "number", 0, -1000000, 1000000, 0.001, Unit: "unit", Group: "Rigid Transform", Description: "Finite translation along the target frame X axis."),
                new("y", "Y Translation", "number", 0, -1000000, 1000000, 0.001, Unit: "unit", Group: "Rigid Transform", Description: "Finite translation along the target frame Y axis."),
                new("angleDeg", "Rotation", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Rigid Transform", Description: "Finite counter-clockwise rotation in degrees.")
            ],
            "Create a rigid 2D transform between named engineering or pixel coordinate frames with strict finite-value, frame and unit validation. Emits translation/rotation scalars, frame names, unit and JSON diagnostics (including determinant).",
            "builtin"),
        new(
            "coordinate.transformInverse",
            "Inverse Transform",
            "Coordinate",
            [ExecIn, new("transform", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [],
            "Invert a Transform2D and swap source/target frame and units. Validates every matrix element and rejects singular transforms deterministically; emits frame names, determinant and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformCompose",
            "Compose Transform",
            "Coordinate",
            [ExecIn, new("transformA", VisionDataType.Transform2D), new("transformB", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [],
            "Compose A:S->M with B:M->T to produce S->T, enforcing frame/unit compatibility and rejecting singular or non-finite transforms. For image geometry, compose roi.transform (ReferenceImage->CurrentImage) as A with image.crop (CurrentImage->CropImage) as B. Emits frames, determinant and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.frameTree",
            "Frame Tree",
            "Coordinate",
            [ExecIn],
            [new("tree", VisionDataType.FrameTree2D), new("transformCount", VisionDataType.Integer), new("frameCount", VisionDataType.Integer), new("frames", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("transformsJson", "Transforms JSON", "textarea",
                    "[{\"sourceFrame\":\"Workpiece\",\"targetFrame\":\"Fixture\",\"unit\":\"mm\",\"x\":100,\"y\":50,\"angleDeg\":5},{\"sourceFrame\":\"Fixture\",\"targetFrame\":\"RobotBase\",\"unit\":\"mm\",\"x\":500,\"y\":200,\"angleDeg\":0}]",
                    Group: "Frame Tree",
                    Description: "Static rigid transforms. Runtime dynamic transforms can still be composed as normal workflow data.")
            ],
            "Build a bidirectional frame graph from validated rigid transforms and resolve transforms by named frame path. Rejects empty, duplicate or non-finite edges deterministically; emits transform/frame counts, the frame list and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.frameResolve",
            "Resolve Frame Transform",
            "Coordinate",
            [ExecIn, new("tree", VisionDataType.FrameTree2D)],
            [new("transform", VisionDataType.Transform2D), new("path", VisionDataType.String), new("hopCount", VisionDataType.Integer), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("sourceFrame", "Source Frame", "text", "Workpiece", Group: "Resolve", Description: "Frame to resolve from; must exist in the frame tree."),
                new("targetFrame", "Target Frame", "text", "RobotBase", Group: "Resolve", Description: "Frame to resolve to; an unreachable target fails deterministically.")
            ],
            "Resolve the shortest transform path in a FrameTree2D, automatically using inverse edges where required. Validates frame names and the resolved transform; emits hop count, frame names, path and JSON diagnostics, and fails when no path exists.",
            "builtin"),
        new(
            "coordinate.pose",
            "Coordinate Pose",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("thetaDeg", VisionDataType.Double, Required: false), new("tree", VisionDataType.FrameTree2D, Required: false)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("x", "X", "number", 500, -1000000, 1000000, 0.001, Group: "Pose", Description: "Used when the X input is not connected."),
                new("y", "Y", "number", 200, -1000000, 1000000, 0.001, Group: "Pose", Description: "Used when the Y input is not connected."),
                new("thetaDeg", "Theta", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pose", Description: "Used when the theta input is not connected. The output angle is normalized to (-180, 180]."),
                new("frame", "Frame", "text", "RobotBase", Group: "Pose", Description: "Must be non-empty. When a frame tree is connected, the name must exist on that tree. There is no global frame registry."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Pose")
            ],
            "Create a frame-aware 2D pose. Optional X/Y/theta inputs override the constants so a match result can drive the pose. Values must be finite and theta is normalized to (-180, 180]. The frame must be non-empty; a connected frame tree checks it against known frames.",
            "builtin"),
        new(
            "coordinate.poseToTransform",
            "Pose to Transform",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D)],
            [new("transform", VisionDataType.Transform2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [new("sourceFrame", "Source Frame", "text", "Tool", Group: "Frames", Description: "Child/tool frame named by the new transform; must differ from the pose frame.")],
            "Interpret a pose expressed in its parent frame as a rigid transform from Source Frame to that parent frame, rejecting a source frame equal to the pose frame. Useful for dynamic Tool->RobotBase transforms. Emits scalars, frames, determinant and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePoint",
            "Transform Coordinate Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.CoordinatePoint2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Transform a frame-aware point through another engineering-frame Transform2D with strict frame/unit, finite-value and singularity validation. Emits the point plus scalar x/y, target frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePose",
            "Transform Coordinate Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Transform a frame-aware pose through another engineering-frame Transform2D with strict frame/unit, finite-value and singularity validation. Emits the pose plus scalar x/y/theta, target frame, unit and JSON diagnostics.",
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
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device"),
                new("triggerMode", "Trigger", "select", "Level", Options:
                [
                    new("Level", "Level"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Condition", Description: "Level waits while the condition is true. Rising waits for false-to-true and Falling for true-to-false. The first sample cannot be an edge."),
                new("stableMs", "Stable Hold", "number", 0, 0, 120000, 1, Unit: "ms", Group: "Wait", Description: "The armed condition must stay true for this long. 0 accepts the first qualifying sample. Cannot exceed Timeout."),
                new("qualityMode", "Accepted Quality", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Good", "Good"),
                    new("Good or Uncertain", "GoodOrUncertain")
                ], Group: "Condition", Description: "Samples outside the accepted quality do not match, do not move the edge memory, and do not count toward the stable hold."),
                new("onTimeout", "On Timeout", "select", "Fail", Options:
                [
                    new("Fail the node", "Fail"),
                    new("Continue unmatched", "Continue")
                ], Group: "Wait", Description: "Fail throws the existing timeout error. Continue succeeds with matched=false so a downstream branch can handle the miss.")
            ],
            "Poll a device tag until the condition is true, an edge is seen, or the timeout expires. Level is the default and still fails on timeout. Rising, falling, stable-hold, quality, and a matched=false timeout result are optional.",
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
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("robot", "Robot", "text", "ABB", Group: "Target"),
                new("guidanceMode", "Guidance Mode", "select", "EyeToHand", Options: [new("Eye-to-Hand", "EyeToHand"), new("Eye-in-Hand", "EyeInHand"), new("Manual", "Manual")], Group: "Target"),
                new("expectedFrame", "Expected Frame", "text", "RobotBase", Group: "Target"),
                new("offsetX", "Pick Offset X", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset"),
                new("offsetY", "Pick Offset Y", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset"),
                new("angleOffsetDeg", "Angle Offset", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pick Offset")
            ],
            "Create a vendor-neutral RobotTarget2D from a pose already resolved into RobotBase (or another configured robot frame). Validates frame, finite pose/offsets and guidance mode; pick offset is applied in pose-local coordinates. Emits frame, unit and JSON diagnostics. Does not send motion to hardware.",
            "builtin"),
        new(
            "robot.j4TcpCompensation",
            "J4 / TCP Compensation",
            "Robot",
            [ExecIn, new("target", VisionDataType.RobotTarget2D)],
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), new("eccentricity", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [
                new("tcpOffsetX", "TCP Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("tcpOffsetY", "TCP Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("j4PivotOffsetX", "J4 Pivot Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("j4PivotOffsetY", "J4 Pivot Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity"),
                new("angleZeroOffsetDeg", "J4 Angle Zero Offset", "number", 0, -360, 360, 0.001, Unit: "deg", Group: "Rotation")
            ],
            "Planar compensation for J4 rotation-center/TCP eccentricity: converts a desired TCP pose into the J4 pivot command pose using (TCP offset - pivot offset) rotated by the commanded angle. Validates all finite inputs; emits eccentricity magnitude and JSON diagnostics. Outputs a target only; it does not send motion to hardware.",
            "builtin"),
        // ===== 批次一（2026-10-13 节点补齐）：通用数值 / 布尔判定族 =====
        new(
            "value.number",
            "Number Constant",
            "Value",
            [ExecIn],
            [new("value", VisionDataType.Double), new("integer", VisionDataType.Integer), new("isInteger", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), Next],
            [new("value", "Value", "number", 0, -1000000000, 1000000000, 1, Description: "Finite constant within the signed 64-bit integer range; non-finite values fail deterministically.")],
            "Emit a constant number (with an integer variant and an isInteger flag) for comparisons, arithmetic and thresholds. Non-finite or out-of-range values are rejected instead of silently propagating; includes JSON diagnostics.",
            "builtin"),
        new(
            "value.arithmetic",
            "Arithmetic",
            "Value",
            [ExecIn, new("a", VisionDataType.Double), new("b", VisionDataType.Double, Required: false)],
            [new("result", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [
                new("op", "Operation", "select", "add", Description: "Strictly one of add/subtract/multiply/divide; unknown operations fail instead of defaulting to add.", Options:
                [
                    new("Add (a + b)", "add"),
                    new("Subtract (a - b)", "subtract"),
                    new("Multiply (a * b)", "multiply"),
                    new("Divide (a / b)", "divide"),
                    new("Power (a ^ b)", "pow"),
                    new("Square root (a)", "sqrt"),
                    new("Absolute value (a)", "abs"),
                    new("Minimum (min a, b)", "min"),
                    new("Maximum (max a, b)", "max"),
                    new("Clamp (a)", "clamp")
                ]),
                new("clampMin", "Clamp Minimum", "number", 0, -1000000000, 1000000000, 0.001, Group: "Clamp", Description: "Lower bound used only by Clamp."),
                new("clampMax", "Clamp Maximum", "number", 1, -1000000000, 1000000000, 0.001, Group: "Clamp", Description: "Upper bound used only by Clamp. Must be greater than or equal to Clamp Minimum.")
            ],
            "Combine measurements. Add, subtract, multiply, divide, power, min, and max read a and b. Square root, absolute value, and clamp read a only. An unknown operation, division by zero, a negative square root, reversed clamp bounds, or a non-finite result fails instead of guessing. Emits JSON diagnostics with operands, operation and result.",
            "builtin"),
        new(
            "value.range",
            "Range Check",
            "Value",
            [ExecIn, new("value", VisionDataType.Double)],
            [new("pass", VisionDataType.Boolean), new("inside", VisionDataType.Boolean), new("margin", VisionDataType.Double), new("deviation", VisionDataType.Double), new("clamped", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [
                new("min", "Min", "number", 0, -1000000000, 1000000000, 1, Description: "Inclusive lower bound; reversed bounds are swapped and reported in diagnostics."),
                new("max", "Max", "number", 100, -1000000000, 1000000000, 1, Description: "Inclusive upper bound; must be finite."),
                new("invert", "Invert", "boolean", false, Description: "When true, pass means the value lies outside the inclusive window.")
            ],
            "Inclusive range check. Reversed min/max are swapped and flagged in diagnostics. deviation is 0 inside the window and the signed distance outside it; clamped is the value limited to the window; margin is the signed distance to the nearest bound. invert flips pass only. Emits pass, inside, margin, deviation, clamped and JSON diagnostics.",
            "builtin"),
        new(
            "logic.boolean",
            "Boolean Logic",
            "Logic",
            [ExecIn, new("a", VisionDataType.Boolean), new("b", VisionDataType.Boolean, Required: false)],
            [new("result", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), Next],
            [
                new("op", "Operation", "select", "and", Description: "Strictly one of and/or/xor/not; NOT uses input a only, unknown operations fail.", Options:
                [
                    new("AND", "and"),
                    new("OR", "or"),
                    new("XOR", "xor"),
                    new("NOT (a)", "not")
                ])
            ],
            "Combine boolean results with strict operation validation (NOT uses a only; unknown operations fail instead of defaulting to AND). Feeds flow.result / flow.if for the final OK/NG disposition; emits JSON diagnostics.",
            "builtin"),
        new(
            "flow.if",
            "If",
            "Flow",
            [ExecIn, new("value", VisionDataType.Double), new("previous", VisionDataType.Boolean, Required: false)],
            [new("true", VisionDataType.Control), new("false", VisionDataType.Control), new("condition", VisionDataType.Boolean), new("value", VisionDataType.Double), new("margin", VisionDataType.Double), new("diagnostics", VisionDataType.String)],
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
                    ], Description: "Used in threshold mode."),
                new("threshold", "Compare Value", "number", 15000, -99999999, 99999999, 1, Description: "Used in threshold mode."),
                new("mode", "Compare Mode", "select", "threshold", Options:
                [
                    new("Threshold", "threshold"),
                    new("Inside range", "range")
                ], Description: "Threshold compares one value. Inside range checks a closed window and ignores the operator."),
                new("tolerance", "Tolerance", "number", 1e-9, 0, 1000000, 1e-9, Description: "Absolute band for == and !=. The default 1e-9 preserves the previous equality behavior."),
                new("rangeMin", "Range Minimum", "number", 0, -99999999, 99999999, 1, Description: "Inclusive lower bound for inside-range mode. Must not exceed Range Maximum."),
                new("rangeMax", "Range Maximum", "number", 100, -99999999, 99999999, 1, Description: "Inclusive upper bound for inside-range mode."),
                new("hysteresis", "Hysteresis", "number", 0, 0, 1000000, 0.001, Description: "Applied only when the previous condition input is connected. There is no memory across runs. 0 disables it.")
            ],
            "Compare a number and branch true or false. Threshold mode keeps the original operators, with a configurable equality tolerance. Inside-range mode checks a closed window. Hysteresis uses the optional previous condition and does not remember state across runs. Emits condition, echoed value, signed margin and JSON diagnostics.",
            "builtin"),
        new(
            "flow.result",
            "Result / Disposition",
            "Flow",
            [ExecIn, new("pass", VisionDataType.Boolean)],
            [new("pass", VisionDataType.Boolean), new("disposition", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Set the automatic inspection disposition (OK/NG) for traceability with JSON diagnostics. Inside Parallel branches the value is scoped to its branch and merged at Join with any-NG semantics.",
            "builtin"),
        new(
            "flow.parallel",
            "Parallel",
            "Flow",
            [ExecIn],
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control), new("diagnostics", VisionDataType.String)],
            [],
            "Start two Workflow Core parallel paths. Emits a JSON diagnostics marker; branch scheduling and any-NG disposition merging are handled by the structured runtime.",
            "builtin"),
        .. Batch2ImageCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch2UtilityCatalog.Items,
        .. Batch3GeometryCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch3RecognitionCatalog.Items,
        .. Batch4IterationCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch5PrecisionCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. CaliperWidthCatalog.Items,
        .. ContourThicknessCatalog.Items,
        .. RegionCatalog.Items,
        .. DefectCatalog.Items,
        .. AlignmentRegionCatalog.Items,
        .. ColorTextCatalog.Items,
        .. GeometryToleranceCatalog.Items,
        .. FeatureRegionCatalog.Items,
        new(
            "flow.join",
            "Join",
            "Flow",
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control)],
            [Next, new("diagnostics", VisionDataType.String)],
            [],
            "Structured branch join marker. Merges Parallel branch dispositions with any-NG semantics (NG wins) into the run disposition; emits a JSON diagnostics marker for tracing.",
            "builtin")
    ];

    // Compute-only built-ins explicitly declare absence of device actions, including for Try validation.
    // File-backed template matching stays outside automatic retry because its file can change between attempts.
    public static readonly IReadOnlyList<NodeCatalogItem> Items = RawItems
        .Select(item => item.Type == "match.template"
            ? item with { Capabilities = (item.Capabilities ?? new VisionToolCapabilities()) with { Deterministic = false } }
            : item.Actions is null && Runtime.VisionExecutionPolicy.IsPipelineEligible(item)
                ? item with { Actions = [] }
                : item)
        .Select(DeclareLocalRoiParameters)
        .ToArray();

    /// <summary>
    /// 执行器通过 <c>NodeDefinition.GetRoi(parameterName)</c> 读取的本地 ROI 参数名。
    /// 目录声明与编译期校验共用这一处，避免"目录里没有、运行时却在读"的漂移。
    /// </summary>
    public static IReadOnlyList<string> RoiParameterNames(string type, VisionToolCapabilities? capabilities)
    {
        var names = new List<string>(3);
        if (capabilities?.SupportsRoi == true || ReadsLocalRoiWithoutCapability(type)) names.Add("roi");
        if (type.Equals("match.shape", StringComparison.OrdinalIgnoreCase))
            names.AddRange(["searchRoi", "templateRoi", "clutterRoi"]);
        if (type.Equals("match.template", StringComparison.OrdinalIgnoreCase))
            names.AddRange(["searchRoi", "templateRoi"]);
        return names;
    }

    // 这些节点运行时会读本地 ROI，但尚未声明 SupportsRoi（能力位待补）；契约仍必须暴露真实参数。
    private static bool ReadsLocalRoiWithoutCapability(string type) =>
        type.Equals("measure.blob", StringComparison.OrdinalIgnoreCase)
        || type.Equals("recognition.barcode", StringComparison.OrdinalIgnoreCase)
        || type.Equals("recognition.ocr", StringComparison.OrdinalIgnoreCase)
        || type.Equals("region.fromRoi", StringComparison.OrdinalIgnoreCase);

    private static NodeCatalogItem DeclareLocalRoiParameters(NodeCatalogItem item)
    {
        var declared = item.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RoiParameterNames(item.Type, item.Capabilities)
            .Where(name => !declared.Contains(name))
            .Select(name => new ParameterDescriptor(name, RoiLabel(name), "roi", Group: "ROI", Internal: true,
                Description: "Canvas-authored ROI object stored on the node. Edited through the ROI overlay, not as a form field."))
            .ToArray();
        return missing.Length == 0 ? item : item with { Parameters = [.. item.Parameters, .. missing] };
    }

    private static string RoiLabel(string name) => name switch
    {
        "searchRoi" => "Search ROI",
        "templateRoi" => "Template ROI",
        "clutterRoi" => "Clutter ROI",
        _ => "ROI"
    };

    public static NodeCatalogItem Require(string type) =>
        Items.FirstOrDefault(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Built-in catalog item '{type}' was not found.");
}
