namespace VisionStudio.Engine;

public static class FeatureRegionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] RegionOutputs = [Region, Image, new("found", VisionDataType.Boolean), new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("diagnostics", VisionDataType.String, Required: false), Next];
    private static readonly ParameterDescriptor[] Kernel = [new("kernelWidth", "Kernel Width", "number", 15, 1, 255, 2), new("kernelHeight", "Kernel Height", "number", 15, 1, 255, 2)];
    private static readonly ParameterDescriptor Union = new("unionFirst", "Union Objects First", "boolean", false);
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.topHat", "White Top Hat", "Preprocess", [Exec, Image], [Image, new("diagnostics", VisionDataType.String), Next], Kernel,
            "Extract small bright features with gray minus rectangular grayscale opening. Requires 8-bit input; color is converted to gray. Kernel dimensions must be odd in 1..255. Full-frame native border behavior; choose a kernel larger than the target feature.", "builtin"),
        new("image.bottomHat", "Black Bottom Hat", "Preprocess", [Exec, Image], [Image, new("diagnostics", VisionDataType.String), Next], Kernel,
            "Extract small dark features with rectangular grayscale closing minus gray. Requires 8-bit input; color is converted to gray. Kernel dimensions must be odd in 1..255. Full-frame native border behavior; choose a kernel larger than the target feature.", "builtin"),
        new("image.morphGradient", "Morphological Gradient", "Preprocess", [Exec, Image], [Image, new("diagnostics", VisionDataType.String), Next], Kernel,
            "Local grayscale range: rectangular dilation minus erosion. Returns owned 8-bit gray; color is converted to gray. Kernel dimensions must be odd in 1..255. Gradient bands are kernel-dependent and are not subpixel edge positions.", "builtin"),
        new("measure.grayHistogram", "Gray Histogram and Percentiles", "Measurement",
            [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false)],
            [new("found", VisionDataType.Boolean), new("pixelCount", VisionDataType.Integer), new("binsJson", VisionDataType.String), new("cdfJson", VisionDataType.String), new("diagnostics", VisionDataType.String),
             .. new[] { "mean", "stdDev", "minGray", "maxGray", "median", "entropy", "percentileLow", "percentileHigh" }.Select(key => new PortDescriptor(key, VisionDataType.Double, Required: false)), Next],
            [new("lowPercent", "Lower Percentile", "number", 5, 0, 100, .1), new("highPercent", "Upper Percentile", "number", 95, 0, 100, .1)],
            "Native 256-bin histogram over the union of selected pixels intersected with the image domain. Connect exactly one of image/domainImage; optional region restricts it further. 8-bit color converts to gray. Percentiles use the first occupied level whose cumulative count reaches p% (p=0 gives the minimum); entropy uses log2. Empty selection returns zero arrays and omits statistics.", "builtin"),
        new("region.fromRoi", "Rasterize ROI", "Region", [new("exec", VisionDataType.Control, Required: false), new("image", VisionDataType.Image, Required: false), new("roi", VisionDataType.Roi, Required: false)], RegionOutputs,
            [new("canvasWidth", "Region Canvas Width", "number", 512, 1, 16384, 1), new("canvasHeight", "Region Canvas Height", "number", 384, 1, 16384, 1)],
            "Rasterize a connected or configured rectangle/circle/polygon/brush ROI into one native pixel region, clipped to the canvas. Brush strokes support ordered add and erase operations. Optional image supplies canvas dimensions only. Rectangle includes pixel centres in [x,x+width) and [y,y+height); circle and polygon use native rasterization. Missing ROI is an error. Zero rectangle size or circle radius is empty. Limits: 64 million canvas pixels, 1000 polygon points, coordinates within +/-32768.", "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new("region.convexHull", "Region Convex Hull", "Region", [Exec, Region], RegionOutputs, [Union],
            "Native convex hull per region object. unionFirst=true computes a common hull, bridging gaps between objects. Fills concavities and holes; this is a pixel region, not a subpixel contour. Empty input remains empty.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.boundary", "Region Boundary", "Region", [Exec, Region], RegionOutputs,
            [Union, new("boundaryMode", "Boundary Side", "select", "inner", Options: [new("Inner", "inner"), new("Outer", "outer")])],
            "Native inner or outer pixel boundary, including hole edges. Operates per object unless unionFirst=true removes internal overlaps first. Outer boundaries are clipped to canvas bounds. Output objects retain grouping; use connectedComponents to split them.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("region.skeleton", "Region Skeleton", "Region", [Exec, Region], RegionOutputs, [Union],
            "Native binary skeleton per region object, or after union when unionFirst=true. Output is a pixel region mask; count is the number of nonempty region objects, not branches, endpoints or physical length. Small spurs and sampling effects remain visible.", "builtin", new VisionToolCapabilities(EmitsOverlay: true))
    ];
}
