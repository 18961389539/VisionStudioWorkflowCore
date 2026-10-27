namespace VisionStudio.Engine;

/// <summary>
/// 图像预处理专项补全（node-gap-analysis.md 第九节）。
///
/// 这一批的共同点：**都用 OpenCV 或纯托管算量实现**，不走 JLVision ——
/// CLAHE、极坐标重映射、双边滤波在 JLVision 1.0.0 里没有对应算子，
/// 强行用 `jlvision.imageOp` 的 107 个算子组合不出来（见第九节 9.2）。
///
/// 全部声明 <c>Actions: []</c>，可在 pipeline 内执行。
/// </summary>
public static class PreprocessGapCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static readonly PortDescriptor ImageOut = new("image", VisionDataType.Image);
    private static readonly PortDescriptor ImageIn = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Width = new("width", VisionDataType.Integer);
    private static readonly PortDescriptor Height = new("height", VisionDataType.Integer);
    private static readonly PortDescriptor Channels = new("channels", VisionDataType.Integer);
    private static readonly PortDescriptor RoiIn = new("roi", VisionDataType.Roi, Required: false);

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.clahe", "CLAHE (Local Contrast)", "Preprocess",
            [Exec, ImageIn, RoiIn],
            [ImageOut, Width, Height, Channels, new("clipLimit", VisionDataType.Double),
             new("tilesX", VisionDataType.Integer), new("tilesY", VisionDataType.Integer), Diagnostics, Next],
            [
                new("clipLimit", "Clip Limit", "number", 2, .1, 40, .1, Group: "CLAHE",
                    Description: "Contrast amplification ceiling. The histogram of each tile is clipped at this multiple of the uniform level, which is exactly what stops CLAHE from exploding noise in flat areas. 1 is almost no enhancement; 4+ is aggressive."),
                new("tilesX", "Tile Columns", "number", 8, 1, 64, 1, Group: "CLAHE",
                    Description: "Horizontal tile count. Each tile is equalised independently and then bilinearly blended, so smaller tiles follow local shading more closely but cost more and can create blocky artefacts."),
                new("tilesY", "Tile Rows", "number", 8, 1, 64, 1, Group: "CLAHE",
                    Description: "Vertical tile count. Pick tiles so one tile still contains a real background patch — a tile smaller than the defect turns the defect itself into the local histogram."),
                new("colorMode", "Color Handling", "select", "luminance", Group: "CLAHE", Options:
                [
                    new("Luminance only (keep color)", "luminance"),
                    new("Grayscale output", "gray")
                ], Description: "luminance applies CLAHE to the Lab L channel and converts back, so color stays intact (what color defects need). gray collapses to single channel — use it when the next node thresholds grayscale anyway."),
            ],
            "Contrast Limited Adaptive Histogram Equalization on 8-bit input. Unlike image.contrastNormalize (one global stretch) and jlvision.imageOp equHisto (one global histogram), CLAHE equalises per tile with a clip limit, so a part lit from one side keeps detail in both the bright and the dark half. Key params: clipLimit, tilesX/tilesY, colorMode. ROI only replaces pixels inside the selection. algorithmBackend=opencv.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("image.polarUnwrap", "Polar Unwrap", "Preprocess",
            [Exec, ImageIn, new("circle", VisionDataType.Circle, Required: false)],
            [ImageOut, Width, Height, new("centerX", VisionDataType.Double), new("centerY", VisionDataType.Double),
             new("innerRadius", VisionDataType.Double), new("outerRadius", VisionDataType.Double),
             new("angleStartDeg", VisionDataType.Double), new("angleSweepDeg", VisionDataType.Double), Diagnostics, Next],
            [
                new("centerX", "Center X", "number", 0, -1000000, 1000000, .5, Group: "Center",
                    Description: "Unwrap center X when the circle input is disconnected. A connected circle overrides both center coordinates."),
                new("centerY", "Center Y", "number", 0, -1000000, 1000000, .5, Group: "Center",
                    Description: "Unwrap center Y when the circle input is disconnected."),
                new("innerRadius", "Inner Radius (0=auto)", "number", 0, 0, 1000000, .5, Unit: "px", Group: "Radial Band",
                    Description: "First radius included in the strip. 0 starts at the center; use it to skip the hub of a gear or the hole of a ring."),
                new("outerRadius", "Outer Radius (0=auto)", "number", 0, 0, 1000000, .5, Unit: "px", Group: "Radial Band",
                    Description: "Last radius included. 0 derives it from the connected circle radius, or from the distance to the nearest image border."),
                new("angleStartDeg", "Start Angle", "number", 0, -3600, 3600, .5, Unit: "deg", Group: "Angular",
                    Description: "Angle mapped to the left edge of the strip, measured CCW from +X in image coordinates."),
                new("angleSweepDeg", "Sweep Angle", "number", 360, 1, 360, .5, Unit: "deg", Group: "Angular",
                    Description: "Angular span covered by the strip. 360 wraps the full circle; a smaller sweep inspects one sector only."),
                new("angularSamples", "Angular Samples (0=auto)", "number", 0, 0, 8192, 1, Group: "Sampling",
                    Description: "Strip width in pixels. 0 derives it from the mid-band circumference, so the unwrapped image keeps roughly square pixels."),
                new("radialSamples", "Radial Samples (0=auto)", "number", 0, 0, 4096, 1, Group: "Sampling",
                    Description: "Strip height in pixels. 0 corresponds to one pixel per radius unit of the band."),
                new("interpolation", "Interpolation", "select", "linear", Group: "Sampling", Options:
                [
                    new("Nearest", "nearest"),
                    new("Linear", "linear"),
                    new("Cubic", "cubic")
                ], Description: "Resampling used by the polar remap. Linear is the usual choice; nearest is only sensible when the source is a hard-edged mask."),
                new("direction", "Direction", "select", "counterClockwise", Group: "Angular", Options:
                [
                    new("Counter-clockwise", "counterClockwise"),
                    new("Clockwise", "clockwise")
                ], Description: "Which way the angle grows across the strip. Image coordinates are y-down, so clockwise here means increasing image angle."),
            ],
            "Maps an annular band onto a straight strip: X = angle, Y = radius. Straightening is what lets calipers, FFT filters and thread/gear nodes work on round parts (gears, bearings, O-rings, bores) whose features are periodic in angle. Neither a homography (image.perspective) nor an affine warp can express this. Wire: image + optional circle -> strip image. Auto radii keep the band inside the image; the pixel budget is rejected instead of allocating a huge Mat.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("image.bilateral", "Bilateral Filter", "Preprocess",
            [Exec, ImageIn, RoiIn],
            [ImageOut, Width, Height, Channels, new("sigmaColor", VisionDataType.Double),
             new("sigmaSpace", VisionDataType.Double), new("diameter", VisionDataType.Integer), Diagnostics, Next],
            [
                new("diameter", "Diameter (0=auto)", "number", 0, 0, 31, 1, Group: "Kernel",
                    Description: "Neighbourhood diameter in pixels. 0 derives it from sigmaSpace (5 for small sigmas, up to 9), which is what OpenCV documents as the sane default."),
                new("sigmaColor", "Sigma Color", "number", 25, 1, 500, 1, Group: "Kernel",
                    Description: "How different a neighbour's gray level may be and still be averaged. Smaller keeps edges harder and removes less texture; this is the parameter that separates 'smooth the brushed surface' from 'erase the scratch'."),
                new("sigmaSpace", "Sigma Space", "number", 25, 1, 500, 1, Group: "Kernel",
                    Description: "Spatial extent of the kernel in pixels. Large values average far-away similar pixels, which is slow but flattens low-frequency texture."),
            ],
            "Edge-preserving smoothing (OpenCV bilateral filter) for 8-bit 1/3 channel images. Gaussian blur averages across edges and non-local means denoises; neither separates 'brushed texture' from 'a scratch', which is what sigmaColor does here: only neighbours with a similar gray level are averaged. Wire: image -> image; ROI only replaces pixels inside the selection. Failures: non-8-bit input, or a channel count other than 1 or 3. algorithmBackend=opencv.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("image.whiteBalance", "White Balance", "Preprocess",
            [Exec, ImageIn, RoiIn],
            [ImageOut, new("gainR", VisionDataType.Double), new("gainG", VisionDataType.Double), new("gainB", VisionDataType.Double),
             new("mode", VisionDataType.String), new("channelMeansJson", VisionDataType.String), Diagnostics, Next],
            [
                new("mode", "Estimation Mode", "select", "grayWorld", Group: "Gains", Options:
                [
                    new("Gray world (channel means)", "grayWorld"),
                    new("White patch (bright percentile)", "whitePatch"),
                    new("Manual gains", "manual")
                ], Description: "Gray world assumes the scene averages to gray (good for mixed product shots). White patch assumes the brightest percentile is a white reference (better when one dominant material fills the frame). Manual applies the three gains below unchanged."),
                new("percentile", "White Patch Percentile", "number", 99, 50, 100, .5, Unit: "%", Group: "Gains",
                    VisibleWhen: new("mode", ["whitePatch"]),
                    Description: "Percentile of each channel treated as the white reference. 99 ignores the top 1% (specular highlights) while still catching the bright reference surface."),
                new("manualR", "Manual Gain R", "number", 1, .1, 10, .01, Group: "Gains",
                    VisibleWhen: new("mode", ["manual"]), Description: "Red channel multiplier applied as-is."),
                new("manualG", "Manual Gain G", "number", 1, .1, 10, .01, Group: "Gains",
                    VisibleWhen: new("mode", ["manual"]), Description: "Green channel multiplier applied as-is."),
                new("manualB", "Manual Gain B", "number", 1, .1, 10, .01, Group: "Gains",
                    VisibleWhen: new("mode", ["manual"]), Description: "Blue channel multiplier applied as-is."),
                new("preserveBrightness", "Preserve Brightness", "boolean", true, Group: "Gains",
                    Description: "Rescales all three gains so the mean brightness stays where it was. Without it a white balance can silently push the whole image toward clipping."),
            ],
            "Estimates and applies per-channel gains on a 3-channel 8-bit image. The apply step is already expressible with jlvision.imageOp (split, scale, compose3); what was missing is the estimation, which is why this node exists. Outputs the applied gains for traceability, so a later run or recipe can reuse them. Requires a 3-channel image; grayscale input fails instead of pretending. algorithmBackend=managed.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("image.qualityMetrics", "Image Quality Metrics", "Preprocess",
            [Exec, ImageIn, RoiIn],
            [new("found", VisionDataType.Boolean), new("brightness", VisionDataType.Double), new("contrast", VisionDataType.Double),
             new("sharpness", VisionDataType.Double), new("noiseSigma", VisionDataType.Double),
             new("saturationRatio", VisionDataType.Double), new("darkRatio", VisionDataType.Double),
             new("entropy", VisionDataType.Double), new("pixelCount", VisionDataType.Integer), Diagnostics, Next],
            [
                new("saturationThreshold", "Saturation Threshold", "number", 254, 1, 255, 1, Group: "Thresholds",
                    Description: "Gray level at or above which a pixel counts as clipped. 254 catches pixels that are already quantisation-dead even if they are not exactly 255."),
                new("darkThreshold", "Dark Threshold", "number", 1, 0, 254, 1, Group: "Thresholds",
                    Description: "Gray level at or below which a pixel counts as crushed black."),
            ],
            "One call, one row of numbers an auto-exposure or auto-focus loop can act on: brightness (masked mean), contrast (P95-P5), sharpness (mean squared Sobel gradient), noiseSigma (high-pass residual estimate), saturationRatio, darkRatio, entropy and pixelCount. Every value is derivable from existing nodes, but it takes 3-5 of them wired together and the saturated-pixel fraction has no direct source at all. Values are comparable across frames of the same scene, not calibrated physical units. algorithmBackend=opencv.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("image.depthScale", "Depth Scale", "Preprocess",
            [Exec, ImageIn],
            [ImageOut, new("inputBits", VisionDataType.Integer), new("outputBits", VisionDataType.Integer),
             new("scaleUsed", VisionDataType.Double), new("offsetUsed", VisionDataType.Double), Diagnostics, Next],
            [
                new("inputBits", "Input Bits (0=auto)", "number", 0, 0, 16, 1, Group: "Mapping",
                    Description: "Bit depth of the current sample values. 0 reads it from the Mat type (8 or 16). Set it to 10/12/14 when a 16-bit container carries a narrower camera range, so the auto scale does not darken the image."),
                new("outputBits", "Output Bits", "select", "8", Group: "Mapping", Options:
                [
                    new("8-bit", "8"),
                    new("16-bit", "16")
                ], Description: "Container depth written downstream. 8-bit is what most measurement nodes expect; 16-bit keeps headroom for accumulation or a later Python/CSV export."),
                new("scale", "Scale (0=auto)", "number", 0, 0, 1000000, .0001, Group: "Mapping",
                    Description: "Explicit multiplier. 0 derives (2^out-1)/(2^in-1), which maps the full input range onto the full output range without clipping."),
                new("offset", "Offset", "number", 0, -1000000, 1000000, 1, Group: "Mapping",
                    Description: "Added after scaling. Negative offsets are useful to lift a black level that sits above zero."),
            ],
            "Explicit, auditable bit-depth conversion between 8 and 16 bits. Today the only conversions are implicit: threshold nodes scale UInt16 by 1/257, and jlvision.imageOp convertType/scale can do it in two steps with the parameters spread out. This node makes the mapping (inputBits, scale, offset) visible in the trace, which matters for 10/12/16-bit cameras. Rejects float input instead of guessing.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("image.channelMix", "Channel Mix", "Preprocess",
            [Exec, ImageIn],
            [ImageOut, new("channels", VisionDataType.Integer), new("weightsJson", VisionDataType.String), Diagnostics, Next],
            [
                new("weight1", "Weight 1 (B / gray)", "number", .114, -10, 10, .001, Group: "Weights",
                    Description: "Weight of channel 1: blue for BGR input, the only channel for grayscale. Defaults follow the Rec.601 luma coefficients."),
                new("weight2", "Weight 2 (G)", "number", .587, -10, 10, .001, Group: "Weights",
                    Description: "Weight of channel 2 (green). Ignored when the input has fewer channels."),
                new("weight3", "Weight 3 (R)", "number", .299, -10, 10, .001, Group: "Weights",
                    Description: "Weight of channel 3 (red). Negative weights are allowed on purpose: red-minus-blue is a cheap way to make a chromatic defect pop."),
                new("weight4", "Weight 4 (A)", "number", 0, -10, 10, .001, Group: "Weights",
                    Description: "Weight of channel 4 (alpha). Zero unless the input is BGRA."),
                new("normalize", "Normalize Weights", "boolean", true, Group: "Weights",
                    Description: "Divides the weights by their sum so the result keeps the source brightness. Turn it off when the absolute scale matters (for example a difference-style mix)."),
                new("offset", "Offset", "number", 0, -255, 255, 1, Group: "Weights",
                    Description: "Added after the mix and before saturation, useful to re-center a signed mix into the visible range."),
            ],
            "Weighted linear mix of the input channels into one 8-bit gray image. image.grayscale offers three fixed luma presets; when a defect is only visible in one color direction you need arbitrary (including negative) coefficients, which previously took jlvision.imageOp imageToChannels + scale + add. Wire: image -> image. Channels beyond the input count are ignored; all-zero weights fail.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: [])
    ];
}
