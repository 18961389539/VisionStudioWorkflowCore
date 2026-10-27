using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>Catalog entries for the OpenCV 5 colour/denoise/stitch/ArUco nodes (2026-10-27).</summary>
internal static class OpenCvExtraCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Roi = new("roi", VisionDataType.Roi, Required: false);

    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("color.classify", "Color Cluster / Classify", "Recognition", [Exec, Image, Roi],
            [
                new("image", VisionDataType.Image), new("found", VisionDataType.Boolean),
                new("count", VisionDataType.Integer), new("classCounts", VisionDataType.String),
                new("dominantClass", VisionDataType.Integer), new("unmatchedPixels", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("mode", "Color Mode", "select", "cluster", Group: "Mode", Options:
                [
                    new("K-means clustering", "cluster"),
                    new("Reference colors", "reference")
                ], Description: "cluster learns the dominant colours in the ROI; reference assigns every pixel to the nearest configured colour within its tolerance."),
                new("colorSpace", "Color Space", "select", "lab", Group: "Mode", Options:
                [
                    new("CIELAB (L*, a*, b*)", "lab"),
                    new("RGB (0-255)", "rgb")
                ], Description: "Working space for distances and cluster centres. Lab is perceptual, so a fixed tolerance behaves more evenly across colours; RGB is easier to copy from a colour picker."),
                new("clusterCount", "Cluster Count", "number", 4, 2, 32, 1, Group: "Clustering",
                    VisibleWhen: new("mode", ["cluster"]), Description: "Number of K-means clusters (2..32). Output labels use 1..N; 0 stays background."),
                new("sampleStride", "Sample Stride", "number", 2, 1, 32, 1, Group: "Clustering",
                    VisibleWhen: new("mode", ["cluster"]), Description: "K-means sees every Nth pixel in X and Y. 1 uses every pixel; larger values lower fit cost without changing the final per-pixel label assignment."),
                new("maxSamples", "Maximum Samples", "number", 1000000, 1000, 4000000, 1000, Group: "Clustering",
                    VisibleWhen: new("mode", ["cluster"]), Description: "Hard cap on sampled pixels. Exceeding it fails instead of silently truncating the colour distribution; raise it or increase Sample Stride."),
                new("referenceColorsJson", "Reference Colors JSON", "textarea",
                    """[{"name":"red","r":255,"g":0,"b":0,"tolerance":30},{"name":"green","r":0,"g":255,"b":0,"tolerance":30}]""",
                    Group: "Reference", VisibleWhen: new("mode", ["reference"]),
                    Description: "JSON array of reference colours. In lab mode provide {name,l,a,b,tolerance}; in rgb mode provide {name,r,g,b,tolerance}. Tolerance is the maximum distance in the selected space; omitted values use Default Tolerance."),
                new("defaultTolerance", "Default Tolerance", "number", 30, 0, 500, 0.1, Group: "Reference",
                    VisibleWhen: new("mode", ["reference"]), Description: "Tolerance used when a reference entry omits its own tolerance. Lab values are Delta E 76 distances; RGB values are Euclidean distances in 0..255."),
            ],
            "Cluster or classify colours in an 8-bit 1/3/4-channel image. cluster runs deterministic K-means on ROI pixels and emits a 1..N label map, class counts and dominant class; reference assigns pixels to configured colours within their tolerance and marks the rest as 0. Output image is a single-channel label map. Use ROI to exclude background. Failures: non-8-bit image, too few samples, empty reference list, duplicate names or invalid tolerances. algorithmBackend=opencv.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false, Deterministic: true, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: [], Frames: [new(NodeImageFramePolicy.PreserveInput)], RoiImageBindings: [new("roi", "image")]),

        new("image.denoise", "Non-Local Means Denoise", "Preprocess", [Exec, Image, Roi],
            [
                new("image", VisionDataType.Image), new("width", VisionDataType.Integer),
                new("height", VisionDataType.Integer), new("channels", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("mode", "Denoise Mode", "select", "auto", Group: "Denoising", Options:
                [
                    new("Automatic (by channels)", "auto"),
                    new("Grayscale", "gray"),
                    new("Color (BGR)", "color")
                ], Description: "auto chooses grayscale for 1-channel input and colour for 3-channel input; gray and color require the matching channel count."),
                new("h", "Luminance Strength", "number", 3, 0, 100, 0.5, Group: "Denoising",
                    Description: "Luminance noise strength. Higher values remove more noise but also flatten fine texture; 3..10 is the usual range and 0 disables the filter."),
                new("hColor", "Color Strength", "number", 3, 0, 100, 0.5, Group: "Denoising",
                    VisibleWhen: new("mode", ["auto", "color"]), Description: "Colour-channel noise strength, used only when the effective mode is colour."),
                new("templateWindowSize", "Template Window Size", "number", 7, 3, 99, 2, Group: "Neighborhood",
                    Description: "Odd patch size used for similarity. 7 is the OpenCV default; larger patches are slower and smooth more."),
                new("searchWindowSize", "Search Window Size", "number", 21, 3, 255, 2, Group: "Neighborhood",
                    Description: "Odd search window for similar patches. 21 is the OpenCV default; larger windows cost more time."),
            ],
            "Fast non-local means denoising for low-light or high-gain images before measurement. Requires an 8-bit 1 or 3 channel image; 16-bit/float inputs must be converted first. ROI only replaces pixels inside the selection, leaving the rest of the image untouched. Failures: non-8-bit input, channel/mode mismatch, even window sizes, or out-of-range strengths. algorithmBackend=opencv.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false, Deterministic: true, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: [], Frames: [new(NodeImageFramePolicy.PreserveInput)], RoiImageBindings: [new("roi", "image")]),

        new("image.stitch", "Image Stitch", "Preprocess",
            [Exec, new("image1", VisionDataType.Image), new("image2", VisionDataType.Image),
                new("image3", VisionDataType.Image, Required: false), new("image4", VisionDataType.Image, Required: false),
                new("image5", VisionDataType.Image, Required: false), new("image6", VisionDataType.Image, Required: false),
                new("image7", VisionDataType.Image, Required: false), new("image8", VisionDataType.Image, Required: false)],
            [
                new("image", VisionDataType.Image), new("width", VisionDataType.Integer),
                new("height", VisionDataType.Integer), new("inputCount", VisionDataType.Integer),
                new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("mode", "Stitch Mode", "select", "panorama", Group: "Stitching", Options:
                [
                    new("Panorama", "panorama"),
                    new("Document scans", "scans")
                ], Description: "Panorama handles arbitrary overlapping views; Document scans assumes ordered, mostly translation-only scans and is usually more stable for conveyor/line-scan images."),
                new("maxInputPixels", "Maximum Input Pixels", "number", 64000000, 1, 500000000, 1000000, Group: "Stitching",
                    Description: "Total pixel budget across all connected inputs. Exceeding it fails before cloning or stitching, so a mistaken full-resolution batch cannot exhaust memory."),
            ],
            "Stitch 2..8 overlapping 8-bit images into one panorama with OpenCV Stitcher (features, homography, bundle adjustment, seam and blend). Inputs must share the same channel count. A stitch failure returns the OpenCV status in the error instead of a half-composed image. algorithmBackend=opencv.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: [], Frames: [new(NodeImageFramePolicy.NewSpatialFrame, InputPort: "image1", FrameLabel: "stitched frame")]),

        new("recognition.aruco", "ArUco Marker", "Recognition", [Exec, Image, new("calibrationJson", VisionDataType.String, Required: false)],
            [
                new("found", VisionDataType.Boolean), new("count", VisionDataType.Integer),
                new("markerIds", VisionDataType.String), new("detections", VisionDataType.DetectionList),
                new("corners", VisionDataType.String), new("poses", VisionDataType.String, Required: false),
                new("pose", VisionDataType.Transform3D, Required: false), new("poseAvailable", VisionDataType.Boolean),
                new("rejectedCount", VisionDataType.Integer), new("overlay", VisionDataType.Image, Required: false),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("dictionary", "ArUco Dictionary", "select", "Dict4X4_50", Group: "Dictionary", Options:
                [
                    new("4x4, 50 markers", "Dict4X4_50"),
                    new("5x5, 100 markers", "Dict5X5_100"),
                    new("6x6, 250 markers", "Dict6X6_250"),
                    new("7x7, 50 markers", "Dict7X7_50"),
                    new("ArUco original", "DictArucoOriginal"),
                    new("AprilTag 36h10", "DictAprilTag_36h10"),
                    new("AprilTag 36h11", "DictAprilTag_36h11")
                ], Description: "Marker family used at the station. The dictionary must match the printed/engraved markers; a mismatch produces no IDs rather than wrong IDs."),
                new("maxMarkers", "Maximum Markers", "number", 100, 1, 1000, 1, Group: "Dictionary",
                    Description: "Hard cap on detected markers. Exceeding it fails instead of silently dropping markers."),
                new("cornerRefinement", "Corner Refinement", "select", "none", Group: "Detection", Options:
                [
                    new("None", "none"),
                    new("Subpixel", "subpix"),
                    new("Contour", "contour")
                ], Description: "Optional subpixel corner refinement. Subpixel improves pose stability on clean markers; contour can recover blurred edges at extra cost."),
                new("adaptiveThreshWinSizeMin", "Adaptive Threshold Min Window", "number", 3, 3, 101, 2, Group: "Detection",
                    Description: "Smallest adaptive-threshold window. Windows are compared from min to max in steps; keep min small for small markers."),
                new("adaptiveThreshWinSizeMax", "Adaptive Threshold Max Window", "number", 23, 3, 201, 2, Group: "Detection",
                    Description: "Largest adaptive-threshold window. Must be at least the minimum; larger values accept markers with broad lighting gradients."),
                new("adaptiveThreshWinSizeStep", "Adaptive Threshold Window Step", "number", 10, 1, 50, 1, Group: "Detection",
                    Description: "Step between adaptive-threshold windows. Smaller steps are slower but more tolerant of marker size changes."),
                new("adaptiveThreshConstant", "Adaptive Threshold Constant", "number", 7, -100, 100, 0.5, Group: "Detection",
                    Description: "Constant subtracted from the local threshold. Raise it for low-contrast markers; lower it for washed-out backgrounds."),
                new("minMarkerPerimeterRate", "Minimum Marker Perimeter Rate", "number", 0.03, 0, 1, 0.005, Group: "Detection",
                    Description: "Minimum marker perimeter as a fraction of the larger image dimension. Lower it for small markers; raise it to reject noise."),
                new("minDistanceToBorder", "Minimum Distance to Border", "number", 3, 0, 200, 1, Group: "Detection",
                    Description: "Markers closer than this many pixels to the image border are rejected. Keep a quiet zone around the marker."),
                new("errorCorrectionRate", "Error Correction Rate", "number", 0.6, 0, 1, 0.05, Group: "Detection",
                    Description: "Fraction of erroneous bits the decoder may correct. Higher values tolerate damaged markers but increase false IDs."),
                new("markerSize", "Marker Size", "number", 50, 0.001, 1000000, 0.1, Group: "Pose",
                    Description: "Physical marker side length used by solvePnP. Only read when calibrationJson is connected or filled."),
                new("markerSizeUnit", "Marker Size Unit", "text", "mm", Group: "Pose",
                    Description: "Unit of Marker Size and of the emitted 3D transform. 1..16 characters, for example mm or m."),
                new("calibrationJson", "Calibration JSON", "textarea", "{}", Group: "Pose",
                    Description: "Optional CameraCalibrationModel JSON from calibration.cameraIntrinsics. Connected calibrationJson takes precedence. Without calibration the node still detects markers but poseAvailable=false; fisheye and telecentric models are rejected for pose.")
                    { OverriddenByPorts = ["calibrationJson"] },
            ],
            "Detect ArUco/AprilTag markers and optionally solve their 3D pose. Outputs marker IDs, typed detections, corner JSON and, when calibrationJson is available, a marker-to-camera Transform3D plus poses JSON. Pose uses a pinhole Brown-Conrady model; fisheye and telecentric models are rejected because their Z interpretation differs. Failures: empty image, unknown dictionary, too many markers, invalid calibration JSON or pose-only parameters. algorithmBackend=opencv.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: true, Deterministic: true, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: [], Frames: [new(NodeImageFramePolicy.PreserveInput)], OutputFrames: [new(NodeOutputFrameSource.ImageInput, OutputPort: "detections", InputPort: "image")]),

        new("match.feature", "Feature Point Match", "Matching",
            [Exec, Image, new("template", VisionDataType.Image, Required: false), new("roi", VisionDataType.Roi, Required: false)],
            [
                new("image", VisionDataType.Image), new("found", VisionDataType.Boolean),
                new("score", VisionDataType.Double), new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false), new("angle", VisionDataType.Double, Required: false),
                new("scale", VisionDataType.Double, Required: false), new("center", VisionDataType.Point2D, Required: false),
                new("pose", VisionDataType.Pose2D, Required: false), new("matchCount", VisionDataType.Integer),
                new("inlierCount", VisionDataType.Integer), new("inlierRatio", VisionDataType.Double),
                new("reprojErrorPx", VisionDataType.Double), new("templateKeypoints", VisionDataType.Integer),
                new("sceneKeypoints", VisionDataType.Integer), new("transformJson", VisionDataType.String, Required: false),
                new("inlierPoints", VisionDataType.PointList, Required: false), new("detections", VisionDataType.DetectionList, Required: false),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("templatePath", "Template File", "text", string.Empty, Group: "Template",
                    Description: "Used when the template input port is not connected (absolute path, or relative to the host working directory). A connected template port overrides it.")
                    { OverriddenByPorts = ["template"] },
                new("detector", "Detector", "select", "sift", Group: "Detector", Options:
                [
                    new("SIFT (rotation + scale, float descriptors)", "sift"),
                    new("ORB (fast, binary descriptors)", "orb")
                ], Description: "SIFT tolerates large rotation, scale change and mild occlusion at higher cost. ORB is several times faster and suits high-rate lines with mostly planar, well-textured parts."),
                new("maxFeatures", "Maximum Keypoints", "number", 2000, 100, 20000, 100, Group: "Detector",
                    Description: "Upper bound on keypoints kept per image. Higher values help on large images and repeated textures but cost time and memory."),
                new("contrastThreshold", "Contrast Threshold", "number", 0.04, 0.001, 1, 0.001, Group: "Detector",
                    VisibleWhen: new("detector", ["sift"]),
                    Description: "SIFT contrast gate. Lower it (0.01) for low-contrast parts, raise it (0.08) to drop noise keypoints. ORB ignores this value."),
                new("matchRatio", "Lowe Ratio", "number", 0.75, 0, 0.95, 0.01, Group: "Matching",
                    Description: "Nearest-neighbour ratio test: a match is kept only when its distance is below ratio times the second-best distance. 0 disables the test; lower values keep fewer but cleaner matches."),
                new("transformType", "Transform Type", "select", "similarity", Group: "Matching", Options:
                [
                    new("Similarity (rotation + uniform scale)", "similarity"),
                    new("Affine (adds anisotropic scale and shear)", "affine"),
                    new("Homography (planar perspective)", "homography")
                ], Description: "Similarity needs the fewest inliers and is the most stable for pick-and-place. Affine handles stretched parts; homography handles oblique views but amplifies noise."),
                new("ransacReprojThresholdPx", "RANSAC Reprojection Threshold", "number", 3, 0.1, 50, 0.1, Unit: "px", Group: "Matching",
                    Description: "A correspondence within this distance of the model counts as an inlier. Keep it near the expected keypoint localisation error (2-4 px typical)."),
                new("minMatches", "Minimum Matches", "number", 8, 3, 500, 1, Group: "Gates",
                    Description: "Minimum ratio-test matches required before RANSAC runs. Below it the node returns found=false with reason=too-few-matches."),
                new("minInliers", "Minimum Inliers", "number", 6, 3, 500, 1, Group: "Gates",
                    Description: "Minimum RANSAC inliers for a valid result. Too low a value lets accidental alignments pass; 6-10 is typical for textured parts."),
                new("minInlierRatio", "Minimum Inlier Ratio", "number", 0.2, 0, 1, 0.01, Group: "Gates",
                    Description: "Minimum inliers/matches. Low ratios mean the matches disagreed, which usually indicates a repeated texture rather than a real target."),
                new("angleRangeDeg", "Angle Range (±deg)", "number", 180, 0, 180, 1, Unit: "deg", Group: "Gates",
                    Description: "Reject results whose rotation exceeds this half-width. 180 disables the gate."),
                new("scaleMinPercent", "Minimum Scale", "number", 50, 10, 400, 1, Unit: "%", Group: "Gates",
                    Description: "Lower accepted scale bound relative to the template. Results below it return found=false with reason=scale-out-of-range."),
                new("scaleMaxPercent", "Maximum Scale", "number", 200, 10, 400, 1, Unit: "%", Group: "Gates",
                    Description: "Upper accepted scale bound relative to the template. Must be at least Minimum Scale."),
            ],
            "Descriptor-based alignment for the cases gray-edge shape match cannot hold: large rotation, noticeable scale change, weak edges with rich texture, or partial occlusion. SIFT/ORB keypoints are matched with KNN plus a Lowe ratio test, then RANSAC estimates the template-pixel to scene-pixel transform. Outputs the mapped template centre as x/y/pose plus angle (deg), scale, inlier statistics and a 3x3 transformJson that can feed jlvision.homMat. Insufficient matches return found=false with a reason in diagnostics instead of failing the run; configuration errors (no template descriptors, out-of-range parameters) fail hard. Single-target only, no rotation-trained multi-target search - use match.shape for that. algorithmBackend=opencv.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true, Deterministic: true, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: [], Frames: [new(NodeImageFramePolicy.PreserveInput)], RoiImageBindings: [new("roi", "image")])
    ];
}
