namespace VisionStudio.Engine;

public static class RegionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] Outputs = [Region, new("image", VisionDataType.Image), new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String, Required: false), new("sourceMappings", VisionDataType.String, Required: false), new("filterDetails", VisionDataType.String, Required: false), new("holesJson", VisionDataType.String, Required: false), new("filledPixels", VisionDataType.Integer, Required: false), new("connectivity", VisionDataType.Integer, Required: false), new("warningCount", VisionDataType.Integer, Required: false), Next];
    private static readonly PortDescriptor[] Images = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false)];
    private static readonly PortDescriptor[] RangeImages = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false), new("minGray", VisionDataType.Double, Required: false), new("maxGray", VisionDataType.Double, Required: false)];
    // count 是区域对象数（阈值区域不自动拆分连通域），blobCount 才是目标个数，单独成端口避免误读。
    private static readonly PortDescriptor[] RangeOutputs = [Region, new("image", VisionDataType.Image), new("count", VisionDataType.Integer), new("blobCount", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String, Required: false), Next];
    internal static IReadOnlyList<ParameterDescriptor> MaskParameters { get; } =
    [
        new("maskMode", "Mask Mode", "select", "byte", Group: "Mask",
            Options: [new("8-bit Grayscale", "byte"), new("Float Probability", "probability")],
            Description: "Select the existing 8-bit grayscale conversion or threshold a single-channel float probability image directly."),
        new("threshold", "Mask Threshold", "number", 1, 1, 255, 1, Group: "Mask", VisibleWhen: new("maskMode", ["byte"]),
            Description: "Pixels at or above this level are foreground. 1 selects all non-zero pixels.") { Integer = true },
        new("probabilityThreshold", "Probability Threshold", "number", 0.5, 0, 1, 0.01, Group: "Mask", VisibleWhen: new("maskMode", ["probability"]),
            Description: "Compare float32 or float64 probabilities in their original 0 to 1 value range. Values at or above the threshold are foreground."),
        new("invert", "Invert Mask", "boolean", false, Group: "Mask", Description: "Swap foreground and background after thresholding.")
    ];

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("region.fromRange", "Range Segmentation (band)", "Region", RangeImages, RangeOutputs,
            [
                new("mode", "Range Mode", "select", "gray", Group: "Mode", Options: [new("Gray", "gray"), new("Color", "color")], Description: "Gray mode keeps the source pixel type and validates thresholds against its actual bit depth; color mode handles three channels in the selected color space."),
                new("minGray", "Minimum Gray", "number", 128, -(double)float.MaxValue, (double)float.MaxValue, 1, Group: "Gray Range", VisibleWhen: new("mode", ["gray"]), Description: "Inclusive lower bound; byte 0..255, uint2 0..65535, int2 -32768..32767, int4 the signed 32-bit range, real any finite value. No 8-bit rescaling is applied. A wired minGray input overrides it.") { OverriddenByPorts = ["minGray"] },
                new("maxGray", "Maximum Gray", "number", 255, -(double)float.MaxValue, (double)float.MaxValue, 1, Group: "Gray Range", VisibleWhen: new("mode", ["gray"]), Description: "Inclusive upper bound; must be >= the lower bound and within the actual input pixel type's range. Diagnostics report pixelType and the effective range. A wired maxGray input overrides it.") { OverriddenByPorts = ["maxGray"] },
                new("space", "Color Space", "select", "hsv", Group: "Color Range", Options: [new("HSV (degrees, 0-1, 0-1)", "hsv"), new("sRGB (0-1)", "rgb"), new("CIELAB (D65)", "lab")], VisibleWhen: new("mode", ["color"]), Description: "Color space for channel bounds when mode=color."),
                new("min1", "Channel 1 Minimum", "number", 0, -360, 360, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive lower bound for channel 1 (H/L*/R)."),
                new("max1", "Channel 1 Maximum", "number", 360, -360, 360, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive upper bound for channel 1. HSV hue may wrap when min1>max1."),
                new("min2", "Channel 2 Minimum", "number", 0, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive lower bound for channel 2 (S/a*/G)."),
                new("max2", "Channel 2 Maximum", "number", 1, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive upper bound for channel 2."),
                new("min3", "Channel 3 Minimum", "number", .5, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive lower bound for channel 3 (V/b*/B)."),
                new("max3", "Channel 3 Maximum", "number", 1, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive upper bound for channel 3.")
            ,
                new("invert", "Invert Selection", "boolean", false, Group: "Output", Description: "Keep pixels outside the range instead. The complement stays inside the ROI when one is set, and inside the connected region/domain otherwise."),
                new("emptyPolicy", "Empty Result Policy", "select", "empty", Group: "Output", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "Behavior when no pixels pass the range.")],
            "Create a Region from an inclusive gray or three-channel color range (a band with both bounds), keeping the source pixel type. Connect exactly one of image or domainImage; optional region further restricts, and a ROI limits the result to the selection. Key params: channel bounds, invert, empty policy. count is the region object count — a threshold region keeps every run in one object, so it is 1 even with several targets; blobCount is the 8-connected component count and answers \"how many targets\". Need a single cut level and a 0/255 image instead? Use image.threshold; this node answers \"between two levels\" and returns a Region. Too-wide range swallows background; too-narrow drops the target.", "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new("region.connectedComponents", "Connected Regions", "Region", [Exec, Region], Outputs, [
                new("connectivity", "Connectivity", "select", "8", Options: [new("4-connected", "4"), new("8-connected", "8")], Description: "Explicit pixel adjacency. Diagonal pixels form two objects with 4-connectivity and one with 8-connectivity."),
                new("emptyPolicy", "Empty Result Policy", "select", "empty", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "When the result region is empty: emit empty keeps outputs with found=false; fail stops the graph.")],
            "Union input region objects and split with JLVision native connectivity. Object indices are not tracking IDs. Empty input returns zero objects.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.filter", "Filter Regions", "Region", [Exec, Region], Outputs,
            [new("feature", "Region Feature", "select", "area", Options: [new("Area", "area"), new("Circularity", "circularity"), new("Width", "width"), new("Height", "height"), new("Rectangularity", "rectangularity"), new("Anisometry", "anisometry")],
                Description: "Which per-object attribute to gate. Area is pixel count; anisometry is equivalent-ellipse axis ratio."),
             new("min", "Minimum Feature", "number", 0, 0, 99999999, 0.01, Description: "Inclusive lower bound. Area/width/height use pixels; circularity and rectangularity use 0..1; anisometry uses >=1."),
             new("max", "Maximum Feature", "number", 99999999, 0, 99999999, 0.01, Description: "Inclusive upper bound; must be >= min. Circularity and rectangularity are clamped by validation to the unit interval.")],
            "JLVision attribute selection on existing objects; does not split objects. Chain filters for multiple criteria. Area counts pixels; anisometry is the equivalent-ellipse axis ratio.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.boolean", "Region Boolean", "Region", [Exec, new("a", VisionDataType.Region), new("b", VisionDataType.Region)], Outputs,
            [new("operation", "Region Operation", "select", "union", Options: [new("Union", "union"), new("Intersection", "intersection"), new("A minus B", "difference"), new("Exclusive OR", "xor")],
                Description: "Set operation on the unions of A and B. Difference is A\\B. Both regions must share canvas size.")],
            "Set operations on the unions of A and B. Both must use the same image dimensions and coordinate system. Difference is A minus B; split the result explicitly if needed.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.fillHoles", "Fill Region Holes", "Region", [Exec, Region], Outputs, [new("maxHoleArea", "Maximum Hole Area (0=all)", "number", 0, 0, 100000000, 1, Description: "Fill only closed background holes at or below this pixel area; 0 fills every closed hole. Holes open to the canvas are never filled."), new("emptyPolicy", "Empty Region Policy", "select", "empty", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "Behavior when the input or filled result is empty.")],
            "Fill enclosed holes in each JLVision region object without bridging disconnected objects or altering input runs.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("image.reduceDomain", "Reduce Image Domain", "Region", [.. Images, Region],
            [new("domainImage", VisionDataType.DomainImage), Region, new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("requestedRegion", VisionDataType.Region, Required: false), new("existingRegion", VisionDataType.Region, Required: false), new("requestedArea", VisionDataType.Double), new("existingArea", VisionDataType.Double, Required: false), new("effectiveArea", VisionDataType.Double), new("warningsJson", VisionDataType.String), Next], [new("emptyPolicy", "Empty Domain Policy", "select", "fail", Options: [new("Fail", "fail"), new("Passthrough", "passthrough")], Description: "Behavior when the region is empty."),
                ],
            "Preserve owned image pixels and attach an explicit region domain. Repeated reductions intersect domains. Emits found (area>0) and diagnostics; emptyPolicy=fail rejects an empty region while passthrough emits an empty domain. Connect domainImage to region.fromRange; regular Image ports deliberately reject it to prevent silent domain loss.", "builtin"),
        new("region.fromMask", "Region From Mask", "Region", [Exec, new("image", VisionDataType.Image)],
            [new("region", VisionDataType.Region), new("image", VisionDataType.Image), new("count", VisionDataType.Integer), new("area", VisionDataType.Integer), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), Next],
            MaskParameters,
            "Convert a strict single-channel 8-bit mask or float32/float64 probability map into 8-connected regions. Byte mode never converts color or other depths; probability mode rejects NaN/Inf and values outside 0..1. The image output echoes a binary 0/255 mask and area counts its foreground pixels. found is false when the mask has no foreground. The region budget is 10000 components and 1000000 total runs; exceeding either fails with structured count/limit/threshold/sample diagnostics instead of truncating.", Capabilities: new VisionToolCapabilities(SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: [])
    ];
}
