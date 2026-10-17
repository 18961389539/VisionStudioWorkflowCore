# Planar Calibration

`calibration.planar` maps image coordinates into a named planar target frame. Supply at least four `{ imageX, imageY, worldX, worldY }` pairs in `pairsJson`; nine or more points spread across the working area are recommended so the fit can expose local errors instead of only reproducing four corners.

## Fit quality

The solver normalizes image and target coordinates before fitting and uses a rank-revealing least-squares QR solve. This reduces sensitivity to large coordinate offsets and scale differences, and rejects point layouts that do not constrain a full projective transform.

The node reports `rmse`, `maxError`, the zero-based `worstPairIndex`, and `residualsJson` with signed X/Y errors and one total error per calibration pair. The residual list follows the input pair order. Optional `maxRmse` and `maxPointError` parameters fail the node when the fitted errors exceed their limits; both default to `0` (disabled), and their units are the selected target unit. The calibration workspace exposes the same limits and rejects a solve that exceeds them before the result can be saved or applied. Use both gates in production workflows to stop a poor calibration from silently feeding coordinate transforms.

`targetUnit` supports `mm`, `cm`, `m`, and `px`. `sourceFrame` and `targetFrame` identify the coordinate systems carried by the resulting `Transform2D`; they do not convert units by themselves.

## Limits

- This is a planar projective calibration. It does not model lens distortion or non-planar work surfaces; calibrate camera intrinsics and undistort images before fitting when lens distortion matters.
- The pair fit is ordinary least squares. A wrong point increases the residuals and can trip the quality gates, but the node does not automatically identify and discard outliers.
- `assetId` and `assetVersion` pin an immutable calibration version for provenance. The node solves from `pairsJson` stored in the workflow definition; when a version is pinned, dependency capture verifies the workflow's frames, units, quality gates, and enabled point pairs match that version. Calibration asset reads recompute the snapshot SHA-256, and publishing rejects content that no longer matches its recorded hash. Re-apply the chosen version after changing its calibration points.
