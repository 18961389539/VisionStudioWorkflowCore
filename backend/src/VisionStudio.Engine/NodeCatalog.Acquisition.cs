namespace VisionStudio.Engine;

public static partial class BuiltInNodeCatalog
{
    private static NodeCatalogItem[] AcquisitionItems =>
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
                new("height", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next
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
                new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
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
                new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
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
            [new("image", VisionDataType.Image), new("width", VisionDataType.Integer), new("height", VisionDataType.Integer), new("channels", VisionDataType.Integer), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
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
                new("angleStepDeg", "Angle Step", "number", 5, -180, 180, 1, Unit: "deg", Group: "Placement",
                    Description: "Rotation added per sample index in sequence mode.", VisibleWhen: new("placementMode", ["sequence"])),
                new("randomSeed", "Random Seed", "number", 1, 0, 2147483647, 1, Group: "Random",
                    Description: "Fixed seed for repeatable pseudo-random positions.", VisibleWhen: new("placementMode", ["seededRandom"])),
                new("maxOffsetX", "Maximum X Shift", "number", 160, 0, 500, 1, Group: "Random",
                    Description: "Random horizontal shift is selected from this symmetric range.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"])),
                new("maxOffsetY", "Maximum Y Shift", "number", 100, 0, 400, 1, Group: "Random",
                    Description: "Random vertical shift is selected from this symmetric range.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"])),
                new("maxAngleDeg", "Maximum Angle", "number", 15, 0, 180, 1, Unit: "deg", Group: "Random",
                    Description: "Random angle is selected from the symmetric range -value..+value.", VisibleWhen: new("placementMode", ["seededRandom", "randomEachRun"]))
            ,


                ],
            "Generate a large test image with a fixed-size pattern. The pattern can stay fixed, follow a sample-index sequence, use repeatable seeded random offsets, or choose a new valid random placement on every run. Sequence and seeded placements are minimally translated into the image when needed; random-each-run placements are resampled instead.",
            "builtin",
            // placementMode=randomEachRun 每次运行都重新采样位置与角度，不能进自动重试（重试会得到另一张图）。
            new VisionToolCapabilities(Deterministic: false)),
        new(
            "image.threshold",
            "Threshold (single level)",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("threshold", VisionDataType.Double, Required: false),
             new("foregroundRatio", VisionDataType.Double, Required: false), new("warningCount", VisionDataType.Integer, Required: false),
             new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("nonZeroCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next],
            [
                new("mode", "Threshold Mode", "select", "manual", Group: "Mode", Options:
                [new("Manual", "manual"), new("Adaptive", "adaptive"), new("Otsu", "otsu")], Description: "manual uses Threshold; adaptive uses local mean/Gaussian; otsu picks a global level."),
                new("inputBitDepth", "Input Bit Depth", "select", "auto", Group: "Input", Options:
                [new("Auto (full 16-bit range)", "auto"), new("8-bit", "8"), new("10-bit", "10"), new("12-bit", "12"), new("14-bit", "14"), new("16-bit", "16")],
                    Description: "How 16-bit unsigned inputs are scaled to byte before thresholding. auto keeps the historical full-range mapping (divide by 257); a fixed depth maps 0..(2^bits-1) onto 0..255 so 10/12-bit cameras keep their contrast. Ignored for other pixel types."),
                new("threshold", "Threshold", "number", 128, 0, 255, 1, Group: "Threshold",
                    Description: "Manual gray threshold (0..255).", VisibleWhen: new("mode", ["manual"])),
                new("maxValue", "Foreground Value", "number", 255, 1, 255, 1, Group: "Output",
                    Description: "Value written for foreground pixels (1..255). Use 1 when downstream expects a 0/1 mask; the foreground ratio still counts non-zero pixels, so it does not change."),
                new("blockSize", "Block Size", "number", 31, 3, 99, 2, Group: "Threshold",
                    Description: "Local window size (coerced to odd).", VisibleWhen: new("mode", ["adaptive"])),
                new("c", "C Constant", "number", 5, 0, 50, 1, Group: "Threshold",
                    Description: "Subtracted from the local mean/Gaussian (adaptive only).", VisibleWhen: new("mode", ["adaptive"])),
                new("method", "Adaptive Method", "select", "mean", Group: "Threshold", Options:
                [new("Mean", "mean"), new("Gaussian", "gaussian")], Description: "Local statistic for adaptive thresholding.", VisibleWhen: new("mode", ["adaptive"])),
                new("invert", "Invert", "boolean", false, Group: "Output",
                    Description: "Swap foreground/background polarity.", VisibleWhen: new("mode", ["manual", "adaptive", "otsu"]))
            ],
            "Binary threshold with one cut level: manual, local adaptive, or Otsu. All modes convert color to gray and scale UInt16 by 1/257 (or by the declared inputBitDepth) to an 8-bit binary image; manual/Otsu also emit the threshold value. A ROI limits the Otsu and adaptive statistics to the ROI bounding box and makes the foreground ratio relative to the ROI area; pixels outside the ROI are cleared. Emits the foreground pixel ratio so binarization drift is visible, and raises a warning with an Otsu suggestion when a manual level selects almost nothing or almost everything. Need a 100..200 band instead of one cut? Use region.fromRange — it keeps the source bit depth and returns a Region; this node answers \"above/below one level\" and returns an Image. Harsh lighting can make Otsu swallow the target; follow with region cleanup when needed.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
    ];
}
