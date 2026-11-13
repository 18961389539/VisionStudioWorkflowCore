namespace VisionStudio.Engine;

/// <summary>
/// 缺陷检出之后的四个环节（2026-10-28）：去伪、度量、统计、回流。
///
/// 核对现有 Defect 能力后确认：检测层已经完整（差分、参考比对含 variationK 统计容差带、
/// 动态阈值、平场校正、灰度统计判定、聚类、规则分级、inference.anomaly），
/// 缺的全部发生在"检出之后"：
/// 1) 判定层——误检没有抑制手段，只能调阈值（调高漏检、调低误报）；
/// 2) 度量层——cluster 明说"簇的圆度只能粗筛，真实轮廓请回 region/contour 重算"，
///    这条绕行路线每个项目都要重接一遍；边缘缺陷（崩角/毛刺/缺料）没有专用节点；
/// 3) 统计层——缺陷在单件上的空间分布、连续件的趋势都只能导出到 Excel 自己画；
/// 4) 闭环层——inference.* 只有推理，没有任何把现场样本送回去的通道。
///
/// 六个节点都是纯计算节点（dataset 例外，只写相对工作目录），不引入新的跨帧状态。
/// </summary>
public static class DefectAnalysisCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Detections = new("detections", VisionDataType.DetectionList);
    private static readonly PortDescriptor Failure = new("found", VisionDataType.Boolean);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("defect.filter", "False-Positive Filter", "Defect",
            [
                Exec,
                Detections,
                new("image", VisionDataType.Image, Required: false),
                new("region", VisionDataType.Region, Required: false)
            ],
            [
                Detections,
                new("count", VisionDataType.Integer),
                new("inputCount", VisionDataType.Integer),
                new("removedCount", VisionDataType.Integer),
                new("removedByJson", VisionDataType.String),
                Failure,
                Diagnostics,
                Next
            ],
            [
                new("minArea", "Min Area (px)", "number", 3, 0, 1e8, 0.5,
                    Group: "Size", Description: "Drop defects smaller than this. Also the cheapest noise gate, but it is the one that hurts: a real scratch can be 4 px². Prefer the contrast gate first."),
                new("maxArea", "Max Area (px)", "number", 0, 0, 1e9, 1,
                    Group: "Size", Description: "Drop defects larger than this. 0 disables. Use it to reject whole-region blobs caused by an exposure change, which are never real defects."),
                new("maxAspectRatio", "Max Aspect Ratio", "number", 0, 0, 1000, 0.1,
                    Group: "Shape", Description: "Reject shapes whose long/short side ratio exceeds this. 0 disables. Line-shaped false positives (edges of the part, tooling shadows) usually sit above 8."),
                new("minCircularity", "Min Circularity", "number", 0, 0, 1, 0.01,
                    Group: "Shape", Description: "Reject shapes below this 4*pi*area/perimeter^2 value. 0 disables. Use it to keep only compact dirt specks and drop thin edge artifacts."),
                new("minContrast", "Min Contrast", "number", 0, 0, 1e6, 1,
                    Group: "Contrast", Description: "Reject defects whose intensity range inside the bounding box is below this. Requires the image port; 0 disables. This is the single most effective false-positive gate, because tooling shadows are real edges with zero local range."),
                new("edgeMarginPx", "Edge Margin (px)", "number", 0, 0, 4096, 1,
                    Group: "Edge", Description: "Reject defects whose centre lies within this distance of the canvas border. 0 disables. Edge-of-image artifacts (vignetting, interpolation, part clamping) dominate the false-positive count in most cells."),
                new("canvasWidth", "Canvas Width", "number", 0, 0, 32768, 1,
                    Group: "Edge", Description: "Canvas width used for the edge margin. 0 takes it from the image port, or from the detection bounding box when no image is connected."),
                new("canvasHeight", "Canvas Height", "number", 0, 0, 32768, 1,
                    Group: "Edge", Description: "Canvas height used for the edge margin. 0 takes it from the image port."),
                new("maxCount", "Max Defects Kept", "number", 0, 0, 10000, 1,
                    Group: "Limit", Description: "Keep at most this many defects, largest first. 0 keeps all. Useful to bound downstream cost when a lighting fault floods the image.")
            ],
            "Suppress false positives with an explicit, attributable rule set instead of one global threshold. Each rejected defect is counted per reason (area / aspect / circularity / contrast / edge margin / count), so a sudden jump in one bucket tells you which knob to move - the usual alternative is lowering the threshold and silently accepting missed defects. Contrast and edge margin need the image or canvas ports; without them those two gates are skipped and reported as such rather than silently passing everything. All gates are AND-combined: a defect is kept only when it survives every enabled rule.",
            "builtin", new VisionToolCapabilities(Deterministic: true)),

        new("defect.geometry", "Defect Geometry", "Defect",
            [Exec, Detections, new("contours", VisionDataType.Contour, Required: false)],
            [
                new("geometryJson", VisionDataType.String),
                new("count", VisionDataType.Integer),
                new("maxLength", VisionDataType.Double),
                new("maxWidth", VisionDataType.Double),
                new("dominantAngleDeg", VisionDataType.Double),
                new("elongatedCount", VisionDataType.Integer),
                new("totalArea", VisionDataType.Double),
                Failure,
                Diagnostics,
                Next
            ],
            [
                new("maxDefects", "Max Defects", "number", 200, 1, 10000, 1,
                    Description: "Geometry is reported for the first maxDefects entries; the summary scalars still cover all of them."),
                new("elongationThreshold", "Elongated Above", "number", 3, 1, 1000, 0.1,
                    Description: "Defects whose length/width ratio exceeds this are counted as elongated (scratches, cracks, burrs). 3 separates a scratch from a smear in most cells."),
                new("angleMode", "Dominant Angle", "select", "length-weighted",
                    Options: [new("Length-weighted (ignores specks)", "length-weighted"), new("Simple mean of elongated defects", "mean")],
                    Description: "length-weighted folds angles into 0..180 deg and weights by length, so a 200 px scratch decides the direction instead of 50 dirt specks."),
                new("contourOffset", "Contour Index Offset", "number", 0, 0, 10000, 1,
                    Description: "Index of the first contour in the contours port that corresponds to defect 0. Use it when the contour list also contains holes or the part outline.")
            ],
            "Measure defects instead of only counting them: length, width, orientation, elongation and fill ratio per defect, plus the summary a quality report needs (largest length/width, dominant direction, elongated count). Connect contours to get exact minimum-area-rectangle geometry; without contours the node falls back to the axis-aligned bounding box and says so in diagnostics, because a 45-degree scratch measured on its bounding box looks twice as wide as it is. The dominant angle is folded to 0..180 deg: a scratch at 10 and at 190 deg is the same scratch. Empty detection list returns found=false with zeroed summaries rather than an error.",
            "builtin", new VisionToolCapabilities(Deterministic: true)),

        new("defect.edgeDefect", "Edge Defect (chip / burr)", "Defect",
            [
                Exec,
                new("samples", VisionDataType.PointList),
                new("nominalLine", VisionDataType.Line2D, Required: false),
                new("nominalCircle", VisionDataType.Circle, Required: false)
            ],
            [
                Detections,
                new("count", VisionDataType.Integer),
                new("maxDepth", VisionDataType.Double),
                new("totalLength", VisionDataType.Double),
                new("segmentsJson", VisionDataType.String),
                Failure,
                Diagnostics,
                Next
            ],
            [
                new("maxDeviation", "Max Deviation (px)", "number", 0.5, 0.01, 10000, 0.01,
                    Description: "Deviation from the nominal edge that counts as a defect. Set it just above the edge noise, not at the drawing tolerance: this node finds chips and burrs, not dimensional error."),
                new("polarity", "Polarity", "select", "both",
                    Options: [new("Both sides (chips and burrs)", "both"), new("Material outside nominal (burr)", "excess"), new("Material missing (chip)", "short")],
                    Description: "Which side of the nominal counts. 'both' is the default because a chipped edge usually has a raised burr next to it."),
                new("minSegmentLength", "Min Segment Length (px)", "number", 5, 0.5, 10000, 0.5,
                    Description: "Adjacent out-of-tolerance samples are merged into one defect; segments shorter than this along the contour are dropped as noise."),
                new("mergeGapPx", "Merge Gap (px)", "number", 2, 0, 1000, 0.5,
                    Description: "Two over-tolerance segments separated by less than this along the contour belong to the same chip. A single chip produces two lobes when the burr in the middle dips back inside tolerance."),
                new("canvasWidth", "Canvas Width", "number", 0, 0, 32768, 1,
                    Description: "Canvas width for the produced detection bounding boxes. 0 takes it from the sample extents."),
                new("canvasHeight", "Canvas Height", "number", 0, 0, 32768, 1,
                    Description: "Canvas height for the produced detection bounding boxes.")
            ],
            "Turn an edge deviation profile into discrete edge defects. Wire samples (contour.toPoints / feature.ridge output) plus exactly one nominal - a line or a circle - and the node measures the signed normal deviation, marks the over-tolerance samples, merges them along the contour (including the two lobes of one chip separated by a burr), and emits one DetectionList entry per defect with its depth, arc length and bounding box. This is the missing counterpart of tolerance.profile: that node answers 'is the edge within tolerance', this one answers 'where are the chips and how big are they'. Requires samples ordered along the contour; unordered point clouds produce meaningless segments, which is why the deviation is measured along the polyline rather than by nearest-point search.",
            "builtin", new VisionToolCapabilities(Deterministic: true)),

        new("defect.heatmap", "Defect Heatmap", "Defect",
            [Exec, Detections],
            [
                new("image", VisionDataType.Image),
                new("countsJson", VisionDataType.String),
                new("hotspotX", VisionDataType.Double),
                new("hotspotY", VisionDataType.Double),
                new("hotspotCount", VisionDataType.Integer),
                new("concentration", VisionDataType.Double),
                new("count", VisionDataType.Integer),
                new("occupiedCells", VisionDataType.Integer),
                Failure,
                Diagnostics,
                Next
            ],
            [
                new("gridCols", "Grid Columns", "number", 16, 2, 256, 1,
                    Description: "Horizontal cells. 16x12 is a good first look for a 4:3 sensor; finer grids turn sparse defects into visual noise."),
                new("gridRows", "Grid Rows", "number", 12, 2, 256, 1,
                    Description: "Vertical cells."),
                new("canvasWidth", "Canvas Width", "number", 0, 0, 32768, 1,
                    Description: "Canvas width that the grid spans. 0 uses the bounding box of all defects, which hides absolute position - set it explicitly when comparing across parts."),
                new("canvasHeight", "Canvas Height", "number", 0, 0, 32768, 1,
                    Description: "Canvas height that the grid spans. 0 uses the bounding box of all defects."),
                new("cellPixels", "Cell Size (px)", "number", 24, 4, 512, 1,
                    Description: "Rendered size of one cell in the output image. Only affects the picture, not the statistics."),
                new("colormap", "Colormap", "select", "heat",
                    Options: [new("Blue to red heat", "heat"), new("Grayscale", "gray")],
                    Description: "How cell counts are coloured. Heat is easier to read in a report; gray is easier to threshold.")
            ],
            "Spatial distribution of defects on one part: counts per grid cell, rendered as a heatmap plus a hot-spot coordinate and a concentration index (share of defects in the top three cells). The question it answers is 'are defects clustered in one region' - a run of parts failing along the same edge points at tooling or lighting, not at the process. Concentration near 1 means clustered; near 0 means uniformly spread. The grid spans canvasWidth/canvasHeight when given, so the same node parameters stay comparable across parts; leaving them 0 makes the grid follow the defects themselves.",
            "builtin", new VisionToolCapabilities(Deterministic: true)),

        new("defect.trend", "Defect Trend (SPC)", "Defect",
            [Exec, new("series", VisionDataType.NumberList)],
            [
                new("mean", VisionDataType.Double),
                new("sigma", VisionDataType.Double),
                new("ucl", VisionDataType.Double),
                new("lcl", VisionDataType.Double),
                new("last", VisionDataType.Double),
                new("movingAverage", VisionDataType.Double),
                new("slope", VisionDataType.Double),
                new("direction", VisionDataType.String),
                new("outOfControl", VisionDataType.Boolean),
                new("count", VisionDataType.Integer),
                Failure,
                Diagnostics,
                Next
            ],
            [
                new("window", "Moving Window", "number", 5, 2, 1000, 1,
                    Description: "Samples in the moving average. Roughly one shift's worth of parts: long enough to average out part-to-part noise, short enough to see a drift starting."),
                new("sigmaK", "Control Limit Sigma", "number", 3, 1, 6, 0.1,
                    Description: "Control limits sit at mean +/- K*sigma of the whole series. 3 is the SPC convention; lower it to 2 only when the consequence of a false alarm is cheap."),
                new("minSamples", "Min Samples", "number", 5, 2, 100000, 1,
                    Description: "Below this the sigma estimate is meaningless and the node returns found=false instead of a confident-looking limit."),
                new("baselineSamples", "Baseline Samples", "number", 0, 0, 100000, 1,
                    Description: "Estimate mean/sigma from the FIRST N samples only, i.e. a known-good period. 0 uses the whole series - which lets the very spike you want to catch inflate the limits and hide itself. Set it to the number of stable parts before the suspected drift.")
            ],
            "Run-chart statistics for a defect count series (one number per part, oldest first): mean, sigma, mean +/- K*sigma control limits, the moving average of the last window samples, the least-squares slope and a direction verdict. outOfControl fires when the latest sample leaves the limits - that is the signal to stop the line and look at the process, not at the part. A positive slope with a stable mean is the signature of a slow drift (tool wear, lamp ageing); a single spike with a flat slope is usually a handling event. This node is stateless: feed it a NumberList accumulated upstream (value.numberList / output.database readback), it does not remember previous runs.",
            "builtin", new VisionToolCapabilities(Deterministic: true)),

        new("defect.dataset", "Defect Sample Export", "Defect",
            [Exec, Detections, new("image", VisionDataType.Image)],
            [
                new("directory", VisionDataType.String),
                new("savedCount", VisionDataType.Integer),
                new("skippedCount", VisionDataType.Integer),
                new("samplesJson", VisionDataType.String),
                Failure,
                Diagnostics,
                Next
            ],
            [
                new("dataset", "Dataset Name", "text", "defect-samples",
                    Description: "Used as the file name prefix and as the dataset tag in samplesJson. Keep it stable per defect class so the exported folder can be handed to labelling as-is."),
                new("directory", "Output Directory", "text", "artifacts/defect-samples",
                    Description: "Relative to the working directory, which is what makes this node safe: absolute paths and '..' are rejected, so a workflow cannot be used to write outside the project."),
                new("maxSamples", "Max Samples", "number", 50, 1, 10000, 1,
                    Description: "Upper bound on exported crops per run. 50 keeps a full shift's worth of NG parts reviewable without filling the disk."),
                new("cropMarginPx", "Crop Margin (px)", "number", 4, 0, 512, 1,
                    Description: "Padding around the defect box. Some context is what makes a crop useful to a human labeller or a classifier; a tight crop of a 6 px speck is useless."),
                new("format", "Format", "select", "png",
                    Options: [new("PNG (lossless, recommended)", "png"), new("JPEG (smaller, lossy)", "jpg")],
                    Description: "PNG keeps the exact pixel values that triggered the decision. JPEG is smaller but can erase a low-contrast defect, which defeats the purpose of the sample."),
                new("jpegQuality", "JPEG Quality", "number", 92, 1, 100, 1,
                    Description: "Only used when format=jpg."),
                new("conflictPolicy", "On Existing File", "select", "suffix",
                    Options: [new("Append a counter (never overwrite)", "suffix"), new("Overwrite", "overwrite"), new("Skip", "skip")],
                    Description: "suffix is the default: a re-run must not destroy the samples collected earlier in the shift.")
            ],
            "Export defect crops as image files plus a machine-readable index, which is the missing return path between production and training. inference.* can only consume models; without this node every field sample has to be dragged out by hand, and that is where deep-learning projects stall in practice. Writes one file per defect into a work-directory-relative folder and emits samplesJson with the path, class, area and position of each crop, ready to be fed to output.database or reviewed in the artefacts folder. Absolute paths and '..' are rejected by design. A missing image port, an empty detection list or an unusable directory returns found=false with the reason, and files already on disk are never silently destroyed under the default conflict policy.",
            "builtin", new VisionToolCapabilities(Deterministic: false))
    ];
}
