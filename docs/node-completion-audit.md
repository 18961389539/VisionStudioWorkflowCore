# VisionStudioWorkflowCore - Node Completion Audit (re-run)

> Baseline snapshot: the scores and limitation statements below predate the follow-up work recorded at the end of this file. They are not current scores for nodes that have since changed.

- Date: 2026-10-18 (Asia/Shanghai)
- Repo: D:\SourceCode\Workflow, branch `local/halcon-cognex-node-audit`, HEAD `5b816eb` (Batch F, local, ahead of remote)
- Catalog source: `frontend/src/generated/catalog.generated.json` - **142 nodes**
- Scope: static audit only; no business code changed for this report.

## Methodology

Same 4-dimension weighted method as the previous HALCON/Cognex audit, relative to a mature HALCON/Cognex implementation of the same node (not absolute perfection):

| Dimension | Weight | Evidence checked (script + manual spot-check) |
|---|---|---|
| Functional depth | 40% | parameter count, select-option count, non-control outputs/inputs, executor + delegated helper LOC (e.g. ManagedShapeMatch, InferenceStats, JLVision*), engine/layout options in catalog |
| Robustness | 25% | throw/Require/Validate/TryGet/Clamp/catch evidence in executor, delegated LOC, empty/null handling |
| Usability / debug | 20% | image/region/contour debug outputs, description length, parameter descriptions, zh i18n name in `frontend/src/i18n/catalog.ts`, specialised frontend panels/editors (frontend hits) |
| Docs / tests | 15% | backend test hits for executor class / node type / delegated helper classes, sample-workflow usage, zh i18n, description |

Script: automatic rough score per node from the evidence above, then manual calibration on inspected anchors (see below). Narrow-scope nodes (coordinate/flow/value transforms) get a functional floor - a correct point transform is complete for its scope - so their low scores reflect thin test/doc/debug evidence, not broken math. Complex product nodes are capped where a known product feature is absent (e.g. OCR font training, fisheye/telecentric, instance-seg e2e/NMS).

Manual anchor calibrations applied:
- `match.shape` -> **85**: 41 params/18 outs, native+managedChamfer+auto engines, ManagedShapeMatch+JLVisionMatching delegated code not captured by executor LOC; prior audit anchor 85
- `calibration.cameraIntrinsics` -> **72**: node alone 69 by script; +wizard UX (Batch E/F: quality gates, session, export) raises usability; no fisheye/telecentric so capped low-70s
- Caps: `recognition.ocr`<=74, `defect.referenceCompare`<=74, `inference.anomaly`<=74, `inference.classification`<=74, `calibration.cameraIntrinsics/handEye2D`<=76, `inference.segmentation`<=76, `inference.instanceSegmentation/pose`<=78, `image.acquire`<=76, all others <=88.

Limitations: (1) test-hit counting is textual - delegated helpers (VisionImage/InferenceStats) inflate hits, so Docs/Tests is capped in the formula and hits are evidence, not coverage %. (2) No fresh test run and no runtime/HALCON side-by-side benchmark in this audit; last reported Windows Engine run was 780/780 after Batch F. (3) Wizard/device UI outside the catalog (calibration wizard, camera panels) only credited via the manual anchor, not per-node script points. (4) Scores are comparable within this audit and to the cited previous anchors, not to external products' marketing feature lists.

## Summary

- **Total: 142 nodes; median 64; mean 65.6; min 55; max 88**

| Band | Count |
|---|---|
| 90+ | 0 |
| 80-89 | 6 |
| 70-79 | 27 |
| 60-69 | 82 |
| <60 | 27 |

### By catalog category

| Category | n | Median | Mean | Range |
|---|---|---|---|---|
| Inference | 6 | 76 | 74.5 | 66-78 |
| Feature | 4 | 75 | 74.0 | 70-76 |
| Calibration | 4 | 72 | 72.2 | 63-81 |
| Matching | 3 | 72 | 72.7 | 61-85 |
| Value | 3 | 69 | 68.7 | 67-70 |
| Logic | 1 | 69 | 69.0 | 69-69 |
| Acquisition | 4 | 68 | 68.8 | 63-75 |
| Contour | 4 | 67 | 67.5 | 63-73 |
| Flow | 11 | 66 | 66.3 | 59-71 |
| Defect | 5 | 66 | 66.6 | 60-74 |
| Region | 16 | 65 | 66.3 | 62-73 |
| Preprocess | 22 | 64 | 63.8 | 56-77 |
| Recognition | 5 | 64 | 66.0 | 62-74 |
| Device | 4 | 64 | 64.5 | 64-66 |
| Measurement | 26 | 63 | 65.8 | 56-88 |
| Output | 3 | 63 | 62.3 | 61-63 |
| Robot | 4 | 62 | 62.8 | 58-69 |
| Geometry | 3 | 59 | 59.0 | 58-60 |
| Coordinate | 14 | 58 | 57.7 | 55-65 |

