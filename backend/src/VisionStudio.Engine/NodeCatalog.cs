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
                new("height", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next
            ],
            [
                new("cameraId", "Camera ID", "text", "virtual-1", Group: "Camera", Description: "Camera device id to acquire from."),
                new("frameMode", "Frame Mode", "select", "Latest", Group: "Capture", Options:
                [
                    new("Latest", "Latest"),
                    new("Next", "Next")
                ], Description: "Latest vs next frame grab mode."),
                new("timeoutMs", "Timeout (ms)", "number", 1500, 50, 10000, 50, Group: "Capture", Description: "Timeout in milliseconds for the operation."),
                new("autoStart", "Auto Start", "boolean", true, Group: "Camera", Description: "Start the camera stream automatically when needed."),
                new("triggerBeforeGrab", "Trigger Before Grab", "boolean", false, Group: "Capture", Description: "Fire a software trigger before grabbing."),
                new("exposureUs", "Exposure (us, 0=leave)", "number", 0, 0, 10000000, 100, Group: "Device Adjust", Description: "Applied through the camera device layer before grabbing; 0 leaves the current exposure unchanged. Cameras without exposure support fail deterministically."),
                new("gainDb", "Gain (dB, -1=leave)", "number", -1, -1, 48, 0.5, Group: "Device Adjust", Description: "Applied through the camera device layer before grabbing; -1 leaves the current gain unchanged."),
                // 兼容/宿主注入参数：autoOpen 是 Auto Start 的旧键名；__replay* 只允许离线回放宿主写入。
                new("autoOpen", "Auto Open (legacy)", "boolean", true, Group: "Compatibility", Internal: true,
                    Description: "Legacy alias of Auto Start. Read only when Auto Start is absent."),
                new("__replayInputPath", "Replay Input Path", "text", null, Group: "Offline Replay", Internal: true,
                    Description: "Host-injected offline replay image path. Authorization is enforced by the host replay resolver; workflow documents cannot enable it."),
                new("__replaySequence", "Replay Sequence", "number", null, Group: "Offline Replay", Internal: true, Description: "Internal replay sequence counter (tools only)."),
                new("__replayTimestamp", "Replay Timestamp", "text", null, Group: "Offline Replay", Internal: true, Description: "Internal replay timestamp (tools only)."),
                new("__replayCameraId", "Replay Camera ID", "text", null, Group: "Offline Replay", Internal: true, Description: "Internal replay camera id (tools only)."),
                new("requireNonEmpty", "Require Non-Empty Frame", "boolean", true, Group: "Capture", Description: "Fail when the grabbed frame is empty."),


                ],
            "Grab one frame from a running camera stream (CameraFrameHub). Wire: no image input; output image feeds preprocess/match; cameraId selects the device. Key params: frameMode Latest reuses the newest frame, Next waits for a newer sequence; timeoutMs; exposureUs=0 and gainDb=-1 leave device settings unchanged; triggerBeforeGrab fires a soft trigger before the grab. Backend is the camera device layer, not JLVision. Common failures: camera not open/autoStart, grab timeout, or camera rejects the exposure/gain set.",
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
                new("cameras", VisionDataType.String),
                new("skewMs", VisionDataType.Double),
                new("groupId", VisionDataType.String),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("groupId", "Synchronization Group", "text", "sync-group-1", Group: "Synchronization", Description: "Non-empty group id (max 128 chars)."),
                new("scheduled", "Scheduled Action", "boolean", false, Group: "Synchronization", Description: "Use scheduled (time-triggered) capture when the camera group supports it."),
                new("leadTimeMs", "Scheduled Lead Time", "number", 100, 10, 60000, 10, Unit: "ms", Group: "Synchronization", Description: "Lead time before the scheduled capture instant; used only when Scheduled Action is enabled.") { VisibleWhen = new("scheduled", ["true"]) },
                new("frameTimeoutMs", "Frame Timeout", "number", 3000, 100, 30000, 100, Unit: "ms", Group: "Acquisition", Description: "Maximum wait in milliseconds for every group member frame after the trigger."),
                new("requireWithinTolerance", "Require Skew Within Tolerance", "boolean", false, Group: "Synchronization", Description: "Fail the node when the captured FrameSet reports withinTolerance=false."),
                new("minCameraCount", "Minimum Cameras", "number", 1, 1, 64, 1, Group: "Synchronization", Description: "Partial captures below this count fail instead of returning a thin FrameSet."),
                new("maxSkewUs", "Max Skew (us, 0=off)", "number", 0, 0, 100000000, 10, Group: "Synchronization", Description: "Fail when the measured trigger skew exceeds this many microseconds; 0 disables this explicit gate (requireWithinTolerance uses the service tolerance).")
            ,







                ],
            "Issue one synchronization-group Action Command, collect one new frame from every member camera, and output a workflow-owned FrameSet with measured skew, strict parameter checks, partial-capture gating and diagnostics.",
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
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [new("cameraId", "Camera ID", "text", "camera-1", Description: "Must match a key in the FrameSet; missing camera or empty image fails."),












                ],
            "Select one camera image from a synchronized FrameSet without cloning the underlying frame. Emits diagnostics with size and skew context.",
            "builtin",
            Resources: [new("camera", "cameraId")]),
        new(
            "image.synthetic",
            "Synthetic Image",
            "Acquisition",
            [],
            [new("image", VisionDataType.Image), new("width", VisionDataType.Integer), new("height", VisionDataType.Integer), new("channels", VisionDataType.Integer), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("width", "Width", "number", 1280, 160, 1920, 1, Group: "Pattern", Description: "Generated image width in pixels."),
                new("height", "Height", "number", 960, 120, 1080, 1, Group: "Pattern", Description: "Generated image height in pixels."),
                new("centerX", "Circle X", "number", 640, 0, 1920, 1, Group: "Pattern", Description: "Pattern centre X in pixels before placement offsets."),
                new("centerY", "Circle Y", "number", 480, 0, 1080, 1, Group: "Pattern", Description: "Pattern centre Y in pixels before placement offsets."),
                new("radius", "Radius", "number", 80, 5, 400, 1, Group: "Pattern", Description: "Radius of the circular pattern feature in pixels."),
                new("placementMode", "Placement Mode", "select", "fixed", Group: "Placement", Options:
                [new("Fixed", "fixed"), new("Sequence", "sequence"), new("Seeded random", "seededRandom"), new("Random each run", "randomEachRun")], Description: "How the pattern moves per sample: fixed, sequence offsets, seeded random, or random each run."),
                new("sampleIndex", "Sample Index", "number", 0, 0, 10000, 1, Group: "Placement",
                    Description: "Zero-based sample identifier. The same mode, settings and index reproduce the same image.",
                    VisibleWhen: new("placementMode", ["sequence", "seededRandom"])),
                new("offsetStepX", "X Shift per Sample", "number", 20, -500, 500, 1, Group: "Placement",
                    Description: "Horizontal translation per sample index in sequence mode.", VisibleWhen: new("placementMode", ["sequence"])),
                new("offsetStepY", "Y Shift per Sample", "number", 0, -500, 500, 1, Group: "Placement",
                    Description: "Vertical translation per sample index in sequence mode.", VisibleWhen: new("placementMode", ["sequence"])),
                new("angleStepDeg", "Angle Step", "number", 5, -180, 180, 1, Group: "Placement",
                    Description: "Rotation added per sample index in sequence mode.", VisibleWhen: new("placementMode", ["sequence"])),
                new("randomSeed", "Random Seed", "number", 1, 0, 2147483647, 1, Group: "Random",
                    Description: "Fixed seed for repeatable pseudo-random positions.", VisibleWhen: new("placementMode", ["seededRandom"])),
                new("maxOffsetX", "Maximum X Shift", "number", 160, 0, 500, 1, Group: "Random",
                    Description: "Random horizontal shift is selected from this symmetric range.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"])),
                new("maxOffsetY", "Maximum Y Shift", "number", 100, 0, 400, 1, Group: "Random",
                    Description: "Random vertical shift is selected from this symmetric range.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"])),
                new("maxAngleDeg", "Maximum Angle", "number", 15, 0, 180, 1, Group: "Random",
                    Description: "Random angle is selected from the symmetric range -value..+value.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"]))
            ,


                ],
            "Generate a large test image with a fixed-size pattern. The pattern can stay fixed, follow a sample-index sequence, use repeatable seeded random offsets, or choose a new valid random placement on every run. Sequence and seeded placements are minimally translated into the image when needed; random-each-run placements are resampled instead.",
            "builtin"),
        new(
            "image.threshold",
            "Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("threshold", VisionDataType.Double, Required: false),
             new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("nonZeroCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("mode", "Threshold Mode", "select", "manual", Group: "Mode", Options:
                [new("Manual", "manual"), new("Adaptive", "adaptive"), new("Otsu", "otsu")], Description: "manual uses Threshold; adaptive uses local mean/Gaussian; otsu picks a global level."),
                new("threshold", "Threshold", "number", 128, 0, 255, 1, Group: "Threshold",
                    Description: "Manual gray threshold (0..255).", VisibleWhen: new("mode", ["manual"])),
                new("blockSize", "Block Size", "number", 31, 3, 99, 2, Group: "Threshold",
                    Description: "Local window size (coerced to odd).", VisibleWhen: new("mode", ["adaptive"])),
                new("c", "C Constant", "number", 5, 0, 50, 1, Group: "Threshold",
                    Description: "Subtracted from the local mean/Gaussian (adaptive only).", VisibleWhen: new("mode", ["adaptive"])),
                new("method", "Adaptive Method", "select", "mean", Group: "Threshold", Options:
                [new("Mean", "mean"), new("Gaussian", "gaussian")], Description: "Local statistic for adaptive thresholding.", VisibleWhen: new("mode", ["adaptive"])),
                new("invert", "Invert", "boolean", false, Group: "Output",
                    Description: "Swap foreground/background polarity.", VisibleWhen: new("mode", ["manual", "adaptive", "otsu"]))
            ],
            "Binary threshold: manual, local adaptive, or Otsu. All modes convert color to gray and scale UInt16 by 1/257 to an 8-bit binary image; manual/Otsu also emit the threshold value. Harsh lighting can make Otsu swallow the target; follow with region cleanup when needed.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        // ===== 批次一（2026-10-13 节点补齐）：图像预处理族 =====
        new(
            "image.grayscale",
            "Grayscale",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("mode", "Mode", "select", "luminance", Description: "Conversion mode: BT.601 luminance, channel average, or per-pixel channel maximum. Unknown modes fail instead of silently using luminance.", Options:
                [
                    new("Luminance (BT.601)", "luminance"),
                    new("Average", "average"),
                    new("Max", "max")
                ])
            ,
                new("weights", "Gray Weights", "select", "bt601", Options: [new("BT.601", "bt601"), new("BT.709", "bt709"), new("Average", "avg")], Description: "Luma weights for RGB to gray.")],
            "Convert color to single-channel gray for thresholding/measurement; single-channel input passes through. Strictly validates mode and emits channel/size diagnostics. Common prelude to threshold, match, and calipers.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "image.channel",
            "Channel Extract",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
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
            ,
                ],
            "Extract one channel (gray/B/G/R or HSV H/S/V, plus average/max gray) as a single-channel image. Color channels require color input; unknown channels fail deterministically.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "image.filter",
            "Image Filter",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String, Required: false), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("method", "Filter Method", "select", "gaussian", Options:
                [
                    new("Gaussian", "gaussian"),
                    new("Median", "median")
                ], Description: "Filter kernel: gaussian for general noise, median for salt-and-pepper noise."),
                new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2, Description: "Odd kernel size in pixels (3..31)."),
                new("sigmaX", "Sigma X", "number", 0, 0, 20, 0.5,
                    Description: "0 derives sigma from the kernel size.", VisibleWhen: new("method", ["gaussian"])),
                new("sigmaY", "Sigma Y", "number", 0, 0, 20, 0.5,
                    Description: "0 mirrors sigmaX.", VisibleWhen: new("method", ["gaussian"]))
            ,
                new("borderType", "Border Type", "select", "reflect", Options: [new("Reflect", "reflect"), new("Replicate", "replicate"), new("Constant", "constant")], Description: "Border extrapolation for the filter kernel.")],
            "Gaussian suppresses general noise; median removes salt-and-pepper while preserving edges. Wire: image → filtered image. Kernel size must be odd. Common before calipers/edges; large kernels hurt localization.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.blur.gaussian",
            "Gaussian Blur",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2, Description: "Gaussian aperture (coerced to an odd value in 1-31 and reported in diagnostics)."),
                new("sigmaX", "Sigma X", "number", 0, 0, 20, 0.5, Description: "0 derives sigma from the kernel size."),
                new("sigmaY", "Sigma Y", "number", 0, 0, 20, 0.5, Description: "0 mirrors sigmaX."),
                new("borderType", "Border Type", "select", "default", Options: [new("Default", "default"), new("Constant", "constant"), new("Replicate", "replicate"), new("Reflect", "reflect"), new("Wrap", "wrap")], Description: "Edge policy; non-default uses pad-then-crop around the Gaussian aperture.")
            ],
            "Gaussian blur for noise suppression before edge/blob analysis; kernel size is coerced to an odd value and reported. Optional borderType pad-then-crop. Validates finite, non-negative sigma values and emits JSON diagnostics.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.blur.median",
            "Median Blur",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [new("kernelSize", "Kernel Size", "number", 5, 3, 31, 2, Description: "Median aperture (coerced to an odd value in 3-31 and reported in diagnostics); larger kernels remove stronger impulse noise."),
             new("borderType", "Border Type", "select", "replicate", Options: [new("Constant", "constant"), new("Replicate", "replicate"), new("Reflect", "reflect"), new("Wrap", "wrap")], Description: "Edge policy applied via pad-then-crop because OpenCV medianBlur has no native borderMode.")],
            "Median filter: removes salt-and-pepper noise while preserving edges (odd kernel required). borderType pads the image before filtering and crops back. Emits JSON diagnostics with kernel, border, size and channels.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.morphology",
            "Morphology",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("stdIntensity", VisionDataType.Double), new("nonZeroCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String, Required: false), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("operation", "Operation", "select", "open", Group: "Operation", Options:
                [
                    new("Erode", "erode"),
                    new("Dilate", "dilate"),
                    new("Open (erode then dilate)", "open"),
                    new("Close (dilate then erode)", "close"),
                    new("White Top Hat (bright details)", "whiteTopHat"),
                    new("Black Bottom Hat (dark details)", "blackBottomHat"),
                    new("Gradient (local gray range)", "gradient")
                ], Description: "Morphology operation: erode, dilate, open, close, white top hat, black bottom hat or gradient."),
                new("kernelSize", "Kernel Size", "number", 3, 1, 31, 2, Group: "Binary Kernel",
                    VisibleWhen: new("operation", ["erode", "dilate", "open", "close"]), Description: "Square structuring-element size for binary erode/dilate/open/close (odd, 1..31). Gray top-hat and gradient use Kernel Width/Height instead."),
                new("iterations", "Iterations", "number", 1, 1, 10, 1, Group: "Binary Kernel",
                    VisibleWhen: new("operation", ["erode", "dilate", "open", "close"]), Description: "How many times the operation repeats (1..10)."),
                new("shape", "Kernel Shape", "select", "rect", Group: "Binary Kernel", Options:
                [
                    new("Rectangle", "rect"),
                    new("Ellipse", "ellipse"),
                    new("Cross", "cross")
                ], VisibleWhen: new("operation", ["erode", "dilate", "open", "close"]), Description: "Structuring-element shape: rect, ellipse or cross."),
                new("kernelWidth", "Kernel Width", "number", 15, 1, 255, 2, Group: "Gray Kernel",
                    VisibleWhen: new("operation", ["whiteTopHat", "blackBottomHat", "gradient"]), Description: "Rectangular structuring-element width for white top-hat, black bottom-hat and gradient (odd, 1..255)."),
                new("kernelHeight", "Kernel Height", "number", 15, 1, 255, 2, Group: "Gray Kernel",
                    VisibleWhen: new("operation", ["whiteTopHat", "blackBottomHat", "gradient"]), Description: "Rectangular structuring-element height for white top-hat, black bottom-hat and gradient (odd, 1..255).")
            ],
            "Image morphology: erode/dilate/open/close for binary cleanup; white/black top-hat and gradient for gray feature extraction. Gray modes convert color to gray and use a rectangular odd kernel. Oversized kernels merge or erase small objects.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.threshold.adaptive",
            "Adaptive Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("foregroundRatio", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), Next],
            [
                new("blockSize", "Block Size", "number", 31, 3, 99, 2, Description: "Local window size (coerced to odd)."),
                new("c", "C Constant", "number", 5, 0, 50, 1, Description: "Finite constant subtracted from the local threshold; larger values increase bright foreground when invert=false (the reverse when inverted)."),
                new("method", "Method", "select", "mean", Options:
                [
                    new("Mean", "mean"),
                    new("Gaussian", "gaussian")
                ], Description: "Local statistic: plain mean or Gaussian-weighted mean. Unknown methods fail."),
                new("invert", "Invert", "boolean", false, Description: "Swap foreground and background after adaptive thresholding.")
            ,

                ],
            "Local adaptive threshold for uneven illumination (color input is auto-converted to gray). Strictly validates method and C, and emits the foreground pixel ratio plus JSON diagnostics so binarization drift is visible without opening the image.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.threshold.otsu",
            "Otsu Threshold",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("threshold", VisionDataType.Double), new("foregroundRatio", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [new("invert", "Invert", "boolean", false, Description: "Invert the binary output (dark objects become foreground)."),

                ],
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
                new("circle", VisionDataType.Circle, Required: false), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("mode", "Blob Mode", "select", "all", Options:
                [new("All Blobs", "all"), new("Largest Blob", "largest")], Group: "Mode", Description: "all returns every filtered blob up to Max Count; largest returns only the largest qualifying blob."),
                new("minArea", "Min Area", "number", 100, 0, 99999999, 10, Group: "Filter", Description: "Minimum accepted area in pixels."),
                new("maxArea", "Max Area", "number", 0, 0, 99999999, 10, Group: "Filter", Description: "0 disables the upper bound.", VisibleWhen: new("mode", ["all"])),
                new("minCircularity", "Min Circularity", "number", 0, 0, 1, 0.05, Group: "Filter", Description: "4*pi*A/P^2, 1 = perfect circle.", VisibleWhen: new("mode", ["all"])),
                new("minWidth", "Min Width", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the lower bound.", VisibleWhen: new("mode", ["all"])),
                new("maxWidth", "Max Width", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the upper bound.", VisibleWhen: new("mode", ["all"])),
                new("minHeight", "Min Height", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the lower bound.", VisibleWhen: new("mode", ["all"])),
                new("maxHeight", "Max Height", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the upper bound.", VisibleWhen: new("mode", ["all"])),
                new("minRectangularity", "Min Rectangularity", "number", 0, 0, 1, 0.05, Group: "Filter", Description: "Area / bounding-box area, 1 = perfect rectangle.", VisibleWhen: new("mode", ["all"])),
                new("maxCount", "Max Count", "number", 20, 1, 200, 1, Group: "Sorting", VisibleWhen: new("mode", ["all"]), Description: "Maximum blobs returned after sorting. Used only in All Blobs mode."),
                new("sortBy", "Sort By", "select", "area", Group: "Sorting", Options:
                [
                    new("Area", "area"),
                    new("X (left to right)", "x"),
                    new("Y (top to bottom)", "y"),
                    new("Circularity", "circularity")
                ], VisibleWhen: new("mode", ["all"]), Description: "Field used to rank blobs before Max Count is applied."),
                new("descending", "Descending", "boolean", true, Group: "Sorting", VisibleWhen: new("mode", ["all"]), Description: "Rank largest or highest first; turn off for ascending order.")
            ,
                ],
            "Analyze either all filtered/sorted blobs or only the largest one. Filters combine area, circularity, bounding-box width/height and rectangularity (HALCON select_shape style); sorting supports area, X, Y and circularity. Outputs a DetectionList, the selected target geometry and source image; zero blobs is a normal result (count=0).",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true), OutputFrames: [new(NodeOutputFrameSource.ImageInput, InputPort: "image")]),
        new(
            "match.template",
            "Template Match",
            "Matching",
            [ExecIn, new("image", VisionDataType.Image), new("template", VisionDataType.Image, Required: false), new("searchRoi", VisionDataType.Roi, Required: false), new("templateRoi", VisionDataType.Roi, Required: false), new("ignoreRoi", VisionDataType.Roi, Required: false)],
            [
                new("found", VisionDataType.Boolean),
                new("score", VisionDataType.Double),
                new("angle", VisionDataType.Double, Required: false),
                new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("rectangle", VisionDataType.Rectangle2D, Required: false),
                new("scale", VisionDataType.Double, Required: false),
                new("scoreMap", VisionDataType.Image, Required: false),
                new("scoreMapStack", VisionDataType.Image, Required: false),
                new("count", VisionDataType.Integer),
                new("detections", VisionDataType.DetectionList),
                new("pyramidLevels", VisionDataType.Double, Required: false),
                new("nmsSuppressedCount", VisionDataType.Double, Required: false),
                new("nmsSoftDecayedCount", VisionDataType.Double, Required: false),
                new("nmsMode", VisionDataType.String, Required: false),
                new("ignoreMode", VisionDataType.String, Required: false),
                new("scaleSearch", VisionDataType.String, Required: false),
                new("scoreMapEmitted", VisionDataType.Boolean, Required: false),
                new("modelCacheHit", VisionDataType.Boolean, Required: false),
                new("polarityMode", VisionDataType.String, Required: false),
                new("angleSpanDeg", VisionDataType.Double, Required: false),
                new("subpixelMethod", VisionDataType.String, Required: false),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("templatePath", "Template File", "text", string.Empty, Group: "Model", Description: "Used when the template input port is not connected (absolute path, or relative to the host working directory)."),
                new("angleRangeDeg", "Angle Range (deg)", "number", 0, 0, 180, 1, Unit: "deg", Group: "Rotation", Description: "Legacy symmetric half-width. When Search Start and End are both 0, a positive Angle Range still opens +/- that window. Set Angle Range to 0 when using an explicit Start/End window (including a true zero-only search)."),
                new("searchAngleStartDeg", "Search Start Angle", "number", 0, -180, 180, 1, Unit: "deg", Group: "Rotation", Description: "Lower angle bound (HALCON find_ncc_model style). Default 0; with End also 0, Angle Range still applies as +/- half-width. Set both Start/End for asymmetric windows."),
                new("searchAngleEndDeg", "Search End Angle", "number", 0, -180, 180, 1, Unit: "deg", Group: "Rotation", Description: "Upper angle bound. Must be >= Search Start. Together with Start, overrides the legacy Angle Range when either bound is non-zero (or Angle Range is 0)."),
                new("angleStepDeg", "Search Angle Step", "number", 5, 0.5, 30, 0.5, Unit: "deg", Group: "Rotation", Description: "Angular search step in degrees inside the configured angle range."),
                new("pyramidLevels", "Pyramid Levels (0=auto)", "number", 0, 0, 8, 1, Group: "Rotation", Description: "0 derives a coarse-to-fine pyramid from the template size. 1 forces single-level search; models are cached by pixel content, levels, angle coverage and polarity."),
                new("parameterSource", "Parameter Source", "select", "manual", Options:
                [
                    new("Manual (engineer-set values)", "manual"),
                    new("Automatic (JLVision determine once per template)", "auto")
                ], Group: "Auto Tune", Description: "manual keeps existing NCC recipes unchanged. auto calls determine_ncc_model_params once per template (cached by templatePath+mtime, or by a sampled pixel fingerprint for port templates) and overrides Pyramid Levels and Angle Step before CreateNccModel. The find path is untouched."),
                new("autoTuneProfile", "Auto Tune Profile", "select", "balanced", Options:
                [
                    new("Balanced", "balanced"),
                    new("Speed (one level less, coarser step)", "speed"),
                    new("Accuracy (one level more, finer step)", "accuracy")
                ], Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Post-adjusts the native recommendation inside the pyramid-level budget."),
                new("autoTuneLockedParams", "Locked Parameters", "text", "", Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Comma-separated parameters the auto tune must not override: num_levels, angle_step. Locked values keep the node's current setting."),
                new("ignorePolarity", "Ignore Polarity", "boolean", false, Group: "Matching", Description: "When true, CreateNccModel uses ignore_global_polarity so light/dark inverted copies can still match. Default use_polarity requires the same contrast polarity as the template."),
                new("emitScoreMap", "Emit Score Map", "boolean", false, Group: "Diagnostics", Description: "When true, emit an 8-bit OpenCV unit-scale NCC response image on scoreMap (full scene size, template top-left convention). Angle comes from scoreMapAngleMode. Not the JLVision pyramid search surface. Default off for speed."),
                new("scoreMapAngleMode", "Score Map Angle", "select", "zero", Group: "Diagnostics", VisibleWhen: new("emitScoreMap", ["true"]), Options:
                [
                    new("0 deg (PD-5)", "zero"),
                    new("Best matched angle", "matched"),
                    new("Search window midpoint", "searchMid"),
                    new("Multi-angle stack", "stack")
                ], Description: "Rotation of the OpenCV diagnostic template. stack samples the search angle window into up to scoreMapStackMaxPlanes planes, emits scoreMap as a max projection and scoreMapStack as a vertical montage. Still discrete unit-scale planes — not a JLVision pyramid cube."),
                new("scoreMapStackMaxPlanes", "Score Map Stack Planes", "number", 9, 1, 17, 1, Group: "Diagnostics", VisibleWhen: new("emitScoreMap", ["true"]), Description: "Max planes when Score Map Angle is stack. Actual count is min(this, samples implied by angle step across the search window), at least 2 when the window has span."),
                new("scoreMapWeighting", "Score Map Weighting", "select", "canvas", Group: "Diagnostics", VisibleWhen: new("emitScoreMap", ["true"]), Options:
                [
                    new("Canvas post (PD-5/6)", "canvas"),
                    new("OpenCV template mask", "templateMask")
                ], Description: "canvas: MatchTemplate then apply scene ignoreRoi on the canvas. templateMask: true OpenCV MatchTemplate(..., mask) using templateRoi (or all-ones). Scene ignoreRoi is still applied afterward. Diagnostic only — not the JLVision FindNccModel path."),
                new("ignoreMode", "Ignore Mode", "select", "hard", Group: "Ignore Region", Options:
                [
                    new("Hard (ReduceDomain)", "hard"),
                    new("Soft (mean-lerp + score weight)", "soft")
                ], Description: "Default hard uses JLVision ReduceDomain + reject centers inside ignoreRoi. soft is explicit engine-managed mean-lerp (not JL soft-NCC): lerp ignored pixels toward scene mean by (1-ignoreSoftWeight), optional feather, and multiply candidate scores by residual weight."),
                new("ignoreSoftWeight", "Ignore Soft Weight", "number", 0, 0, 1, 0.05, Group: "Ignore Region", VisibleWhen: new("ignoreMode", ["soft"]), Description: "Residual weight inside ignoreRoi when Ignore Mode is soft (0 ≈ full mean-fill / score suppress; 1 ≈ no ignore). Ignored for hard mode."),
                new("ignoreFeatherPx", "Ignore Feather (px)", "number", 0, 0, 64, 1, Unit: "px", Group: "Ignore Region", VisibleWhen: new("ignoreMode", ["soft"]), Description: "Gaussian feather on the soft weight mask / score-map weights. 0 = sharp soft edge. Hard mode ignores this."),
                new("minScore", "Min Score", "number", 0.5, 0, 1, 0.01, Group: "Matching", Description: "Minimum NCC score (0..1) for a match to be reported as found."),
                new("method", "Method", "select", "ccoeff-normed", Group: "Matching", Options:
                [
                    new("Normalized cross-correlation", "ccoeff-normed"),
                    new("Normalized squared difference", "sqdiff-normed")
                ], Description: "Template matching score method used by the NCC search."),
                new("maxTargets", "Max Targets", "number", 1, 1, 50, 1, Group: "Multi-target", Description: "Accepted matches. found/score/x/y/angle stay the best match. Default 1 keeps older single-result workflows."),
                new("minCenterDistance", "Min Center Distance", "number", 0, 0, 10000, 1, Unit: "px", Group: "Multi-target", Description: "0 keeps every accepted center. A positive distance drops a later match whose center is closer than this to a better one."),
                new("duplicateOverlapThreshold", "Duplicate Box IoU Threshold (0=off)", "number", 0.3, 0, 1, 0.01, Group: "Multi-target", Description: "IoU gate for multi-match NMS on axis-aligned boxes. hard: drop later overlaps; soft: linear Soft-NMS decay score*=(1-IoU). 0 disables overlap NMS (center distance still applies)."),
                new("nmsMode", "NMS Mode", "select", "hard", Group: "Multi-target", Options:
                [
                    new("Hard (drop overlaps)", "hard"),
                    new("Soft (linear Soft-NMS)", "soft")
                ], Description: "Default hard prefers JLVision FindNccModel MaxOverlap for primary multi-match dedupe, with a light AABB fine pass. soft (advanced): raise native MaxOverlap and apply linear Soft-NMS in C#. AABB IoU only; not oriented-box NMS."),
                new("nmsSortKey", "Rank / Sort Key", "select", "score", Group: "Multi-target", Options:
                [
                    new("Score only", "score"),
                    new("Score then |angle|", "scoreThenAbsAngle"),
                    new("Score then |scale-1|", "scoreThenScale")
                ], Description: "Candidate order before NMS. scoreThenAbsAngle prefers nearer 0 deg on ties; scoreThenScale prefers nearer 100% scale. Primary key is always match score (after soft-ignore weight)."),
                new("nmsAngleToleranceDeg", "NMS Angle Tolerance (0=off)", "number", 0, 0, 180, 1, Unit: "deg", Group: "Multi-target", Description: "Angle-aware suppress: when >0, IoU NMS only applies if |delta angle| is within this tolerance (circular). Distinct orientations at the same place can both survive. 0 = classic angle-blind IoU."),
                new("scaleMinPercent", "Scale Min", "number", 100, 50, 200, 1, Unit: "%", Group: "Scale", Description: "Uniform scale is a discrete resampled NCC search, not a continuous scaled model. 100 with Scale Max 100 keeps the original single-scale search. Use match.shape for trained scale."),
                new("scaleMaxPercent", "Scale Max", "number", 100, 50, 200, 1, Unit: "%", Group: "Scale", Description: "Largest uniform scale to resample. Must be greater than or equal to Scale Min."),
                new("scaleStepPercent", "Scale Search Step", "number", 10, 1, 50, 1, Unit: "%", Group: "Scale", Description: "Ignored when min and max are both 100. At most 41 samples; widen the step or narrow the range if the search is rejected.")
            ,
                new("diagnosticsLevel", "Diagnostics Level", "select", "standard", Group: "Diagnostics", Options: [new("Minimal", "minimal"), new("Standard", "standard"), new("Verbose", "verbose")], Description: "Controls how many fields are included in the diagnostics JSON payload."),
                ],
            "JLVision cached NCC pyramid matching (CreateNccModel / FindNccModel). Wire: scene image + template port or templatePath; optional templateRoi (ReduceDomain before create), searchRoi, ignoreRoi; outputs found/score/pose/detections for roi.transform or measurement. Key params: minScore, angle window (searchAngleStart/End or angleRangeDeg), pyramidLevels, maxTargets, duplicateOverlapThreshold, ignorePolarity; default hard NMS prefers FindNccModel MaxOverlap. algorithmBackend=jlvision. Explicit-only engine extras: soft ignoreMode (mean-lerp, not JL soft-NCC) and Soft-NMS; scale search is discrete resampled models; scoreMap is diagnostic OpenCV unit-scale planes, not a JLVision pyramid score cube. No clutter/deformation/continuous scaled NCC — use match.shape. angle is CCW template-to-image. found=false: low score, outside angle window, ignoreRoi blocks, or missing template.",
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
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false),
                Next
            ],
            [new("minArea", "Min Area", "number", 1000, 1, 999999, 10, Description: "Minimum blob area in pixels; smaller components are ignored.")],
            "Largest blob with typed geometry and shape features; blobs JSON lists every blob ≥ minArea, largest first. No qualifying blob → found=false (does not throw). Multi-target: use measure.blobs.",
            "builtin"),
        new(
            "feature.edge",
            "Edge / Canny",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("edgeCount", VisionDataType.Integer), new("found", VisionDataType.Boolean), new("chainCount", VisionDataType.Integer), new("longestChain", VisionDataType.Integer), new("chains", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("lowThreshold", "Low Threshold", "number", 50, 0, 255, 1, Unit: "gray", Group: "Edge", Description: "Canny low hysteresis threshold (0..255, <= highThreshold)."),
                new("highThreshold", "High Threshold", "number", 150, 0, 255, 1, Unit: "gray", Group: "Edge", Description: "Canny high hysteresis threshold (0..255, >= lowThreshold)."),
                new("blurSize", "Blur Kernel (0=off)", "number", 0, 0, 7, 2, Unit: "px", Group: "Edge", Description: "Optional Gaussian pre-blur; must be 0, 3, 5, or 7."),
                new("subpixel", "Subpixel Chain Points", "boolean", false, Group: "Edge", Description: "Refine chain points along the local gradient (parabolic peak interpolation) and include them in the chains JSON, stride-sampled to at most 200 points per chain.")


                ],
            "Canny edge extraction with chain analysis and optional image ROI. Returns binary edge image plus chains JSON (optional subpixel points). Thresholds must satisfy 0≤low≤high≤255. Denoise first when noisy; no chains → count 0.",
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
                new("lines", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next
            ],
            [
                new("houghThreshold", "Minimum Edge Evidence", "number", 45, 1, 500, 1, Group: "Detection", Description: "Hough accumulator threshold for line votes."),
                new("minLength", "Min Length", "number", 60, 1, 4000, 1, Unit: "px", Group: "Detection", Description: "Minimum accepted line length in pixels."),
                new("maxGap", "Max Gap", "number", 12, 0, 1000, 1, Unit: "px", Group: "Detection", Description: "Maximum gap bridged when merging collinear segments."),
                new("cannyLow", "Canny Low Threshold", "number", 50, 0, 255, 1, Unit: "gray", Group: "Edge", Description: "Canny low hysteresis threshold."),
                new("cannyHigh", "Canny High Threshold", "number", 150, 0, 255, 1, Unit: "gray", Group: "Edge", Description: "Canny high hysteresis threshold."),
                new("maxLines", "Max Lines", "number", 10, 1, 100, 1, Group: "Detection", Description: "Maximum lines returned."),
                new("angleToleranceDeg", "Collinear Angle Tolerance", "number", 5, 0.5, 45, 0.5, Unit: "deg", Group: "Merging", Description: "Angle clustering tolerance in degrees."),
                new("distanceTolerance", "Collinear Distance Tolerance", "number", 6, 0, 1000, 1, Unit: "px", Group: "Merging", Description: "Distance clustering tolerance in pixels.")
            ,


                ],
            "Multi-line finder: edges → Hough/LSD → collinear merge → JLVision point-set line fit. Wire: image → primary line/length/angle plus lines JSON (RMS/inliers). Key params: houghThreshold, minLength, maxGap, Canny thresholds, maxLines, collinear tolerances. found=false when nothing qualifies (does not throw).",
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
                new("circles", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next
            ],
            [
                new("minRadius", "Min Radius", "number", 15, 1, 5000, 1, Unit: "px", Group: "Geometry", Description: "Smallest circle radius to detect, in pixels."),
                new("maxRadius", "Max Radius", "number", 180, 1, 5000, 1, Unit: "px", Group: "Geometry", Description: "Largest circle radius to detect, in pixels."),
                new("minDist", "Min Center Distance", "number", 40, 1, 5000, 1, Unit: "px", Group: "Detection", Description: "Minimum distance between detected circle centers; closer candidates are suppressed."),
                new("edgeThreshold", "Edge Threshold", "number", 100, 1, 500, 1, Group: "Detection", Description: "Canny high threshold used to build the edge map for circle detection."),
                new("centerThreshold", "Center Threshold", "number", 24, 1, 500, 1, Group: "Detection", Description: "Accumulator threshold for circle centers; lower values detect more, weaker circles."),
                new("blurSize", "Blur Kernel", "number", 9, 3, 15, 2, Unit: "px", Group: "Detection", Description: "Gaussian pre-blur kernel; must be odd (3-15)."),
                new("refineBand", "Refinement Band", "number", 3, 0.5, 25, 0.5, Unit: "px", Group: "Refinement", Description: "Edge points within this radial distance of a candidate are used for the geometric circle refit."),
                new("maxCircles", "Max Circles", "number", 10, 1, 100, 1, Group: "Detection", Description: "Maximum number of circles returned after ranking by inlier support.")
            ,


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
                new("searchLine", VisionDataType.Line2D), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next
            ],
            [
                new("startX", "Start X", "number", 120, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start X in pixels.") { OverriddenByPorts = ["searchLine"] },
                new("startY", "Start Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start Y in pixels.") { OverriddenByPorts = ["searchLine"] },
                new("endX", "End X", "number", 520, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end X in pixels.") { OverriddenByPorts = ["searchLine"] },
                new("endY", "End Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end Y in pixels.") { OverriddenByPorts = ["searchLine"] },
                new("sampleWidth", "Projection Length", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling",
                    Description: "The projection width must be odd; even values are rounded up to the next odd value (for example, 4 becomes 5)."),
                new("edgeThreshold", "Edge Threshold", "number", 12, 0, 255, 1, Unit: "gray", Group: "Sampling", Description: "Minimum edge strength to accept a caliper hit."),
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
                ], Group: "Sampling", Description: "Enable subpixel refinement of the edge location."),
                new("diagnosticsLevel", "Diagnostics Level", "select", "standard", Options: [new("Minimal", "minimal"), new("Standard", "standard"), new("Verbose", "verbose")], Description: "Diagnostic detail level.")
            ],
            "Single-caliper grayscale edge location along a configurable search line. Measurements use 8-bit grayscale; UInt16 input is scaled linearly from its full range to 0..255. Select edge polarity and search order, refine to subpixel, and optionally mask with ROI. No qualified edge returns found=false. Optional rigid px-to-px transform maps reference searchLine/endpoint parameters and sampling direction into the current image.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true), RoiImageBindings: [new("roi", "image")]),
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
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false),
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
            [new("point", VisionDataType.Point2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("withinSegments", VisionDataType.Boolean), new("angleDeg", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [new("parallelTolerance", "Parallel Tolerance", "number", 0.000000001, 0, 0.1, 0.000000001, Group: "Validation", Description: "Minimum |determinant| below which lines are rejected as parallel/coincident instead of returning an unstable point."), new("requireWithinSegments", "Require Within Segments", "boolean", false, Group: "Validation", Description: "When true, reject intersections that lie outside either finite segment; when false, intersect the infinite lines."),
                ],
            "Intersect two Line2D values with strict parallel/degenerate validation. Reports point, segment membership (withinSegments), inter-line angle and diagnostics; optionally require the crossing inside both finite segments. Parallel or near-parallel lines fail execution.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "geometry.distance",
            "Point Distance",
            "Geometry",
            [ExecIn, new("pointA", VisionDataType.Point2D), new("pointB", VisionDataType.Point2D)],
            [new("distance", VisionDataType.Double), new("dx", VisionDataType.Double), new("dy", VisionDataType.Double), new("midX", VisionDataType.Double), new("midY", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [new("metric", "Distance Metric", "select", "euclidean", Options: [new("Euclidean", "euclidean"), new("Manhattan", "manhattan")], Description: "Distance metric between the two points."),
                ],
            "Euclidean or Manhattan distance between two finite Point2D values, with signed dx/dy, midpoint, direction angle and JSON diagnostics. Non-finite inputs are rejected deterministically (no NaN propagation).",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: true)),
        new(
            "geometry.angle",
            "Line Angle",
            "Geometry",
            [ExecIn, new("lineA", VisionDataType.Line2D), new("lineB", VisionDataType.Line2D)],
            [new("angle", VisionDataType.Double), new("signedAngleDeg", VisionDataType.Double), new("lineALength", VisionDataType.Double), new("lineBLength", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("degrees", VisionDataType.Double), new("radians", VisionDataType.Double), new("quadrant", VisionDataType.Integer), Next],
            [
                ],
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
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("sourceFrame", "Source Frame", "text", "ImagePixel", Group: "Frames", Description: "Name of the source coordinate frame."),
                new("targetFrame", "Target Frame", "text", "Workpiece", Group: "Frames", Description: "Name of the target coordinate frame."),
                new("targetUnit", "Target Unit", "select", "mm", Options:
                [
                    new("mm", "mm"),
                    new("cm", "cm"),
                    new("m", "m"),
                    new("px", "px")
                ], Group: "Frames", Description: "Unit of the target frame."),
                new("assetId", "Calibration Asset ID", "text", "", Group: "Provenance", Description: "Optional immutable calibration asset provenance."),
                new("assetVersion", "Calibration Asset Version", "number", 0, 0, 999999, 1, Group: "Provenance", Description: "Calibration asset schema version."),
                new("maxRmse", "Maximum Calibration RMSE (0=off)", "number", 0, 0, 1000000000, 0.001, Unit: "target", Group: "Quality Gate", Description: "Fail the node when the fit RMSE exceeds this value; 0 disables the gate."),
                new("maxPointError", "Maximum Point Error (0=off)", "number", 0, 0, 1000000000, 0.001, Unit: "target", Group: "Quality Gate", Description: "Fail the node when any calibration pair residual exceeds this value; 0 disables the gate."),
                new("pairsJson", "Calibration Pairs JSON", "textarea", "[]",
                    Group: "Calibration",
                    Description: "JSON array of at least 4 objects: {imageX,imageY,worldX,worldY}. Nine well-spread pairs are recommended for stable validation.")
            ,







                ],
            "Coordinate-normalized, rank-revealing least-squares planar homography from image coordinates to a named target frame. Requires at least four finite, spatially distributed pairs; reports per-pair residuals and supports optional RMSE and maximum-point-error gates. Singular or poorly conditioned geometry fails deterministically.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "coordinate.transformPoint",
            "Transform Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.Point2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [
                ],
            "Map an image-space Point2D through a validated Transform2D into a frame/unit-aware point. Wire: point + transform → point + frame/unit/diagnostics. Rejects non-finite points and singular transforms. Typical after match/register.",
            "builtin"),
        new(
            "coordinate.transformPoint3D",
            "Transform 3D Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.CoordinatePoint3D), new("transform", VisionDataType.Transform3D)],
            [new("point", VisionDataType.CoordinatePoint3D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("z", VisionDataType.Double), new("magnitude", VisionDataType.Double), new("xyMagnitude", VisionDataType.Double), new("azimuthDeg", VisionDataType.Double), new("elevationDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [],
            "Transform a frame-aware 3D point through a rigid row-major Transform3D. Requires exact source-frame and translation-unit matches, validates the 4x4 rigid transform, and emits target coordinates plus frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformLine",
            "Transform Line",
            "Coordinate",
            [ExecIn, new("line", VisionDataType.Line2D), new("transform", VisionDataType.Transform2D)],
            [new("line", VisionDataType.CoordinateLine2D), new("length", VisionDataType.Measurement), new("lengthValue", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [
                new("minLength", "Min Length (0=off)", "number", 0, 0, 1e9, 0.001, Description: "Optional minimum transformed length gate; 0 disables."),
                ],
            "Transform both endpoints of a pixel Line2D into a coordinate-aware line. Validates line degeneracy and transform singularity; emits length, direction angle, frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformPose",
            "Transform Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.Pose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [
                ],
            "Transform X/Y and local orientation into a named target frame through a validated Transform2D. Rejects non-finite pose values and singular transforms; emits scalars, frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.point",
            "Coordinate Point",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("tree", VisionDataType.FrameTree2D, Required: false)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("x", "X", "number", 20, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Used when the X input is not connected.") { OverriddenByPorts = ["x"] },
                new("y", "Y", "number", 10, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Used when the Y input is not connected.") { OverriddenByPorts = ["y"] },
                new("frame", "Frame", "text", "Workpiece", Group: "Point", Description: "Must be non-empty. When a frame tree is connected, the name must exist on that tree. There is no global frame registry."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Point", Description: "Coordinate unit carried on the point, e.g. px or mm.")
            ,
                ],
            "Create a coordinate-aware reference point. Optional X/Y inputs override the constants so a match or detection can drive the point. Values must be finite. The frame must be non-empty; a connected frame tree checks it against known frames.",
            "builtin"),
        new(
            "coordinate.point3D",
            "Coordinate Point 3D",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("z", VisionDataType.Double, Required: false)],
            [new("point", VisionDataType.CoordinatePoint3D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("z", VisionDataType.Double),
             new("magnitude", VisionDataType.Double), new("xyMagnitude", VisionDataType.Double),
             new("azimuthDeg", VisionDataType.Double), new("elevationDeg", VisionDataType.Double),
             new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("x", "X", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "X in the named frame. Optional X input overrides this constant; must be finite within +/-1e9.") { OverriddenByPorts = ["x"] },
                new("y", "Y", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Y in the named frame. Optional Y input overrides this constant; must be finite within +/-1e9.") { OverriddenByPorts = ["y"] },
                new("z", "Z", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Z in the named frame. Optional Z input overrides this constant; must be finite within +/-1e9.") { OverriddenByPorts = ["z"] },
                new("frame", "Frame", "text", "Workpiece", Group: "Point", Description: "Non-empty frame name stamped on the CoordinatePoint3D. There is no global registry; downstream transforms must match this name."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Point", Description: "Length unit carried with the point. Downstream transforms must use the same unit.")
            ,
                ],
            "Create a frame-aware 3D coordinate point. Optional X/Y/Z inputs override constants. Emits magnitude, XY magnitude, azimuth and elevation (deg) plus JSON diagnostics. Coordinates must be finite within +/-1e9; frame must be non-empty; unit is mm/cm/m.",
            "builtin"),
        new(
            "coordinate.distance",
            "Coordinate Distance",
            "Coordinate",
            [ExecIn, new("pointA", VisionDataType.CoordinatePoint2D), new("pointB", VisionDataType.CoordinatePoint2D)],
            [new("measurement", VisionDataType.Measurement), new("distance", VisionDataType.Double), new("dx", VisionDataType.Double), new("dy", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("coincident", VisionDataType.Boolean), Next],
            [

                new("metric", "Distance Metric", "select", "euclidean", Options: [new("Euclidean", "euclidean"), new("Manhattan", "manhattan")], Description: "Distance metric between the two coordinate points; both keep signed dx/dy."),
                ],
            "Distance between two frame-aware points, only when frame and unit match exactly. Emits a Measurement, scalar distance, signed dx/dy and JSON diagnostics; mismatched or non-finite points fail deterministically.",
            "builtin"),
        new(
            "coordinate.transformConstant",
            "Constant Transform",
            "Coordinate",
            [ExecIn],
            [new("transform", VisionDataType.Transform2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
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
            ,
                ],
            "Create a rigid 2D transform between named engineering or pixel coordinate frames with strict finite-value, frame and unit validation. Emits translation/rotation scalars, frame names, unit and JSON diagnostics (including determinant).",
            "builtin"),
        new(
            "coordinate.transformInverse",
            "Inverse Transform",
            "Coordinate",
            [ExecIn, new("transform", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("inputDeterminant", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [

                ],
            "Invert a Transform2D and swap source/target frame and units. Validates every matrix element and rejects singular transforms deterministically; emits frame names, determinant and JSON diagnostics.",
            "builtin", TransformFrames: new(NodeTransformFramePolicy.Invert, PrimaryPort: "transform")),
        new(
            "coordinate.transformCompose",
            "Compose Transform",
            "Coordinate",
            [ExecIn, new("transformA", VisionDataType.Transform2D), new("transformB", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("valid", VisionDataType.Boolean), Next],
            [

                new("order", "Compose Order", "select", "outerInner", Options: [new("Outer*Inner", "outerInner"), new("Inner*Outer", "innerOuter")], Description: "Matrix multiply order when composing transforms."),
                ],
            "Compose A:S->M with B:M->T to produce S->T, enforcing frame/unit compatibility and rejecting singular or non-finite transforms. For image geometry, compose roi.transform (ReferenceImage->CurrentImage) as A with image.crop (CurrentImage->CropImage) as B. Emits frames, determinant and JSON diagnostics.",
            "builtin", TransformFrames: new(NodeTransformFramePolicy.Compose, PrimaryPort: "transformA", SecondaryPort: "transformB")),
        new(
            "coordinate.frameTree",
            "Frame Tree",
            "Coordinate",
            [ExecIn],
            [new("tree", VisionDataType.FrameTree2D), new("transformCount", VisionDataType.Integer), new("frameCount", VisionDataType.Integer), new("frames", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("edgeCount", VisionDataType.Integer), new("valid", VisionDataType.Boolean), Next],
            [
                new("transformsJson", "Transforms JSON", "textarea", "[]",
                    Group: "Frame Tree",
                    Description: "Static rigid transforms. Runtime dynamic transforms can still be composed as normal workflow data.")
            ,

                ],
            "Build a bidirectional frame graph from validated rigid transforms and resolve transforms by named frame path. Rejects empty, duplicate or non-finite edges deterministically; emits transform/frame counts, the frame list and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.frameResolve",
            "Resolve Frame Transform",
            "Coordinate",
            [ExecIn, new("tree", VisionDataType.FrameTree2D)],
            [new("transform", VisionDataType.Transform2D), new("path", VisionDataType.String), new("hopCount", VisionDataType.Integer), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [
                new("sourceFrame", "Source Frame", "text", "Workpiece", Group: "Resolve", Description: "Frame to resolve from; must exist in the frame tree."),
                new("targetFrame", "Target Frame", "text", "RobotBase", Group: "Resolve", Description: "Frame to resolve to; an unreachable target fails deterministically.")
            ,

                ],
            "Resolve the shortest transform path in a FrameTree2D, automatically using inverse edges where required. Validates frame names and the resolved transform; emits hop count, frame names, path and JSON diagnostics, and fails when no path exists.",
            "builtin"),
        new(
            "coordinate.pose",
            "Coordinate Pose",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("thetaDeg", VisionDataType.Double, Required: false), new("tree", VisionDataType.FrameTree2D, Required: false)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("x", "X", "number", 500, -1000000, 1000000, 0.001, Group: "Pose", Description: "Used when the X input is not connected.") { OverriddenByPorts = ["x"] },
                new("y", "Y", "number", 200, -1000000, 1000000, 0.001, Group: "Pose", Description: "Used when the Y input is not connected.") { OverriddenByPorts = ["y"] },
                new("thetaDeg", "Theta", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pose", Description: "Used when the theta input is not connected. The output angle is normalized to (-180, 180].") { OverriddenByPorts = ["thetaDeg"] },
                new("frame", "Frame", "text", "RobotBase", Group: "Pose", Description: "Must be non-empty. When a frame tree is connected, the name must exist on that tree. There is no global frame registry."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Pose", Description: "Length unit stamped on the pose; it does not convert the numeric X/Y values.")
            ,
                ],
            "Create a frame-aware 2D pose. Optional X/Y/theta inputs override the constants so a match result can drive the pose. Values must be finite and theta is normalized to (-180, 180]. The frame must be non-empty; a connected frame tree checks it against known frames.",
            "builtin"),
        new(
            "coordinate.poseToTransform",
            "Pose to Transform",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D)],
            [new("transform", VisionDataType.Transform2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [new("sourceFrame", "Source Frame", "text", "Tool", Group: "Frames", Description: "Child/tool frame named by the new transform; must differ from the pose frame."),

                ],
            "Interpret a pose expressed in its parent frame as a rigid transform from Source Frame to that parent frame, rejecting a source frame equal to the pose frame. Useful for dynamic Tool->RobotBase transforms. Emits scalars, frames, determinant and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePoint",
            "Transform Coordinate Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.CoordinatePoint2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("sourceFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
            [
                ],
            "Transform a frame-aware point through another engineering-frame Transform2D with strict frame/unit, finite-value and singularity validation. Emits the point plus scalar x/y, target frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePose",
            "Transform Coordinate Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next],
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
                new("boolValueValid", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("numberValueValid", VisionDataType.Boolean),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                new("ageMs", VisionDataType.Double),
                new("isGoodQuality", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Non-empty device id registered in DeviceManager (max 128 chars)."),
                new("tagId", "Tag ID", "text", "trigger", Group: "Tag", Description: "Non-empty tag id on that device (max 128 chars)."),
                new("fresh", "Fresh Read", "boolean", false, Group: "Read", Description: "When true, force a device poll instead of returning a cached sample."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device automatically if it is currently disconnected."),
                new("requireGoodQuality", "Require Good Quality", "boolean", false, Group: "Read", Description: "Fail the node when the sample quality is not Good."),
                new("maxAgeMs", "Max Sample Age (ms, 0=off)", "number", 0, 0, 86400000, 100, Group: "Read", Description: "Reject samples older than this age in milliseconds; 0 disables the freshness gate."),
                new("rejectNullValue", "Reject Null Value", "boolean", false, Group: "Read", Description: "Fail when the tag sample value is null after a successful read.")
            ,









                ],
            "Read a typed device tag, quality and timestamp. boolValueValid/numberValueValid distinguish a converted false/zero from conversion failure. Numeric values must be finite; use the typed value or textValue to preserve Int64 precision.",
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
                new("boolValueValid", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("numberValueValid", VisionDataType.Boolean),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                new("ageMs", VisionDataType.Double),
                new("isGoodQuality", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Non-empty device id registered in DeviceManager (max 128 chars)."),
                new("tagId", "Tag ID", "text", "statusText", Group: "Tag", Description: "Non-empty tag id on that device (max 128 chars)."),
                new("valueType", "Fallback Type", "select", "String", Options: [new("Boolean", "Boolean"), new("Integer", "Integer"), new("Double", "Double"), new("String", "String")], Group: "Fallback", Description: "How the fallback value text is parsed when no value input is connected: boolean, integer, double or string.") { OverriddenByPorts = ["value"] },
                new("fallbackValue", "Fallback Value", "text", "Vision OK", Group: "Fallback", Description: "Used only when the optional Any input is not connected.") { OverriddenByPorts = ["value"] },
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device first when it is disconnected.")
            ,






                ],
            "Write a typed device tag and read it back. Connected input overrides both fallback parameters. Int64 inputs retain their integer precision and numeric conversions reject non-finite values. Conversion validity ports describe the read-back sample.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        // ===== 自定义协议通信（2026-10-26 · 缺口分析 P0-7）：私有 TCP / UDP / 串口协议 =====
        new(
            "device.tcpRequest",
            "TCP Request",
            "Device",
            [ExecIn, new("payload", VisionDataType.String, Required: false)],
            [
                new("response", VisionDataType.String),
                new("responseHex", VisionDataType.String),
                new("bytesRead", VisionDataType.Integer),
                new("matched", VisionDataType.Boolean),
                new("elapsedMs", VisionDataType.Double),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "tcp-1", Group: "Device", Description: "Logical device name used for the dispatch permit and audit trail (max 128 chars); it is not the network address."),
                new("host", "Host", "text", "127.0.0.1", Group: "Endpoint", Description: "Peer host name or IPv4 address."),
                new("port", "Port", "number", 5000, 1, 65535, 1, Group: "Endpoint", Description: "Peer TCP port (1-65535)."),
                new("timeoutMs", "Timeout", "number", 1000, 50, 60000, 50, Unit: "ms", Group: "Endpoint", Description: "Overall budget for connect + send + read. On expiry the connection is closed and the node fails; the request may already have reached the peer, so verify device state before retrying."),
                new("payload", "Request Payload", "textarea", "", Group: "Request", Description: "Bytes to send. Overridden by the payload input port. Compose dynamic payloads with value.string first.") { OverriddenByPorts = ["payload"] },
                new("payloadEncoding", "Payload Encoding", "select", "text", Options: [new("Text (UTF-8)", "text"), new("Hex", "hex")], Group: "Request", Description: "text sends the UTF-8 bytes of the payload; hex parses pairs such as '02 31 0D' (spaces, '-' and ':' are ignored)."),
                new("sendTerminator", "Send Terminator", "select", "none", Options: [new("None", "none"), new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Request", Description: "Bytes appended after the payload."),
                new("readMode", "Read Mode", "select", "untilTerminator", Options: [new("Until terminator", "untilTerminator"), new("Fixed length", "fixedLength"), new("No reply", "none")], Group: "Response", Description: "How the reply is delimited. No reply sends the request and returns immediately."),
                new("readTerminator", "Read Terminator", "select", "crlf", Options: [new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Response", VisibleWhen: new("readMode", ["untilTerminator"]), Description: "Reply terminator; it is stripped from the response before expectPattern is evaluated."),
                new("readLength", "Read Length", "number", 0, 0, 65535, 1, Group: "Response", VisibleWhen: new("readMode", ["fixedLength"]), Description: "Exact reply length in bytes; must be greater than 0 in fixed-length mode. A short reply fails instead of returning half a frame."),
                new("maxResponseBytes", "Max Response Bytes", "number", 4096, 16, 1048576, 1, Group: "Response", Description: "Hard reply cap. Exceeding it fails the node instead of silently truncating."),
                new("expectPattern", "Expect Pattern", "text", "", Group: "Validation", Description: "Optional .NET regular expression evaluated against the reply text (100 ms budget). Empty disables the check."),
                new("requireMatch", "Require Match", "boolean", false, Group: "Validation", Description: "When true a reply that does not match expectPattern fails the node; when false it only sets matched=false."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Validation", Description: "Case-insensitive expectPattern matching.")
                ],
            "Talk to devices that only speak a private TCP protocol (scanners, light controllers, torque tools). One connection per request: connect, send, read per readMode, close. The request may be binary (payloadEncoding=hex) and the reply is exposed both as UTF-8 text and as hex. Responses are capped by maxResponseBytes; timeouts close the connection and fail with a message that states the request may already have reached the peer. The dispatch is guarded by the device ownership permit for deviceId, so a released lease blocks the send.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "device.udpMessage",
            "UDP Message",
            "Device",
            [ExecIn, new("payload", VisionDataType.String, Required: false)],
            [
                new("response", VisionDataType.String),
                new("responseHex", VisionDataType.String),
                new("bytesRead", VisionDataType.Integer),
                new("matched", VisionDataType.Boolean),
                new("elapsedMs", VisionDataType.Double),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "udp-1", Group: "Device", Description: "Logical device name used for the dispatch permit and audit trail (max 128 chars)."),
                new("host", "Host", "text", "127.0.0.1", Group: "Endpoint", Description: "Peer host name or IPv4 address."),
                new("port", "Port", "number", 5001, 1, 65535, 1, Group: "Endpoint", Description: "Peer UDP port (1-65535)."),
                new("timeoutMs", "Timeout", "number", 1000, 50, 60000, 50, Unit: "ms", Group: "Endpoint", Description: "Budget for sending and for waiting for the reply datagram."),
                new("payload", "Payload", "textarea", "", Group: "Request", Description: "Datagram contents. Overridden by the payload input port.") { OverriddenByPorts = ["payload"] },
                new("payloadEncoding", "Payload Encoding", "select", "text", Options: [new("Text (UTF-8)", "text"), new("Hex", "hex")], Group: "Request", Description: "text sends UTF-8 bytes; hex parses pairs such as '02 31 0D'."),
                new("sendTerminator", "Send Terminator", "select", "none", Options: [new("None", "none"), new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Request", Description: "Bytes appended after the payload."),
                new("expectReply", "Expect Reply", "boolean", true, Group: "Response", Description: "When true the node waits for one reply datagram within timeoutMs; when false it only sends."),
                new("expectPattern", "Expect Pattern", "text", "", Group: "Validation", Description: "Optional regular expression over the reply text; empty disables it."),
                new("requireMatch", "Require Match", "boolean", false, Group: "Validation", Description: "Fail when the reply does not match expectPattern instead of only reporting matched=false."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Validation", Description: "Case-insensitive expectPattern matching.")
                ],
            "Send one UDP datagram to a private-protocol device and optionally wait for a single reply datagram. Same payload encoding, terminator and expectPattern semantics as TCP Request, but no connection state: suitable for broadcast-style devices and simple ASCII protocols over Ethernet.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "device.serialPort",
            "Serial Request",
            "Device",
            [ExecIn, new("payload", VisionDataType.String, Required: false)],
            [
                new("response", VisionDataType.String),
                new("responseHex", VisionDataType.String),
                new("bytesRead", VisionDataType.Integer),
                new("matched", VisionDataType.Boolean),
                new("elapsedMs", VisionDataType.Double),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "serial-1", Group: "Device", Description: "Logical device name used for the dispatch permit and audit trail (max 128 chars)."),
                new("portName", "Port Name", "text", "COM1", Group: "Endpoint", Description: "Serial port name such as COM3."),
                new("baudRate", "Baud Rate", "number", 9600, 50, 4000000, 1, Group: "Endpoint", Description: "Baud rate (50-4000000)."),
                new("dataBits", "Data Bits", "number", 8, 5, 8, 1, Group: "Endpoint", Description: "Data bits (5-8)."),
                new("parity", "Parity", "select", "none", Options: [new("None", "none"), new("Odd", "odd"), new("Even", "even"), new("Mark", "mark"), new("Space", "space")], Group: "Endpoint", Description: "Parity mode."),
                new("stopBits", "Stop Bits", "select", "one", Options: [new("1", "one"), new("1.5", "onePointFive"), new("2", "two")], Group: "Endpoint", Description: "Stop bits."),
                new("dtrEnable", "DTR", "boolean", false, Group: "Endpoint", Description: "Assert DTR while the port is open; some RS-485 converters need it."),
                new("rtsEnable", "RTS", "boolean", false, Group: "Endpoint", Description: "Assert RTS while the port is open; no handshake is used."),
                new("timeoutMs", "Timeout", "number", 1000, 50, 60000, 50, Unit: "ms", Group: "Endpoint", Description: "Budget for write + read. On expiry the port is closed and the node fails; the command may already have reached the device."),
                new("payload", "Request Payload", "textarea", "", Group: "Request", Description: "Bytes to send. Overridden by the payload input port.") { OverriddenByPorts = ["payload"] },
                new("payloadEncoding", "Payload Encoding", "select", "text", Options: [new("Text (UTF-8)", "text"), new("Hex", "hex")], Group: "Request", Description: "text sends UTF-8 bytes; hex parses pairs such as '02 31 0D'."),
                new("sendTerminator", "Send Terminator", "select", "crlf", Options: [new("None", "none"), new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Request", Description: "Bytes appended after the payload; CRLF matches most ASCII instruments."),
                new("readMode", "Read Mode", "select", "untilTerminator", Options: [new("Until terminator", "untilTerminator"), new("Fixed length", "fixedLength"), new("No reply", "none")], Group: "Response", Description: "How the reply is delimited."),
                new("readTerminator", "Read Terminator", "select", "crlf", Options: [new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Response", VisibleWhen: new("readMode", ["untilTerminator"]), Description: "Reply terminator; stripped from the response."),
                new("readLength", "Read Length", "number", 0, 0, 65535, 1, Group: "Response", VisibleWhen: new("readMode", ["fixedLength"]), Description: "Exact reply length in bytes; must be greater than 0 in fixed-length mode."),
                new("maxResponseBytes", "Max Response Bytes", "number", 4096, 16, 1048576, 1, Group: "Response", Description: "Hard reply cap; exceeding it fails instead of truncating."),
                new("expectPattern", "Expect Pattern", "text", "", Group: "Validation", Description: "Optional regular expression over the reply text; empty disables it."),
                new("requireMatch", "Require Match", "boolean", false, Group: "Validation", Description: "Fail when the reply does not match expectPattern instead of only reporting matched=false."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Validation", Description: "Case-insensitive expectPattern matching.")
                ],
            "Talk to RS-232/485 devices that only speak a private ASCII or binary protocol (light controllers, handheld scanners, torque tools): open the port, send, read per readMode, close — the port is not held between requests so a stuck recipe cannot block the station. Same payload encoding, terminator and expectPattern semantics as TCP Request. UnauthorizedAccessException is reported as 'port held by another program'. Because the .NET serial stream ignores cancellation, replies are read byte by byte with ReadTimeout and a cancellation check between bytes.",
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
                new("boolValueValid", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("numberValueValid", VisionDataType.Boolean),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                new("matched", VisionDataType.Boolean),
                new("waitMs", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Device identifier."),
                new("tagId", "Tag ID", "text", "trigger", Group: "Tag", Description: "Tag / PLC address to wait on."),
                new("operator", "Condition", "select", "True", Options: [new("True", "True"), new("False", "False"), new("==", "=="), new("!=", "!="), new(">", ">"), new(">=", ">="), new("<", "<"), new("<=", "<=")], Group: "Condition", Description: "Comparison operator for the wait condition."),
                new("compareValue", "Compare Value", "text", "1", Group: "Condition", Description: "Ignored for True/False. Integer tags compare in decimal arithmetic to preserve Int64 precision; double comparisons require finite numbers; text equality ignores case.", VisibleWhen: new("operator", ["==", "!=", ">", ">=", "<", "<="])),
                new("timeoutMs", "Timeout", "number", 5000, 50, 120000, 50, Unit: "ms", Group: "Wait", Description: "Timeout in milliseconds for the operation."),
                new("pollMs", "Poll Interval", "number", 25, 10, 5000, 5, Unit: "ms", Group: "Wait", Description: "Integer polling interval in milliseconds; cannot exceed Timeout."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device/robot automatically when needed."),
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
            ,



                ],
            "Poll a device tag until a condition/edge has held for the requested duration or timeout. Elapsed time is independent of wall-clock changes. Integer comparisons preserve Int64 precision. Conversion validity ports describe the last sample. Timeout Continue returns matched=false; external cancellation still cancels.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false),
            Resources: [new("device", "deviceId")]),
        new(
            "device.writeVisionResult",
            "Write Vision Result",
            "Device",
            [ExecIn, new("pass", VisionDataType.Boolean), new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("r", VisionDataType.Double, Required: false)],
            [new("written", VisionDataType.Boolean), new("pass", VisionDataType.Boolean), new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("r", VisionDataType.Double, Required: false),
                new("xWritten", VisionDataType.Boolean), new("yWritten", VisionDataType.Boolean), new("rWritten", VisionDataType.Boolean), new("tagsWritten", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Non-empty device id registered in DeviceManager (max 128 chars)."),
                new("resultReadyTag", "Result Ready Tag", "text", "resultReady", Group: "Mapping", Description: "Ready/handshake tag written 0 before the payload and 1 after it."),
                new("resultOkTag", "Result OK Tag", "text", "resultOk", Group: "Mapping", Description: "Tag receiving the pass (OK/NG) boolean."),
                new("xTag", "X Tag", "text", "resultX", Group: "Mapping", Description: "Tag receiving the X coordinate; empty disables that write."),
                new("yTag", "Y Tag", "text", "resultY", Group: "Mapping", Description: "Tag receiving the Y coordinate; empty disables that write."),
                new("rTag", "R Tag", "text", "resultR", Group: "Mapping", Description: "Tag receiving the rotation R in degrees; empty disables that write."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device first when it is disconnected.")
            ,







                new("missingCoordinatePolicy", "Missing Coordinate Policy", "select", "skip", Group: "Coordinates", Options: [new("Skip Coordinate", "skip"), new("Fail Entire Write", "fail"), new("Write Zero", "zero")],
                    Description: "When a coordinate tag is set but its input is not connected, defaults to skip; an empty tag always disables the field. Only coordinates actually written produce x/y/r; xWritten/yWritten/rWritten report whether each coordinate was written."),




                ],
            "Writes in order Ready=0, OK/NG and enabled coordinates, Ready=1. An empty coordinate tag disables that field; a missing input is skipped, rejected, or written as zero according to the policy. Validation completes before writing and duplicate tags are rejected. tagsWritten counts actual write operations (including the two Ready writes), not distinct tags. The legacy payloadFormat/onOverflow parameters are no longer used.",
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
                new("x", VisionDataType.Double),
                new("y", VisionDataType.Double),
                new("thetaDeg", VisionDataType.Double),
                new("hasError", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("robotId", "Robot ID", "text", "virtual-abb-1", Group: "Robot", Description: "Non-empty robot id (max 128 chars)."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Robot", Description: "Connect the robot automatically when the connection is down."),
                new("requireInPosition", "Require In Position", "boolean", false, Group: "Gates", Description: "Fail when the robot does not report InPosition."),
                new("requireNoError", "Require No Error", "boolean", false, Group: "Gates", Description: "Fail when the robot reports a handshake or device error."),
                new("expectedFrame", "Expected Frame (empty=any)", "text", "", Group: "Gates", Description: "When non-empty, fail if the reported pose frame does not match exactly.")
            ,






                ],
            "Read the current planar robot pose and neutral handshake state from RobotManager with id validation and diagnostics. Vendor protocol objects remain outside Workflow Core.",
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
                new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("robotId", "Robot ID", "text", "virtual-abb-1", Group: "Robot", Description: "Robot device identifier."),
                new("action", "Action", "select", "Handshake", Options: [new("Industrial Handshake", "Handshake"), new("Move", "Move"), new("Send Target Only", "SendTarget")], Group: "Command", Description: "Robot motion / action verb."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Command", Description: "Connect the device/robot automatically when needed."),
                new("waitForInPosition", "Wait For Complete", "boolean", true, Group: "Completion", VisibleWhen: new("action", ["Handshake", "Move"]), Description: "Block until the robot reports in-position."),
                new("timeoutMs", "Timeout", "number", 5000, 50, 120000, 50, Unit: "ms", Group: "Completion", VisibleWhen: new("action", ["Handshake", "Move"]), Description: "Timeout in milliseconds for the operation."),
                new("maxRetries", "Max Retries", "number", 1, 0, 10, 1, Group: "Handshake", VisibleWhen: new("action", ["Handshake"]), Description: "Maximum retries on transient failures."),
                new("retryDelayMs", "Retry Delay", "number", 100, 0, 10000, 10, Unit: "ms", Group: "Handshake", VisibleWhen: new("action", ["Handshake"]), Description: "Delay between retries in milliseconds."),
                new("autoAck", "Auto Ack", "boolean", true, Group: "Handshake", VisibleWhen: new("action", ["Handshake"]), Description: "Automatically acknowledge robot completion signals.")
            ,






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
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [
                new("robot", "Robot", "text", "ABB", Group: "Target", Description: "Robot identifier the generated target is intended for."),
                new("guidanceMode", "Guidance Mode", "select", "EyeToHand", Options: [new("Eye-to-Hand", "EyeToHand"), new("Eye-in-Hand", "EyeInHand"), new("Manual", "Manual")], Group: "Target", Description: "How the input pose is interpreted before offsets are applied."),
                new("expectedFrame", "Expected Frame", "text", "RobotBase", Group: "Target", Description: "Frame the input pose must already be resolved into; mismatch is rejected."),
                new("offsetX", "Pick Offset X", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset", Description: "X offset added to the pose in the target frame/units."),
                new("offsetY", "Pick Offset Y", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset", Description: "Y offset added to the pose in the target frame/units."),
                new("angleOffsetDeg", "Angle Offset", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pick Offset", Description: "Rotation offset added to the pose angle in degrees.")
            ,



                new("frame", "Guidance Frame", "text", "", Group: "Target", Description: "Optional frame label stamped into diagnostics."),





                ],
            "Create a vendor-neutral RobotTarget2D from a pose already resolved into RobotBase (or another configured robot frame). Validates frame, finite pose/offsets and guidance mode; pick offset is applied in pose-local coordinates. Emits frame, unit and JSON diagnostics. Does not send motion to hardware.",
            "builtin"),
        new(
            "robot.j4TcpCompensation",
            "J4 / TCP Compensation",
            "Robot",
            [ExecIn, new("target", VisionDataType.RobotTarget2D)],
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), new("eccentricity", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("compensationMode", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [
                new("tcpOffsetX", "TCP Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "TCP offset X from the J4 rotation centre, in target units."),
                new("tcpOffsetY", "TCP Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "TCP offset Y from the J4 rotation centre, in target units."),
                new("j4PivotOffsetX", "J4 Pivot Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "J4 pivot offset X in the same frame/units as the target."),
                new("j4PivotOffsetY", "J4 Pivot Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "J4 pivot offset Y in the same frame/units as the target."),
                new("angleZeroOffsetDeg", "J4 Angle Zero Offset", "number", 0, -360, 360, 0.001, Unit: "deg", Group: "Rotation", Description: "Angle added to the target rotation before compensation (degrees, normalised to -180..180)."),
                new("maxEccentricity", "Max Eccentricity (0=off)", "number", 0, 0, 10000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "Fail when TCP-to-pivot eccentricity exceeds this value; 0 disables the gate."),
                new("compensationMode", "Compensation Mode", "select", "tcpToPivot", Options: [new("TCP desired → J4 pivot command", "tcpToPivot"), new("J4 pivot desired → TCP command", "pivotToTcp")], Group: "Eccentricity", Description: "tcpToPivot converts a desired TCP pose into the J4 pivot command; pivotToTcp inverts that mapping."),
                new("angleUnit", "Angle Unit", "select", "deg", Options: [new("Degrees", "deg"), new("Radians", "rad")], Group: "Rotation", Description: "Unit of target.RDeg / angleZeroOffsetDeg; rad is converted to degrees before compensation.")
            ,

                new("maxAngleDeg", "Max |Angle| deg (0=off)", "number", 0, 0, 360, 0.1, Group: "Limits", Description: "Optional magnitude gate on compensation angle; 0 disables."),





                ],
            "Planar compensation for J4 rotation-center/TCP eccentricity: converts a desired TCP pose into the J4 pivot command pose using (TCP offset - pivot offset) rotated by the commanded angle. Validates all finite inputs; emits eccentricity magnitude and JSON diagnostics. Outputs a target only; it does not send motion to hardware.",
            "builtin"),
        // ===== 批次一（2026-10-13 节点补齐）：通用数值 / 布尔判定族 =====
        new(
            "value.number",
            "Number Constant",
            "Value",
            [ExecIn],
            [new("value", VisionDataType.Double), new("integer", VisionDataType.Integer), new("isInteger", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("isFinite", VisionDataType.Boolean), new("isZero", VisionDataType.Boolean), new("sign", VisionDataType.Integer), Next],
            [new("value", "Value", "number", 0, -1000000000, 1000000000, 1, Description: "Finite constant within the signed 64-bit integer range; non-finite values fail deterministically."),
                new("min", "Minimum (optional)", "number", 0, VisibleWhen: new("enforceBounds", ["true"]), Description: "Optional lower bound; when set with enforceBounds, values below fail."),
                new("max", "Maximum (optional)", "number", 0, VisibleWhen: new("enforceBounds", ["true"]), Description: "Optional upper bound; when set with enforceBounds, values above fail."),
                new("enforceBounds", "Enforce Min/Max", "boolean", false, Description: "When true, reject values outside min/max."),
                new("rounding", "Rounding Mode", "select", "away", Options: [new("Away from zero", "away"), new("To even", "even")], Description: "How the integer port is rounded from value."),
                ],
            "Emit a constant number (with an integer variant and an isInteger flag) for comparisons, arithmetic and thresholds. Non-finite or out-of-range values are rejected instead of silently propagating; includes JSON diagnostics.",
            "builtin"),
        new(
            "value.arithmetic",
            "Arithmetic",
            "Value",
            [ExecIn, new("a", VisionDataType.Double), new("b", VisionDataType.Double, Required: false) { RequiredWhen = new("op", ["add", "subtract", "multiply", "divide", "pow", "min", "max"]) }],
            [new("result", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("isFinite", VisionDataType.Boolean), new("a", VisionDataType.Double), new("b", VisionDataType.Double), new("op", VisionDataType.String), Next],
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
                new("clampMin", "Clamp Minimum", "number", 0, -1000000000, 1000000000, 0.001, Group: "Clamp", VisibleWhen: new("op", ["clamp"]), Description: "Lower bound used only by Clamp."),
                new("clampMax", "Clamp Maximum", "number", 1, -1000000000, 1000000000, 0.001, Group: "Clamp", VisibleWhen: new("op", ["clamp"]), Description: "Upper bound used only by Clamp. Must be greater than or equal to Clamp Minimum.")
            ,

                new("onNonFinite", "Non-Finite Policy", "select", "fail", Options: [new("Fail", "fail"), new("Clamp zero", "zero")], Description: "When the arithmetic result is non-finite: fail stops the graph; zero emits 0 with isFinite=false.")],
            "Combine measurements. Add, subtract, multiply, divide, power, min, and max read a and b. Square root, absolute value, and clamp read a only. An unknown operation, division by zero, a negative square root, reversed clamp bounds, or a non-finite result fails instead of guessing. Emits JSON diagnostics with operands, operation and result.",
            "builtin"),
        new(
            "value.range",
            "Range Check",
            "Value",
            [ExecIn, new("value", VisionDataType.Double)],
            [new("pass", VisionDataType.Boolean), new("inside", VisionDataType.Boolean), new("margin", VisionDataType.Double), new("deviation", VisionDataType.Double), new("clamped", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [
                new("min", "Min", "number", 0, -1000000000, 1000000000, 1, Description: "Inclusive lower bound; reversed bounds are swapped and reported in diagnostics."),
                new("max", "Max", "number", 100, -1000000000, 1000000000, 1, Description: "Inclusive upper bound; must be finite."),
                new("invert", "Invert", "boolean", false, Description: "When true, pass means the value lies outside the inclusive window.")
            ,
                new("boundPolicy", "Bound Policy", "select", "inclusive", Options: [new("Inclusive", "inclusive"), new("Exclusive", "exclusive")], Description: "Whether min/max bounds are inclusive.")],
            "Inclusive range check. Reversed min/max are swapped and flagged in diagnostics. deviation is 0 inside the window and the signed distance outside it; clamped is the value limited to the window; margin is the signed distance to the nearest bound. invert flips pass only. Emits pass, inside, margin, deviation, clamped and JSON diagnostics.",
            "builtin"),
        new(
            "value.string",
            "String Tool",
            "Value",
            [ExecIn,
                new("a", VisionDataType.String, Required: false) { RequiredWhen = new("op", ["join", "tonumber", "substring", "trim", "replace", "pad", "upper", "lower", "length"]) },
                new("b", VisionDataType.String, Required: false),
                new("c", VisionDataType.String, Required: false),
                new("d", VisionDataType.String, Required: false),
                new("value", VisionDataType.Double, Required: false) { RequiredWhen = new("op", ["numbertostring"]) }],
            [new("textValue", VisionDataType.String), new("charCount", VisionDataType.Integer), new("numberValue", VisionDataType.Double, Required: false), new("truncated", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), Next],
            [
                new("op", "Operation", "select", "template", Description: "Strictly one of the listed operations; unknown operations fail instead of passing text through.", Options:
                [
                    new("Template (named placeholders)", "template"),
                    new("Join with separator", "join"),
                    new("Number to text", "numbertostring"),
                    new("Text to number", "tonumber"),
                    new("Substring", "substring"),
                    new("Trim whitespace", "trim"),
                    new("Replace", "replace"),
                    new("Pad", "pad"),
                    new("Uppercase", "upper"),
                    new("Lowercase", "lower"),
                    new("Length", "length")
                ]),
                new("template", "String Template", "textarea", "{a}", Group: "Template", VisibleWhen: new("op", ["template"]),
                    Description: "Placeholders {a} {b} {c} {d} {value} and aliases {0}..{3} are replaced; {{ and }} emit literal braces. Unknown placeholders, unclosed braces and a {value} placeholder without a connected value input fail. Unconnected string inputs expand to an empty string."),
                new("separator", "Separator", "text", "_", Group: "Join", VisibleWhen: new("op", ["join"]),
                    Description: "Inserted between the non-empty parts of a, b, c and d. Empty parts are skipped; an empty separator concatenates without gaps."),
                new("format", "Number Format", "text", "0.###", Group: "Number", VisibleWhen: new("op", ["numbertostring"]),
                    Description: ".NET custom or standard numeric format string applied with invariant culture (0.### drops trailing zeros, F3 fixes three decimals, D6 zero-pads integers). Invalid formats fail."),
                new("startIndex", "Start Index", "number", 0, 0, 1000000, 1, Group: "Substring", VisibleWhen: new("op", ["substring"]),
                    Description: "Zero-based UTF-16 index into input a. Values past the end are clamped and reported as truncated=true."),
                new("takeLength", "Take Length (0=to end)", "number", 0, 0, 1000000, 1, Group: "Substring", VisibleWhen: new("op", ["substring"]),
                    Description: "Number of UTF-16 code units to take. 0 takes the remainder of the string; values past the end are clamped and reported as truncated=true."),
                new("trimSide", "Trim Side", "select", "both", Group: "Trim", VisibleWhen: new("op", ["trim"]), Options:
                [
                    new("Both ends", "both"),
                    new("Start only", "start"),
                    new("End only", "end")
                ], Description: "Which end of input a is stripped of Unicode whitespace."),
                new("find", "Find Text", "text", "", Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Literal text, or a regular expression when Use Regex is enabled. Must not be empty; at most 4096 characters."),
                new("replaceWith", "Replace With", "text", "", Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Written literally, with no $1 group expansion. May be empty to delete the matches."),
                new("replaceAll", "Replace All", "boolean", true, Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "When false only the first match is replaced; when true every match is replaced."),
                new("useRegex", "Use Regex", "boolean", false, Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Treat Find Text as a .NET regular expression with a fixed 100 ms match timeout. Invalid patterns and timeouts fail the node."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Case-insensitive matching for both literal and regex modes."),
                new("padWidth", "Pad Width", "number", 8, 0, 1048576, 1, Group: "Pad", VisibleWhen: new("op", ["pad"]),
                    Description: "Target total length in UTF-16 code units. Text that is already this long or longer is returned unchanged (never truncated)."),
                new("padChar", "Pad Character", "text", "0", Group: "Pad", VisibleWhen: new("op", ["pad"]),
                    Description: "Exactly one character used to fill the gap; longer or empty values fail."),
                new("padSide", "Pad Side", "select", "left", Group: "Pad", VisibleWhen: new("op", ["pad"]), Options:
                [
                    new("Left", "left"),
                    new("Right", "right")
                ], Description: "Side that receives the padding characters."),
                new("maxLength", "Max Characters", "number", 65536, 16, 1048576, 1, Group: "Limits",
                    Description: "Upper bound on the produced string in UTF-16 code units. Results above it fail instead of being stored truncated.")
                ],
            "Compose and reshape strings that other nodes already produce (barcode text, OCR text, timestamps, paths). Wire: optional string inputs a/b/c/d and optional number input value; textValue is the result, charCount its UTF-16 length, truncated reports clamped substrings, numberValue is emitted only by Text to number. Template mode assembles {a}..{d} and {value}; Join skips empty parts; Number to text uses an invariant-culture format; Text to number parses with NumberStyles.Float and fails on non-numeric or non-finite text; Substring clamps out-of-range indexes and sets truncated; Trim/Replace/Pad/Uppercase/Lowercase/Length operate on input a. Empty find text, multi-character pad characters, unknown placeholders and invalid number formats fail deterministically; missing string inputs are treated as empty.",
            "builtin"),
        // ===== 通用列表类型配套（2026-10-26 · 缺口分析 P1-8）=====
        new(
            "value.numberList",
            "Number List",
            "Value",
            [ExecIn,
                new("a", VisionDataType.Double, Required: false), new("b", VisionDataType.Double, Required: false),
                new("c", VisionDataType.Double, Required: false), new("d", VisionDataType.Double, Required: false),
                new("e", VisionDataType.Double, Required: false), new("f", VisionDataType.Double, Required: false),
                new("g", VisionDataType.Double, Required: false), new("h", VisionDataType.Double, Required: false)],
            [new("numberList", VisionDataType.NumberList), new("count", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next],
            [],
            "Collect up to eight scalar measurements into one NumberList so value.statistics can summarize them. Only connected inputs are collected, in port order a..h: an unconnected input contributes nothing instead of a zero, because a silent zero would distort mean, range and CP/CPK. Non-finite inputs fail; connecting nothing fails with an explicit message rather than producing an empty list.",
            "builtin"),
        new(
            "value.statistics",
            "Statistics",
            "Value",
            [ExecIn, new("numberList", VisionDataType.NumberList)],
            [new("count", VisionDataType.Integer), new("mean", VisionDataType.Double), new("std", VisionDataType.Double),
                new("min", VisionDataType.Double), new("max", VisionDataType.Double), new("median", VisionDataType.Double),
                new("range", VisionDataType.Double), new("sum", VisionDataType.Double),
                new("cp", VisionDataType.Double, Required: false), new("cpk", VisionDataType.Double, Required: false),
                new("pass", VisionDataType.Boolean, Required: false), new("diagnostics", VisionDataType.String), Next],
            [
                new("deviation", "Deviation", "select", "sample", Group: "Statistics", Options:
                [
                    new("Sample (n-1)", "sample"),
                    new("Population (n)", "population")
                ], Description: "Standard deviation divisor. Sample is the default for measured samples; population treats the list as the whole population."),
                new("enableLimits", "Enable Spec Limits", "boolean", false, Group: "Capability", Description: "When false no cp/cpk/pass outputs are emitted, so downstream logic cannot mistake 'no specification' for 'in tolerance'."),
                new("lsl", "LSL", "number", 0, -1000000000, 1000000000, 0.001, Group: "Capability", VisibleWhen: new("enableLimits", ["true"]), Description: "Lower specification limit; must be strictly below USL."),
                new("usl", "USL", "number", 1, -1000000000, 1000000000, 0.001, Group: "Capability", VisibleWhen: new("enableLimits", ["true"]), Description: "Upper specification limit; must be strictly above LSL.")
                ],
            "Summarize a NumberList: count, mean, standard deviation, min, max, median, range and sum. Median averages the two middle samples for even counts. With spec limits enabled it also emits cp = (USL-LSL)/(6σ), cpk = min(USL-mean, mean-LSL)/(3σ) and pass = mean within [LSL, USL], using the selected deviation divisor; a zero standard deviation with limits enabled fails instead of returning infinity. Empty lists and non-finite samples fail.",
            "builtin"),
        new(
            "logic.boolean",
            "Boolean Logic",
            "Logic",
            [ExecIn, new("a", VisionDataType.Boolean), new("b", VisionDataType.Boolean, Required: false) { RequiredWhen = new("op", ["and", "or", "xor"]) }],
            [new("result", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("operationApplied", VisionDataType.String), Next],
            [
                new("op", "Operation", "select", "and", Description: "Strictly one of and/or/xor/not; NOT uses input a only, unknown operations fail.", Options:
                [
                    new("AND", "and"),
                    new("OR", "or"),
                    new("XOR", "xor"),
                    new("NOT (a)", "not")
                ])
            ,

                new("invertResult", "Invert Result", "boolean", false, Description: "When true, negate the boolean result after applying op."),

                ],
            "Combine boolean results with strict operation validation (NOT uses a only; unknown operations fail instead of defaulting to AND). Feeds flow.result / flow.if for the final OK/NG disposition; emits JSON diagnostics.",
            "builtin"),
        new(
            "flow.if",
            "If",
            "Flow",
            [ExecIn, new("value", VisionDataType.Double), new("previous", VisionDataType.Boolean, Required: false)],
            [new("true", VisionDataType.Control), new("false", VisionDataType.Control), new("condition", VisionDataType.Boolean), new("value", VisionDataType.Double), new("margin", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false)],
            [
                new(
                    "operator",
                    "Operator",
                    "select",
                    ">",
                    Group: "Condition",
                    Options:
                    [
                        new(">", ">"),
                        new(">=", ">="),
                        new("<", "<"),
                        new("<=", "<="),
                        new("==", "=="),
                        new("!=", "!=")
                    ], Description: "Used only in threshold mode.", VisibleWhen: new("mode", ["threshold"])),
                new("threshold", "Compare Value", "number", 15000, -99999999, 99999999, 1, Group: "Condition", Description: "Used only in threshold mode.", VisibleWhen: new("mode", ["threshold"])),
                new("mode", "Compare Mode", "select", "threshold", Group: "Condition", Options:
                [
                    new("Threshold", "threshold"),
                    new("Inside range", "range")
                ], Description: "Threshold compares one value. Inside range checks a closed window and ignores the operator."),
                new("tolerance", "Tolerance", "number", 1e-9, 0, 1000000, 1e-9, Group: "Condition", Description: "Used only for == and != in threshold mode. Differences must be strictly less than this band; 0 requires exact equality.", VisibleWhen: new("mode", ["threshold"])) { VisibleWhenAll = [new("operator", ["==", "!="])] },
                new("rangeMin", "Range Minimum", "number", 0, -99999999, 99999999, 1, Group: "Condition", Description: "Inclusive lower bound used only in range mode. Must not exceed Range Maximum.", VisibleWhen: new("mode", ["range"])),
                new("rangeMax", "Range Maximum", "number", 100, -99999999, 99999999, 1, Group: "Condition", Description: "Inclusive upper bound used only in range mode.", VisibleWhen: new("mode", ["range"])),
                new("hysteresis", "Hysteresis", "number", 0, 0, 1000000, 0.001, Group: "Hysteresis", Description: "Applied only when the previous condition input is connected. There is no memory across runs. 0 disables it.")
            ,

                ],
            "Compare a number and branch true or false. Threshold mode keeps the original operators, with a configurable equality tolerance. Inside-range mode checks a closed window. Hysteresis uses the optional previous condition and does not remember state across runs. Emits condition, echoed value, signed margin and JSON diagnostics.",
            "builtin"),
        new(
            "flow.result",
            "Result / Disposition",
            "Flow",
            [ExecIn, new("pass", VisionDataType.Boolean)],
            [new("pass", VisionDataType.Boolean), new("disposition", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), Next],
            [new("okLabel", "OK Label", "text", "OK", Description: "Disposition string when pass is true."),
                new("ngLabel", "NG Label", "text", "NG", Description: "Disposition string when pass is false."),


                ],
            "Set the automatic inspection disposition (OK/NG) for traceability with JSON diagnostics. Inside Parallel branches the value is scoped to its branch and merged at Join with any-NG semantics.",
            "builtin"),
        new(
            "flow.parallel",
            "Parallel",
            "Flow",
            [ExecIn],
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control), new("branches", VisionDataType.Integer), new("nodeId", VisionDataType.String), new("marker", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("branchCount", VisionDataType.Integer), new("joined", VisionDataType.Boolean), new("elapsedMs", VisionDataType.Double)],
            [
                ],
            "Start two Workflow Core parallel paths. Emits a JSON diagnostics marker; branch scheduling and any-NG disposition merging are handled by the structured runtime.",
            "builtin"),
        .. Batch2ImageCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch2UtilityCatalog.Items,
        .. Batch3GeometryCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch3RecognitionCatalog.Items,
        .. Batch4IterationCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch5PrecisionCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. CaliperWidthCatalog.Items,
        .. MetrologyCatalog.Items,
        .. JlVisionOperatorCatalog.Items,
        .. JlVisionAdvancedCatalog.Items,
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
            [Next, new("joined", VisionDataType.Boolean), new("nodeId", VisionDataType.String), new("marker", VisionDataType.String), new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("overlay", VisionDataType.Image, Required: false)],
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
        .Select(OperatorParameterForms.Apply)
        .Select(NodeOutputContracts.Apply)
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
            names.AddRange(["searchRoi", "templateRoi", "ignoreRoi"]);
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
                Description: RoiDescription(item.Type, name)))
            .ToArray();
        return missing.Length == 0 ? item : item with { Parameters = [.. item.Parameters, .. missing] };
    }

    private static string RoiDescription(string nodeType, string name) => (nodeType, name) switch
    {
        ("match.template", "templateRoi") =>
            "Canvas-authored template mask. Applied with ReduceDomain before JLVision CreateNccModel so FindNccModel uses only masked template pixels (HALCON-style; no separate Find mask arg). Distinct from scene ignoreRoi. Edited through the ROI overlay.",
        ("match.template", "ignoreRoi") =>
            "Canvas-authored scene ignore region. Hard mode ReduceDomains the search image; soft mode mean-lerps (approximate — not soft scene-weighted NCC). Distinct from templateRoi.",
        ("match.template", "searchRoi") =>
            "Canvas-authored search window in full-image coordinates. Edited through the ROI overlay, not as a form field.",
        _ => "Canvas-authored ROI object stored on the node. Edited through the ROI overlay, not as a form field."
    };

    private static string RoiLabel(string name) => name switch
    {
        "searchRoi" => "Search ROI",
        "templateRoi" => "Template ROI",
        "ignoreRoi" => "Ignore ROI",
        "clutterRoi" => "Clutter ROI",
        _ => "ROI"
    };

    public static NodeCatalogItem Require(string type) =>
        Items.FirstOrDefault(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Built-in catalog item '{type}' was not found.");
}
