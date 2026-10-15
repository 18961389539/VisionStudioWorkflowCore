namespace VisionStudio.Engine;

public static class RegionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] Outputs = [Region, new("image", VisionDataType.Image), new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), Next];
    private static readonly PortDescriptor[] Images = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false)];
    private static readonly PortDescriptor[] RangeImages = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false)];
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("region.fromRange", "Range Segmentation", "Region", RangeImages, Outputs,
            [
                new("mode", "Range Mode", "select", "gray", Options: [new("Gray", "gray"), new("Color", "color")]),
                new("minGray", "Minimum Gray", "number", 128, -65536, 65536, 1, VisibleWhen: new("mode", ["gray"])),
                new("maxGray", "Maximum Gray", "number", 255, -65536, 65536, 1, VisibleWhen: new("mode", ["gray"])),
                new("space", "Color Space", "select", "hsv", Options: [new("HSV (degrees, 0-1, 0-1)", "hsv"), new("sRGB (0-1)", "rgb"), new("CIELAB (D65)", "lab")], VisibleWhen: new("mode", ["color"])),
                new("min1", "Channel 1 Minimum", "number", 0, -360, 360, .01, VisibleWhen: new("mode", ["color"])),
                new("max1", "Channel 1 Maximum", "number", 360, -360, 360, .01, VisibleWhen: new("mode", ["color"])),
                new("min2", "Channel 2 Minimum", "number", 0, -200, 200, .01, VisibleWhen: new("mode", ["color"])),
                new("max2", "Channel 2 Maximum", "number", 1, -200, 200, .01, VisibleWhen: new("mode", ["color"])),
                new("min3", "Channel 3 Minimum", "number", .5, -200, 200, .01, VisibleWhen: new("mode", ["color"])),
                new("max3", "Channel 3 Maximum", "number", 1, -200, 200, .01, VisibleWhen: new("mode", ["color"]))
            ],
            "Create a Region from an inclusive grayscale or three-channel color range. Connect exactly one image or domainImage; an optional region further restricts the selection.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.fromThreshold", "Threshold Region", "Region", Images, Outputs,
            [new("minGray", "Minimum Gray", "number", 128, -65536, 65536, 1), new("maxGray", "Maximum Gray", "number", 255, -65536, 65536, 1)],
            "JLVision inclusive gray-range region. Connect exactly one image or domainImage. Color is converted to gray. Disconnected objects remain one region until connectedComponents.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.connectedComponents", "Connected Regions", "Region", [Exec, Region], Outputs, [],
            "Union input region objects and split with JLVision native connectivity. Object indices are not tracking IDs. Empty input returns zero objects.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.filter", "Filter Regions", "Region", [Exec, Region], Outputs,
            [new("feature", "Region Feature", "select", "area", Options: [new("Area", "area"), new("Circularity", "circularity"), new("Width", "width"), new("Height", "height"), new("Rectangularity", "rectangularity"), new("Anisometry", "anisometry")]),
             new("min", "Minimum Feature", "number", 0, 0, 99999999, 0.01), new("max", "Maximum Feature", "number", 99999999, 0, 99999999, 0.01)],
            "JLVision attribute selection on existing objects; does not split objects. Chain filters for multiple criteria. Area counts pixels; anisometry is the equivalent-ellipse axis ratio.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.boolean", "Region Boolean", "Region", [Exec, new("a", VisionDataType.Region), new("b", VisionDataType.Region)], Outputs,
            [new("operation", "Region Operation", "select", "union", Options: [new("Union", "union"), new("Intersection", "intersection"), new("A minus B", "difference"), new("Exclusive OR", "xor")])],
            "Set operations on the unions of A and B. Both must use the same image dimensions and coordinate system. Difference is A minus B; split the result explicitly if needed.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.fillHoles", "Fill Region Holes", "Region", [Exec, Region], Outputs, [],
            "Fill enclosed holes in each JLVision region object without bridging disconnected objects or altering input runs.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("image.reduceDomain", "Reduce Image Domain", "Region", [.. Images, Region],
            [new("domainImage", VisionDataType.DomainImage), Region, new("area", VisionDataType.Double), Next], [],
            "Preserve owned image pixels and attach an explicit region domain. Repeated reductions intersect domains. Connect domainImage to region.fromThreshold; regular Image ports deliberately reject it to prevent silent domain loss.", "builtin")
    ];
}
