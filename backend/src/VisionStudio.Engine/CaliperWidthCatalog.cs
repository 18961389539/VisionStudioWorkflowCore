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
                new("sampleCount", VisionDataType.Integer), new("searchLine", VisionDataType.Line2D), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("next", VisionDataType.Control)],
            [
                new("startX", "Start X", "number", 120, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start X in pixels."),
                new("startY", "Start Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start Y in pixels."),
                new("endX", "End X", "number", 520, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end X in pixels."),
                new("endY", "End Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end Y in pixels."),
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
                new("minWidth", "Minimum Pair Width", "number", 0, 0, 10000000, 0.1, Unit: "px", Group: "Validation", Description: "Minimum accepted width in pixels; 0 disables."),
                new("maxWidth", "Maximum Pair Width (0=unlimited)", "number", 0, 0, 10000000, 0.1, Unit: "px", Group: "Validation", Description: "Maximum accepted width in pixels; 0 disables."),
                new("nominalWidth", "Nominal Width (closest selection)", "number", 20, 0, 10000000, 0.1, Unit: "px", Group: "Validation",
                    Description: "Only used when Pair Selection is Closest to nominal width; must be greater than zero in that mode.")
            ],
            "Native subpixel edge-pair measurement of bright strips, dark gaps and line widths. Returns width along the search line, ordered endpoints and center; supports width filtering and nominal-width selection. Place the line perpendicular to the target boundaries for perpendicular width. Uses Gaussian sigma=1 and at least 8 contiguous profile samples. ROI holes split profiles and cannot be bridged. No qualifying pair returns found=false with no width or points. Supports rigid px-to-px localization transforms; outputs remain in image pixels.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),

        // Product-depth PD-1: Cognex-style multi-station width array (卡尺阵列).
        // Honest limits: linear station layout only; no circular array; no interactive score-curve UI;
        // pair selection matches measure.caliperWidth (not PatMax / full Caliper Tool ranking).
        new("measure.caliperArray", "Caliper Array", "Measurement",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image),
                new("searchLine", VisionDataType.Line2D, Required: false), new("transform", VisionDataType.Transform2D, Required: false),
                new("roi", VisionDataType.Roi, Required: false)],
            [new("image", VisionDataType.Image), new("found", VisionDataType.Boolean),
                new("widthMean", VisionDataType.Double, Required: false), new("widthMin", VisionDataType.Double, Required: false),
                new("widthMax", VisionDataType.Double, Required: false), new("widthStd", VisionDataType.Double, Required: false),
                new("widthCv", VisionDataType.Double, Required: false), new("center", VisionDataType.Point2D, Required: false),
                new("strength", VisionDataType.Double, Required: false), new("caliperCount", VisionDataType.Integer),
                new("validCount", VisionDataType.Integer), new("calipersJson", VisionDataType.String),
                new("searchLine", VisionDataType.Line2D), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("next", VisionDataType.Control)],
            [
                new("startX", "Start X", "number", 120, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start X in pixels."),
                new("startY", "Start Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line start Y in pixels."),
                new("endX", "End X", "number", 520, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end X in pixels."),
                new("endY", "End Y", "number", 240, -10000000, 10000000, 1, Unit: "px", Group: "Search Line", Description: "Search-line end Y in pixels."),
                new("edgeLength", "Array Span Along Edge (0=auto)", "number", 0, 0, 10000, 1, Unit: "px", Group: "Array",
                    Description: "Distance along the edge (perpendicular to the search line) over which stations are distributed. 0 uses the search-line length."),
                new("caliperCount", "Caliper Count", "number", 9, 1, 101, 1, Group: "Array",
                    Description: "Number of width stations along the array. Use 1 for a single caliperWidth-equivalent measurement."),
                new("minValidCalipers", "Minimum Valid Calipers", "number", 1, 1, 101, 1, Group: "Array",
                    Description: "found=true only when at least this many stations return a qualifying pair (and maxWidthStd gate passes)."),
                new("sampleWidth", "Projection Length", "number", 5, 1, 99, 2, Unit: "px", Group: "Sampling",
                    Description: "Odd projection width; even values round up. Average across the strip perpendicular to each station search line."),
                new("edgeThreshold", "Edge Threshold", "number", 12, 0, 255, 1, Unit: "gray", Group: "Sampling", Description: "Minimum edge strength to accept a caliper hit."),
                new("polarity", "Pair Polarity", "select", "bright", Options:
                    [new("Bright strip", "bright"), new("Dark gap", "dark"), new("Any", "any")], Group: "Sampling", Description: "Edge polarity (dark-to-light, light-to-dark, or either)."),
                new("direction", "Pair Selection", "select", "strongest", Options:
                    [new("Strongest", "strongest"), new("First from start", "firstfromstart"), new("First from end", "firstfromend"),
                        new("Closest to nominal width", "closestwidth")], Group: "Sampling", Description: "Scan direction along the search geometry."),
                new("minWidth", "Minimum Pair Width", "number", 0, 0, 10000000, 0.1, Unit: "px", Group: "Validation", Description: "Minimum accepted width in pixels; 0 disables."),
                new("maxWidth", "Maximum Pair Width (0=unlimited)", "number", 0, 0, 10000000, 0.1, Unit: "px", Group: "Validation", Description: "Maximum accepted width in pixels; 0 disables."),
                new("nominalWidth", "Nominal Width (report only)", "number", 20, 0, 10000000, 0.1, Unit: "px", Group: "Validation", Description: "Used only to report width deviation; it does not gate found and has no tolerance parameter."),
                new("maxWidthStd", "Maximum Width Std (0=off)", "number", 0, 0, 10000000, 0.01, Unit: "px", Group: "Validation",
                    Description: "Reject the array (found=false) when the population std of valid widths exceeds this value. 0 disables the gate."),
                new("includeProfiles", "Include Profile Samples", "boolean", false, Group: "Diagnostics",
                    Description: "When true, calipersJson embeds a downsampled grayscale profile per station for edge-curve diagnostics."),
                new("profileSampleCap", "Profile Sample Cap", "number", 64, 8, 256, 1, Group: "Diagnostics", VisibleWhen: new("includeProfiles", ["true"]),
                    Description: "Maximum samples stored per station when includeProfiles is enabled.")
            ],
            "Multi-station edge-pair width array (Cognex Caliper Tool / HALCON measure_pairs style). Places caliperCount search profiles parallel to the authored search line, spaced along the edge-normal direction over edgeLength (or the search-line length when 0). Each station reuses native JLVision pair measurement with the same polarity, threshold, width filter and pair-selection rules as measure.caliperWidth. Outputs aggregate width mean/min/max/std/cv, validCount, mean center/strength, and calipersJson with per-station status, geometry and optional profile samples. found requires minValidCalipers and optional maxWidthStd. Linear layout only—circular arrays use measure.caliperCircle. No interactive score-curve editor. Supports ROI masks and rigid px-to-px transforms; results stay in image pixels.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true))
    ];
}