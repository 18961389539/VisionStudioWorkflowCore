# Camera Intrinsic Calibration

`calibration.cameraIntrinsics` supports three lens models from at least three varied calibration views. Each view contains corresponding 3D object points and 2D image points. The guided calibration wizard collects and filters views, previews board detections, selects the lens model, and applies the same node solve.

- `pinhole` uses JLVision's native `area_scan_polynomial` model, including its native distortion representation.
- `fisheye` uses OpenCV's four-coefficient Kannala-Brandt angular model. Fitted per-view RMS is computed by projecting each board view through its solved pose and fisheye model.
- `telecentric` supports standard object-side area-scan telecentric lenses through JLVision's native `area_scan_telecentric_polynomial` model. The native camera tuple is retained in the model JSON and used by undistortion. Provide lens magnification and sensor pixel pitch from the equipment datasheets; magnification is an initial value, while pixel pitch stays fixed during calibration.

## Quality gates and outputs

- `maxViewRms` checks the planar homography pre-fit for pinhole and telecentric views. For fisheye views, it checks per-view reprojection through the fitted fisheye model.
- `maxPoseViewRms` checks per-view reprojection through the fitted camera model when supported. It is unavailable for telecentric projection and when native JLVision polynomial distortion is active because the pinhole residual calculator cannot score those models accurately.
- `maxRms` checks the final RMS from the selected camera model and remains the overall acceptance gate.

All three limits default to `0` (disabled) and use pixels. The wizard exposes the same gates; when applied to a camera-intrinsics node, it transfers the view data, selected lens model, and limits. The node reports `viewStats`, worst view errors and indexes, an outlier view index when the fitted-model metric is available, and mean image coverage across views.

The input is bounded to 500 views, 5000 correspondences per view, and 100000 correspondences total. Image dimensions must be between 16 and 32768 pixels. Object coordinates must remain representable after the node's millimetre-to-metre conversion.

## Versioned model assets

After solving in the Intrinsics Wizard, create an immutable SQLite-backed asset version. Each version stores the validated camera model, the full wizard document with accepted/rejected source views, solve summary, content hashes, and publication history. Versions can be loaded, published, and rolled back from the wizard.

Applying a saved version to `image.undistort` writes the model JSON and its asset id/version into the workflow. Workflow execution, job publication, and production dependency checks verify that the embedded model matches the exact stored version; the dependency manifest records its snapshot hash. A pinned asset cannot be combined with a connected `calibrationJson` input because the upstream model is resolved dynamically. Changing the model or selecting another version requires reapplying it from the wizard.

For fisheye models, `image.undistort.fisheyeBalance` controls the output projection's field of view from 0 (tighter crop) to 1 (wider field with more possible black borders). Pinhole, telecentric, and Brown-Conrady models require this setting to remain 0.

## Current model limits

- Telecentric support covers object-side area-scan polynomial models. Tilted, bilateral, or non-central telecentric variants are not modeled by this camera type. The projection does not determine metric depth from a single planar calibration board.
- The reported per-view pose residual is unavailable for telecentric projection and active JLVision native polynomial distortion. Use `maxRms` or the planar `maxViewRms` pre-fit gate for those views.
- Fisheye uses OpenCV's central four-coefficient angular model; non-central fisheye optics and vendor-specific fisheye models are not imported automatically.
- Intrinsics scale to images with a different resolution during undistortion. Measurements should be created in the undistorted image's coordinate space because the node does not emit a pixel-coordinate transform.
