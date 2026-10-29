namespace VisionStudio.Engine;

/// <summary>
/// 计量对齐与固定半径拟合（2026-10-28）。
///
/// 对照 ISO 1101 与成熟计量软件后确认的两个缺口：
/// 1) 最佳拟合对齐：<c>tolerance.profile</c> / <c>measure.contourDeviation</c> 都要求"已对齐的轮廓"，
///    但目录里没有任何节点负责把实测轮廓按最小二乘刚体对齐到理论轮廓。现场只能靠定位跟随
///    （<c>roi.transform</c>）或标定绕过，GD&T 结果直接差一个档。
/// 2) 固定半径拟合：销钉、探针、标准球、卡簧槽这类"半径已知、只求圆心"的场景，
///    <c>measure.fitCircle</c> 让半径自由浮动，弱边一多圆心就被拉偏。
///
/// 两个节点都是纯 managed 计算（不依赖 JLVision 原生对象），因为它们的价值在算法口径
/// （trimmed ICP、Kabsch 闭式解、Späth 迭代）而不是算子覆盖。
/// </summary>
public static class MeasurementAlignCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Diagnostics = new("diagnostics", VisionDataType.String);
    private static readonly PortDescriptor Found = new("found", VisionDataType.Boolean);

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("measure.bestFitAlign", "Best-Fit Alignment", "Measurement",
            [
                Exec,
                new("measured", VisionDataType.PointList),
                new("reference", VisionDataType.PointList)
            ],
            [
                new("transform", VisionDataType.Transform2D),
                new("alignedPoints", VisionDataType.PointList),
                new("rotationDeg", VisionDataType.Double),
                new("deltaX", VisionDataType.Double),
                new("deltaY", VisionDataType.Double),
                new("scale", VisionDataType.Double),
                new("rmsError", VisionDataType.Double),
                new("maxError", VisionDataType.Double),
                new("meanError", VisionDataType.Double),
                new("inlierCount", VisionDataType.Integer),
                new("pointCount", VisionDataType.Integer),
                new("iterations", VisionDataType.Integer),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("fitMode", "Fit Mode", "select", "rigid",
                    Options: [new("Rigid (rotation + translation)", "rigid"), new("Similarity (adds uniform scale)", "similarity")],
                    Group: "Fit", Description: "rigid is the metrology default: the part cannot grow or shrink, so scale must stay 1. similarity also solves a uniform scale, useful for thermal drift and film/print inspection, but it hides true size errors."),
                new("trimFraction", "Trim Fraction", "number", 0.1, 0, 0.45, 0.01,
                    Group: "Fit", Description: "Share of worst correspondences dropped each iteration (trimmed ICP). 0.1 tolerates burrs and partial occlusion; 0 disables trimming and is only safe on complete, clean contours."),
                new("maxIterations", "Max Iterations", "number", 40, 1, 200, 1,
                    Group: "Fit", Description: "ICP iteration budget. Convergence is usually reached in 5..15 iterations on industrial contours."),
                new("tolerancePx", "Convergence Tolerance (px)", "number", 0.0001, 1e-9, 10, 0.0001,
                    Group: "Fit", Description: "Stop when both the incremental rotation and the incremental translation fall below this value. Smaller than the measurement resolution is pointless."),
                new("maxPoints", "Point Budget", "number", 600, 60, 1500, 20,
                    Group: "Data", Description: "Both contours are thinned to at most this many points before matching. Cost grows with the square of this number: 600 keeps a 40-iteration fit in the tens of milliseconds."),
                new("initialRotationDeg", "Initial Rotation (deg)", "number", 0, -180, 180, 0.5,
                    Group: "Initial Alignment", Description: "Starting rotation applied before the first iteration. Needed when the part may be flipped or rotated far from the nominal, because ICP only converges to the nearest local minimum. Ignored when autoInitialRotation is on."),
                new("autoInitialRotation", "Auto Initial Rotation", "boolean", true,
                    Group: "Initial Alignment", Description: "Coarse-search a full turn in 30 deg steps before refining, and start the ICP from the best candidate. Costs one extra pass over a thinned point set and removes the most common failure: a valid part that aligns to the wrong pose because the caller did not know the angle."),
                new("closedReference", "Closed Reference", "boolean", true,
                    Group: "Data", Description: "Treat the reference polyline as a closed loop (last point connects back to the first). Turn off for open profiles such as a machined edge that does not wrap around.")
            ],
            "Rigid (or similarity) best-fit alignment of a measured contour onto a reference contour, the step every GD&T node silently assumes. Trimmed ICP matches each measured point to the nearest segment of the reference polyline, drops the worst trimFraction, and solves the optimal 2D transform in closed form (Kabsch); no SVD and no randomness, so the same input always gives the same transform. Emits the transform, the aligned point list and the residual statistics BEFORE alignment is discarded — feed alignedPoints to tolerance.profile / measure.contourDeviation and the residual becomes the form error. Failure modes are explicit: fewer than 3 points, degenerate (collinear) point sets and non-convergence all return found=false with the reason in diagnostics. Budget: measured/reference are thinned to maxPoints, so a 20k-point contour is fitted on its envelope, not on every pixel.",
            "builtin"),

        new("measure.fitCircleFixedRadius", "Fixed Radius Circle Fit", "Measurement",
            [Exec, new("points", VisionDataType.PointList)],
            [
                new("circle", VisionDataType.Circle),
                new("centerX", VisionDataType.Double),
                new("centerY", VisionDataType.Double),
                new("radius", VisionDataType.Double),
                new("rmsError", VisionDataType.Double),
                new("maxError", VisionDataType.Double),
                new("pointCount", VisionDataType.Integer),
                new("iterations", VisionDataType.Integer),
                Found,
                Diagnostics,
                Next
            ],
            [
                new("radius", "Known Radius", "number", 50, 0.001, 10000000, 0.001,
                    Description: "The radius that must not change (pin, probe tip, standard ball, circlip groove). Unlike measure.fitCircle this value is never re-estimated, so a weak arc cannot drag the centre."),
                new("maxRmsError", "Max RMS Error", "number", 0, 0, 1000000, 0.001,
                    Description: "Optional quality gate: fail when the RMS radial residual exceeds this value. 0 disables the gate. Set it to roughly the expected edge noise."),
                new("maxIterations", "Max Iterations", "number", 100, 1, 1000, 1,
                    Description: "Spath fixed-point iteration budget. Convergence is quadratic near the solution, so 100 is generous."),
                new("tolerancePx", "Convergence Tolerance (px)", "number", 1e-6, 1e-12, 1, 1e-7,
                    Description: "Stop when the centre moves less than this between iterations.")
            ],
            "Least-squares circle fit with a known, fixed radius: it solves for the centre only, so every input point is judged as 'too close' or 'too far' and the centre cannot drift to absorb a bad arc. This is the circle-fit counterpart of measure.mzCircle for hole/shaft limits: use it when the radius comes from the drawing or from a calibrated artefact. Points come from a PointList (contour.toPoints / feature outputs); fewer than 3 points or a degenerate arc returns found=false. Residuals are radial distances (measured minus known radius) in pixels; rmsError and maxError are reported in the same unit as the input.",
            "builtin")
    ];
}
