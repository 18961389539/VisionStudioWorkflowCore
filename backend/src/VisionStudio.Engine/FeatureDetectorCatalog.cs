namespace VisionStudio.Engine;

/// <summary>
/// 特征检测补全（2026-10-28）：角点、椭圆、脊线/中心线、Gabor 纹理。
///
/// 对照成熟视觉软件的检测器清单核对后确认的四个缺口：
/// 1) 角点：桥里只有 <c>jlvision.featureExtract</c> 的 pointsHarris / pointsFoerstner 选项，
///    没有独立节点，也没有"按响应排序 + 最小间距抑制 + 直接输出点列"的引导；
/// 2) 椭圆：<c>feature.circle</c> 只能找圆，<c>measure.fitEllipse</c> 要求先有轮廓，
///    斜视圆孔 / O 型圈 / 透视变形的圆没有一步到位的检测器；
/// 3) 脊线：桥里的 linesGauss / linesFacet 同样藏在通用下拉里，划痕、焊缝、线缆的中心线拿不到；
/// 4) 纹理：全目录没有 Gabor / Laws，方向性纹理（织纹、拉丝、磨砂）只能靠频域硬调。
///
/// 四个节点都声明 SupportsRoi（角点、椭圆、Gabor 直接在 ROI 内计算；脊线沿用 JLVision 全图语义后按 ROI 过滤点）。
/// </summary>
public static class FeatureDetectorCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);

    private static readonly ParameterDescriptor[] Canny =
    [
        new("cannyLow", "Canny Low", "number", 50, 1, 255, 1, Description: "Lower hysteresis threshold on the 8-bit gradient magnitude."),
        new("cannyHigh", "Canny High", "number", 150, 1, 255, 1, Description: "Upper hysteresis threshold; must be >= Canny Low.")
    ];

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("feature.corner", "Corner Detection", "Feature",
            [Exec, Image],
            [
                Image,
                new("points", VisionDataType.PointList),
                new("count", VisionDataType.Integer),
                new("maxResponse", VisionDataType.Double),
                new("meanResponse", VisionDataType.Double),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("method", "Detector", "select", "shi-tomasi",
                    Options: [new("Shi-Tomasi (good features to track)", "shi-tomasi"), new("Harris", "harris")],
                    Description: "Shi-Tomasi is the industrial default: stable ranking, one point per corner. Harris also responds to edges and needs a higher quality level to stay clean."),
                new("maxCorners", "Max Corners", "number", 200, 1, 5000, 1,
                    Description: "Upper bound on returned points. Shi-Tomasi and Harris stop at the strongest maxCorners; FAST keeps everything above the threshold and is then distance-suppressed."),
                new("qualityLevel", "Quality Level", "number", 0.01, 1e-6, 1, 0.005,
                    Description: "Fraction of the strongest response kept (Shi-Tomasi/Harris). Raise it when the image is full of texture."),
                new("minDistance", "Min Distance (px)", "number", 10, 0.5, 5000, 0.5,
                    Description: "Non-maximum suppression radius: no two corners closer than this. Should be about the smallest feature spacing on the part, not the pixel noise scale."),
                new("blockSize", "Block Size", "number", 3, 3, 31, 2,
                    Description: "Averaging window for the structure tensor (odd, 3..31). Larger stabilises gradients on noisy images and blurs nearby corners together."),
                new("harrisK", "Harris K", "number", 0.04, 0.001, 0.25, 0.001,
                    Description: "Harris sensitivity constant; only used when method=harris."),
                new("subpixel", "Subpixel Refinement", "boolean", false,
                    Description: "Refine every corner to sub-pixel accuracy with a 5x5 cornerSubPix window. Metrology needs this (0.05..0.2 px repeatability); leave it off for preview or coarse location work.")
            ],
            "Corner detection with a ranked PointList output, so corners can feed measure.fitLine / measure.fitCircleFixedRadius / measure.bestFitAlign directly instead of going through an 8-operator dropdown. Shi-Tomasi and Harris use OpenCV good-features-to-track with an explicit non-maximum suppression radius; optional cornerSubPix refinement lifts the points off the pixel grid when sub-pixel centres matter. Responses are reported from the minimum-eigenvalue map so the ranking is comparable between the two detectors. Insufficient contrast, an empty ROI or a degenerate block size returns found=false with the reason in diagnostics.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),

        new("feature.ellipse", "Ellipse Detection", "Feature",
            [Exec, Image],
            [
                Image,
                new("ellipses", VisionDataType.String),
                new("count", VisionDataType.Integer),
                new("ellipse", VisionDataType.String, Required: false),
                new("center", VisionDataType.Point2D, Required: false),
                new("majorAxis", VisionDataType.Double, Required: false),
                new("minorAxis", VisionDataType.Double, Required: false),
                new("angleDeg", VisionDataType.Double, Required: false),
                new("rmsError", VisionDataType.Double, Required: false),
                new("coverageDeg", VisionDataType.Double, Required: false),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("cannyLow", "Canny Low", "number", 50, 1, 255, 1, Description: "Lower hysteresis threshold used to build the edge map that the contours are traced from."),
                new("cannyHigh", "Canny High", "number", 150, 1, 255, 1, Description: "Upper hysteresis threshold; must be >= Canny Low."),
                new("minArea", "Min Area (px)", "number", 200, 1, 1e8, 1, Description: "Reject ellipses whose area is smaller than this; the cheapest way to drop edge noise."),
                new("maxArea", "Max Area (px)", "number", 10000000, 1, 1e9, 1, Description: "Reject ellipses larger than this. Set to the image area to disable."),
                new("minAspectRatio", "Min Aspect Ratio", "number", 1, 1, 100, 0.05, Description: "Minor/major axis ratio floor. 1 only accepts near-circles; lower it to accept flattened ellipses."),
                new("maxAspectRatio", "Max Aspect Ratio", "number", 1, 1, 100, 0.05, Description: "Minor/major axis ratio ceiling. Keep below 1 unless the inspection explicitly allows round features, because a circle also fits an ellipse."),
                new("minCoverageDeg", "Min Coverage (deg)", "number", 200, 10, 360, 5, Description: "Angular coverage of the supporting contour. Partially occluded holes still pass at 200..300; anything below this is usually a straight edge faking an ellipse."),
                new("maxRmsError", "Max RMS Error (px)", "number", 2, 0, 1000, 0.1, Description: "Reject fits whose radial RMS residual exceeds this. 0 disables the gate. Typical clean edges land below 0.5 px."),
                new("maxEllipses", "Max Ellipses", "number", 10, 1, 200, 1, Description: "Maximum number of returned ellipses, strongest support first."),
                new("minPoints", "Min Support Points", "number", 20, 5, 100000, 1, Description: "Minimum contour points backing one ellipse; OpenCV needs 5, but 20+ is what actually separates a real ellipse from a noisy arc.")
            ],
            "Detect ellipses straight from the image: Canny edge map, contour tracing, ellipse fit per contour, then an explicit consistency filter (area, axis ratio, angular coverage, radial RMS). This is the one-step path for foreshortened holes, O-rings and perspective-distorted circles that feature.circle (circles only) and measure.fitEllipse (needs a contour) cannot serve alone. Coverage uses the parametric angle in the ellipse frame, so an occluded ellipse still reports how much of it was actually supported. Largest-support ellipse is echoed on the scalar ports for direct wiring; the full list is in ellipses JSON. No ellipse converges, no contour passes the filters, or the ROI is empty returns found=false.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),

        new("feature.ridge", "Ridge / Centre Line", "Feature",
            [Exec, Image],
            [
                Image,
                new("contours", VisionDataType.Contour),
                new("points", VisionDataType.PointList),
                new("count", VisionDataType.Integer),
                new("totalLength", VisionDataType.Double),
                new("meanWidth", VisionDataType.Double),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("sigma", "Sigma", "number", 1.5, 0.1, 100, 0.1,
                    Description: "Gaussian pre-smoothing, in pixels. Should be about half the ridge width; larger sigma merges nearby ridges into one and loses thin scratches."),
                new("low", "Low Threshold", "number", 5, 0, 1e9, 0.5,
                    Description: "Lower hysteresis threshold on the ridge response. Lower it for faint scratches and expect more false segments."),
                new("high", "High Threshold", "number", 10, 0, 1e9, 0.5,
                    Description: "Upper hysteresis threshold; must be >= Low. Only segments anchored above High survive."),
                new("polarity", "Polarity", "select", "light",
                    Options: [new("Light ridges on dark background", "light"), new("Dark ridges on light background", "dark"), new("Both", "both")],
                    Description: "Which side the ridge sits on. 'both' costs two extraction passes and doubles the candidates."),
                new("lineModel", "Line Model", "select", "bar-shaped",
                    Options: [new("Bar-shaped", "bar-shaped"), new("Parabolic", "parabolic"), new("Gaussian", "gaussian")],
                    Description: "Profile model used to localise the centre line. bar-shaped suits machined grooves and printed lines; parabolic/gaussian suit soft edges such as welds and scratches."),
                new("extractWidth", "Extract Width", "boolean", true,
                    Description: "Also return the local ridge width per point. Needed for thickness checks; turn it off when only the path matters, because width estimation adds work."),
                new("completeJunctions", "Complete Junctions", "boolean", true,
                    Description: "Bridge ridges across crossings so a grid does not break into isolated stubs."),
                new("minLength", "Min Length (px)", "number", 10, 1, 1e6, 1,
                    Description: "Drop extracted segments shorter than this; short segments are almost always noise around a junction.")
            ],
            "Subpixel ridge (centre line) extraction on JLVision linesGauss, exposed as a first-class node instead of an operator option. Purpose: thin bright or dark structures - scratches, welds, cables, printed traces, glue beads - where the interesting quantity is the path and the width, not a region. Emits contours (XLD with per-point width attributes), a flattened PointList for downstream fitting, totalLength and meanWidth over all segments. sigma must track the actual ridge width: too large and two parallel ridges merge, too small and the centre line jitters. Empty image, no response above the thresholds or no polarity match returns found=false.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),

        new("feature.gaborBank", "Gabor Texture Energy", "Feature",
            [Exec, Image],
            [
                Image,
                new("energy", VisionDataType.String),
                new("dominantDirectionDeg", VisionDataType.Double),
                new("anisotropy", VisionDataType.Double),
                new("meanEnergy", VisionDataType.Double),
                new("maxEnergy", VisionDataType.Double),
                new("count", VisionDataType.Integer),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("wavelength", "Wavelength (px)", "number", 8, 1.5, 500, 0.5,
                    Description: "Period of the texture the bank should respond to, in pixels. Match it to one weave/grain period, not to the defect size."),
                new("orientations", "Orientations", "number", 6, 1, 24, 1,
                    Description: "Number of directions sampled over 180 deg. 6 (30 deg steps) is enough to tell weave from scratch; raise it when the defect differs from the background by a few degrees only."),
                new("sigmaRatio", "Sigma / Wavelength", "number", 0.56, 0.2, 1.5, 0.02,
                    Description: "Gaussian envelope relative to wavelength. 0.56 is the classic value; smaller means a wider frequency band and more sensitivity to scale drift."),
                new("gamma", "Spatial Aspect Ratio", "number", 0.5, 0.1, 1, 0.05,
                    Description: "Ellipticity of the Gabor kernel. 0.5 gives elongated kernels that discriminate direction strongly."),
                new("energyMode", "Energy Combination", "select", "sum",
                    Options: [new("Sum over orientations", "sum"), new("Maximum over orientations", "max")],
                    Description: "sum answers 'how much texture is here', max answers 'is there a strong oriented structure here'. For defect detection sum is the usual pick; for dominant-direction work choose max."),
                new("directionMap", "Emit Direction Map", "boolean", true,
                    Description: "Also emit an 8-bit image where each pixel holds the orientation (0..180 deg mapped to 0..255) of its strongest response. Doubles the analysis buffer.")
            ],
            "Gabor filter bank texture energy: one filter per orientation at a fixed wavelength, squared and combined. Catches what a single band-pass cannot - woven cloth, brushed metal, sand-blasted or knurled surfaces - because the energy is directional while the defect usually is not. Outputs a normalised energy image, the per-orientation energy array, the dominant direction and an anisotropy ratio (max/min over orientations). Anisotropy near 1 means isotropic texture; a value well above 1 flags a strongly oriented background that will fight any rotation-invariant threshold. Wavelength must be within 1.5..500 px and no larger than half the image; otherwise the kernel degenerates and the node fails instead of returning an all-black image.",
            "builtin", new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true))
    ];
}
