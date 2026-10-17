namespace VisionStudio.Engine;

public static class DefectCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] Source = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false)];
    private static readonly PortDescriptor[] RegionOutputs = [Region, Image, new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), Next];
    private static readonly ParameterDescriptor Polarity = new("polarity", "Defect Polarity", "select", "both", Options: [new("Bright defects", "bright"), new("Dark defects", "dark"), new("Both polarities", "both")]);
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.flatFieldCorrect", "Flat-field Correction", "Defect", [Exec, Image, new("dark", VisionDataType.Image), new("bright", VisionDataType.Image)], [Image, Next],
            [new("targetMean", "Target Gray (0=auto)", "number", 0, 0, 65535, 1), new("minGain", "Minimum Bright-dark Gain", "number", 1, 0.000001, 65535, 0.1), new("outputType", "Correction Output", "select", "byte", Options: [new("Byte (0-255)", "byte"), new("Real (unclipped)", "real")])],
            "Grayscale flat-field correction: (image-dark)/(bright-dark)*target. Auto target is mean bright-dark. All gain pixels must exceed minGain. References must match dimensions; byte output clips, real output preserves range.", "builtin"),
        new("image.dynamicThreshold", "Dynamic Defect Threshold", "Defect", [.. Source, new("background", VisionDataType.Image, Required: false), new("region", VisionDataType.Region, Required: false)], RegionOutputs,
            [Polarity, new("offset", "Defect Contrast", "number", 10, 0.001, 65535, 1), new("windowSize", "Background Window", "number", 31, 3, 255, 2)],
            "Detect local bright/dark deviations using JLVision dynamic threshold. Background comes from the input or a local mean with an odd window. Connect image or domainImage; optional region further limits detection. Output objects are not split.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("image.difference", "Image Difference", "Defect", [Exec, Image, new("reference", VisionDataType.Image)],
            [Image, new("mean", VisionDataType.Double), new("stdDev", VisionDataType.Double), new("min", VisionDataType.Double), new("max", VisionDataType.Double), new("aboveThresholdFraction", VisionDataType.Double, Required: false), Next],
            [new("mode", "Difference Mode", "select", "absolute", Options: [new("Absolute difference", "absolute"), new("Signed image-reference", "signed"), new("Normalized (image-ref)/(image+ref)", "normalized")]), new("scale", "Difference Scale", "number", 1, 0.001, 1000, 0.1), new("threshold", "Exceedance Threshold (0=off)", "number", 0, 0, 65535, 1, Description: "When > 0, also report the fraction of pixels whose absolute difference reaches this value.")],
            "Compare aligned grayscale images with JLVision arithmetic. Output is float32; signed mode preserves negative differences, normalized mode reports the illumination ratio in [-1,1]. Mean/stdDev/min/max are always reported; no automatic alignment is performed (use defect.referenceCompare for registration and grading).", "builtin"),
        new("defect.referenceCompare", "Reference Defect Compare", "Defect", [.. Source, new("reference", VisionDataType.Image), new("region", VisionDataType.Region, Required: false)],
            [Region, Image, new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), new("defects", VisionDataType.String), new("maxDefectArea", VisionDataType.Double), new("grade", VisionDataType.String), Next],
            [
                Polarity, new("threshold", "Reference Difference Threshold", "number", 15, 0.001, 65535, 1), new("minArea", "Minimum Defect Area", "number", 5, 0, 99999999, 1, Unit: "px²"),
                new("maxArea", "Maximum Defect Area (0=unbounded)", "number", 0, 0, 99999999, 1, Unit: "px²"),
                new("registrationTolerance", "Registration Tolerance", "number", 0, 0, 25, 1, Unit: "px", Description: "Exhaustively search reference shifts within +/- this many pixels and keep the best alignment. 0 disables registration."),
                new("compensation", "Illumination Compensation", "select", "none", Options: [new("None", "none"), new("Offset Only", "offset"), new("Gain + Offset", "gainoffset")], Description: "Least-squares fit of the reference toward the inspected image before differencing (variation-model style)."),
                new("morphologyOpen", "Morphology Open Kernel", "number", 0, 0, 15, 2, Unit: "px", Description: "Opening kernel applied to the defect mask before splitting; 0 disables, otherwise 3-15 odd."),
                new("morphologyClose", "Morphology Close Kernel", "number", 0, 0, 15, 2, Unit: "px", Description: "Closing kernel applied after opening; 0 disables, otherwise 3-15 odd."),
                new("variationK", "Variation Model K (0=off)", "number", 0, 0, 20, 0.5, Description: "When > 0, a pixel must differ by at least threshold + K * local reference standard deviation (variation-model style; local variation is estimated from the single reference)."),
                new("variationWindow", "Variation Window", "number", 15, 3, 101, 2, Unit: "px", Description: "Odd window for the local variation estimate."),
                new("majorArea", "Major Defect Area", "number", 500, 0, 99999999, 1, Unit: "px²"),
                new("criticalArea", "Critical Defect Area", "number", 5000, 0, 99999999, 1, Unit: "px²")
            ],
            "Golden-template defect comparison (PatInspect/variation-model style): optional small-shift registration and illumination compensation align the good reference, differences are thresholded, morphology cleans the mask, defects are split, area-filtered and graded minor/major/critical. defects JSON lists area and bounding box per defect; grade is the worst defect grade (none when clean). Bright means image-reference; dark means reference-image. Optional region/domain limits inspection. Threshold includes equality.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.regionIntensity", "Region Gray Statistics", "Defect", [.. Source, Region],
            [.. RegionOutputs, new("mean", VisionDataType.Double, Required: false), new("stdDev", VisionDataType.Double, Required: false), new("min", VisionDataType.Double, Required: false), new("max", VisionDataType.Double, Required: false), new("range", VisionDataType.Double, Required: false)], [],
            "Grayscale mean, population deviation, min/max and range over the union of region pixels intersected with the image domain. Overlapping objects count once. Empty selection returns found=false and omits statistics; check found before consuming them.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