### By requested bucket

| Bucket | n | Median | Mean | Range |
|---|---|---|---|---|
| Region/Contour (Region+Contour) | 20 | 65 | 66.5 | 62-73 |
| Inference | 6 | 76 | 74.5 | 66-78 |
| Calibration | 4 | 72 | 72.2 | 63-81 |
| Matching | 3 | 72 | 72.7 | 61-85 |
| Feature/Measure/Geometry | 33 | 63 | 66.2 | 56-88 |
| Image (Preprocess+Acquisition) | 26 | 64 | 64.6 | 56-77 |
| Flow/Logic/Value/Collection | 15 | 67 | 66.9 | 59-71 |
| Coordinate | 14 | 58 | 57.7 | 55-65 |
| Device/Robot/Output | 11 | 64 | 63.3 | 58-69 |
| Recognition/Defect | 10 | 65 | 66.3 | 60-74 |

Note: `collection.detections.*` are catalogued under Measurement, so they sit in Feature/Measure above, not Flow/Logic/Value.

## Highest nodes

| # | Node | Category | Params | Outs | F/R/U/D | Score |
|---|---|---|---|---|---|---|
| 1 | `measure.caliperCircle` Caliper Circle | Measurement | 19 | 11 | 90/93/80/90 | **88** |
| 2 | `match.shape` Shape Match | Matching | 41 | 18 | 88/80/85/90 | **85** |
| 3 | `roi.transform` ROI Transform | Measurement | 7 | 7 | 76/93/78/90 | **83** |
| 4 | `calibration.detectBoard` Detect Calibration Board | Calibration | 14 | 9 | 83/89/65/80 | **81** |
| 5 | `measure.blobs` Multi Blob | Measurement | 12 | 9 | 82/70/80/90 | **80** |
| 6 | `measure.caliperLine` Caliper Line | Measurement | 15 | 9 | 87/71/79/80 | **80** |
| 7 | `inference.instanceSegmentation` ONNX Instance Segmentation (YOLO-Seg) | Inference | 18 | 6 | 82/81/75/80 | **78** |
| 8 | `inference.pose` ONNX Pose (YOLO-Pose) | Inference | 20 | 5 | 84/89/71/80 | **78** |
| 9 | `measure.caliper` Caliper Edge | Measurement | 9 | 7 | 85/63/74/90 | **78** |
| 10 | `image.crop` Crop Image | Preprocess | 0 | 5 | 61/92/79/90 | **77** |
| 11 | `feature.line` Line Finder | Feature | 8 | 9 | 72/77/72/90 | **76** |
| 12 | `inference.segmentation` ONNX Semantic Segmentation | Inference | 14 | 8 | 82/84/77/80 | **76** |
| 13 | `feature.circle` Circle Finder | Feature | 8 | 10 | 72/73/73/90 | **75** |
| 14 | `feature.edgeSubpixel` Subpixel Edge | Feature | 9 | 7 | 83/62/74/80 | **75** |
| 15 | `image.acquire` Acquire Image | Acquisition | 7 | 6 | 71/74/73/90 | **75** |

## Lowest 15 nodes

| # | Node | Category | Params | Outs | F/R/U/D | Score | Why low (evidence) |
|---|---|---|---|---|---|---|---|
| 1 | `coordinate.point` Coordinate Point | Coordinate | 4 | 1 | 68/40/51/50 | **55** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 2 | `coordinate.pose` Coordinate Pose | Coordinate | 5 | 1 | 68/40/50/49 | **55** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 3 | `coordinate.transformConstant` Constant Transform | Coordinate | 6 | 1 | 57/40/62/66 | **55** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 4 | `coordinate.transformInverse` Inverse Transform | Coordinate | 0 | 1 | 68/52/51/40 | **56** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 5 | `coordinate.transformLine` Transform Line | Coordinate | 0 | 3 | 68/52/51/40 | **56** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 6 | `image.blur.median` Median Blur | Preprocess | 1 | 1 | 47/52/63/80 | **56** | few params/outputs and/or weak direct test/debug evidence for its scope |
| 7 | `measure.circleSpacing` Circle Spacing | Measurement | 0 | 3 | 49/57/56/76 | **56** | few params/outputs and/or weak direct test/debug evidence for its scope |
| 8 | `measure.rotatedRect` Rotated Rectangle | Measurement | 1 | 5 | 56/57/59/50 | **56** | few params/outputs and/or weak direct test/debug evidence for its scope |
| 9 | `coordinate.transformCoordinatePoint` Transform Coordinate Point | Coordinate | 0 | 1 | 68/52/56/40 | **57** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 10 | `coordinate.distance` Coordinate Distance | Coordinate | 0 | 2 | 68/52/51/50 | **58** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 11 | `coordinate.frameTree` Frame Tree | Coordinate | 1 | 1 | 68/40/67/50 | **58** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 12 | `coordinate.transformPoint` Transform Point | Coordinate | 0 | 3 | 68/52/51/50 | **58** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 13 | `coordinate.transformPose` Transform Pose | Coordinate | 0 | 1 | 68/52/52/50 | **58** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 14 | `geometry.intersection` Line Intersection | Geometry | 0 | 1 | 68/52/52/50 | **58** | narrow wrapper: few/no params, 1 output, little/no direct test-hit or debug output (scope-complete, evidence-thin) |
| 15 | `robot.guidance2d` Robot Guidance 2D | Robot | 6 | 4 | 64/52/58/50 | **58** | few params/outputs and/or weak direct test/debug evidence for its scope |

