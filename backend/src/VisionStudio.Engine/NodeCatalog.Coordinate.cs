namespace VisionStudio.Engine;

public static partial class BuiltInNodeCatalog
{
    private static NodeCatalogItem[] CoordinateItems =>
    [
        new(
            "coordinate.transformPoint",
            "Transform Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.Point2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                ],
            "Map an image-space Point2D through a validated Transform2D into a frame/unit-aware point. Wire: point + transform → point + frame/unit/diagnostics. Rejects non-finite points and singular transforms. Typical after match/register.",
            "builtin"),
        new(
            "coordinate.transformPoint3D",
            "Transform 3D Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.CoordinatePoint3D), new("transform", VisionDataType.Transform3D)],
            [new("point", VisionDataType.CoordinatePoint3D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("z", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Transform a frame-aware 3D point through a rigid row-major Transform3D. Requires exact source-frame and translation-unit matches, validates the 4x4 rigid transform, and emits target coordinates plus frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformLine",
            "Transform Line",
            "Coordinate",
            [ExecIn, new("line", VisionDataType.Line2D), new("transform", VisionDataType.Transform2D)],
            [new("line", VisionDataType.CoordinateLine2D), new("length", VisionDataType.Measurement), new("lengthValue", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("minLength", "Min Length (0=off)", "number", 0, 0, 1e9, 0.001, Description: "Optional minimum transformed length gate; 0 disables."),
                ],
            "Transform both endpoints of a pixel Line2D into a coordinate-aware line. Validates line degeneracy and transform singularity; emits length, direction angle, frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformPose",
            "Transform Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.Pose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                ],
            "Transform X/Y and local orientation into a named target frame through a validated Transform2D. Rejects non-finite pose values and singular transforms; emits scalars, frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.point",
            "Coordinate Point",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("tree", VisionDataType.FrameTree2D, Required: false)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("x", "X", "number", 20, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Used when the X input is not connected.") { OverriddenByPorts = ["x"] },
                new("y", "Y", "number", 10, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Used when the Y input is not connected.") { OverriddenByPorts = ["y"] },
                new("frame", "Frame", "text", "Workpiece", Group: "Point", Description: "Must be non-empty. When a frame tree is connected, the name must exist on that tree. There is no global frame registry."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Point", Description: "Coordinate unit carried on the point, e.g. px or mm.")
            ,
                ],
            "Create a coordinate-aware reference point. Optional X/Y inputs override the constants so a match or detection can drive the point. Values must be finite. The frame must be non-empty; a connected frame tree checks it against known frames.",
            "builtin"),
        new(
            "coordinate.point3D",
            "Coordinate Point 3D",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("z", VisionDataType.Double, Required: false)],
            [new("point", VisionDataType.CoordinatePoint3D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("z", VisionDataType.Double),
             new("magnitude", VisionDataType.Double), new("xyMagnitude", VisionDataType.Double),
             new("azimuthDeg", VisionDataType.Double), new("elevationDeg", VisionDataType.Double),
             new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("x", "X", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "X in the named frame. Optional X input overrides this constant; must be finite within +/-1e9.") { OverriddenByPorts = ["x"] },
                new("y", "Y", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Y in the named frame. Optional Y input overrides this constant; must be finite within +/-1e9.") { OverriddenByPorts = ["y"] },
                new("z", "Z", "number", 0, -1000000, 1000000, 0.001, Unit: "target", Group: "Point", Description: "Z in the named frame. Optional Z input overrides this constant; must be finite within +/-1e9.") { OverriddenByPorts = ["z"] },
                new("frame", "Frame", "text", "Workpiece", Group: "Point", Description: "Non-empty frame name stamped on the CoordinatePoint3D. There is no global registry; downstream transforms must match this name."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Point", Description: "Length unit carried with the point. Downstream transforms must use the same unit.")
            ,
                ],
            "Create a frame-aware 3D coordinate point. Optional X/Y/Z inputs override constants. Emits magnitude, XY magnitude, azimuth and elevation (deg) plus JSON diagnostics. Coordinates must be finite within +/-1e9; frame must be non-empty; unit is mm/cm/m.",
            "builtin"),
        new(
            "coordinate.distance",
            "Coordinate Distance",
            "Coordinate",
            [ExecIn, new("pointA", VisionDataType.CoordinatePoint2D), new("pointB", VisionDataType.CoordinatePoint2D)],
            [new("measurement", VisionDataType.Measurement), new("distance", VisionDataType.Double), new("dx", VisionDataType.Double), new("dy", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [

                new("metric", "Distance Metric", "select", "euclidean", Options: [new("Euclidean", "euclidean"), new("Manhattan", "manhattan")], Description: "Distance metric between the two coordinate points; both keep signed dx/dy."),
                ],
            "Distance between two frame-aware points, only when frame and unit match exactly. Emits a Measurement, scalar distance, signed dx/dy and JSON diagnostics; mismatched or non-finite points fail deterministically.",
            "builtin"),
        new(
            "coordinate.transformConstant",
            "Constant Transform",
            "Coordinate",
            [ExecIn],
            [new("transform", VisionDataType.Transform2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("angleDeg", VisionDataType.Double), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("sourceFrame", "Source Frame", "text", "Workpiece", Group: "Frames", Description: "Frame the transform maps from; must be non-empty."),
                new("targetFrame", "Target Frame", "text", "RobotBase", Group: "Frames", Description: "Frame the transform maps to; must be non-empty."),
                new("unit", "Unit", "select", "mm", Options:
                [
                    new("Pixels (px)", "px"), new("mm", "mm"), new("cm", "cm"), new("m", "m")
                ], Group: "Frames", Description: "Use px for image-space transforms used by image.warpAffine or caliper measurement; use mm/cm/m for engineering coordinates."),
                new("x", "X Translation", "number", 0, -1000000, 1000000, 0.001, Unit: "unit", Group: "Rigid Transform", Description: "Finite translation along the target frame X axis."),
                new("y", "Y Translation", "number", 0, -1000000, 1000000, 0.001, Unit: "unit", Group: "Rigid Transform", Description: "Finite translation along the target frame Y axis."),
                new("angleDeg", "Rotation", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Rigid Transform", Description: "Finite counter-clockwise rotation in degrees.")
            ,
                ],
            "Create a rigid 2D transform between named engineering or pixel coordinate frames with strict finite-value, frame and unit validation. Emits translation/rotation scalars, frame names, unit and JSON diagnostics (including determinant).",
            "builtin"),
        new(
            "coordinate.transformInverse",
            "Inverse Transform",
            "Coordinate",
            [ExecIn, new("transform", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("inputDeterminant", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [

                ],
            "Invert a Transform2D and swap source/target frame and units. Validates every matrix element and rejects singular transforms deterministically; emits frame names, determinant and JSON diagnostics.",
            "builtin", TransformFrames: new(NodeTransformFramePolicy.Invert, PrimaryPort: "transform")),
        new(
            "coordinate.transformCompose",
            "Compose Transform",
            "Coordinate",
            [ExecIn, new("transformA", VisionDataType.Transform2D), new("transformB", VisionDataType.Transform2D)],
            [new("transform", VisionDataType.Transform2D), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [

                new("order", "Compose Order", "select", "outerInner", Options: [new("Outer*Inner", "outerInner"), new("Inner*Outer", "innerOuter")], Description: "Matrix multiply order when composing transforms."),
                ],
            "Compose A:S->M with B:M->T to produce S->T, enforcing frame/unit compatibility and rejecting singular or non-finite transforms. order=outerInner (default) runs Compose(A,B) and requires A.target == B.source; order=innerOuter runs Compose(B,A) and requires B.target == A.source. For image geometry with the default order, compose roi.transform (ReferenceImage->CurrentImage) as A with image.crop (CurrentImage->CropImage) as B. Emits frames, determinant and JSON diagnostics.",
            "builtin", TransformFrames: new(NodeTransformFramePolicy.Compose, PrimaryPort: "transformA", SecondaryPort: "transformB")),
        new(
            "coordinate.frameTree",
            "Frame Tree",
            "Coordinate",
            [ExecIn],
            [new("tree", VisionDataType.FrameTree2D), new("transformCount", VisionDataType.Integer), new("frameCount", VisionDataType.Integer), new("frames", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("transformsJson", "Transforms JSON", "textarea", "[]",
                    Group: "Frame Tree",
                    Description: "Static rigid transforms. Runtime dynamic transforms can still be composed as normal workflow data.")
            ,

                ],
            "Build a bidirectional frame graph from validated rigid transforms and resolve transforms by named frame path. Rejects empty, duplicate or non-finite edges deterministically; emits transform/frame counts, the frame list and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.frameResolve",
            "Resolve Frame Transform",
            "Coordinate",
            [ExecIn, new("tree", VisionDataType.FrameTree2D)],
            [new("transform", VisionDataType.Transform2D), new("path", VisionDataType.String), new("hopCount", VisionDataType.Integer), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("sourceFrame", "Source Frame", "text", "Workpiece", Group: "Resolve", Description: "Frame to resolve from; must exist in the frame tree."),
                new("targetFrame", "Target Frame", "text", "RobotBase", Group: "Resolve", Description: "Frame to resolve to; an unreachable target fails deterministically.")
            ,

                ],
            "Resolve the shortest transform path in a FrameTree2D, automatically using inverse edges where required. Validates frame names and the resolved transform; emits hop count, frame names, path and JSON diagnostics, and fails when no path exists.",
            "builtin"),
        new(
            "coordinate.pose",
            "Coordinate Pose",
            "Coordinate",
            [ExecIn, new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("thetaDeg", VisionDataType.Double, Required: false), new("tree", VisionDataType.FrameTree2D, Required: false)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [
                new("x", "X", "number", 500, -1000000, 1000000, 0.001, Group: "Pose", Description: "Used when the X input is not connected.") { OverriddenByPorts = ["x"] },
                new("y", "Y", "number", 200, -1000000, 1000000, 0.001, Group: "Pose", Description: "Used when the Y input is not connected.") { OverriddenByPorts = ["y"] },
                new("thetaDeg", "Theta", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pose", Description: "Used when the theta input is not connected. The output angle is normalized to (-180, 180].") { OverriddenByPorts = ["thetaDeg"] },
                new("frame", "Frame", "text", "RobotBase", Group: "Pose", Description: "Must be non-empty. When a frame tree is connected, the name must exist on that tree. There is no global frame registry."),
                new("unit", "Unit", "select", "mm", Options: [new("mm", "mm"), new("cm", "cm"), new("m", "m")], Group: "Pose", Description: "Length unit stamped on the pose; it does not convert the numeric X/Y values.")
            ,
                ],
            "Create a frame-aware 2D pose. Optional X/Y/theta inputs override the constants so a match result can drive the pose. Values must be finite and theta is normalized to (-180, 180]. The frame must be non-empty; a connected frame tree checks it against known frames.",
            "builtin"),
        new(
            "coordinate.poseToTransform",
            "Pose to Transform",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D)],
            [new("transform", VisionDataType.Transform2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("sourceFrame", VisionDataType.String), new("targetFrame", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [new("sourceFrame", "Source Frame", "text", "Tool", Group: "Frames", Description: "Child/tool frame named by the new transform; must differ from the pose frame."),

                ],
            "Interpret a pose expressed in its parent frame as a rigid transform from Source Frame to that parent frame, rejecting a source frame equal to the pose frame. Useful for dynamic Tool->RobotBase transforms. Emits scalars, frames, determinant and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePoint",
            "Transform Coordinate Point",
            "Coordinate",
            [ExecIn, new("point", VisionDataType.CoordinatePoint2D), new("transform", VisionDataType.Transform2D)],
            [new("point", VisionDataType.CoordinatePoint2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("sourceFrame", VisionDataType.String), new("determinant", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [
                ],
            "Transform a frame-aware point through another engineering-frame Transform2D with strict frame/unit, finite-value and singularity validation. Emits the point plus scalar x/y, target frame, unit and JSON diagnostics.",
            "builtin"),
        new(
            "coordinate.transformCoordinatePose",
            "Transform Coordinate Pose",
            "Coordinate",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D), new("transform", VisionDataType.Transform2D)],
            [new("pose", VisionDataType.CoordinatePose2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("thetaDeg", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [],
            "Transform a frame-aware pose through another engineering-frame Transform2D with strict frame/unit, finite-value and singularity validation. Emits the pose plus scalar x/y/theta, target frame, unit and JSON diagnostics.",
            "builtin"),
    ];
}
