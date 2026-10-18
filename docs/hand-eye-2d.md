# 2D Hand-Eye Calibration

`calibration.handEye2D` solves a planar SE(2) transform from robot and vision pose pairs. Use `eyeInHand` when the camera is mounted on the robot tool, and `eyeToHand` when the camera is fixed. Provide at least four distinct robot poses; the node accepts up to 1000 pairs.

## Motion coverage

The solver requires both robot and vision poses to cover at least 8 degrees of rotation and 0.001 coordinate units of translation. Angular coverage is measured as the smallest circular arc containing the headings, so samples near +180 and -180 degrees are treated as neighboring angles. The result reports coverage and translation span for both sample sets. Repeated robot poses fail with the repeated pair indexes.

## Fit balance and acceptance

The solver minimizes translation and rotation closure together. `angularWeight` converts radians into translation units for that objective; it defaults to 50 to preserve existing behavior. Increase it when angular consistency should have more influence relative to translation, or reduce it when translation consistency should dominate. It must be between 0.001 and 1,000,000 translation units per radian.

`maxTranslationError` and `maxAngleErrorDeg` optionally reject any sample whose residual exceeds its limit. Errors identify the worst sample for the corresponding metric, and typed outputs expose both indexes. `worstSampleIndex` reports the largest combined residual under the configured angular weight.

The output transform carries validated source/target frames and units. Pose coordinates must be finite and within ±1e9. This node does not solve 3D hand-eye calibration or camera intrinsics.