## Change vs previous known anchors

| Node | Previous | Now | Note |
|---|---|---|---|
| `recognition.ocr` | 28 -> 68/71 | **74 (capped)** | char boxes, blacklist, PSM options (Batch C/D); still no font training, so capped |
| `defect.referenceCompare` | 32 -> 70 | **74 (capped)** | variation-model/alignment work (Batch C); script raw was higher, capped for missing full variation-model product depth |
| `feature.line` | 35 -> 75 | **76** | consistent with prior re-score |
| `match.shape` | 85 | **85** | anchor-calibrated (see methodology); native+managed chamfer+auto |
| `inference.segmentation` | 63 -> 72 | **76 (capped)** | YOLO26-semantic layout + overlay; semantic only |
| `inference.anomaly` | ~72 | **74 (capped)** | Otsu mode / threshold suggestion; no normal-sample training (and per user, no training nodes planned) |
| `inference.classification` | - | **66** | YOLO26 layout added, but thin direct test hits and single-image scope keep it below detection/segmentation - lowest inference node |
| `inference.detection` | raised | **75** | xyxy/yolo/yolov5 + YOLO26 e2e via xyxy |
| `inference.instanceSegmentation` | did not exist | **78** | new YOLO-Seg raw dual-head; no e2e/NMS model support |
| `inference.pose` | did not exist | **78** | new YOLO-Pose yolo/xyxy; no OBB |
| `calibration.cameraIntrinsics` | ~71-72 | **72** | wizard anchor; pinhole only |
| `calibration.handEye2D` | ~48 -> mid/high-60s expected | **73** | per-sample residuals, gates, hand-eye wizard (Batch E/F) |
| `calibration.detectBoard` | - | **81** | blur/coverage/quality outputs feed the wizard |
| `image.difference` | 62 -> 72 | **71** | region/SSIM modes (Batch D); essentially flat vs Batch D re-score |

Only anchors actually cited in earlier batches are compared above; a full previous 140-node table no longer exists in the repo (the old audit file was overwritten by Batch E wizard notes), so no other deltas are invented.

## Full table (all nodes, score descending)

