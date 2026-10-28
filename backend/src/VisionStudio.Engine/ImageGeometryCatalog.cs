namespace VisionStudio.Engine;

/// <summary>
/// 图像几何、梯度与距离变换（2026-10-28）。
///
/// 现场缺口（对照成熟视觉库逐项核对目录后确认，三个能力在 <c>jlvision.*</c> 算子桥里也没有）：
/// 1) 图像级镜像：107 个 <c>jlvision.imageOp</c> 算子中没有 mirror/flip，<c>regionOp.mirrorRegion</c>
///    只作用于区域。相机倒装、镜像工件、与模板对齐时只能 <c>image.rotate</c> 转 180°，或落回 C# 脚本。
/// 2) 梯度方向：桥里只有 <c>sobelAmp</c>（幅值），纹理主方向、各向异性、梯度方向直方图都缺方向分量。
/// 3) 距离变换：只有 <c>regionAnalysis</c> 的"最近点变换"顺带输出距离图，它按区域对象计算，
///    与"掩膜到最近零像素的像素距离"不是同一语义，分割/骨架/壁厚前处理没有直接手段。
///
/// 三个节点都只做像素级一一映射（不改变尺寸），因此沿用输入图像的坐标帧，不额外声明 Frames。
/// </summary>
public static class ImageGeometryCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);

    /// <summary>与既有 image.* 节点一致的输出形状：图像 + 尺寸 + 通道 + 诊断。</summary>
    private static readonly PortDescriptor[] ImageAndSize =
    [
        Image,
        new("width", VisionDataType.Integer),
        new("height", VisionDataType.Integer),
        new("channels", VisionDataType.Integer),
        Diagnostics,
        Next
    ];

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.flip", "Mirror Flip", "Preprocess", [Exec, Image], ImageAndSize,
            [
                new("mode", "Flip Mode", "select", "horizontal",
                    Options:
                    [
                        new("Horizontal (x -> width-1-x)", "horizontal"),
                        new("Vertical (y -> height-1-y)", "vertical"),
                        new("Both (180 deg)", "both")
                    ],
                    Description: "Mirror axis. Horizontal maps x to width-1-x, vertical maps y to height-1-y, both applies the two together (equivalent to a 180 deg rotation, not a transposition)."),
                new("emptyPolicy", "Empty Image Policy", "select", "fail",
                    Options: [new("Fail", "fail"), new("Passthrough", "passthrough")],
                    Description: "Behavior when the input image is empty: fail stops the graph; passthrough emits an empty 1x1 8-bit image with zero diagnostics counters.")
            ],
            "Exact pixel mirror with no interpolation and no size change, for inverted cameras, mirrored workpieces and template alignment. Pixel depth and channel count are preserved as-is (8/16-bit, int16, float32; 1..4 channels). Geometry that already carries coordinates (poses, ROIs, detections) must be mirrored separately with the same mode — this node only flips the image buffer.",
            "builtin"),

        new("image.gradient", "Gradient Magnitude and Direction", "Preprocess", [Exec, Image],
            [
                Image,
                new("direction", VisionDataType.Image, Required: false),
                new("meanMagnitude", VisionDataType.Double),
                new("maxMagnitude", VisionDataType.Double),
                new("p99Magnitude", VisionDataType.Double),
                new("directionUnit", VisionDataType.String),
                Diagnostics,
                Next
            ],
            [
                new("operator", "Gradient Operator", "select", "sobel",
                    Options: [new("Sobel", "sobel"), new("Scharr (3x3, better rotation symmetry)", "scharr")],
                    Description: "First-order derivative operator. Scharr is fixed at 3x3 and trades a little noise sensitivity for rotation symmetry."),
                new("kernelSize", "Kernel Size", "number", 3, 1, 7, 2,
                    Description: "Odd Sobel kernel size in 1..7 (3/5/7 are the only sensible values). Ignored when operator=scharr."),
                new("scaleMode", "Magnitude Scaling", "select", "none",
                    Options: [new("None (raw, saturated to source depth)", "none"), new("Stretch to 0..255 by P99", "stretch")],
                    Description: "none keeps raw derivative magnitudes (recommended for measurement and Otsu work). stretch divides by the 99th percentile and clamps to 0..255 so weak gradients become visible in the preview; the numeric metrics always report raw values."),
                new("emitDirection", "Emit Direction Image", "boolean", true,
                    Description: "When true, also emits a float32 direction image in degrees 0..360 measured by atan2(dy,dx), i.e. the direction of increasing gray. Texture analysis and anisotropic filtering need it; pure edge-strength work can turn it off to save a buffer.")
            ],
            "First-order gradient magnitude plus direction, as a single auditable step instead of wiring Sobel X, Sobel Y and a magnitude op by hand. Accepts 8/16-bit or float32, 1..4 channels (color is reduced to gray with the standard luminance weights). Magnitude keeps the source depth with saturation — negative lobes never wrap back into bright edges. Direction is float32 in degrees with 0 pointing along +x; undirected textures should fold it to 0..180.",
            "builtin"),

        new("image.distanceTransform", "Distance Transform", "Preprocess", [Exec, Image],
            [
                Image,
                new("maxDistance", VisionDataType.Double),
                new("meanDistance", VisionDataType.Double),
                new("foregroundCount", VisionDataType.Integer),
                Diagnostics,
                Next
            ],
            [
                new("distanceType", "Distance Metric", "select", "l2",
                    Options: [new("Euclidean (L2)", "l2"), new("City block (L1)", "l1")],
                    Description: "Distance metric. L2 is the physical distance; L1 is cheaper and still adequate for skeleton/proximity ordering."),
                new("maskSize", "Mask Size", "number", 3, 3, 7, 2,
                    Description: "Neighborhood used by the L2 approximation: 3 (exact for small distances) or 5/7 (smoother far field). Fixed at 3 for L1."),
                new("mode", "Measurement Side", "select", "foreground",
                    Options: [new("Inside the mask (foreground to background)", "foreground"), new("Outside the mask (background to foreground)", "background")],
                    Description: "foreground measures each non-zero pixel's distance to the nearest zero pixel (wall thickness, skeleton pruning). background inverts the mask first and measures the free space (clearance, channel width).")
            ],
            "Per-pixel distance to the nearest zero pixel, emitted as float32 in pixels. Requires an 8-bit single-channel mask where non-zero means foreground; color or 16-bit input must be thresholded first instead of being silently guessed. Border handling assumes pixels outside the image are background, so objects touching the frame get a distance that saturates at the frame edge — pad the mask when the true distance matters.",
            "builtin")
    ];
}
