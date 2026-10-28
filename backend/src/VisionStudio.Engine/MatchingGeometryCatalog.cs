namespace VisionStudio.Engine;

/// <summary>
/// 投影几何两个节点（2026-10-28）：通用单应配准与 PnP 位姿。
///
/// 缺口来源：
/// 1) <c>image.register</c> 只能求刚体（固定尺度）变换；单应只在 <c>image.stitch</c> 内部算过，
///    不对外暴露；<c>calibration.planar</c> 是标定语境。倾斜拍摄、相机斜装、大幅透视的工件
///    没有可复用的单应节点。
/// 2) 3D 位姿只有 <c>recognition.aruco</c>（标记板）。普通零件拿到 4 个以上已知世界点后
///    无法求 SE(3)，机器人抓取只能停在 2D。
///
/// 两个节点都要求显式给出对应点，而不是"猜"：单应至少 4 组、PnP 至少 4 组，且顺序必须一一对应。
/// 这样失败原因是可解释的（点太少 / 退化 / 重投影超限），而不是一个空结果。
/// </summary>
public static class MatchingGeometryCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("match.homography", "Homography Registration", "Matching",
            [
                Exec,
                new("sourcePoints", VisionDataType.PointList),
                new("targetPoints", VisionDataType.PointList),
                new("image", VisionDataType.Image, Required: false)
            ],
            [
                new("transform", VisionDataType.Transform2D),
                new("warped", VisionDataType.Image, Required: false),
                new("inlierCount", VisionDataType.Integer),
                new("pointCount", VisionDataType.Integer),
                new("reprojRms", VisionDataType.Double),
                new("maxError", VisionDataType.Double),
                new("scaleX", VisionDataType.Double),
                new("scaleY", VisionDataType.Double),
                new("rotationDeg", VisionDataType.Double),
                new("skewDeg", VisionDataType.Double),
                new("perspectiveX", VisionDataType.Double),
                new("perspectiveY", VisionDataType.Double),
                new("corners", VisionDataType.String),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("method", "Estimator", "select", "ransac",
                    Options: [new("RANSAC (robust, recommended)", "ransac"), new("Least squares (all points count)", "least-squares"), new("LMEDS (least median of squares)", "lmeds")],
                    Description: "RANSAC rejects outliers before solving; least-squares uses every point and is only safe on clean correspondences; LMEDS tolerates up to 50% gross outliers but has no guarantee on the threshold."),
                new("ransacThresholdPx", "RANSAC Threshold (px)", "number", 2, 0.1, 100, 0.1,
                    Description: "A correspondence counts as an inlier when its reprojection lands within this distance. Should be about twice the point localisation noise, not the feature size."),
                new("maxIterations", "Max Iterations", "number", 2000, 50, 20000, 50,
                    Description: "RANSAC sampling budget. 2000 is plenty for a 4-point model with 50% inliers."),
                new("confidence", "Confidence", "number", 0.995, 0.5, 0.9999, 0.0005,
                    Description: "Target probability that RANSAC found the correct model; stops early once reached."),
                new("minInliers", "Min Inliers", "number", 4, 4, 100000, 1,
                    Description: "Reject the homography when fewer points survive. Four is the mathematical minimum and is almost never enough in practice; 8..12 is a sane floor."),
                new("warpImage", "Warp Image", "boolean", false,
                    Description: "Also resample the input image through the homography. Needs the image port connected; costs one full-frame warp."),
                new("outputWidth", "Output Width", "number", 0, 0, 16384, 1,
                    Description: "Canvas width for the warped image. 0 uses the input image width."),
                new("outputHeight", "Output Height", "number", 0, 0, 16384, 1,
                    Description: "Canvas height for the warped image. 0 uses the input image height.")
            ],
            "Fit a 3x3 projective transform from point correspondences, then expose it as a Transform2D plus a local decomposition (scale, rotation, skew, perspective terms) evaluated at the point set centroid. Use it for tilted cameras, skewed fixtures and any part whose four corners do not form a parallelogram - cases a rigid registration cannot express. A homography maps a PLANE to a plane: it will happily fit a non-planar scene and produce a wrong-but-low-residual answer, so keep the correspondences on one surface. Accuracy is limited by the point localisation, not by the solver: RANSAC with a 2 px threshold assumes sub-pixel corners. Fewer than 4 pairs, degenerate (collinear) configurations or fewer inliers than minInliers returns found=false with the reason in diagnostics.",
            "builtin", new VisionToolCapabilities(Deterministic: true)),

        new("match.pose3d", "PnP Pose", "Matching",
            [Exec, new("imagePoints", VisionDataType.PointList)],
            [
                new("pose", VisionDataType.Transform3D),
                new("x", VisionDataType.Double),
                new("y", VisionDataType.Double),
                new("z", VisionDataType.Double),
                new("rxDeg", VisionDataType.Double),
                new("ryDeg", VisionDataType.Double),
                new("rzDeg", VisionDataType.Double),
                new("rvecJson", VisionDataType.String),
                new("tvecJson", VisionDataType.String),
                new("reprojectionRms", VisionDataType.Double),
                new("maxError", VisionDataType.Double),
                new("pointCount", VisionDataType.Integer),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("objectPointsJson", "Object Points (JSON)", "text", "[[0,0,0],[100,0,0],[100,60,0],[0,60,0]]",
                    Description: "World coordinates of the correspondences, in the same order as imagePoints: [[x,y,z],...]. Keep them on one plane unless the solver is given depth variation; a planar target with a non-planar solver is the most common source of a mirrored pose."),
                new("cameraMatrixJson", "Camera Matrix (JSON)", "text", "[1000,0,640,0,1000,360,0,0,1]",
                    Description: "3x3 intrinsics in row-major order: [fx,0,cx, 0,fy,cy, 0,0,1]. Take it from calibration.cameraIntrinsics; a guessed focal length gives a pose that is self-consistent but metrically wrong."),
                new("distortionJson", "Distortion (JSON)", "text", "[]",
                    Description: "OpenCV distortion coefficients [k1,k2,p1,p2,k3,...]. Empty means undistorted input. If the image was already corrected, leave this empty instead of applying it twice."),
                new("solver", "Solver", "select", "iterative",
                    Options: [new("Iterative (Levenberg-Marquardt)", "iterative"), new("IPPE (planar, fast)", "ippe"), new("AP3P (minimal, 4 points)", "ap3p")],
                    Description: "IPPE exploits planarity and is the best choice for flat targets; iterative is the safe general default; AP3P is minimal but unstable on near-collinear points."),
                new("useExtrinsicGuess", "Use Extrinsic Guess", "boolean", false,
                    Description: "Seed the solver with rvec/tvec from the parameters below instead of a fresh guess. Only useful when tracking a target across frames; ignored by SQPnP."),
                new("guessRvecJson", "Guess rvec (JSON)", "text", "[0,0,0]",
                    Description: "Initial rotation vector for useExtrinsicGuess, in radians."),
                new("guessTvecJson", "Guess tvec (JSON)", "text", "[0,0,1000]",
                    Description: "Initial translation for useExtrinsicGuess, in the object unit."),
                new("unit", "Object Unit", "text", "mm",
                    Description: "Unit of objectPointsJson and of the reported x/y/z. Intrinsics are unit-agnostic, so the pose comes back in whatever unit the object points use."),
                new("objectFrame", "Object Frame", "text", "object",
                    Description: "Frame name stamped on the produced Transform3D source side."),
                new("cameraFrame", "Camera Frame", "text", "camera",
                    Description: "Frame name stamped on the produced Transform3D target side."),
                new("maxReprojectionRms", "Max Reprojection RMS (px)", "number", 0, 0, 1000, 0.1,
                    Description: "Quality gate: fail when the reprojection RMS exceeds this. 0 disables it. A clean planar target usually lands below 0.5 px; 2 px usually means a wrong correspondence order.")
            ],
            "Solve PnP: image points plus their known 3D coordinates give the object-to-camera SE(3), reported as a 4x4 Transform3D plus translation and ZYX Euler angles. This is the missing link between 2D detection and robot picking for anything that is not an ArUco board. Correspondence order is what defines the pose - the points are matched by index, not by geometry - so a shuffled object list yields a plausible but wrong pose; the reprojection RMS gate is the cheapest guard against it. At least 4 pairs are required, and 3 near-collinear points make the solution ambiguous. Failure returns found=false with the reason rather than a zero pose, because a zero pose silently commands the robot to the origin.",
            "builtin", new VisionToolCapabilities(Deterministic: true))
    ];
}
