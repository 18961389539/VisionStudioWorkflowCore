# Planar Calibration

`calibration.planar` maps image coordinates into a named planar target frame. Supply at least four `{ imageX, imageY, worldX, worldY }` pairs in `pairsJson`; nine or more points spread across the working area are recommended so the fit can expose local errors instead of only reproducing four corners.

## Fit quality

The solver normalizes image and target coordinates before fitting and uses a rank-revealing least-squares QR solve. This reduces sensitivity to large coordinate offsets and scale differences, and rejects point layouts that do not constrain a full projective transform.

The node reports `rmse`, `maxError`, the zero-based `worstPairIndex`, and `residualsJson` with signed X/Y errors and one total error per calibration pair. The residual list follows the input pair order. Optional `maxRmse` and `maxPointError` parameters fail the node when the fitted errors exceed their limits; both default to `0` (disabled), and their units are the selected target unit. The calibration workspace exposes the same limits and rejects a solve that exceeds them before the result can be saved or applied. Use both gates in production workflows to stop a poor calibration from silently feeding coordinate transforms.

`targetUnit` supports `mm`, `cm`, `m`, and `px`. `sourceFrame` and `targetFrame` identify the coordinate systems carried by the resulting `Transform2D`; they do not convert units by themselves.

## Limits

- This is a planar projective calibration. It does not model lens distortion or non-planar work surfaces; calibrate camera intrinsics and undistort images before fitting when lens distortion matters.
- The pair fit is ordinary least squares. A wrong point increases the residuals and can trip the quality gates, but the node does not automatically identify and discard outliers.
- `assetId` and `assetVersion` are provenance fields. The node uses the `pairsJson` stored in the workflow definition for its solve. Calibration asset versions store an immutable workspace/result snapshot; loading or publishing a version recomputes its SHA-256 and rejects content that no longer matches the recorded hash. Production dependency capture reads the same version and therefore also rejects a damaged snapshot. The workflow still carries its own point pairs, so re-apply the chosen asset version after changing calibration points.
