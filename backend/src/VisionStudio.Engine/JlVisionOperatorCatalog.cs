using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine;

/// <summary>
/// 通用算子节点的目录条目。下拉选项直接从算子表生成，新增算子不需要再改目录，
/// 避免"表里有、UI 里没有"的漂移。
/// </summary>
internal static class JlVisionOperatorCatalog
{
    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("jlvision.imageOp", "Image Operations", "Preprocess",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image), new("image2", VisionDataType.Image, Required: false) { RequiredWhen = new("operator", ["compose3", "rgb3ToGray"]) }, new("image3", VisionDataType.Image, Required: false) { RequiredWhen = new("operator", ["compose3", "rgb3ToGray"]) }],
            [new("image", VisionDataType.Image), new("overlay", VisionDataType.Image), new("transform", VisionDataType.Transform2D, Required: false), new("operator", VisionDataType.String),
                new("width", VisionDataType.Integer), new("height", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("operator", "Operator", "select", "abs", Group: "Operation", Options:
                    [.. JlImageOperations.All.Values.OrderBy(op => op.Key, StringComparer.Ordinal)
                        .Select(op => new ParameterOption($"{op.Label} ({op.Key})", op.Key))],
                    Description: "JLVision JlImage operator to run. Spatial operations use explicit pixel-affine mappings and also emit transform; other entries map to the native operation. Parameters adapt to the selected operation."),
                new("lutJson", "Gray Lookup Table", "textarea", System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 256)), Group: "Parameters", Description: "JSON array of 256 integers in 0..255; entry i defines the output for input gray i."),
                new("row", "Row", "number", 0, -10000000, 10000000, 1, Unit: "px", Group: "Parameters", Description: "Crop origin row or top margin for the selected operation."),
                new("column", "Column", "number", 0, -10000000, 10000000, 1, Unit: "px", Group: "Parameters", Description: "Crop origin column or left margin for the selected operation."),
                new("size", "Size", "number", 3, 1, 511, 2, Group: "Parameters", Description: "Kernel / mask size for filters and morphology (odd values; even inputs are rounded up)."),
                new("size2", "Size 2", "number", 3, 1, 511, 2, Group: "Parameters", Description: "Second kernel dimension for rectangular masks; defaults to Size."),
                new("mult", "Multiplier", "number", 1, -100000, 100000, 0.1, Group: "Parameters", Description: "Multiplicative factor: scale/add-multiply operators, gamma value, LoG sigma, noise amplitude."),
                new("offset", "Offset / Exponent", "number", 0, -100000, 100000, 0.1, Group: "Parameters", Description: "Additive constant for scale/arithmetic operators, or the exponent for pow."),
                new("threshold", "Threshold", "number", 128, 0, 1000000, 1, Group: "Parameters", Description: "Threshold used by the selected operator: sigma-filter cutoff, gamma-encode step, edge amplitude, or mean/sp filter threshold."),
                new("count", "Count / Shift", "number", 1, 0, 32, 1, Group: "Parameters", Description: "Integer argument: bit shift, bit mask, bit slice index."),
                new("mode", "Mode", "text", "byte", Group: "Parameters", Description: "Operator-specific string: pixel type for convertType, encode flag for gamma, result type for laplace, filter type for sobelAmp/smooth, base for exp/log.")
            ],
            "Runs one native JLVision JlImage operator selected by the Operator parameter and returns the resulting image. " +
            "This exposes the point-arithmetic, trigonometry, bitwise, filter and gray-morphology operators that previously had no node: the table covers abs/invert/sqrt/exp/log/pow/scale/gamma/convertType, sin/cos/tan/asin/acos/atan/atan2, " +
            "add/sub/mult/div/absDiff/min/max, bit and/or/xor/not/shift/mask/slice, gauss/gaussImage/mean/binomial/medianRect/sigma/laplace/laplaceOfGauss/sobelAmp/smooth, " +
            "gray erosion/dilation/opening/closing/top-hat/bottom-hat/skeleton/inside, channel compose/append/split and domain helpers, plus mean/deviation/equalisation/topographic/noise. " +
            "Gray morphology uses image2 as the structuring element image and, when image2 is absent, generates a flat size x size structuring element; operators that need a second image use the connected image2 or fall back to the image itself, and an empty connected image2 fails. " +
            "Pixel-type errors surface as a node error naming the operator and the parameters it consumes, never as a silent no-op.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.regionFeature", "Region Feature Extraction", "Measurement",
            [new("exec", VisionDataType.Control), new("region", VisionDataType.Region), new("image", VisionDataType.Image, Required: false)],
            [new("value", VisionDataType.Double), new("values", VisionDataType.String), new("region", VisionDataType.Region),
                new("overlay", VisionDataType.Image), new("feature", VisionDataType.String),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("feature", "Feature", "select", "area", Group: "Operation", Options:
                    [.. JlRegionFeatures.All.Values.OrderBy(f => f.Key, StringComparer.Ordinal)
                        .Select(f => new ParameterOption($"{f.Label} ({f.Key})", f.Key))],
                    Description: "JLVision JlRegion feature to compute. Value is the primary number; Values carries every named component as JSON."),
                new("size", "Size", "number", 5, 1, 511, 1, Group: "Parameters", Description: "Secondary integer argument (cpar for fuzzy features, kernel size for noise estimation)."),
                new("count", "Count", "number", 1, 1, 10000, 1, Group: "Parameters", Description: "Integer argument: apar for fuzzy features, character height for text orientation, histogram quantisation."),
                new("row", "Row", "number", 10, -100000, 100000, 0.5, Group: "Parameters", Description: "Sample point row for shapeHistoPoint."),
                new("column", "Column", "number", 10, -100000, 100000, 0.5, Group: "Parameters", Description: "Sample point column for shapeHistoPoint."),
                new("min", "Min", "number", 0, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Lower gray bound for grayHistoRange."),
                new("max", "Max", "number", 1, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Upper gray bound for grayHistoRange."),
                new("mode", "Mode", "text", "", Group: "Parameters", Description: "Feature-specific string: feature name list for regionFeatures/grayFeatures/shapeHisto, or projection mode.")
            ],
            "Computes one JLVision JlRegion feature for the input region: geometry (area, circularity, compactness, convexity, rectangularity, Euler number, " +
            "eccentricity, elliptic axis, roundness, height/width ratio, smallest circle/rectangle1/rectangle2, inner circle/rectangle, diameter, holes), " +
            "moments (2nd/3rd order, central and invariant), run-length features, named regionFeatures queries, and image-driven statistics " +
            "(grayFeatures, areaCenterGray, entropy, fuzzy entropy/perimeter, plane deviation, noise distribution, gray histograms/projections, text line orientation/slant, shape histograms). " +
            "Value outputs the primary scalar; Values is a JSON object with every named component (for example row/column/radius). " +
            "Features that need pixels validate the image input up front instead of failing inside the native call.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.regionOp", "Region Operations", "Region",
            [new("exec", VisionDataType.Control), new("region", VisionDataType.Region), new("region2", VisionDataType.Region, Required: false), new("image", VisionDataType.Image, Required: false)],
            [new("region", VisionDataType.Region), new("overlay", VisionDataType.Image), new("operator", VisionDataType.String),
                new("components", VisionDataType.Integer), new("area", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("operator", "Operator", "select", "complement", Group: "Operation", Options:
                    [.. JlRegionOperations.All.Values.OrderBy(op => op.Key, StringComparer.Ordinal)
                        .Select(op => new ParameterOption($"{op.Label} ({op.Key})", op.Key))],
                    Description: "JLVision JlRegion operator to run. The node generates a rectangular structuring element from Size for the structural morphology entries."),
                new("size", "Size", "number", 5, 1, 511, 1, Group: "Parameters", Description: "Structuring element size (square), circle diameter, or generator width depending on the operator."),
                new("count", "Count / Iterations", "number", 1, 0, 100000, 1, Group: "Parameters", Description: "Iteration count for morphology, or the integer argument of pruning/eliminateRuns/hammingChangeRegion."),
                new("row", "Row", "number", 10, -100000, 100000, 0.5, Group: "Parameters", Description: "Reference / generator row."),
                new("column", "Column", "number", 10, -100000, 100000, 0.5, Group: "Parameters", Description: "Reference / generator column."),
                new("phi", "Phi", "number", 0, -180, 180, 0.5, Unit: "deg", Group: "Parameters", Description: "Orientation for the generator operators."),
                new("length1", "Length 1", "number", 10, 0, 100000, 0.5, Group: "Parameters", Description: "Second radius / height / end coordinate depending on the operator."),
                new("length2", "Length 2", "number", 10, 0, 100000, 0.5, Group: "Parameters", Description: "Reserved second length for generators that need it."),
                new("min", "Min", "number", 0, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Lower bound for the selection operators."),
                new("max", "Max", "number", 1e9, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Upper bound for the selection operators."),
                new("percent", "Percent", "number", 50, 0, 100, 1, Unit: "%", Group: "Parameters", Description: "Percentage argument for selectShapeStd / partitionDynamic."),
                new("mode", "Mode", "text", "", Group: "Parameters", Description: "Operator-specific string: Golay element, mirror axis, sort mode, shape feature name, generator type.")
            ],
            "Runs one native JLVision JlRegion operator on the input region and returns the resulting region. " +
            "Covers set operations (complement/difference/backgroundSeg), structural morphology with an auto-generated rectangular structuring element " +
            "(erosion/dilation/opening/closing/top-hat/bottom-hat/Minkowski/skeleton/SKIZ/pruning/rank/eliminateRuns/fitting), Golay thinning and thickening, " +
            "geometric transforms (clip/move/zoom/mirror/transpose/partition/interjacent), selection and ordering (selectShapeStd/selectShapeProto/selectRegionPoint/sortRegion/expandRegion/removeNoiseRegion/fillUpShape/hammingChangeRegion), " +
            "gray-driven selection, and region generators (rectangle2/ellipse/circle sector/line/polygon/checker/random/struct element). " +
            "Ergonomics: no hand-drawn structuring element is required; Size drives a square SE, and reference-point operators read Row/Column. " +
            "Region2 and Image inputs are validated before execution, so a missing input fails with an actionable message instead of a native error code.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.geometry", "Geometry Operations", "Geometry",
            [new("exec", VisionDataType.Control)],
            [new("value", VisionDataType.Double), new("values", VisionDataType.String),
                new("overlay", VisionDataType.Image), new("points", VisionDataType.Contour),
                new("point", VisionDataType.Point2D, Required: false), new("inputPoints", VisionDataType.Contour),
                new("count", VisionDataType.Integer), new("found", VisionDataType.Boolean), new("componentCount", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("operator", "Operator", "select", "distancePp", Group: "Operation", Options:
                    [.. JlGeometryOperations.All.Values.OrderBy(op => op.Key, StringComparer.Ordinal)
                        .Select(op => new ParameterOption($"{op.Label} ({op.Key})", op.Key))],
                    Description: "JLVision JlMisc geometry operator. Angle results are radians; Values carries every named component."),
                new("row1", "Row 1", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Description: "First point row / line A start row."),
                new("column1", "Column 1", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Description: "First point column / line A start column."),
                new("row2", "Row 2", "number", 10, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Second point row / line A end row."),
                new("column2", "Column 2", "number", 10, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Second point column / line A end column."),
                new("row3", "Row 3", "number", 10, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Line B start row."),
                new("column3", "Column 3", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Line B start column."),
                new("row4", "Row 4", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Line B end row."),
                new("column4", "Column 4", "number", 10, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Line B end column."),
                new("radius1", "Radius 1", "number", 10, 0, 1e9, 0.5, Group: "Geometry", Description: "First ellipse radius."),
                new("radius2", "Radius 2", "number", 10, 0, 1e9, 0.5, Group: "Geometry", Description: "Second ellipse radius."),
                new("phi", "Phi", "number", 0, -6.2832, 6.2832, 0.01, Group: "Geometry", Description: "Ellipse orientation in radians."),
                new("angle", "Angle", "number", 1.5708, -6.2832, 6.2832, 0.01, Group: "Geometry", Description: "Ellipse parameter angle in radians."),
                new("size", "Size", "number", 1, 0, 1000, 0.1, Group: "Geometry", Description: "Scalar argument: sigma for the Gaussian kernel, alpha for filter info."),
                new("mode", "Mode", "text", "", Group: "Geometry", Description: "Filter name for infoEdges/infoSmooth.")
            ],
            "JLVision JlMisc geometry and filter-info operators, evaluated from scalar parameters (no image needed): " +
            "point/point, point/line, point/segment, segment/line and segment/segment distances (min and max), " +
            "line angle, angle between two lines, line intersection with a parallel flag, line orientation and line position (centre/length/phi), " +
            "point on ellipse, 1D Gaussian kernel, and info_edges/info_smooth filter coefficient queries. " +
            "Angle outputs are radians; Value is the primary scalar and Values holds every named component (row/column/min/max/phiDeg). " +
            "point/points/count/found describe computed 2D points only (intersection, projection, ellipse point or line centre); parallel lines have no point. inputPoints are debug inputs; componentCount counts named scalar fields. " +
            "This node covers the geometry math that used to be hand-written inside scripts when converting a located pose into robot offsets.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.matrixOp", "Matrix Operations", "Geometry",
            [new("exec", VisionDataType.Control), new("matrixA", VisionDataType.String, Required: false), new("matrixB", VisionDataType.String, Required: false)],
            [new("value", VisionDataType.Double), new("matrix", VisionDataType.String), new("values", VisionDataType.String),
                new("overlay", VisionDataType.Image), new("plot", VisionDataType.Contour),
                new("rows", VisionDataType.Integer), new("columns", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("operator", "Operator", "select", "transpose", Group: "Operation", Options:
                    [.. JlMatrixOperations.All.Values.OrderBy(op => op.Key, StringComparer.Ordinal)
                        .Select(op => new ParameterOption($"{op.Label} ({op.Key})", op.Key))],
                    Description: "JLVision JlMatrix linear-algebra operator. Matrices are JSON arrays of rows."),
                new("matrixA", "Matrix A", "textarea", "[[1,0],[0,1]]", Group: "Matrices", Description: "A wired matrixA takes precedence; otherwise the matrix below is used. Edit as a table or JSON."),
                new("matrixB", "Matrix B", "textarea", "[[1,0],[0,1]]", Group: "Matrices", Description: "A wired matrixB takes precedence; otherwise the matrix below is used."),
                new("scalar", "Scalar", "number", 1, -1e9, 1e9, 0.01, Group: "Matrices", Description: "Scalar argument: scale factor, power, epsilon, or the fill value for create."),
                new("row", "Row", "number", 0, 0, 100000, 1, Group: "Matrices", Description: "Row index for getValue/getSubMatrix."),
                new("column", "Column", "number", 0, 0, 100000, 1, Group: "Matrices", Description: "Column index for getValue/getSubMatrix."),
                new("rows", "Rows", "number", 2, 1, 100000, 1, Group: "Matrices", Description: "Row count for create/repeat/getSubMatrix; diagonal index for getDiagonal."),
                new("columns", "Columns", "number", 2, 1, 100000, 1, Group: "Matrices", Description: "Column count for create/repeat/getSubMatrix."),
                new("mode", "Mode", "text", "", Group: "Matrices", Description: "Operator-specific string: matrixType (general/symmetric/positive_definite), normType, sumType or decomposition type.")
            ],
            "JLVision JlMatrix linear algebra with JSON matrix inputs and outputs: transpose, invert, determinant, add/sub/mult, element-wise multiply/divide, " +
            "abs/sqrt, scale, matrix and element-wise power, norm, sum/max/min/mean, size, single element, sub matrix, diagonal, repeat, " +
            "linear solve, symmetric/generalized symmetric eigenvalues, orthogonal decomposition and type decomposition, plus constant matrix creation. " +
            "Value is the element count (or the scalar result for determinant/norm/getValue); Matrix is the result as JSON; Values carries rows/columns. " +
            "Connect a previous matrix output to matrixA/matrixB to compose calculations; connected values override the editable parameters. Matrices are limited to one million elements; expansion operations check the output size before allocation. Heatmaps show the top-left 128x128 cells; plot shows up to 64 normalized debug samples. Full matrix output is retained.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.homMat", "Homogeneous Transform", "Geometry",
            [new("exec", VisionDataType.Control)],
            [new("value", VisionDataType.Double), new("matrix", VisionDataType.String), new("values", VisionDataType.String),
                new("overlay", VisionDataType.Image), new("points", VisionDataType.Contour),
                new("transform", VisionDataType.Transform2D, Required: false),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("operator", "Operator", "select", "translate", Group: "Operation", Options:
                    [.. JlHomMatOperations.All.Values.OrderBy(op => op.Key, StringComparer.Ordinal)
                        .Select(op => new ParameterOption($"{op.Label} ({op.Key})", op.Key))],
                    Description: "JLVision JlHomMat2D operator. Matrices are built from the parameters below and returned as 3x3 JSON plus a platform Transform2D."),
                new("tx", "Translation X", "number", 0, -1e9, 1e9, 0.1, Group: "Transform", Description: "Translation along the column axis."),
                new("ty", "Translation Y", "number", 0, -1e9, 1e9, 0.1, Group: "Transform", Description: "Translation along the row axis."),
                new("phi", "Rotation", "number", 0, -6.2832, 6.2832, 0.001, Unit: "rad", Group: "Transform", Description: "Rotation angle in radians (counter-clockwise)."),
                new("theta", "Slant angle", "number", 0, -1.5708, 1.5708, 0.001, Unit: "rad", Group: "Transform", Description: "Slant angle in radians for the slant operators."),
                new("sx", "Scale X", "number", 1, -1e6, 1e6, 0.01, Group: "Transform", Description: "Scale factor along the column axis."),
                new("sy", "Scale Y", "number", 1, -1e6, 1e6, 0.01, Group: "Transform", Description: "Scale factor along the row axis."),
                new("px", "Point X / P1 column", "number", 0, -1e9, 1e9, 0.1, Group: "Transform", Description: "Fixed point column (or first line point) for rotate/scale/slant/reflect."),
                new("py", "Point Y / P1 row", "number", 0, -1e9, 1e9, 0.1, Group: "Transform", Description: "Fixed point row (or first line point) for rotate/scale/slant/reflect."),
                new("qx", "P2 column / probe X", "number", 10, -1e9, 1e9, 0.1, Group: "Transform", Description: "Second line point column, or the probe point to transform."),
                new("qy", "P2 row / probe Y", "number", 10, -1e9, 1e9, 0.1, Group: "Transform", Description: "Second line point row, or the probe point to transform."),
                new("mode", "Mode", "text", "", Group: "Transform", Description: "Axis for slant/slantLocal: x or y.")
            ],
            "Builds a JLVision JlHomMat2D from parameters and returns it as a 3x3 matrix in JSON plus a platform Transform2D: " +
            "identity, translate/translateLocal, rotate/rotateLocal, scale/scaleLocal, slant/slantLocal, reflect/reflectLocal, transpose, invert, compose, " +
            "determinant, toAffinePar (sx/sy/phi/theta/tx/ty) and point/pixel transformation. " +
            "Because the affine parameters come straight from hom_mat2d_to_affine_par, the reported 3x3 matches exactly what the native operator applies; " +
            "no matrix JSON parsing is required. Use this node to build the offset that roi.transform or a robot guidance step consumes.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.contourOp", "Contour Operator", "Contour",
            [new("exec", VisionDataType.Control), new("contour", VisionDataType.Contour, Required: false) { RequiredWhen = new("operator", JlContourOperations.All.Where(p => !p.Value.IgnoresInput).Select(p => p.Key).ToArray()) },
                new("contour2", VisionDataType.Contour, Required: false), new("region", VisionDataType.Region, Required: false)],
            [new("contours", VisionDataType.Contour), new("overlay", VisionDataType.Image), new("count", VisionDataType.Integer),
                new("pointCount", VisionDataType.Integer), new("operator", VisionDataType.String),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("operator", "Operator", "select", "smoothContours", Group: "Operation", Options:
                    [.. JlContourOperations.All.Values.OrderBy(op => op.Key, StringComparer.Ordinal)
                        .Select(op => new ParameterOption($"{op.Label} ({op.Key})", op.Key))],
                    Description: "JLVision XLD contour operator, applied to each input contour; set/region operators use contour2 or region."),
                new("size", "Size", "number", 5, 0, 100000, 0.5, Group: "Parameters", Description: "Distance/radius/line distance depending on the operator."),
                new("count", "Count", "number", 3, 0, 100000, 1, Group: "Parameters", Description: "Regression point count for smoothing, smooth count for segmentation."),
                new("row", "Row", "number", 0, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Generator centre row / clip row1."),
                new("column", "Column", "number", 0, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Generator centre column / clip column1."),
                new("phi", "Phi", "number", 0, -6.2832, 6.2832, 0.001, Unit: "rad", Group: "Parameters", Description: "Generator orientation."),
                new("length1", "Length 1", "number", 10, 0, 1e9, 0.5, Group: "Parameters", Description: "Second radius / clip height / relative distance."),
                new("length2", "Length 2", "number", 10, 0, 1e9, 0.5, Group: "Parameters", Description: "Second length for rectangle2 and crop."),
                new("min", "Min", "number", 0, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Lower bound for the selection operators."),
                new("max", "Max", "number", 1e9, -1e9, 1e9, 0.5, Group: "Parameters", Description: "Upper bound for the selection operators."),
                new("mode", "Mode", "text", "", Group: "Parameters", Description: "Operator-specific string: parallel mode, crop close flag, merge mode, selection feature name.")
            ],
            "Runs a JLVision XLD contour operator on every input contour and returns the resulting contour list: " +
            "generators (circle/ellipse/rectangle2/cross), close/smooth/segment/parallel, rectangle clipping and cropping, " +
            "shape-based and point-based selection, adjacent/collinear/cocircular union, closed-contour union/difference/intersection, and region intersection. " +
            "Each output XLD object becomes one platform contour with area/perimeter/circularity/bounding box recomputed, so downstream contour.properties and geometry fitting keep working. " +
            "Set operators read contour2 and region intersection reads region; a missing input fails before the native call with a readable message.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.threshold", "Threshold and Region Segmentation", "Preprocess",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image), new("image2", VisionDataType.Image, Required: false)],
            [new("region", VisionDataType.Region), new("overlay", VisionDataType.Image), new("operator", VisionDataType.String),
                new("area", VisionDataType.Integer), new("components", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("operator", "Operator", "select", "threshold", Group: "Operation", Options:
                    [.. JlThresholdOperations.All.Values.OrderBy(op => op.Key, StringComparer.Ordinal)
                        .Select(op => new ParameterOption($"{op.Label} ({op.Key})", op.Key))],
                    Description: "JLVision threshold / segmentation operator. Image in, region out."),
                new("min", "Min", "number", 0, -1e9, 1e9, 1, Group: "Threshold", Description: "Lower gray value (or low hysteresis threshold)."),
                new("max", "Max", "number", 128, -1e9, 1e9, 1, Group: "Threshold", Description: "Upper gray value (or high hysteresis threshold)."),
                new("threshold", "Threshold", "number", 128, -1e9, 1e9, 1, Group: "Threshold", Description: "Absolute threshold for varThreshold."),
                new("size", "Size", "number", 3, 1, 511, 2, Group: "Threshold", Description: "Mask size / sigma / minSize depending on the operator."),
                new("size2", "Size 2", "number", 3, 1, 511, 2, Group: "Threshold", Description: "Second mask dimension for varThreshold."),
                new("mult", "Multiplier", "number", 0, -1e6, 1e6, 0.1, Group: "Threshold", Description: "stdDevScale / offset / tolerance depending on the operator."),
                new("count", "Count", "number", 5, 1, 100000, 1, Group: "Threshold", Description: "minSize / maxLength / raster size depending on the operator."),
                new("mode", "Mode", "text", "", Group: "Threshold", Description: "Method name for localThreshold (default adapted_std_deviation)."),
                new("mode2", "Light/Dark", "text", "", Group: "Threshold", Description: "dark or light for local/var/dyn threshold.")
            ],
            "JLVision threshold and segmentation operators producing a platform region: global min/max threshold, automatic (sigma) threshold, " +
            "binary histogram threshold, dual threshold, fast threshold, hysteresis threshold, local threshold (adapted_std_deviation), variance threshold, " +
            "dynamic threshold against a second image, local maxima/minima and region growing. " +
            "Area and component count are reported so a threshold that silently selects nothing is visible immediately. " +
            "Operators that need a second image fail up front when image2 is missing.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.contourFeature", "Contour Feature Extraction", "Measurement",
            [new("exec", VisionDataType.Control), new("contour", VisionDataType.Contour), new("contour2", VisionDataType.Contour, Required: false) { RequiredWhen = new("feature", JlContourFeatures.All.Where(p => p.Value.NeedsSecond).Select(p => p.Key).ToArray()) }],
            [new("value", VisionDataType.Double), new("values", VisionDataType.String),
                new("contour", VisionDataType.Contour), new("overlay", VisionDataType.Image),
                new("pointCount", VisionDataType.Integer), new("inputCount", VisionDataType.Integer), new("selectedIndex", VisionDataType.Integer), new("feature", VisionDataType.String),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("feature", "Feature", "select", "areaCenter", Group: "Operation", Options:
                    [.. JlContourFeatures.All.Values.OrderBy(f => f.Key, StringComparer.Ordinal)
                        .Select(f => new ParameterOption($"{f.Label} ({f.Key})", f.Key))],
                    Description: "JLVision XLD contour feature. Value is the primary number, Values carries every named component."),
                new("selectedIndex", "Computed Contour Index", "number", 0, 0, 9999, 1, Description: "Zero-based input contour index; the numeric result, contour output, point count and overlay all refer to this contour. Out-of-range values fail explicitly."),
                new("secondIndex", "Reference Contour Index", "number", 0, 0, 9999, 1, Description: "Selects the reference contour from contour2 when a second contour is required."),
                new("row", "Row", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Point row for distancePc, line start row for distanceLc/distanceSc."),
                new("column", "Column", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Point column for distancePc, line start column for distanceLc/distanceSc."),
                new("row2", "Row 2", "number", 10, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Line/segment end row."),
                new("column2", "Column 2", "number", 10, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Line/segment end column."),
                new("mode", "Mode", "text", "", Group: "Geometry", Description: "Distance mode for distanceCc (default point_to_point).")
            ],
            "Computes one JLVision XLD contour feature for the explicitly selected input contour: area with centroid, circularity, compactness, convexity, rectangularity, " +
            "orientation, eccentricity with bulkiness, elliptic axis, maximum-chord diameter, height/width ratio, second-order moments, point count, closed flag, " +
            "and distances to a second contour, a point, a line or a segment. " +
            "XLD features are computed on the contour itself (no region fill), so they stay valid for open or self-touching edges where region statistics would fail. " +
            "Features that need a second contour validate the input before the native call.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false)),

        new("jlvision.measure1d", "1D Edge Measurement", "Measurement",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image)],
            [new("edges", VisionDataType.String), new("overlay", VisionDataType.Image), new("contours", VisionDataType.Contour),
                new("count", VisionDataType.Integer), new("found", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("next", VisionDataType.Control)],
            [
                new("shape", "Measure Shape", "select", "rectangle", Options:
                [
                    new("Rectangle2", "rectangle"), new("Arc", "arc")
                ], Description: "Rectangle2 uses row/column/phi/measureLength1/measureLength2; arc uses row/column/radius/angleStart/angleExtent/annulusRadius."),
                new("method", "Method", "select", "measurePos", Options:
                [
                    new("MeasurePos (single edges)", "measurePos"), new("MeasurePairs (edge pairs)", "measurePairs"),
                    new("MeasureThresh", "measureThresh"), new("MeasureProjection", "measureProjection")
                ], Description: "Native measure method; edges output adapts per method."),
                new("row", "Row", "number", null, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Center row; empty uses the image centre.") { Optional = true },
                new("column", "Column", "number", null, -1e9, 1e9, 0.5, Group: "Geometry", Description: "Center column; empty uses the image centre.") { Optional = true },
                new("phi", "Phi", "number", 0, -180, 180, 0.5, Unit: "deg", Group: "Geometry", VisibleWhen: new("shape", ["rectangle"]), Description: "Rectangle orientation in degrees."),
                new("measureLength1", "Measure Length 1", "number", 40, 1, 100000, 0.5, Unit: "px", Group: "Geometry", VisibleWhen: new("shape", ["rectangle"]), Description: "Half length along the profile direction."),
                new("measureLength2", "Measure Length 2", "number", 2, 0.1, 100000, 0.5, Unit: "px", Group: "Geometry", VisibleWhen: new("shape", ["rectangle"]), Description: "Half width across the profile."),
                new("radius", "Radius", "number", 40, 1, 1e9, 0.5, Unit: "px", Group: "Geometry", VisibleWhen: new("shape", ["arc"]), Description: "Arc mean radius."),
                new("angleStart", "Angle Start", "number", -180, -360, 360, 1, Unit: "deg", Group: "Geometry", VisibleWhen: new("shape", ["arc"]), Description: "Arc start angle in degrees."),
                new("angleExtent", "Angle Extent", "number", 360, 0, 720, 1, Unit: "deg", Group: "Geometry", VisibleWhen: new("shape", ["arc"]), Description: "Arc sweep angle in degrees."),
                new("annulusRadius", "Annulus Radius", "number", 10, 0.1, 1e9, 0.5, Unit: "px", Group: "Geometry", VisibleWhen: new("shape", ["arc"]), Description: "Half width of the annulus."),
                new("translateRow", "Translate Row (unused)", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Internal: true, Description: "Reserved: native TranslateMeasure corrupts the handle, so this value is ignored."),
                new("translateColumn", "Translate Column (unused)", "number", 0, -1e9, 1e9, 0.5, Group: "Geometry", Internal: true, Description: "Reserved: native TranslateMeasure corrupts the handle, so this value is ignored."),
                new("sigma", "Sigma", "number", 1, 0.4, 100, 0.1, Group: "Measure", VisibleWhen: new("method", ["measurePos", "measurePairs", "measureThresh"]), Description: "Gaussian smoothing of the 1D profile."),
                new("threshold", "Threshold", "number", 30, 0.5, 255, 1, Group: "Measure", VisibleWhen: new("method", ["measurePos", "measurePairs", "measureThresh"]), Description: "Minimum edge amplitude."),
                new("transition", "Transition", "select", "all", Options:
                [
                    new("All", "all"), new("Dark to light", "positive"), new("Light to dark", "negative")
                ], Group: "Measure", VisibleWhen: new("method", ["measurePos", "measurePairs"]), Description: "Edge polarity filter."),
                new("select", "Select", "select", "all", Options:
                [
                    new("All", "all"), new("First", "first"), new("Last", "last")
                ], Group: "Measure", VisibleWhen: new("method", ["measurePos", "measurePairs", "measureThresh"]), Description: "Which edges inside the profile participate.")
            ],
            "JLVision native 1D measure object: builds a rectangle2 or arc measure, then runs MeasurePos, MeasurePairs, MeasureThresh or MeasureProjection. " +
            "Edges are returned as a JSON array (row/column/amplitude/distance per edge, or pair columns for MeasurePairs). " +
            "Unlike measure.caliper this node goes through the native measure object, which is what enables arc profiles and gray projection. " +
            "Native quirks of JLVisionLib 1.0.0 honored here: output tuples have inconsistent lengths (the pair width tuple is empty, so width is dropped instead of faked), " +
            "and TranslateMeasure / GetMeasureParam / ResetFuzzyMeasure corrupt or fail the measure handle, so translateRow/translateColumn are intentionally ignored.",
            "builtin", new VisionToolCapabilities(EmitsOverlay: false))
    ];
}
