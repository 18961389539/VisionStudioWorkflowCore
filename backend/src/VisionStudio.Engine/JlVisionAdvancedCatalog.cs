namespace VisionStudio.Engine;

internal static class JlVisionAdvancedCatalog
{
    private static PortDescriptor In(string name, VisionDataType type, bool required = false) => new(name, type, Required: required);
    private static PortDescriptor Out(string name, VisionDataType type, bool required = true) => new(name, type, Required: required);
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static ParameterDescriptor Op(string value, params (string Value, string Label)[] options) =>
        new("operator", "Operation", "select", value, Group: "Operation", Options: options.Select(o => new ParameterOption(o.Label, o.Value)).ToArray(), Description: "Selects the algorithm; the form only shows the settings used by this operation. Missing required inputs fail with an explicit error.");
    private static ParameterDescriptor N(string name, string label, double value, double min, double max, string description, params string[] operations) =>
        new(name, label, "number", value, min, max, 0.1, Group: "Parameters", Description: description, VisibleWhen: operations.Length == 0 ? null : new("operator", operations));
    private static ParameterDescriptor I(string name, string label, int value, int min, int max, string description, params string[] operations) =>
        N(name, label, value, min, max, description, operations) with { Step = 1 };
    private static ParameterDescriptor T(string name, string label, string value, string description, params string[] operations) =>
        new(name, label, "text", value, Group: "Parameters", Description: description, VisibleWhen: operations.Length == 0 ? null : new("operator", operations));
    private static ParameterDescriptor B(string name, string label, bool value, string description, params string[] operations) =>
        new(name, label, "boolean", value, Group: "Parameters", Description: description, VisibleWhen: operations.Length == 0 ? null : new("operator", operations));
    private static ParameterDescriptor S(string name, string label, string value, string description, string[] operations, params (string Value, string Label)[] options) =>
        new(name, label, "select", value, Group: "Parameters", Options: options.Select(o => new ParameterOption(o.Label, o.Value)).ToArray(), Description: description,
            VisibleWhen: operations.Length == 0 ? null : new("operator", operations));

    private static IReadOnlyList<NodeCatalogItem> WithRequirements(NodeCatalogItem[] items) => items.Select(item => item with
    {
        Inputs = item.Inputs.Select(port =>
        {
            string[] operations = (item.Type, port.Name) switch
            {
                ("jlvision.segmentation", "markers") => ["watershedsMarker"],
                ("jlvision.regionAnalysis", "region2") => ["spatialRelation", "selectRegionSpatial", "findNeighbors"],
                ("geometry.intersections", "line") => ["lineCircle", "segments"],
                ("geometry.intersections", "line2") => ["segments"],
                ("geometry.intersections", "circle") => ["lineCircle", "circles"],
                ("geometry.intersections", "circle2") => ["circles"],
                ("jlvision.dxf", "contours") => ["write"],
                ("jlvision.worldPlane", "image") => ["image", "map", "applyMap"],
                ("jlvision.worldPlane", "contours") => ["contour"],
                ("jlvision.worldPlane", "mapRows" or "mapColumns" or "mapInfo") => ["applyMap"],
                ("jlvision.restoration", "image") => ["harmonic", "inpaintingCt", "inpaintingMcf", "inpaintingCed", "inpaintingAniso", "wiener", "wienerNi"],
                ("jlvision.restoration", "region") => ["harmonic", "inpaintingCt", "inpaintingMcf", "inpaintingCed", "inpaintingAniso", "wienerNi"],
                ("jlvision.restoration", "psf") => ["wiener", "wienerNi"],
                ("jlvision.restoration", "filteredImage") => ["wiener"],
                ("jlvision.surface", "gradientColumns") => ["reconstructGradient"],
                _ => []
            };
            return operations.Length > 0 ? port with { RequiredWhen = new("operator", operations) } : port;
        }).ToArray()
    }).ToArray();

