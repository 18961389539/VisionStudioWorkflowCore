namespace VisionStudio.Engine;

public static partial class BuiltInNodeCatalog
{
    private static NodeCatalogItem[] ImageProcessingItems =>
    [
        // ===== 批次一（2026-10-13 节点补齐）：图像预处理族 =====
        new(
            "image.channel",
            "Channel Extract",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
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
                new("weights", "Gray Weights", "select", "bt601", VisibleWhen: new("channel", ["gray", "luminance"]),
                    Options: [new("BT.601", "bt601"), new("BT.709", "bt709"), new("Average", "avg")],
                    Description: "Luma weights for RGB to gray (gray/luminance only). BT.601 uses the native conversion; BT.709 and Average use per-channel weighting.")],
            "Extract one channel (gray/B/G/R or HSV H/S/V, plus average/max gray) as a single-channel image. Gray/luminance also selects luma weights. Color channels require color input; unknown channels fail deterministically.",
            "builtin",
            new VisionToolCapabilities(EmitsOverlay: false)),
        new(
            "image.filter",
            "Image Filter",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String, Required: false), Next],
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
                new("borderType", "Border Type", "select", "reflect", Options: [new("Default", "default"), new("Reflect", "reflect"), new("Replicate", "replicate"), new("Constant", "constant"), new("Wrap", "wrap")], Description: "Border extrapolation for the filter kernel. Default and Wrap are accepted by the gaussian path only; median rejects them.")],
            "Gaussian suppresses general noise; median removes salt-and-pepper while preserving edges. Wire: image → filtered image. Kernel size must be odd. Common before calipers/edges; large kernels hurt localization.",
            "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new(
            "image.morphology",
            "Morphology",
            "Preprocess",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("stdIntensity", VisionDataType.Double), new("nonZeroCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String, Required: false), Next],
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
            "measure.blobs",
            "Multi Blob",
            "Measurement",
            [ExecIn, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false)],
            [
                new("image", VisionDataType.Image),
                new("count", VisionDataType.Integer),
                new("inputCount", VisionDataType.Integer),
                new("truncated", VisionDataType.Boolean),
                new("detections", VisionDataType.DetectionList),
                new("x", VisionDataType.Double, Required: false),
                new("y", VisionDataType.Double, Required: false),
                new("area", VisionDataType.Double, Required: false),
                new("radius", VisionDataType.Double, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("circle", VisionDataType.Circle, Required: false),
                new("found", VisionDataType.Boolean, Required: false),
                new("circularity", VisionDataType.Double, Required: false),
                new("width", VisionDataType.Double, Required: false),
                new("height", VisionDataType.Double, Required: false),
                new("blobs", VisionDataType.String, Required: false),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("mode", "Blob Mode", "select", "all", Options:
                [new("All Blobs", "all"), new("Largest Blob", "largest")], Group: "Mode", Description: "all returns every filtered blob up to Max Count; largest returns the qualifying blob with the largest area. The filters and Min Area apply in both modes; Max Count, Sort By and Descending are used only by all."),
                new("minArea", "Min Area", "number", 100, 0, 99999999, 10, Group: "Filter", Description: "Minimum accepted area in pixels."),
                new("maxArea", "Max Area", "number", 0, 0, 99999999, 10, Group: "Filter", Description: "0 disables the upper bound."),
                new("minCircularity", "Min Circularity", "number", 0, 0, 1, 0.05, Group: "Filter", Description: "4*pi*A/P^2, 1 = perfect circle."),
                new("minWidth", "Min Width", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the lower bound."),
                new("maxWidth", "Max Width", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the upper bound."),
                new("minHeight", "Min Height", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the lower bound."),
                new("maxHeight", "Max Height", "number", 0, 0, 99999999, 1, Unit: "px", Group: "Filter", Description: "0 disables the upper bound."),
                new("minRectangularity", "Min Rectangularity", "number", 0, 0, 1, 0.05, Group: "Filter", Description: "Area / bounding-box area, 1 = perfect rectangle."),
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
            "Analyze either all filtered/sorted blobs or only the largest one. The input must be binary — every non-zero pixel counts as foreground, so feed a threshold/range/mask output rather than raw grayscale. Connect exactly one of image or domainImage; an optional region or ROI restricts the analysis area without copying the image. detections.index is a post-sort ordinal, not a tracking id across frames. area counts real foreground pixels and excludes holes; circularity, rectangularity and the bounding box come from the filled outer contour, so a ring reads as round while its hole is not counted in area. Filters combine area, circularity, bounding-box width/height and rectangularity (HALCON select_shape style); sorting supports area, X, Y and circularity. Outputs a DetectionList, the selected target geometry and source image; zero blobs is a normal result (count=0). count is the emitted length and inputCount the pre-cap candidate count, so truncated=true means maxCount (default 20) cut the list and more blobs exist than the outputs show. totalArea sums the emitted blobs only — compare count with inputCount before treating it as the whole-scene area.",
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
                new("templateAssetId", "Template Asset", "text", string.Empty, Group: "Model", Description: "Optional template-image asset id (kind=template). Used when the template port is not connected and takes precedence over Template File; a missing asset fails instead of falling back."),
                new("templateAssetVersion", "Template Asset Version", "number", 0, 0, 1000000, 1, Group: "Model", Description: "0 loads the asset's latest version; a positive number pins an immutable version."),
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
            "feature.edge",
            "Edge / Canny",
            "Feature",
            [ExecIn, new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("edgeCount", VisionDataType.Integer), new("found", VisionDataType.Boolean), new("chainCount", VisionDataType.Integer), new("longestChain", VisionDataType.Integer), new("chains", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
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
                new("lines", VisionDataType.String), new("diagnostics", VisionDataType.String), Next
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
                new("circles", VisionDataType.String), new("diagnostics", VisionDataType.String), Next
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
                new("searchLine", VisionDataType.Line2D), new("diagnostics", VisionDataType.String), Next
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
                new("sigma", "Sigma", "number", 1, 0.4, 100, 0.1, Group: "Sampling", Description: "Gaussian smoothing sigma of the native 1D edge filter. Larger values suppress noise but can blur closely spaced edges; profiles shorter than 8 samples use a managed gradient fallback that ignores sigma. The default 1 preserves previous behavior."),
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
                new("diagnostics", VisionDataType.String), Next
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
            [new("distance", VisionDataType.Double), new("dx", VisionDataType.Double), new("dy", VisionDataType.Double), new("midX", VisionDataType.Double), new("midY", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
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
            [new("angle", VisionDataType.Double), new("signedAngleDeg", VisionDataType.Double), new("lineALength", VisionDataType.Double), new("lineBLength", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
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
                new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
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
    ];
}
