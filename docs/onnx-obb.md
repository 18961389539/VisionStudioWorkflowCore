# ONNX Oriented Box Detection

`inference.obb` runs Ultralytics YOLOv8-OBB and YOLO26-OBB ONNX exports and returns rotated rectangles in source-image pixels.

## Output layouts

- `yolo` reads raw heads in `[1, C, N]`, `[1, N, C]`, or `[N, C]` form. Channels are `cx, cy, width, height`, one score per class, then the angle in radians. It handles YOLOv8 angle outputs and YOLO26 direct angle outputs. For 3D tensors, `tensorOrder=auto` treats the smaller dimension as channels; set `channelsFirst` or `channelsLast` if your model has an unusual class count or low-resolution input.
- `xywhr` reads YOLO26 end-to-end rows `[cx, cy, width, height, score, classId, angle]`, usually shaped `[1, 300, 7]`. The boxes are center/width/height values; the final angle is in radians.

Choose `coordinates=pixels` for standard Ultralytics exports. Choose `normalized` only if the model output uses 0–1 coordinates. `letterbox=true` matches Ultralytics' gray 114 padding and maps the center and dimensions back to the source image. When letterbox is off, the source and model input must have matching aspect ratios so a rotated rectangle remains rectangular.

## Suppression and outputs

`nmsMode=auto` runs class-aware rotated NMS for raw heads and skips it for YOLO26 end-to-end output. `on` and `off` explicitly override that choice. The overlap calculation uses the exact intersection of the two oriented quadrilaterals, rather than their axis-aligned enclosures.

The node emits:

- `detections`: a `DetectionList`; `rectangle` uses center X/Y, width, height, and angle in degrees. `x`, `y`, `width`, and `height` on the detection item are the axis-aligned enclosing bounds.
- `overlay`: a BGR image with oriented quadrilaterals, class labels, scores, and angles.
- `result`: JSON containing class ID/label, score, center, dimensions, radians and degrees, axis-aligned bounds, and four source-image corner points.
- `count` and `diagnostics`.

Set `labels` to comma-separated class names in model order. With no list, the node uses `class_N` labels. YOLO images should use `swapRB=true`, which is the default for this node.

Embedded-NMS exports, training, track ID management, rotated crops, and model metadata class-name extraction are not implemented in this node. For keypoints use `inference.pose`; for instance masks use `inference.instanceSegmentation`.