| # | Node | Category | Params | Opts | Outs | F | R | U | D | Score |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `measure.caliperCircle` Caliper Circle | Measurement | 19 | 8 | 11 | 90 | 93 | 80 | 90 | **88** |
| 2 | `match.shape` Shape Match | Matching | 41 | 10 | 18 | 88 | 80 | 85 | 90 | **85** |
| 3 | `roi.transform` ROI Transform | Measurement | 7 | 0 | 7 | 76 | 93 | 78 | 90 | **83** |
| 4 | `calibration.detectBoard` Detect Calibration Board | Calibration | 14 | 2 | 9 | 83 | 89 | 65 | 80 | **81** |
| 5 | `measure.blobs` Multi Blob | Measurement | 12 | 6 | 9 | 82 | 70 | 80 | 90 | **80** |
| 6 | `measure.caliperLine` Caliper Line | Measurement | 15 | 8 | 9 | 87 | 71 | 79 | 80 | **80** |
| 7 | `inference.instanceSegmentation` ONNX Instance Segmentation (YOLO-Seg) | Inference | 18 | 3 | 6 | 82 | 81 | 75 | 80 | **78** |
| 8 | `inference.pose` ONNX Pose (YOLO-Pose) | Inference | 20 | 4 | 5 | 84 | 89 | 71 | 80 | **78** |
| 9 | `measure.caliper` Caliper Edge | Measurement | 9 | 8 | 7 | 85 | 63 | 74 | 90 | **78** |
| 10 | `image.crop` Crop Image | Preprocess | 0 | 0 | 5 | 61 | 92 | 79 | 90 | **77** |
| 11 | `feature.line` Line Finder | Feature | 8 | 0 | 9 | 72 | 77 | 72 | 90 | **76** |
| 12 | `inference.segmentation` ONNX Semantic Segmentation | Inference | 14 | 2 | 8 | 82 | 84 | 77 | 80 | **76** |
| 13 | `feature.circle` Circle Finder | Feature | 8 | 0 | 10 | 72 | 73 | 73 | 90 | **75** |
| 14 | `feature.edgeSubpixel` Subpixel Edge | Feature | 9 | 8 | 7 | 83 | 62 | 74 | 80 | **75** |
| 15 | `image.acquire` Acquire Image | Acquisition | 7 | 2 | 6 | 71 | 74 | 73 | 90 | **75** |
| 16 | `inference.detection` ONNX Detection | Inference | 16 | 5 | 3 | 76 | 82 | 60 | 80 | **75** |
| 17 | `defect.referenceCompare` Reference Defect Compare | Defect | 12 | 6 | 8 | 85 | 89 | 81 | 90 | **74** |
| 18 | `inference.anomaly` ONNX Anomaly Score | Inference | 12 | 2 | 8 | 80 | 68 | 67 | 80 | **74** |
| 19 | `recognition.ocr` OCR | Recognition | 11 | 14 | 6 | 85 | 87 | 69 | 80 | **74** |
| 20 | `calibration.handEye2D` 2D Hand-Eye Calibration | Calibration | 8 | 2 | 8 | 71 | 81 | 62 | 80 | **73** |
| 21 | `contour.extract` Contour Extract | Contour | 7 | 6 | 4 | 74 | 62 | 79 | 80 | **73** |
| 22 | `image.synthetic` Synthetic Image | Acquisition | 14 | 4 | 1 | 70 | 61 | 81 | 90 | **73** |
| 23 | `region.fromRoi` Rasterize ROI | Region | 2 | 0 | 5 | 59 | 84 | 73 | 90 | **73** |
| 24 | `calibration.cameraIntrinsics` Camera Intrinsic Calibration | Calibration | 3 | 0 | 5 | 64 | 82 | 72 | 80 | **72** |
| 25 | `image.register` Register Image | Matching | 8 | 0 | 5 | 71 | 72 | 61 | 90 | **72** |
| 26 | `image.undistort` Undistort Image | Preprocess | 1 | 0 | 1 | 68 | 67 | 80 | 80 | **72** |
| 27 | `flow.result` Result / Disposition | Flow | 0 | 0 | 2 | 78 | 52 | 64 | 90 | **71** |
| 28 | `image.difference` Image Difference | Defect | 3 | 3 | 9 | 65 | 64 | 77 | 90 | **71** |
| 29 | `feature.edge` Edge / Canny | Feature | 4 | 0 | 6 | 64 | 61 | 80 | 90 | **70** |
| 30 | `flow.ifBoolean` Boolean If | Flow | 0 | 0 | 1 | 78 | 52 | 59 | 90 | **70** |
| 31 | `image.threshold` Threshold | Preprocess | 6 | 5 | 2 | 67 | 58 | 74 | 90 | **70** |
| 32 | `measure.colorDifference` Color Difference (Delta E 76) | Measurement | 3 | 0 | 8 | 62 | 63 | 77 | 90 | **70** |
| 33 | `value.range` Range Check | Value | 3 | 0 | 1 | 78 | 52 | 59 | 90 | **70** |
| 34 | `contour.fromPoints` Contour from Points | Contour | 2 | 0 | 4 | 68 | 62 | 65 | 90 | **69** |
| 35 | `image.colorConvert` Convert Color Space | Preprocess | 1 | 3 | 4 | 68 | 52 | 77 | 90 | **69** |
| 36 | `logic.boolean` Boolean Logic | Logic | 1 | 4 | 1 | 78 | 52 | 57 | 90 | **69** |
| 37 | `region.fromRange` Range Segmentation | Region | 10 | 5 | 5 | 78 | 45 | 74 | 80 | **69** |
| 38 | `robot.executeTarget` Execute Robot Target | Robot | 8 | 3 | 7 | 72 | 58 | 60 | 90 | **69** |
| 39 | `value.arithmetic` Arithmetic | Value | 1 | 4 | 1 | 78 | 52 | 56 | 90 | **69** |
| 40 | `flow.forEach` For Each Detection | Flow | 1 | 0 | 10 | 78 | 46 | 57 | 90 | **68** |
| 41 | `flow.if` If | Flow | 2 | 6 | 1 | 78 | 52 | 53 | 85 | **68** |
| 42 | `image.reduceDomain` Reduce Image Domain | Region | 0 | 0 | 3 | 68 | 55 | 70 | 90 | **68** |
| 43 | `region.filter` Filter Regions | Region | 3 | 6 | 5 | 68 | 52 | 70 | 90 | **68** |
| 44 | `region.fromColorRange` Color Range Region | Region | 7 | 3 | 5 | 71 | 47 | 72 | 90 | **68** |
| 45 | `region.morphology` Region Morphology | Region | 4 | 4 | 5 | 68 | 52 | 71 | 90 | **68** |
| 46 | `text.validate` Validate Text | Recognition | 9 | 5 | 5 | 75 | 58 | 60 | 76 | **68** |
| 47 | `flow.join` Join | Flow | 0 | 0 | 0 | 78 | 40 | 61 | 90 | **67** |
| 48 | `region.connectedComponents` Connected Regions | Region | 0 | 0 | 5 | 68 | 52 | 71 | 86 | **67** |
| 49 | `value.number` Number Constant | Value | 1 | 0 | 2 | 78 | 45 | 56 | 90 | **67** |
| 50 | `device.waitTag` Wait Device Tag | Device | 7 | 8 | 8 | 73 | 46 | 57 | 90 | **66** |
| 51 | `flow.loopEnd` Loop End | Flow | 0 | 0 | 0 | 78 | 40 | 56 | 90 | **66** |
| 52 | `flow.switch` Switch | Flow | 1 | 0 | 1 | 78 | 56 | 56 | 64 | **66** |
| 53 | `image.contrastNormalize` Normalize Contrast | Preprocess | 3 | 0 | 3 | 68 | 52 | 64 | 90 | **66** |
| 54 | `image.dynamicThreshold` Dynamic Defect Threshold | Defect | 3 | 3 | 5 | 63 | 52 | 70 | 90 | **66** |
| 55 | `image.morphology` Morphology | Preprocess | 6 | 10 | 1 | 67 | 52 | 66 | 90 | **66** |
| 56 | `inference.classification` ONNX Classification | Inference | 15 | 2 | 4 | 74 | 58 | 63 | 64 | **66** |
| 57 | `contour.filter` Contour Filter | Contour | 10 | 3 | 2 | 68 | 52 | 64 | 80 | **65** |
| 58 | `coordinate.transformCompose` Compose Transform | Coordinate | 0 | 0 | 1 | 68 | 52 | 58 | 90 | **65** |
| 59 | `flow.merge` Flow Merge | Flow | 0 | 0 | 0 | 78 | 41 | 56 | 80 | **65** |
| 60 | `flow.try` Try / Catch | Flow | 2 | 0 | 3 | 78 | 48 | 56 | 72 | **65** |
| 61 | `image.filter` Image Filter | Preprocess | 4 | 2 | 1 | 68 | 45 | 73 | 80 | **65** |
| 62 | `image.illuminationNormalize` Normalize Illumination | Preprocess | 3 | 0 | 1 | 68 | 52 | 64 | 82 | **65** |
| 63 | `image.sharpen` Sharpen Image | Preprocess | 2 | 0 | 1 | 68 | 52 | 64 | 82 | **65** |
| 64 | `region.boolean` Region Boolean | Region | 1 | 4 | 5 | 68 | 52 | 71 | 70 | **65** |
| 65 | `region.boundary` Region Boundary | Region | 2 | 2 | 5 | 68 | 41 | 71 | 90 | **65** |
| 66 | `region.convexHull` Region Convex Hull | Region | 1 | 0 | 5 | 68 | 41 | 71 | 90 | **65** |
| 67 | `region.fillHoles` Fill Region Holes | Region | 0 | 0 | 5 | 68 | 52 | 70 | 74 | **65** |
| 68 | `region.properties` Region Properties | Region | 1 | 0 | 10 | 68 | 52 | 57 | 90 | **65** |
| 69 | `region.toDetections` Region to Detections | Region | 0 | 0 | 3 | 68 | 52 | 57 | 90 | **65** |
| 70 | `robot.currentPose` Robot Current Pose | Robot | 2 | 0 | 11 | 68 | 52 | 58 | 90 | **65** |
| 71 | `collection.detections.at` Detection At Index | Measurement | 1 | 0 | 14 | 74 | 44 | 56 | 80 | **64** |
| 72 | `device.readTag` Read Device Tag | Device | 4 | 0 | 6 | 68 | 52 | 57 | 80 | **64** |
| 73 | `device.writeTag` Write Device Tag | Device | 5 | 4 | 6 | 68 | 44 | 61 | 90 | **64** |
| 74 | `device.writeVisionResult` Write Vision Result | Device | 7 | 0 | 5 | 66 | 52 | 57 | 90 | **64** |
| 75 | `flow.parallel` Parallel | Flow | 0 | 0 | 0 | 78 | 40 | 47 | 85 | **64** |
| 76 | `frameset.image` FrameSet Image | Acquisition | 1 | 0 | 6 | 68 | 55 | 65 | 70 | **64** |
| 77 | `image.perspective` Perspective Correction | Preprocess | 10 | 0 | 1 | 65 | 58 | 58 | 80 | **64** |
| 78 | `image.topHat` White Top Hat | Preprocess | 2 | 0 | 1 | 68 | 41 | 64 | 90 | **64** |
| 79 | `image.warpAffine` Warp Affine Image | Preprocess | 3 | 2 | 2 | 55 | 56 | 72 | 90 | **64** |
| 80 | `measure.blob` Largest Blob | Measurement | 1 | 0 | 13 | 56 | 55 | 72 | 90 | **64** |
| 81 | `measure.contourDeviation` Sampled Contour Deviation | Measurement | 6 | 0 | 8 | 65 | 53 | 57 | 90 | **64** |
| 82 | `measure.fitEllipse` Ellipse Fit | Measurement | 2 | 5 | 8 | 63 | 58 | 71 | 68 | **64** |
| 83 | `measure.roundness` Sampled Roundness | Measurement | 2 | 0 | 10 | 68 | 48 | 57 | 90 | **64** |
| 84 | `measure.straightness` Sampled Straightness | Measurement | 1 | 0 | 6 | 68 | 46 | 58 | 90 | **64** |
| 85 | `region.skeleton` Region Skeleton | Region | 1 | 0 | 5 | 68 | 41 | 72 | 78 | **64** |
| 86 | `region.transform` Transform Region | Region | 2 | 0 | 5 | 58 | 52 | 71 | 90 | **64** |
| 87 | `text.match` Match Text | Recognition | 6 | 4 | 3 | 63 | 57 | 57 | 90 | **64** |
| 88 | `calibration.planar` Planar Calibration | Calibration | 6 | 3 | 3 | 62 | 49 | 64 | 90 | **63** |
| 89 | `camera.syncCapture` Synchronized Capture | Acquisition | 4 | 0 | 5 | 68 | 42 | 59 | 90 | **63** |
| 90 | `collection.detections.count` Count Detections | Measurement | 0 | 0 | 1 | 74 | 41 | 51 | 86 | **63** |
| 91 | `contour.properties` Contour Properties | Contour | 2 | 5 | 10 | 63 | 52 | 64 | 80 | **63** |
| 92 | `measure.lineSpacing` Line Spacing | Measurement | 3 | 2 | 7 | 68 | 53 | 57 | 72 | **63** |
| 93 | `measure.wallThickness` Circular Wall Thickness | Measurement | 2 | 0 | 10 | 68 | 45 | 57 | 90 | **63** |
| 94 | `output.exportResult` Export Result | Output | 3 | 2 | 2 | 68 | 56 | 52 | 78 | **63** |
| 95 | `output.httpRequest` HTTP Request | Output | 6 | 5 | 3 | 63 | 65 | 56 | 72 | **63** |
| 96 | `collection.detections.filter` Filter Detections | Measurement | 6 | 0 | 2 | 74 | 44 | 56 | 72 | **62** |
| 97 | `image.bottomHat` Black Bottom Hat | Preprocess | 2 | 0 | 1 | 68 | 41 | 64 | 78 | **62** |
| 98 | `image.morphGradient` Morphological Gradient | Preprocess | 2 | 0 | 1 | 68 | 41 | 64 | 78 | **62** |
| 99 | `measure.regionIntensity` Region Gray Statistics | Defect | 0 | 0 | 10 | 53 | 52 | 70 | 90 | **62** |
| 100 | `recognition.barcode` Barcode / QR Decode | Recognition | 3 | 4 | 5 | 62 | 57 | 56 | 76 | **62** |
| 101 | `region.fromThreshold` Threshold Region | Region | 2 | 0 | 5 | 57 | 47 | 71 | 90 | **62** |
| 102 | `text.regex` Validate Text with Regex | Recognition | 6 | 0 | 3 | 59 | 52 | 58 | 90 | **62** |
| 103 | `image.blur.gaussian` Gaussian Blur | Preprocess | 3 | 0 | 1 | 51 | 52 | 73 | 90 | **61** |
| 104 | `image.threshold.adaptive` Adaptive Threshold | Preprocess | 4 | 2 | 1 | 56 | 52 | 67 | 80 | **61** |
| 105 | `match.template` Template Match | Matching | 6 | 2 | 7 | 66 | 41 | 64 | 80 | **61** |
| 106 | `measure.fitCircle` Fit Circle | Measurement | 0 | 0 | 6 | 68 | 41 | 56 | 80 | **61** |
| 107 | `measure.fitLine` Fit Line | Measurement | 0 | 0 | 6 | 68 | 41 | 57 | 80 | **61** |
| 108 | `measure.grayHistogram` Gray Histogram and Percentiles | Measurement | 2 | 0 | 12 | 57 | 54 | 57 | 90 | **61** |
| 109 | `measure.parallelism` Line Parallelism | Measurement | 0 | 0 | 3 | 68 | 41 | 57 | 82 | **61** |
| 110 | `measure.pointLineDistance` Point to Line Distance | Measurement | 1 | 0 | 2 | 68 | 52 | 51 | 68 | **61** |
| 111 | `output.saveImage` Save Image | Output | 2 | 0 | 2 | 68 | 59 | 56 | 52 | **61** |
| 112 | `collection.detections.sort` Sort Detections | Measurement | 2 | 5 | 1 | 74 | 44 | 51 | 60 | **60** |
| 113 | `geometry.distance` Point Distance | Geometry | 0 | 0 | 1 | 68 | 52 | 52 | 66 | **60** |
| 114 | `image.flatFieldCorrect` Flat-field Correction | Defect | 3 | 2 | 1 | 51 | 53 | 63 | 90 | **60** |
| 115 | `measure.perpendicularity` Line Perpendicularity | Measurement | 0 | 0 | 3 | 68 | 41 | 57 | 74 | **60** |
| 116 | `coordinate.frameResolve` Resolve Frame Transform | Coordinate | 2 | 0 | 2 | 68 | 52 | 57 | 50 | **59** |
| 117 | `coordinate.poseToTransform` Pose to Transform | Coordinate | 1 | 0 | 1 | 68 | 52 | 57 | 50 | **59** |
| 118 | `coordinate.transformCoordinatePose` Transform Coordinate Pose | Coordinate | 0 | 0 | 1 | 68 | 52 | 57 | 50 | **59** |
| 119 | `flow.delay` Delay | Flow | 1 | 0 | 1 | 78 | 43 | 51 | 48 | **59** |
| 120 | `geometry.angle` Line Angle | Geometry | 0 | 0 | 1 | 68 | 52 | 56 | 50 | **59** |
| 121 | `image.channel` Channel Extract | Preprocess | 1 | 9 | 1 | 53 | 52 | 65 | 80 | **59** |
| 122 | `image.grayscale` Grayscale | Preprocess | 1 | 3 | 1 | 50 | 52 | 64 | 90 | **59** |
| 123 | `image.resize` Resize Image | Preprocess | 3 | 4 | 1 | 54 | 52 | 59 | 80 | **59** |
| 124 | `image.rotate` Rotate Image | Preprocess | 2 | 0 | 1 | 49 | 52 | 64 | 90 | **59** |
| 125 | `image.threshold.otsu` Otsu Threshold | Preprocess | 1 | 0 | 2 | 49 | 52 | 63 | 90 | **59** |
| 126 | `measure.concentricity` Circle Centre Offset | Measurement | 0 | 0 | 3 | 68 | 42 | 57 | 66 | **59** |
| 127 | `robot.j4TcpCompensation` J4 / TCP Compensation | Robot | 5 | 0 | 4 | 68 | 52 | 57 | 50 | **59** |
| 128 | `coordinate.distance` Coordinate Distance | Coordinate | 0 | 0 | 2 | 68 | 52 | 51 | 50 | **58** |
| 129 | `coordinate.frameTree` Frame Tree | Coordinate | 1 | 0 | 1 | 68 | 40 | 67 | 50 | **58** |
| 130 | `coordinate.transformPoint` Transform Point | Coordinate | 0 | 0 | 3 | 68 | 52 | 51 | 50 | **58** |
| 131 | `coordinate.transformPose` Transform Pose | Coordinate | 0 | 0 | 1 | 68 | 52 | 52 | 50 | **58** |
| 132 | `geometry.intersection` Line Intersection | Geometry | 0 | 0 | 1 | 68 | 52 | 52 | 50 | **58** |
| 133 | `robot.guidance2d` Robot Guidance 2D | Robot | 6 | 3 | 4 | 64 | 52 | 58 | 50 | **58** |
| 134 | `coordinate.transformCoordinatePoint` Transform Coordinate Point | Coordinate | 0 | 0 | 1 | 68 | 52 | 56 | 40 | **57** |
| 135 | `coordinate.transformInverse` Inverse Transform | Coordinate | 0 | 0 | 1 | 68 | 52 | 51 | 40 | **56** |
| 136 | `coordinate.transformLine` Transform Line | Coordinate | 0 | 0 | 3 | 68 | 52 | 51 | 40 | **56** |
| 137 | `image.blur.median` Median Blur | Preprocess | 1 | 0 | 1 | 47 | 52 | 63 | 80 | **56** |
| 138 | `measure.circleSpacing` Circle Spacing | Measurement | 0 | 0 | 3 | 49 | 57 | 56 | 76 | **56** |
| 139 | `measure.rotatedRect` Rotated Rectangle | Measurement | 1 | 0 | 5 | 56 | 57 | 59 | 50 | **56** |
| 140 | `coordinate.point` Coordinate Point | Coordinate | 4 | 3 | 1 | 68 | 40 | 51 | 50 | **55** |
| 141 | `coordinate.pose` Coordinate Pose | Coordinate | 5 | 3 | 1 | 68 | 40 | 50 | 49 | **55** |
| 142 | `coordinate.transformConstant` Constant Transform | Coordinate | 6 | 4 | 1 | 57 | 40 | 62 | 66 | **55** |

