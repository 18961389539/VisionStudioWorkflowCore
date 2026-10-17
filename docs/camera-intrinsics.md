# Camera Intrinsic Calibration

`calibration.cameraIntrinsics` solves the current JLVision `area_scan_polynomial` camera model from at least three varied calibration views. Each view contains corresponding 3D object points and 2D image points. The guided calibration wizard collects and filters views, previews board detections, and applies the same node solve.

## Quality gates and outputs

- `maxViewRms` checks each planar view's homography fit before the camera solve. It is useful for finding bad corner detections, but does not test the camera model.
- `maxPoseViewRms` checks a fitted-K pinhole reprojection for planar views. This check omits JLVision's native polynomial distortion. The node rejects this gate when fitted native polynomial distortion is active, and suppresses the pinhole-derived outlier flag in that case.
- `maxRms` checks the final JLVision camera-model RMS and remains the overall acceptance gate.

All three limits default to `0` (disabled) and use pixels. The wizard exposes the same gates; when applied to a camera-intrinsics node, it transfers the view data and limits. The node reports `viewStats`, worst view errors and indexes, outlier view index when the pinhole metric is applicable, and mean image coverage across views.

The input is bounded to 500 views, 5000 correspondences per view, and 100000 correspondences total. Image dimensions must be between 16 and 32768 pixels. Object coordinates must remain representable after the node's millimetre-to-metre conversion.

## Versioned model assets

After solving in the Intrinsics Wizard, create an immutable SQLite-backed asset version. Each version stores the validated camera model, the full wizard document with accepted/rejected source views, solve summary, content hashes, and publication history. Versions can be loaded, published, and rolled back from the wizard.

Applying a saved version to `image.undistort` writes the model JSON and its asset id/version into the workflow. Workflow execution, job publication, and production dependency checks verify that the embedded model matches the exact stored version; the dependency manifest records its snapshot hash. A pinned asset cannot be combined with a connected `calibrationJson` input because the upstream model is resolved dynamically. Changing the model or selecting another version requires reapplying it from the wizard.

## Current model limits

- Only the native polynomial pinhole model is available. Fisheye and telecentric models are not supported.
- The reported per-view pinhole pose residual does not include the native polynomial distortion coefficients. Use `maxRms` for acceptance when distortion is active.
- Intrinsics scale to images with a different resolution during undistortion. Measurements should be created in the undistorted image's coordinate space because the node does not emit a pixel-coordinate transform.
