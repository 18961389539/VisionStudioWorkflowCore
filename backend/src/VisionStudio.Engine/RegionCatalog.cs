namespace VisionStudio.Engine;

public static class RegionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] Outputs = [Region, new("image", VisionDataType.Image), new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String, Required: false), Next];
    private static readonly PortDescriptor[] Images = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false)];
    private static readonly PortDescriptor[] RangeImages = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false)];
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("region.fromRange", "Range Segmentation", "Region", RangeImages, Outputs,
            [
                new("mode", "Range Mode", "select", "gray", Group: "Mode", Options: [new("Gray", "gray"), new("Color", "color")], Description: "Gray mode keeps the source pixel type and validates thresholds against its actual bit depth; color mode handles three channels in the selected color space."),
                new("minGray", "Minimum Gray", "number", 128, -(double)float.MaxValue, (double)float.MaxValue, 1, Group: "Gray Range", VisibleWhen: new("mode", ["gray"]), Description: "Inclusive lower bound; byte 0..255, uint2 0..65535, int2 -32768..32767, int4 the signed 32-bit range, real any finite value. No 8-bit rescaling is applied."),
                new("maxGray", "Maximum Gray", "number", 255, -(double)float.MaxValue, (double)float.MaxValue, 1, Group: "Gray Range", VisibleWhen: new("mode", ["gray"]), Description: "Inclusive upper bound; must be >= the lower bound and within the actual input pixel type's range. Diagnostics report pixelType and the effective range."),
                new("space", "Color Space", "select", "hsv", Group: "Color Range", Options: [new("HSV (degrees, 0-1, 0-1)", "hsv"), new("sRGB (0-1)", "rgb"), new("CIELAB (D65)", "lab")], VisibleWhen: new("mode", ["color"]), Description: "Color space for channel bounds when mode=color."),
                new("min1", "Channel 1 Minimum", "number", 0, -360, 360, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive lower bound for channel 1 (H/L*/R)."),
                new("max1", "Channel 1 Maximum", "number", 360, -360, 360, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive upper bound for channel 1. HSV hue may wrap when min1>max1."),
                new("min2", "Channel 2 Minimum", "number", 0, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive lower bound for channel 2 (S/a*/G)."),
                new("max2", "Channel 2 Maximum", "number", 1, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive upper bound for channel 2."),
                new("min3", "Channel 3 Minimum", "number", .5, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive lower bound for channel 3 (V/b*/B)."),
                new("max3", "Channel 3 Maximum", "number", 1, -200, 200, .01, Group: "Color Range", VisibleWhen: new("mode", ["color"]), Description: "Inclusive upper bound for channel 3.")
            ,
                new("emptyPolicy", "Empty Result Policy", "select", "empty", Group: "Output", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "Behavior when no pixels pass the range.")],
            "Create a Region from an inclusive gray or three-channel color range. Connect exactly one of image or domainImage; optional region further restricts. Key params: channel bounds, empty policy. Too-wide range swallows background; too-narrow drops the target.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.connectedComponents", "Connected Regions", "Region", [Exec, Region], Outputs, [
                new("emptyPolicy", "Empty Result Policy", "select", "empty", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "When the result region is empty: emit empty keeps outputs with found=false; fail stops the graph.")],
            "Union input region objects and split with JLVision native connectivity. Object indices are not tracking IDs. Empty input returns zero objects.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.filter", "Filter Regions", "Region", [Exec, Region], Outputs,
            [new("feature", "Region Feature", "select", "area", Options: [new("Area", "area"), new("Circularity", "circularity"), new("Width", "width"), new("Height", "height"), new("Rectangularity", "rectangularity"), new("Anisometry", "anisometry")],
                Description: "Which per-object attribute to gate. Area is pixel count; anisometry is equivalent-ellipse axis ratio."),
             new("min", "Minimum Feature", "number", 0, 0, 99999999, 0.01, Description: "Inclusive lower bound for the selected feature."),
             new("max", "Maximum Feature", "number", 99999999, 0, 99999999, 0.01, Description: "Inclusive upper bound; must be >= min.")],
            "JLVision attribute selection on existing objects; does not split objects. Chain filters for multiple criteria. Area counts pixels; anisometry is the equivalent-ellipse axis ratio.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.boolean", "Region Boolean", "Region", [Exec, new("a", VisionDataType.Region), new("b", VisionDataType.Region)], Outputs,
            [new("operation", "Region Operation", "select", "union", Options: [new("Union", "union"), new("Intersection", "intersection"), new("A minus B", "difference"), new("Exclusive OR", "xor")],
                Description: "Set operation on the unions of A and B. Difference is A\\B. Both regions must share canvas size.")],
            "Set operations on the unions of A and B. Both must use the same image dimensions and coordinate system. Difference is A minus B; split the result explicitly if needed.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.fillHoles", "Fill Region Holes", "Region", [Exec, Region], Outputs, [new("emptyPolicy", "Empty Region Policy", "select", "empty", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "Behavior when the input region is empty.")],
            "Fill enclosed holes in each JLVision region object without bridging disconnected objects or altering input runs.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("image.reduceDomain", "Reduce Image Domain", "Region", [.. Images, Region],
            [new("domainImage", VisionDataType.DomainImage), Region, new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), Next], [new("emptyPolicy", "Empty Domain Policy", "select", "fail", Options: [new("Fail", "fail"), new("Passthrough", "passthrough")], Description: "Behavior when the region is empty."),
                ],
            "Preserve owned image pixels and attach an explicit region domain. Repeated reductions intersect domains. Emits found (area>0) and diagnostics; emptyPolicy=fail rejects an empty region while passthrough emits an empty domain. Connect domainImage to region.fromRange; regular Image ports deliberately reject it to prevent silent domain loss.", "builtin"),
        new("region.fromMask", "Region From Mask", "Region", [Exec, new("image", VisionDataType.Image)],
            [new("region", VisionDataType.Region), new("image", VisionDataType.Image), new("count", VisionDataType.Integer), new("area", VisionDataType.Integer), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), Next],
            [new("threshold", "Mask Threshold", "number", 1, 1, 255, 1, Group: "Mask", Description: "Pixels at or above this level belong to the region; 1 means any non-zero pixel, which is the usual case for segmentation masks.") { Integer = true },
             new("invert", "Invert Mask", "boolean", false, Group: "Mask", Description: "Swap foreground and background, for models or defects that encode the object as dark instead of bright.")],
            "Turn a mask image (for example the mask output of ONNX segmentation) directly into a Region instead of thresholding it again with region.fromRange. The mask is split into 8-connected components, so downstream region.properties / region.filter counts real blobs rather than one merged area; the binary mask is echoed on the image port and area reports the foreground pixel count. found is false when the mask is empty.", Capabilities: new VisionToolCapabilities(SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: [])
    ];
}