## Interpretation

- In this baseline snapshot, no node reaches 90+: the top band vs HALCON/Cognex is limited by product depth (OBB/depth/3D, fisheye/telecentric, OCR font training, and full variation-model workflows were absent - training nodes are explicitly out of scope per user).
- The long 60-69 band is mostly complete-but-thin utility/preprocess/coordinate nodes and a few complex nodes with weak direct evidence (`inference.classification`, `match.template`, `calibration.planar`).
- Lowest scores are evidence-thin narrow transforms, not known-broken nodes; prioritise them only if direct tests/debug outputs matter, not for feature work.

## Follow-up implementation: reference defect model

After the baseline audit, `defect.referenceCompare` was extended to accept one to five known-good image inputs. With multiple samples it aligns extra references by integer translation inside the effective inspection domain, then builds a per-pixel mean and sample-standard-deviation model using online Welford accumulation. `variationK` now uses that measured sample deviation; single-reference workflows retain the prior local-texture estimate. The model keeps the full source grayscale intensity range, including 16-bit images, and reports reference count and sample alignment errors. An optional registration-error gate rejects poorly aligned references or inspected images.

The earlier **74** score and `<=74` cap describe only the baseline snapshot; this implementation has not yet been rescored against the full audit rubric. Remaining product gaps include persistent/cached model assets, subpixel/rotation/scale registration, and a larger managed sample set.
Usage and limits are documented in [Reference Defect Compare](reference-defect-comparison.md).

