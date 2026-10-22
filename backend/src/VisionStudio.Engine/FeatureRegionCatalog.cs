namespace VisionStudio.Engine;

public static class FeatureRegionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] RegionOutputs = [Region, Image, new("found", VisionDataType.Boolean), new("count", VisionDataType.Integer), new("area", VisionDataType.Double),
        new("length", VisionDataType.Double, Required: false), new("endpoints", VisionDataType.Integer, Required: false),
        new("junctions", VisionDataType.Integer, Required: false), new("vertexCount", VisionDataType.Integer, Required: false),
        new("diagnostics", VisionDataType.String, Required: false), Next];
    private static readonly PortDescriptor[] HatOutputs = [Image,
        new("meanIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double),
        new("minIntensity", VisionDataType.Double), new("stdIntensity", VisionDataType.Double),
        new("nonZeroCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String),
        new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), Next];
    private static readonly ParameterDescriptor[] Kernel =
    [
        new("kernelWidth", "Kernel Width", "number", 15, 1, 255, 2, Description: "Odd rectangular structuring-element width in 1..255. Must not exceed twice the image width. Larger than the target bright/dark feature."),
        new("kernelHeight", "Kernel Height", "number", 15, 1, 255, 2, Description: "Odd rectangular structuring-element height in 1..255. Must not exceed twice the image height."),
        new("emptyPolicy", "Empty Image Policy", "select", "fail", Options: [new("Fail", "fail"), new("Passthrough", "passthrough")], Description: "Behavior when the input image is empty: fail stops the graph; passthrough emits an empty owned gray image with zero statistics.")
    ];
    private static readonly ParameterDescriptor Union = new("unionFirst", "Union Objects First", "boolean", false,
        Description: "When true, merge all region objects before the shape transform so hull/boundary/skeleton sees one connected mask; when false, process each object independently.");
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.topHat", "White Top Hat", "Preprocess", [Exec, Image], [.. HatOutputs], Kernel,
            "Extract small bright features with gray minus rectangular grayscale opening. Requires 8-bit input; color is converted to gray. Kernel dimensions must be odd in 1..255. Full-frame native border behavior; choose a kernel larger than the target feature.", "builtin"),
        new("image.bottomHat", "Black Bottom Hat", "Preprocess", [Exec, Image], [.. HatOutputs], Kernel,
            "Extract small dark features with rectangular grayscale closing minus gray. Requires 8-bit input; color is converted to gray. Kernel dimensions must be odd in 1..255. Full-frame native border behavior; choose a kernel larger than the target feature.", "builtin"),
        new("image.morphGradient", "Morphological Gradient", "Preprocess", [Exec, Image], [.. HatOutputs], Kernel,
            "Local grayscale range: rectangular dilation minus erosion. Returns owned 8-bit gray; color is converted to gray. Kernel dimensions must be odd in 1..255. Gradient bands are kernel-dependent and are not subpixel edge positions.", "builtin"),
        new("measure.grayHistogram", "Gray Histogram and Percentiles", "Measurement",
            [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false)],
            [new("found", VisionDataType.Boolean), new("pixelCount", VisionDataType.Integer), new("binsJson", VisionDataType.String), new("cdfJson", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false),
             .. new[] { "mean", "stdDev", "minGray", "maxGray", "median", "entropy", "percentileLow", "percentileHigh" }.Select(key => new PortDescriptor(key, VisionDataType.Double, Required: false)), Next],
            [new("lowPercent", "Lower Percentile", "number", 5, 0, 100, .1, Description: "Lower percentile (0..100) reported as percentileLow; must be <= highPercent."),
             new("highPercent", "Upper Percentile", "number", 95, 0, 100, .1, Description: "Upper percentile (0..100) reported as percentileHigh.")],
            "Native 256-bin histogram over selected pixels ∩ image domain. Connect exactly one of image or domainImage; optional region restricts further. 8-bit color converts to gray. An empty domain emits found=false, pixelCount=0 and zero bins/CDF; scalar statistics are omitted.", "builtin"),
        new("region.fromRoi", "Rasterize ROI", "Region", [new("exec", VisionDataType.Control, Required: false), new("image", VisionDataType.Image, Required: false), new("roi", VisionDataType.Roi, Required: false)], RegionOutputs,
            [new("canvasWidth", "Region Canvas Width", "number", 512, 1, 16384, 1, Description: "Canvas width when rasterizing a ROI to a region."), new("canvasHeight", "Region Canvas Height", "number", 384, 1, 16384, 1, Description: "Canvas height when rasterizing a ROI to a region."),

                new("invert", "Invert Region", "boolean", false, Description: "Invert the rasterized ROI region against the canvas."),
                ],
            "Rasterize a connected or configured rectangle/circle/polygon/brush ROI into one native pixel region, clipped to the canvas. Brush strokes support ordered add and erase operations. Optional image supplies canvas dimensions only. Rectangle includes pixel centres in [x,x+width) and [y,y+height); circle and polygon use native rasterization. Missing ROI is an error. Zero rectangle size or circle radius is empty. Limits: 64 million canvas pixels, 1000 polygon points, coordinates within +/-32768.", "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new("region.convexHull", "Region Convex Hull", "Region", [Exec, Region], RegionOutputs, [Union],
            "Native convex hull per region object. unionFirst=true computes a common hull, bridging gaps between objects. Fills concavities and holes; this is a pixel region, not a subpixel contour. Emits length (outer contour perimeter) and vertexCount from external contours. Empty input remains empty.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.boundary", "Region Boundary", "Region", [Exec, Region], RegionOutputs,
            [Union, new("boundaryMode", "Boundary Side", "select", "inner", Options: [new("Inner", "inner"), new("Outer", "outer")],
                Description: "inner: pixels belonging to the region that touch background (or holes). outer: background pixels that touch the region, clipped to the canvas.")],
            "Native inner or outer pixel boundary, including hole edges. Operates per object unless unionFirst=true removes internal overlaps first. Outer boundaries are clipped to canvas bounds. Emits length as boundary pixel count. Output objects retain grouping; use connectedComponents to split them.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.skeleton", "Region Skeleton", "Region", [Exec, Region], RegionOutputs, [Union],
            "Native binary skeleton per region object, or after union when unionFirst=true. Output is a pixel region mask. Emits length (skeleton pixel count), endpoints (degree-1) and junctions (degree>=3). count is nonempty region objects. Small spurs remain visible.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
