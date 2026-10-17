# ONNX Image Classification

`inference.classification` runs a single image through an ONNX model and returns the top classes, their scores, and the winning label/index. Configure the input dimensions and normalization to match the model export. Use `swapRB=true` for RGB-trained models loaded through OpenCV's BGR image path.

## Output layout and labels

- `layout=scores` accepts `[1,C]` and `[1,C,1,1]` outputs. `applySoftmax=true` converts logits to probabilities using a numerically stable softmax; disable it only when the model already emits scores in the units you need.
- `layout=yolo26` is for Ultralytics YOLO26 classification exports whose graph already applies Softmax. The node validates that values lie in `[0,1]` and sum approximately to one.
- `labels` is an optional comma-separated list in model class order. Its item count must match `C`, or leave it empty to use numeric class indexes.
- `topK` accepts values from 1 to 100 and returns no more than the class count.

## Confidence routing

Set `minimumConfidence` between 0 and 1 to get an `accepted` boolean. The node returns `accepted=false` when the winning probability is below the threshold; this is a normal classification result and does not throw an execution error. A value of 0 disables the gate and returns `accepted=true`. The gate requires probabilities, so use `applySoftmax=true` with `layout=scores`, or use `layout=yolo26`.

Connect `accepted` to a branch node when low-confidence images should take a review, retry, or reject path. The JSON `result` also includes `minimumConfidence` and `accepted` for run records.

The node expects one image at a time and does not train or evaluate models. It does not aggregate crops or multiple views; combine those with explicit workflow branches and collection nodes.
