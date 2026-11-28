using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>
/// P1 补全批次的目录条目（2026-10-27）：缺陷聚类/分级、多帧融合、传动标定、轮廓度。
/// </summary>
internal static class P1CompletionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);

    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("defect.cluster", "Cluster Defects", "Defect",
            [Exec, new("detections", VisionDataType.DetectionList)],
            [
                new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer),
                new("mergedCount", VisionDataType.Integer), new("inputCount", VisionDataType.Integer),
                new("largestArea", VisionDataType.Double), new("clustersJson", VisionDataType.String), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("linkDistancePx", "Link Distance", "number", 5, 0, 10000, 0.5, Unit: "px", Group: "Clustering",
                    Description: "Bounding-box gap at or below this distance joins two detections into one defect. 0 only merges overlapping boxes. Gap (not centroid distance) is what keeps one long scratch from being split into two."),
                new("maxDetections", "Maximum Input Detections", "number", 5000, 1, 50000, 100, Group: "Limits",
                    Description: "Refuse oversized input lists instead of spending minutes in the pairwise pass. Cluster or filter upstream when a noisy threshold produces thousands of specks."),
                new("maxMembers", "Max Members per Cluster (0=off)", "number", 0, 0, 50000, 1, Group: "Limits", Description: "Bound transitive chain growth; oversized clusters fail or split according to limitPolicy."),
                new("maxSpanPx", "Max Cluster Span (0=off)", "number", 0, 0, 1e9, 1, Unit: "px", Group: "Limits", Description: "Maximum bounding-box width or height of a cluster; 0 disables."),
                new("limitPolicy", "Cluster Limit Policy", "select", "fail", Options: [new("Fail", "fail"), new("Split oversized clusters", "split")], Group: "Limits", Description: "fail refuses unbounded clusters; split emits bounded chunks and reports splitClusterCount."),
            ],
            "Merge nearby detections into one defect each: two boxes join when the gap between them is at most linkDistancePx. Wire: detections -> detections/count/mergedCount. The merged area is the sum of member areas and the merged perimeter is the sum of member perimeters, so circularity of a cluster is a coarse filter only - rebuild the true outline with region/contour nodes when the shape matters. A cluster keeps its class id only when every member shares it. Deterministic and order-independent (union-find over a spatial bucket grid).",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true),
            Actions: []),

        new("defect.classify", "Classify Defects", "Defect",
            [Exec, new("detections", VisionDataType.DetectionList)],
            [
                new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer),
                new("ngCount", VisionDataType.Integer), new("ok", VisionDataType.Boolean),
                new("unclassifiedCount", VisionDataType.Integer), new("classCountsJson", VisionDataType.String),
                new("detectionDiagnosticsJson", VisionDataType.String), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("rulesJson", "Rules JSON", "textarea",
                    """[{"name":"scratch","classId":1,"minAspect":3,"minArea":50},{"name":"pit","classId":2,"maxArea":500,"minCircularity":0.6},{"name":"stain","classId":3,"minArea":500}]""",
                    Group: "Rules",
                    Description: "Ordered rule list; the first matching rule wins. Fields: name, classId, minArea, maxArea, minAspect, maxAspect, minCircularity, maxCircularity. 0 or an omitted field disables that bound. Aspect is long side over short side. Business classId must be 1..10000; 0 is reserved for unclassified."),
                new("rejectUnclassified", "Reject Unclassified", "boolean", true, Group: "Rules",
                    Description: "Count detections that match no rule as NG (default). Turn it off while tuning a new rule set so unmatched blobs do not fail the part."),
            ],
            "Grade each detection by an ordered rule table and stamp its class id. Wire: detections -> detections (ClassId set)/ngCount/ok/classCountsJson. Rules are matched on area, aspect ratio and circularity only - intensity-based grading needs a separate node because it requires the source image. Unmatched detections get ClassId=0 (unclassified) and count as NG unless rejectUnclassified is off. Duplicate names, duplicate class ids and inverted ranges fail loudly instead of silently never matching.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true),
            Actions: []),

        new("image.fuse", "Fuse Frames", "Preprocess",
            [Exec,
             new("image1", VisionDataType.Image, Required: false) { RequiredWhen = new("sourceMode", ["images"]) },
             new("image2", VisionDataType.Image, Required: false) { RequiredWhen = new("sourceMode", ["images"]) },
             new("image3", VisionDataType.Image, Required: false), new("image4", VisionDataType.Image, Required: false),
             new("image5", VisionDataType.Image, Required: false), new("image6", VisionDataType.Image, Required: false),
             new("image7", VisionDataType.Image, Required: false), new("image8", VisionDataType.Image, Required: false),
             new("frameSet", VisionDataType.FrameSet, Required: false) { RequiredWhen = new("sourceMode", ["frameSet"]) }],
            [
                new("image", VisionDataType.Image), new("width", VisionDataType.Integer),
                new("height", VisionDataType.Integer), new("channels", VisionDataType.Integer),
                new("inputCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("sourceMode", "Input Source", "select", "images", Group: "Fusion", Options:
                [
                    new("Image ports (image1..image8)", "images"),
                    new("Synchronized FrameSet", "frameSet")
                ], Description: "images reads image1..image8. frameSet reads the camera images of a synchronized FrameSet in camera-ID order. FrameSet synchronization aligns capture times. Choose one input source."),
                new("alignmentPolicy", "Alignment Policy", "select", "sameFrame", Group: "Fusion", Options:
                [
                    new("Require Common Image Frame", "sameFrame"),
                    new("Use Prealigned Inputs", "prealigned"),
                    new("Legacy (No Alignment Check)", "legacy")
                ], Description: "Require Common Image Frame checks that all inputs declare the same known image coordinate frame. Use Prealigned Inputs only after verifying the images align in a common reference. Legacy mode preserves older pixelwise workflows without checking their frames."),
                new("mode", "Fusion Mode", "select", "mean", Group: "Fusion", Options:
                [
                    new("Mean (random noise / sqrt(N))", "mean"),
                    new("Median (removes transient specks)", "median"),
                    new("Minimum (darkest pixel)", "min"),
                    new("Maximum (brightest pixel)", "max")
                ], Description: "Mean is the fast default for noise averaging. Median rejects a bright speck that only appears in one frame but costs O(N) per pixel. Min/max suit multi-exposure fusion and dark/bright reference building."),
                new("maxPixels", "Maximum Input Pixels", "number", 16000000, 1000, 200000000, 1000000, Group: "Limits",
                    Description: "Hard cap on the input area. Median holds every input plus the output in memory, so a full-resolution stack multiplies quickly."),
            ],
            "Fuse registered images pixel by pixel using mean, median, min or max. Images must share size, channel count and depth. Connect two to eight individual image inputs or one synchronized FrameSet. For raw camera images, extract each frame, register and warp it to a shared reference grid, then connect the aligned images to the individual inputs. The default alignment policy checks known coordinate frame metadata, and FrameSet diagnostics list the camera IDs. Mean uses float64 for 32-bit integer, float32 and float64 inputs, float32 for other depths, then converts back to the source depth with saturation. Median supports 8-bit inputs.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true),
            Actions: []),

        new("calibration.conveyor", "Conveyor Tracking", "Calibration",
            [Exec, new("encoder", VisionDataType.Double, Required: false),
             new("speedDirection", VisionDataType.String, Required: false), new("motionState", VisionDataType.String, Required: false)],
            [
                new("position", VisionDataType.Double), new("triggerOffset", VisionDataType.Double),
                new("compensatedPosition", VisionDataType.Double, Required: false), new("targetAllowed", VisionDataType.Boolean),
                new("unit", VisionDataType.String), new("encoderSampleId", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("encoder", "Encoder Count", "number", 0, -1e15, 1e15, 1, Group: "Encoder",
                    Description: "Fallback count when the encoder port is disconnected. A connected encoder input wins, so the latched count at trigger time can drive the node."),
                new("countsPerUnit", "Counts Per Unit", "number", 1000, 0.000001, 1000000000, 0.001, Group: "Encoder",
                    Description: "Encoder counts per engineering unit (counts per millimetre). Measure it once per conveyor: move a known distance and divide the count difference."),
                new("unit", "Unit", "text", "mm", Group: "Encoder",
                    Description: "Label for the emitted positions (1..16 characters). No conversion is applied; the scale already lives in Counts Per Unit."),
                new("lineSpeedUnitPerSec", "Line Speed", "number", 0, 0, 1000000, 0.1, Unit: "unit/s", Group: "Trigger",
                    Description: "Belt speed at production rate. Needed to convert the trigger delay into a travel distance; 0 disables the compensation."),
                new("triggerDelayMs", "Trigger Delay", "number", 0, 0, 60000, 1, Unit: "ms", Group: "Trigger",
                    Description: "Time between the trigger signal (or the encoder latch) and the moment the part is actually picked or inspected: camera latency, processing time, PLC scan and gripper closure combined."),
                new("compensationSign", "Compensation Direction", "select", "add", Group: "Trigger", Options:
                [
                    new("Add (part advanced)", "add"),
                    new("Subtract (reverse running line)", "subtract")
                ], Description: "Must agree with Line Direction: forward=add, reverse=subtract. A mismatch blocks the robot target."),
                new("lineDirection", "Configured Line Direction", "select", "unconfirmed", Group: "Safety", Options:
                [
                    new("Unconfirmed", "unconfirmed"), new("Forward", "forward"), new("Reverse", "reverse")
                ], Description: "Commissioned physical line direction. Unconfirmed direction never emits compensatedPosition."),
                new("speedDirection", "Observed Speed Direction", "select", "unknown", Group: "Safety", Options:
                [
                    new("Unknown", "unknown"), new("Forward", "forward"), new("Reverse", "reverse")
                ], Description: "Direction reported by the encoder/drive at trigger time. A connected speedDirection port overrides this value."),
                new("motionState", "Motion State", "select", "unknown", Group: "Safety", Options:
                [
                    new("Unknown", "unknown"), new("Steady", "steady"), new("Decelerating", "decelerating"),
                    new("Reversing", "reversing"), new("Stopped", "stopped")
                ], Description: "Robot targets are blocked unless the line is steady, or stopped with zero speed. A connected motionState port overrides this value."),
            ],
            "Conveyor tracking math: encoder counts to engineering position, plus the travel distance accumulated during the trigger delay. Wire: encoder -> position/triggerOffset/compensatedPosition/unit. compensatedPosition is emitted only when the configured line direction is confirmed, the observed direction matches, motion is steady (or stopped at zero speed), and compensationSign agrees with the line direction; otherwise targetAllowed=false and no robot target is published. This node reads no hardware: the host latches the encoder count and direction state, which keeps the calibration in the recipe and out of the camera driver. Failures: countsPerUnit <= 0, negative delay or speed.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true),
            Actions: []),

        new("tolerance.profile", "Profile of a Line", "Measurement",
            [Exec, new("samples", VisionDataType.PointList),
             new("nominalLine", VisionDataType.Line2D, Required: false),
             new("nominalCircle", VisionDataType.Circle, Required: false)],
            [
                new("found", VisionDataType.Boolean), new("sampleCount", VisionDataType.Integer),
                new("profile", VisionDataType.Double), new("maxDeviation", VisionDataType.Double),
                new("minDeviation", VisionDataType.Double), new("meanDeviation", VisionDataType.Double),
                new("rmsDeviation", VisionDataType.Double), new("crossedSampleCount", VisionDataType.Integer),
                new("firstCrossedIndex", VisionDataType.Integer, Required: false), new("worstPointIndex", VisionDataType.Integer, Required: false),
                new("worstSignedDeviation", VisionDataType.Double, Required: false), new("nominalType", VisionDataType.String, Required: false),
                new("meanSampleSpacing", VisionDataType.Double, Required: false), new("maxAdjacentPointDistance", VisionDataType.Double, Required: false),
                new("samplingErrorBoundPx", VisionDataType.Double, Required: false),
                new("coverageLength", VisionDataType.Double, Required: false), new("samplingDensitySufficient", VisionDataType.Boolean, Required: false),
                new("reason", VisionDataType.String, Required: false),
                new("worstPoint", VisionDataType.Point2D), new("withinTolerance", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("mode", "Tolerance Zone", "select", "bilateral", Group: "Zone", Options:
                [
                    new("Bilateral (zone straddles nominal)", "bilateral"),
                    new("Unilateral outside (material side)", "unilateralOutside"),
                    new("Unilateral inside (cavity side)", "unilateralInside")
                ], Description: "Bilateral reports the full band width max-min. Unilateral reports only the allowed side and fails when any sample crosses to the other side - the metrology-room reading."),
                new("tolerance", "Profile Tolerance", "number", 0, 0, 1000000, 0.001, Group: "Limits",
                    Description: "Total width of the profile zone (the value in the feature control frame). 0 disables the gate; utilization is not reported, compare profile directly."),
            ],
            "Profile of a line against a theoretical nominal: signed normal distance from every sample point to the nominal line or circle. Wire: samples PointList + exactly one of nominalLine/nominalCircle -> profile/max/min/mean/rms/crossedSampleCount, plus firstCrossedIndex/worstPointIndex/worstSignedDeviation for direct troubleshooting. nominalType states whether a line or a circle was used. Positive deviation means the +90 degree side of the line (below a left-to-right line) or outside the circle. Bilateral profile is max-min; unilateral modes report the allowed side only and count samples that crossed over. meanSampleSpacing/maxAdjacentPointDistance/coverageLength expose the sampling grid: when a sparse grid would miss a narrow high point within 20% of the limit, withinTolerance turns false with reason=dense-resample-required instead of passing. Fewer than 3 samples returns found=false. Units follow the input coordinates; the nominal must be theoretical, not a golden-part fit.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true),
            Actions: [])
    ];
}
