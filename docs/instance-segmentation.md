# ONNX Instance Segmentation

`inference.instanceSegmentation` consumes two ONNX outputs: a detection tensor and a mask-prototype tensor. It supports the Ultralytics YOLO-Seg raw head and the YOLO26 end-to-end head.

| Layout | Detection rows | NMS in `nmsMode=auto` |
|---|---|---|
| `yolo26-seg` | Raw `[1,4+classCount+maskDim,N]` (or row-major equivalent); box, class scores, mask coefficients | Class-aware NMS runs in the node |
| `xyxy` | End-to-end `[1,N,6+maskDim]`; `x1,y1,x2,y2,score,classId,maskCoefficients` | Skipped; the output is already a sparse end-to-end detection set |

Both layouts require prototypes shaped `[1,maskDim,protoHeight,protoWidth]`. `outputName` selects detections and `protoOutputName` selects prototypes; leave both empty to use the model's first two outputs. Set `coordinates=normalized` only when the model emits normalized coordinates. `letterbox=true` reverses Ultralytics-style padding for boxes and masks.

`mask` is a byte class-id image: zero is background and class IDs are stored as `classId+1`, so class IDs must be in 0..254. Since candidates are score-sorted, the highest-scoring retained instance supplies composite pixels where masks overlap. The `instances` JSON includes each object's box, score, class, and area. With `includeMaskRuns=true`, it also includes its independent binary mask as `maskSize: [height,width]` plus `maskRuns`: row-major run lengths alternating background and foreground, starting with a background run (which may be zero). This keeps overlapping masks available. Set `includeMaskRuns=false` to reduce workflow payload size while retaining the composite image mask.

Automatic NMS is recommended for raw heads and skipped for end-to-end rows. `nmsMode=on` or `off` overrides that choice. `maxCount`, `confidenceThreshold`, `nmsThreshold`, and `maskThreshold` control filtering and mask composition. OBB models and embedded NMS exports with a different output contract are not supported by this node.
