namespace VisionStudio.Engine;

internal static class CaliperWidthCatalog
{
    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("measure.caliperWidth", "Caliper Width", "Measurement",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image),
                new("searchLine", VisionDataType.Line2D, Required: false), new("transform", VisionDataType.Transform2D, Required: false),
                new("roi", VisionDataType.Roi, Required: false)],
            [new("image", VisionDataType.Image), new("found", VisionDataType.Boolean),
                new("width", VisionDataType.Double, Required: false), new("point1", VisionDataType.Point2D, Required: false),
                new("point2", VisionDataType.Point2D, Required: false), new("center", VisionDataType.Point2D, Required: false),
                new("strength", VisionDataType.Double, Required: false), new("pairCount", VisionDataType.Integer),
                new("sampleCount", VisionDataType.Integer), new("searchLine", VisionDataType.Line2D), new("next", VisionDataType.Control)],
            [
                new("startX", "Start X", "number", 120, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("startY", "Start Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("endX", "End X", "number", 520, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("endY", "End Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line"),
                new("sampleWidth", "Projection Length", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling",
                    Description: "Odd projection width; even values round up. Average across the strip perpendicular to the search line."),
                new("edgeThreshold", "Edge Threshold", "number", 12, 0, 255, 1, Unit: "gray", Group: "Sampling",
                    Description: "Minimum amplitude of both edges after 8-bit grayscale conversion. UInt16 uses full-range scaling."),
                new("polarity", "Pair Polarity", "select", "bright", Options:
                    [new("Bright strip", "bright"), new("Dark gap", "dark"), new("Any", "any")], Group: "Sampling",
                    Description: "Bright: rising then falling; dark: falling then rising, along start to end. Any includes both."),
                new("direction", "Pair Selection", "select", "strongest", Options:
                    [new("Strongest", "strongest"), new("First from start", "firstfromstart"), new("First from end", "firstfromend"),
                        new("Closest to nominal width", "closestwidth")], Group: "Sampling",
                    Description: "Select after width filtering. Strength is the weaker edge amplitude; closest uses nominalWidth."),
                new("minWidth", "Minimum Pair Width", "number", 0, 0, 10000000, 0.1, Unit: "px", Group: "Validation"),
                new("maxWidth", "Maximum Pair Width (0=unlimited)", "number", 0, 0, 10000000, 0.1, Unit: "px", Group: "Validation"),
                new("nominalWidth", "Nominal Pair Width", "number", 20, 0, 10000000, 0.1, Unit: "px", Group: "Validation",
                    Description: "Must be greater than zero when selecting the closest width.")
            ],
            "Native subpixel edge-pair measurement of bright strips, dark gaps and line widths. Returns width along the search line, ordered endpoints and center; supports width filtering and nominal-width selection. Place the line perpendicular to the target boundaries for perpendicular width. Uses Gaussian sigma=1 and at least 8 contiguous profile samples. ROI holes split profiles and cannot be bridged. No qualifying pair returns found=false with no width or points. Supports rigid px-to-px localization transforms; outputs remain in image pixels.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true))
    ];
}
