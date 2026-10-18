# Calibration Wizard (Batch E) — node audit notes
# Asia/Shanghai

> Historical Batch E snapshot. Current wizard capabilities also include persisted sessions, fisheye calibration, and object-side telecentric calibration; see [camera-intrinsics.md](camera-intrinsics.md) and the follow-up notes in `node-completion-audit.md`.

## Shipped
- Guided **camera intrinsics wizard** (HALCON Calibration Assistant–style slice, not a full product):
  - Engine: `CalibrationViewQuality` (Laplacian blur, coverage, corner completeness → Pass/Warn/Fail + guidance)
  - Engine: `CalibrationIntrinsicsWizard` in-memory session (accumulate / enable-disable / remove / build request)
  - API: `/api/calibration/wizard/sessions*` (create, add view JSON, add view from image via detectBoard, enable/disable, remove, calibrate, export)
  - UI: Devices → Calibration → tab **相机内参向导** (`CalibrationIntrinsicsWizard.tsx`)
- `calibration.detectBoard`: outputs blurVariance / coverage / quality; optional minBlurVariance, minCoverage, rejectWeakViews
- `calibration.handEye2D`: per-sample residuals JSON, maxTranslationError / maxAngleErrorDeg / worstSampleIndex; optional residual gates; rejects duplicate robot poses
- Catalog regen + zh i18n for new params/ports

## Honest limits vs HALCON Calibration Assistant
- No live camera preview / robot-driven pose planner / auto multi-pose choreography
- No fisheye / telecentric / multi-camera bundle
- Wizard sessions are in-memory (lost on process restart); not durable assets like the 9-point SQLite store
- Board detect still requires a complete isolated lattice (same as detectBoard)

## Scores (expected direction; update when full audit script re-run)
- calibration.cameraIntrinsics: already ~71–72 after Batch D; wizard + quality gates should feel closer to guided calib UX (score bump if audit weights UX/gates)
- calibration.handEye2D: residual reporting + pair validation should lift from ~48–60 toward mid/high 60s if audit credits diagnostics
- calibration.detectBoard: quality outputs improve operator feedback

## Instance segmentation (side question)
- Repo has `inference.segmentation` = **ONNX semantic segmentation** only (`[1,C,H,W]` class mask). **No instance-segmentation node.**
- YOLO appears only on `inference.detection` (layouts `yolo` / `yolov5` / `xyxy`). Not used for segmentation. No Mask R-CNN / YOLO-seg instance head.

## Tests (Windows)
- Engine: **768/768** passed after Batch E