    internal static readonly IReadOnlyList<NodeCatalogItem> Items = WithRequirements(
    [
        new("jlvision.featureExtract", "Subpixel Feature Extraction", "Contour", [Exec, In("image", VisionDataType.Image, true)],
            [Out("contours", VisionDataType.Contour), Out("detections", VisionDataType.DetectionList), Out("points", VisionDataType.String), Out("point", VisionDataType.Point2D, false), Out("details", VisionDataType.String), Out("count", VisionDataType.Integer), Out("found", VisionDataType.Boolean), Diagnostics, Next],
            [Op("linesGauss", ("linesGauss", "Gaussian Lines"), ("linesFacet", "Surface Lines"), ("linesColor", "Color Lines"), ("edgesSubPix", "Subpixel Edges"), ("edgesColorSubPix", "Color Subpixel Edges"), ("pointsHarris", "Harris Corners"), ("pointsFoerstner", "Förstner Corners"), ("detectEdgeSegments", "Edge Segments")),
                N("sigma", "Smoothing Scale", 1, .1, 100, "Gaussian scale; used as the gradient scale for Harris/Förstner.", "linesGauss", "linesColor", "edgesSubPix", "edgesColorSubPix", "pointsHarris", "pointsFoerstner"),
                N("low", "Low Threshold", 5, 0, 1e9, "Low response threshold for lines or edges; the minimum magnitude for line-segment detection must be an integer and is never truncated.", "linesGauss", "linesFacet", "linesColor", "edgesSubPix", "edgesColorSubPix", "detectEdgeSegments")
                    with { VariantParameter = "operator", Variants = new Dictionary<string, ParameterPresentation> { ["detectEdgeSegments"] = new(Label: "最小幅值", Step: 1) } },
                N("high", "High Threshold", 10, 0, 1e9, "Must not be smaller than the low threshold.", "linesGauss", "linesFacet", "linesColor", "edgesSubPix", "edgesColorSubPix"),
                I("maskSize", "Window Size", 5, 3, 39, "The filter window must be odd; even values fail with an explicit error.", "linesFacet") with { Step = 2 },
                I("sobelSize", "Gradient Window", 3, 3, 31, "The gradient window must be odd; even values fail with an explicit error.", "detectEdgeSegments") with { Step = 2 },
                S("polarity", "Line Polarity", "light", "Extract bright or dark lines.", ["linesGauss", "linesFacet"], ("light", "Bright Lines"), ("dark", "Dark Lines")),
                B("extractWidth", "Compute Width", true, "Lets the native line operator compute the width on both sides.", "linesGauss", "linesColor"),
                B("completeJunctions", "Complete Junctions", true, "Completes line junctions.", "linesGauss", "linesColor"),
                S("lineModel", "Line Model", "bar-shaped", "Cross-section model for Gaussian lines.", ["linesGauss"], ("bar-shaped", "Bar"), ("parabolic", "Parabolic"), ("gaussian", "Gaussian"), ("none", "No Correction")),
                T("filter", "Edge Filter", "canny", "Name of the native subpixel edge filter, for example canny.", "edgesSubPix", "edgesColorSubPix"),
                N("sigmaSmooth", "Integration Scale", 2, .1, 100, "Integration smoothing scale for the gradient response.", "pointsHarris", "pointsFoerstner"),
                N("sigmaPoints", "Corner Localization Scale", 2, .1, 100, "Förstner feature-point localization scale.", "pointsFoerstner"),
                N("threshold", "Corner Threshold", 100, 0, 1e9, "Minimum corner response or inhomogeneity threshold.", "pointsHarris", "pointsFoerstner"),
                N("alpha", "Harris Coefficient", .04, .001, .25, "Harris response coefficient.", "pointsHarris"),
                N("shapeThreshold", "Shape Threshold", .3, 0, 1, "Förstner feature shape-selection threshold.", "pointsFoerstner"),
                I("maxDistance", "Max Link Distance", 2, 0, 1000, "Maximum link distance for edge segments, in pixels.", "detectEdgeSegments"),
                I("minLength", "Min Segment Length", 10, 1, 100000, "Shortest output segment length, in pixels.", "detectEdgeSegments"),
                I("selectedIndex", "Output Corner Index", 0, 0, 100000, "Zero-based corner index exposed on the point port; out-of-range values leave the port unset.", "pointsHarris", "pointsFoerstner")],
            "Extracts native subpixel edges, lines and corners; contours keep floating-point coordinates. Line/edge operators output contours; corner operators output a points JSON array and the selected point, and detections can feed collection or loop nodes. Förstner also outputs covariance details. Gray operators convert to gray automatically; color operators require three channels.", OutputFrames: [new(NodeOutputFrameSource.ImageInput, InputPort: "image")]),

        new("jlvision.segmentation", "Advanced Region Segmentation", "Region", [Exec, In("image", VisionDataType.Image, true), In("markers", VisionDataType.Region)],
            [Out("region", VisionDataType.Region), Out("secondaryRegion", VisionDataType.Region, false), Out("darkRegion", VisionDataType.Region, false), Out("lightRegion", VisionDataType.Region, false),
                Out("primaryCount", VisionDataType.Integer), Out("secondaryCount", VisionDataType.Integer), Out("darkCount", VisionDataType.Integer, false), Out("lightCount", VisionDataType.Integer, false), Out("count", VisionDataType.Integer), Out("found", VisionDataType.Boolean), Diagnostics, Next],
            [Op("watersheds", ("watersheds", "Watershed"), ("watershedsMarker", "Marker Watershed"), ("watershedsThreshold", "Threshold Watershed"), ("mser", "Maximally Stable Extremal Regions")),
                I("threshold", "Watershed Threshold", 10, 0, 65535, "Gray threshold for merging shallow basins.", "watershedsThreshold"),
                I("minArea", "Min Area", 10, 1, 100000000, "Minimum MSER region area, in pixels.", "mser"),
                I("maxArea", "Max Area", 10000, 1, 100000000, "Maximum MSER region area; must be >= the minimum area.", "mser"),
                I("delta", "Gray Step", 5, 1, 255, "Gray-level step for MSER stability.", "mser"),
                S("polarity", "Region Polarity", "both", "MSER detection polarity. Dark regions output region; bright regions output secondaryRegion.", ["mser"], ("both", "Both Polarities"), ("dark", "Dark Regions"), ("light", "Bright Regions"))],
            "Watershed outputs basin regions and secondaryRegion holds the watershed boundary; count/found only count basin regions. For MSER, darkRegion/lightRegion hold dark/bright regions and remain available as region/secondaryRegion; darkCount/lightCount count each group and count/found count both. primaryCount/secondaryCount always refer to the two original ports."),

        new("jlvision.regionAnalysis", "Region Relations and Distance Analysis", "Region", [Exec, In("region", VisionDataType.Region, true), In("region2", VisionDataType.Region)],
            [Out("image", VisionDataType.Image, false), Out("closestRows", VisionDataType.Image, false), Out("closestColumns", VisionDataType.Image, false), Out("contours", VisionDataType.Contour), Out("relations", VisionDataType.String), Out("count", VisionDataType.Integer), Out("found", VisionDataType.Boolean), Diagnostics, Next],
            [Op("distanceTransform", ("distanceTransform", "Distance Transform"), ("closestPointTransform", "Nearest-Point Transform"), ("spatialRelation", "Spatial Relations"), ("selectRegionSpatial", "Directional Relations"), ("findNeighbors", "Find Neighbors"), ("splitSkeletonLines", "Split Skeleton into Segments")),
                S("metric", "Distance Metric", "euclidean", "Pixel distance used by the distance map.", ["distanceTransform", "closestPointTransform"], ("euclidean", "Euclidean"), ("city-block", "Manhattan"), ("chessboard", "Chessboard")),
                S("foreground", "Distance Domain", "true", "true measures distance to background inside the foreground; false measures distance to foreground inside the background.", ["distanceTransform", "closestPointTransform"], ("true", "Foreground"), ("false", "Background")),
                I("maxDistance", "Max Distance", 2, 0, 100000, "Neighbor threshold or approximate skeleton-segment distance, in pixels.", "findNeighbors", "splitSkeletonLines"),
                I("percent", "Min Overlap Ratio", 50, 0, 100, "Overlap ratio used for spatial relation decisions.", "spatialRelation"),
                S("direction", "Relative Direction", "left", "Direction of region2 relative to region.", ["selectRegionSpatial"], ("left", "Left"), ("right", "Right"), ("above", "Above"), ("below", "Below"))],
            "Relation operators require a same-size region2; relations JSON uses zero-based region object indices. Nearest-point transform also outputs distance and nearest row/column maps. Skeleton splitting requires a region already thinned to a skeleton and outputs open segment contours."),

        new("measure.fitRectangle", "Rectangle Contour Fit", "Measurement", [Exec, In("contours", VisionDataType.Contour, true)],
            [Out("rectangle", VisionDataType.Rectangle2D, false), Out("center", VisionDataType.Point2D, false), Out("pose", VisionDataType.Pose2D, false), Out("rectangles", VisionDataType.String), Out("count", VisionDataType.Integer), Out("found", VisionDataType.Boolean), Diagnostics, Next],
            [S("algorithm", "Fit Method", "tukey", "Fits a rectangle to the contour edges.", [], ("tukey", "Tukey Robust Fit"), ("huber", "Huber Robust Fit"), ("regression", "Regression Fit")) with { Group = "Fit" },
                I("maxPoints", "Max Sample Points", -1, -1, 1000000, "-1 uses every contour point; other values must satisfy the native algorithm's minimum sample requirement.") with { Group = "Sampling" },
                N("maxClosureDistance", "Closure Tolerance", 0, 0, 100000, "Maximum allowed distance between the first and last contour points, in pixels.") with { Group = "Sampling" },
                I("clipEndPoints", "Clipped End Points", 0, 0, 100000, "Number of points excluded from each end before fitting.") with { Group = "Sampling" }, I("iterations", "Iterations", 5, 1, 1000, "Number of robust-fit iterations.") with { Group = "Fit" },
                N("clippingFactor", "Outlier Clipping Factor", 2, .01, 100, "Outlier clipping factor for the robust fit.") with { Group = "Fit" }, I("index", "Result Index", 0, 0, 9999, "Zero-based index selecting the primary rectangle; rectangles always contains every result.") with { Group = "Output" }],
            "Fits each input contour with FitRectangle2ContourXld. Unlike the minimum-area rectangle, this fits edge geometry rather than bounding extent; the output position is the center, sizes are full width/height, and the angle follows the image coordinate system. Empty input returns found=false without error.", OutputFrames: [new(NodeOutputFrameSource.ContourInput, InputPort: "contours")]),

        new("geometry.intersections", "Geometry Intersections", "Geometry", [Exec, In("line", VisionDataType.Line2D), In("line2", VisionDataType.Line2D), In("circle", VisionDataType.Circle), In("circle2", VisionDataType.Circle)],
            [Out("point", VisionDataType.Point2D, false), Out("points", VisionDataType.String), Out("count", VisionDataType.Integer), Out("found", VisionDataType.Boolean), Out("overlapping", VisionDataType.Boolean), Diagnostics, Next],
            [Op("lineCircle", ("lineCircle", "Line and Circle"), ("circles", "Circle and Circle"), ("segments", "Segment and Segment")), I("index", "Intersection Index", 0, 0, 100000, "Zero-based index of the intersection exposed on the point port.")],
            "Line-circle uses line/circle (the line extends infinitely); circle-circle uses circle/circle2; segment intersection uses line/line2 (limited to the endpoints). points is a JSON list of every intersection; found=false when there are none. Use overlapping to detect coincident geometry.", OutputFrames: [new(NodeOutputFrameSource.GeometryInput, InputPort: "circle", WhenParameter: "operator", WhenValues: ["circles"]), new(NodeOutputFrameSource.GeometryInput, InputPort: "line")]),

        new("jlvision.dxf", "DXF Contour I/O", "Contour", [Exec, In("contours", VisionDataType.Contour)],
            [Out("contours", VisionDataType.Contour), Out("count", VisionDataType.Integer), Out("found", VisionDataType.Boolean), Out("path", VisionDataType.String), Out("status", VisionDataType.String), Out("unit", VisionDataType.String), Diagnostics, Next],
            [Op("read", ("read", "Import Contours"), ("write", "Export Contours")), T("path", "DXF File Path", "", "Must be a .dxf file; imports are limited to 100 MB. File coordinates are preserved as-is, so confirm the CAD and image units and orientation first."),
                S("unit", "Import Coordinate Unit", "unspecified", "Unit of the scaled coordinates; unspecified is reported as unknown, never assumed to be pixels.", ["read"], ("unspecified", "Unspecified (Confirm)"), ("px", "Pixel"), ("mm", "Millimeter"), ("cm", "Centimeter"), ("m", "Meter"), ("um", "Micrometer")),
                N("scale", "Coordinate Scale", 1, 1e-9, 1e9, "Coordinates are scaled by this factor, then Y is flipped and offsets are added. For example, meters to millimeters uses 1000.", "read"),
                B("flipY", "Flip Y Axis", false, "Enable when CAD Y points up and the target frame points down.", "read"),
                N("offsetX", "X Offset", 0, -1e9, 1e9, "Added after scaling and flipping, in the import coordinate unit.", "read"), N("offsetY", "Y Offset", 0, -1e9, 1e9, "Added after scaling and flipping, in the import coordinate unit.", "read"),
                B("overwrite", "Overwrite Existing File", false, "When disabled, existing files are rejected; export writes a temporary file and replaces atomically.", "write")],
            "Imports or exports floating-point contours with the native DXF operators. Import explicitly sets the coordinate unit, scale, Y direction and origin offset; an unspecified unit stays unknown. Export preserves coordinate values verbatim and requires matching contour units and frames; no CAD unit header is written. status keeps the native read report."),

        new("jlvision.worldPlane", "Calibrated Plane Mapping", "Calibration", [Exec, In("image", VisionDataType.Image), In("contours", VisionDataType.Contour), In("calibration", VisionDataType.String), In("worldPose", VisionDataType.String), In("mapRows", VisionDataType.Image), In("mapColumns", VisionDataType.Image), In("mapInfo", VisionDataType.String)],
            [Out("image", VisionDataType.Image, false), Out("mapRows", VisionDataType.Image, false), Out("mapColumns", VisionDataType.Image, false), Out("mapInfo", VisionDataType.String, false), Out("contours", VisionDataType.Contour, false), Out("unit", VisionDataType.String, false), Diagnostics, Next],
            [Op("image", ("image", "Image to Plane"), ("map", "Generate Map"), ("applyMap", "Apply Map"), ("contour", "Contour to Plane")),
                T("cameraParameters", "Camera Calibration", "", "The calibration input takes precedence: an intrinsics calibration model JSON, or a native parameter JSON array that includes the camera type.", "image", "map", "contour") with { Type = "textarea" },
                T("worldPoseJson", "Plane Pose", "", "The worldPose input takes precedence; a 7-element JLVision pose JSON array with translation in meters, angles in degrees and the pose type last.", "image", "map", "contour") with { Type = "textarea" },
                S("unit", "Plane Unit", "mm", "Output unit for contour coordinates; also interprets the per-pixel size when mapping an image.", ["image", "map", "contour"], ("m", "Meter"), ("cm", "Centimeter"), ("mm", "Millimeter"), ("um", "Micrometer")),
                N("pixelSize", "Plane Size per Pixel", .1, 1e-9, 1e6, "Plane length represented by each output pixel, expressed in the selected plane unit.", "image", "map"),
                I("width", "Output Width", 512, 1, 16384, "Width of the mapped image; width x height must not exceed 100 MP.", "image", "map"), I("height", "Output Height", 512, 1, 16384, "Height of the mapped image.", "image", "map")],
            "Projects an image or contour onto a calibrated world plane. applyMap must connect mapRows/mapColumns/mapInfo from the same generator node and use the same image source frame. At runtime the source size, map size/type and content fingerprint are verified; mixed or modified maps are rejected. Contour output uses the selected physical unit; the image remains plane-sampled pixels.", Frames: [new(NodeImageFramePolicy.NewFixedFrame, FrameSuffix: "worldPlane", FrameLabel: "calibrated plane")]),

        new("jlvision.restoration", "Image Restoration", "Preprocess", [Exec, In("image", VisionDataType.Image), In("region", VisionDataType.Region), In("psf", VisionDataType.Image), In("filteredImage", VisionDataType.Image)],
            [Out("image", VisionDataType.Image), Out("width", VisionDataType.Integer), Out("height", VisionDataType.Integer), Diagnostics, Next],
            [Op("harmonic", ("harmonic", "Harmonic Inpainting"), ("inpaintingCt", "Coherence Transport Inpainting"), ("inpaintingMcf", "Mean Curvature Inpainting"), ("inpaintingCed", "Coherence Diffusion Inpainting"), ("inpaintingAniso", "Anisotropic Inpainting"), ("wiener", "Wiener Restoration"), ("wienerNi", "Noise-Estimated Wiener Restoration"), ("psfMotion", "Motion Blur Kernel"), ("psfDefocus", "Defocus Blur Kernel")),
                N("precision", "Convergence Precision", .01, 1e-9, 100, "Stopping precision for harmonic inpainting.", "harmonic"),
                N("sigma", "Smoothing Scale", 1, 0, 100, "Smoothing scale for the diffusion or transport process.", "inpaintingCt", "inpaintingMcf", "inpaintingCed"),
                N("rho", "Integration Scale", 2, .01, 100, "Structure-tensor or diffusion integration scale.", "inpaintingCt", "inpaintingCed", "inpaintingAniso"),
                N("theta", "Time Step", .5, .001, 1, "Iteration step size for diffusion.", "inpaintingMcf", "inpaintingCed", "inpaintingAniso"),
                I("iterations", "Iterations", 10, 1, 10000, "Number of inpainting iterations.", "inpaintingMcf", "inpaintingCed", "inpaintingAniso"),
                N("epsilon", "Transport Neighborhood Radius", 5, 1, 20, "Pixel radius of the coherence-transport neighborhood.", "inpaintingCt"), N("kappa", "Direction Sensitivity", 25, 0, 100, "Direction sensitivity of coherence transport.", "inpaintingCt"),
                N("channelCoefficient", "Channel Coefficient", 1, 1e-9, 100000, "Channel weight for coherence transport.", "inpaintingCt"),
                T("mode", "Diffusion Mode", "perona-malik", "Native anisotropic diffusion mode.", "inpaintingAniso"), N("contrast", "Contrast", 5, .01, 65535, "Contrast for anisotropic diffusion.", "inpaintingAniso"),
                I("maskWidth", "Noise Window Width", 5, 1, 511, "Width of the noise-estimation window.", "wienerNi"), I("maskHeight", "Noise Window Height", 5, 1, 511, "Height of the noise-estimation window.", "wienerNi"),
                I("width", "Kernel Width", 31, 1, 4095, "Used when no reference image is connected; a connected image supplies its width.", "psfMotion", "psfDefocus"), I("height", "Kernel Height", 31, 1, 4095, "Used when no reference image is connected; a connected image supplies its height.", "psfMotion", "psfDefocus"),
                N("blur", "Blur Amount", 3, .01, 1000, "Motion length or defocus radius, in pixels.", "psfMotion", "psfDefocus"), I("angle", "Motion Angle", 0, -180, 180, "Motion direction angle in degrees.", "psfMotion"), I("psfType", "Motion Kernel Type", 3, 1, 5, "1=accelerating, 2=fast accelerating, 3=constant, 4=decelerating, 5=fast decelerating.", "psfMotion")],
            "Inpainting requires image and the region to restore; Wiener restoration requires a single-channel real PSF with the same size as the source. Blur-kernel generation may connect image to match its size automatically; otherwise the manual width/height are used. filteredImage must match the source size, channels and type. The output PSF is energy-normalized to 1 and width/height report the actual output size.", Frames: [new(NodeImageFramePolicy.NewFixedFrame, FrameSuffix: "psf", FrameLabel: "PSF kernel", WhenParameter: "operator", WhenValues: ["psfMotion", "psfDefocus"]), new(NodeImageFramePolicy.PreserveInput)]),

        new("jlvision.surface", "Intensity Surface Reconstruction", "Preprocess", [Exec, In("image", VisionDataType.Image, true), In("gradientColumns", VisionDataType.Image)],
            [Out("image", VisionDataType.Image), Out("heightField", VisionDataType.Image, false), Out("shadingImage", VisionDataType.Image, false), Out("resultKind", VisionDataType.String), Out("unit", VisionDataType.String), Diagnostics, Next],
            [Op("sfsPentland", ("sfsPentland", "Pentland Shape from Shading"), ("sfsOrigLr", "Original Local Reconstruction"), ("sfsModLr", "Improved Local Reconstruction"), ("shadeHeightField", "Height Field Rendering"), ("reconstructGradient", "Gradient Integration")),
                N("slant", "Light Slant Angle", .5, 0, Math.PI / 2, "Angle between the light and viewing directions, in radians.", "sfsPentland", "sfsOrigLr", "sfsModLr", "shadeHeightField"),
                N("tilt", "Light Tilt Angle", 0, -Math.PI * 2, Math.PI * 2, "Light direction in the image plane, in radians.", "sfsPentland", "sfsOrigLr", "sfsModLr", "shadeHeightField"),
                N("albedo", "Albedo", 1, .000001, 1e9, "Diffuse reflectance of the surface.", "sfsPentland", "sfsOrigLr", "sfsModLr", "shadeHeightField"),
                N("ambient", "Ambient Light", 0, 0, 1e9, "Ambient light intensity.", "sfsPentland", "sfsOrigLr", "sfsModLr", "shadeHeightField"),
                B("shadows", "Compute Shadows", false, "Computes occlusion shadows during height-field rendering.", "shadeHeightField"),
                T("method", "Gradient Integration Method", "poisson", "Native gradient reconstruction method; both inputs must be same-size single-channel real gradient images.", "reconstructGradient")],
            "Estimates relative height from a gray image or renders a real-valued height field. Reconstruction outputs heightField (resultKind=relativeHeight, unit=relative); rendering outputs shadingImage (resultKind=shading, unit=intensity); image remains a compatibility output. Gradient reconstruction requires same-size single-channel real row/column gradients. Height output is not an absolute 3D measurement in millimeters.")
    ]);
}
