namespace VisionStudio.Engine;

/// <summary>
/// 行业件补全批（node-gap-analysis.md 第十节 P2）：超像素预分割 + 轮廓形状匹配。
///
/// 两项都不是"每台设备都用"，但缺了就只能整体换工具链：纹理件（织造、拉丝、皮革）
/// 没有规则预分割，低对比件（透明、印刷）没有形状匹配。都输出既有端口类型。
/// </summary>
public static class FeatureGapCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static readonly ParameterDescriptor Index = new("index", "Zero-based Index", "number", 0, 0, 1999, 1,
        Description: "Which contour in the connected list to match (0-based). Out of range returns found=false.");

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("feature.superpixel", "Superpixel Segmentation", "Feature",
            [Exec, new("image", VisionDataType.Image)],
            [new("region", VisionDataType.Region), new("labels", VisionDataType.Image),
             new("boundary", VisionDataType.Image), new("count", VisionDataType.Integer),
             new("meanSize", VisionDataType.Double), Diagnostics, Next],
            [
                new("regionSize", "Region Size", "number", 10, 2, 200, 1, Unit: "px", Group: "SLIC",
                    Description: "Target superpixel edge length in pixels. Pick it a few times the texture cell size: too small fragments every grain, too large merges the defect into its background."),
                new("ruler", "Compactness", "number", 10, 1, 100, 1, Group: "SLIC",
                    Description: "Balance between color similarity and spatial compactness. High values give square-ish tiles, low values follow color edges (what texture segmentation usually wants)."),
                new("iterations", "Iterations", "number", 5, 1, 20, 1, Group: "SLIC",
                    Description: "SLIC refinement passes. 5 is the OpenCV default; more changes little and costs linearly."),
                new("enforceConnectivity", "Enforce Connectivity", "boolean", true, Group: "SLIC",
                    Description: "Merges tiny disconnected label fragments after refinement, so every emitted region is a real connected component. Turn it off only when you want the raw label map."),
            ],
            "SLIC/SLICO superpixel segmentation: each superpixel comes out as one Region component, plus the raw label map and the boundary mask. Wire: image -> region/count, then region.filter / region.properties / region.toDetections. This is the texture pre-segmentation that MSER (unstable-region detection) cannot give; image.partitionDynamic is a regular grid and ignores gray values entirely. Region components follow 8-connectivity and the label order, so two runs produce the same component order. algorithmBackend=opencv.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: false), Actions: []),

        new("contour.matchXld", "Contour Shape Match", "Matching",
            [Exec, new("contours", VisionDataType.Contour), new("templateContours", VisionDataType.Contour)],
            [new("found", VisionDataType.Boolean), new("score", VisionDataType.Double), new("rotationDeg", VisionDataType.Double),
             new("scale", VisionDataType.Double), new("center", VisionDataType.Point2D),
             new("sampleCount", VisionDataType.Integer), Diagnostics, Next],
            [
                Index,
                new("templateIndex", "Template Contour Index", "number", 0, 0, 1999, 1,
                    Description: "Which contour in templateContours is the model (0-based)."),
                new("samples", "Signature Samples", "number", 256, 64, 2048, 32, Group: "Matching",
                    Description: "Points sampled around each contour for the radius signature. Higher resolves finer shape detail and costs linearly; 256 is enough for a polygon with tens of vertices."),
                new("minScore", "Minimum Score", "number", .7, 0, 1, .01, Group: "Limits",
                    Description: "Score floor for found. The score is 1 minus the RMS difference of the normalised radius signatures, so 0.9 means the shapes agree within about 10% of the mean radius."),
                new("allowRotation", "Allow Rotation", "boolean", true, Group: "Matching",
                    Description: "Search all cyclic shifts of the signature. Turn it off when the part orientation is already known, which makes the match stricter and faster."),
            ],
            "Shape matching on contours: both the scene and the template are resampled by arc length, reduced to a centroid-radius signature normalised by mean radius, then correlated over cyclic shifts. That makes the score invariant to translation, rotation and scale — which is exactly what match.shape (gray-edge template) cannot do on transparent or printed parts with almost no gray contrast. Outputs score, rotationDeg and scale relative to the template. Reflection is not distinguished from rotation. Units: pixels for center, degrees for rotation.", "builtin",
            new VisionToolCapabilities(EmitsOverlay: true), Actions: [])
    ];
}
