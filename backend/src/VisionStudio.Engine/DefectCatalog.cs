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
        new("image.difference", "Image Difference", "Defect", [Exec, Image, new("reference", VisionDataType.Image)], [Image, Next],
            [new("mode", "Difference Mode", "select", "absolute", Options: [new("Absolute difference", "absolute"), new("Signed image-reference", "signed")])],
            "Compare aligned grayscale images with JLVision arithmetic. Output is float32; signed mode preserves negative differences. No automatic alignment or brightness compensation.", "builtin"),
        new("defect.referenceCompare", "Reference Defect Compare", "Defect", [.. Source, new("reference", VisionDataType.Image), new("region", VisionDataType.Region, Required: false)], RegionOutputs,
            [Polarity, new("threshold", "Reference Difference Threshold", "number", 15, 0.001, 65535, 1), new("minArea", "Minimum Defect Area", "number", 5, 0, 99999999, 1, Unit: "px²")],
            "Compare aligned image and good reference, threshold differences, split defects, and filter pixel area. Bright means image-reference; dark means reference-image. Optional region/domain limits inspection. Threshold includes equality.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.regionIntensity", "Region Gray Statistics", "Defect", [.. Source, Region],
            [.. RegionOutputs, new("mean", VisionDataType.Double, Required: false), new("stdDev", VisionDataType.Double, Required: false), new("min", VisionDataType.Double, Required: false), new("max", VisionDataType.Double, Required: false), new("range", VisionDataType.Double, Required: false)], [],
            "Grayscale mean, population deviation, min/max and range over the union of region pixels intersected with the image domain. Overlapping objects count once. Empty selection returns found=false and omits statistics; check found before consuming them.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