## Follow-up implementation: planar calibration

`calibration.planar` now normalizes image and target coordinates and solves the overdetermined homography with column-pivoted QR instead of normal equations. The node returns per-pair residual JSON and the worst pair index, and can fail on configured RMSE or single-point error limits. The calibration workspace uses the same solver, exposes the same quality gates before saving or applying a calibration, and validates coordinate frames, units, finite verification points, and input size limits. The baseline **63** score has not been rerated against the full audit rubric.

Usage and limitations are documented in [Planar Calibration](planar-calibration.md). Lens distortion correction, automatic outlier rejection, and binding runtime solves directly to immutable calibration assets remain open product gaps.

## Follow-up implementation: image classification

`inference.classification` adds an optional minimum-probability gate and an `accepted` output for workflow routing. The gate is available only when scores represent probabilities (Softmax logits or YOLO26-cls probabilities); disabled by default, it preserves existing classifier behavior. `topK` is explicitly limited to 1–100 at execution. The baseline **66** score has not been rerated.

Usage and limits are documented in [ONNX Image Classification](onnx-classification.md). Multi-image batching, multi-crop aggregation, and calibration of classifier confidence remain open product gaps.

## Follow-up implementation: template matching

`match.template` already supports a cached NCC pyramid, optional subpixel translation and angle search, discrete uniform scale samples, search ROI, template masks, and multiple targets. Multi-target mode now suppresses duplicate matches in score order using configurable axis-aligned box IoU, while the default one-target behavior remains unchanged. The baseline **61** score is historical and has not been rerated.

Usage and limits are documented in [Template Matching](template-matching.md). Continuous scale models, polarity invariance, clutter handling, and deformation tolerance remain in the separate shape-matching family.

## Follow-up implementation: camera intrinsics

The camera-intrinsics node and wizard now expose a fitted-K per-view RMS gate in addition to the planar pre-fit and overall model gates. Since that pose residual omits JLVision's native polynomial distortion, the node rejects this gate when distortion is active and does not mark pinhole-derived views as outliers in that case. Typed outputs now expose worst pose-view errors, coverage, and outlier indexes. Input limits bound calibration sets to 500 views and 100000 total correspondences. The baseline **72** score remains historical and has not been rerated.

Usage and model limits are documented in [Camera Intrinsic Calibration](camera-intrinsics.md). Fisheye and telecentric model support remains a significant gap.

## Follow-up implementation: planar hand-eye calibration

`calibration.handEye2D` now checks circular rotation coverage and translation span in both robot and vision samples, limits inputs to 1000 poses, validates frame/unit metadata, and exposes a configurable angular-to-translation weight while preserving the prior default. Per-sample residuals now identify separate worst translation and angle samples; angle-gate errors point to the correct sample. The baseline score has not been rerated.

Usage and scope are documented in [2D Hand-Eye Calibration](hand-eye-2d.md). The node remains planar SE(2); 3D robot/camera hand-eye calibration is still a substantial feature gap.

