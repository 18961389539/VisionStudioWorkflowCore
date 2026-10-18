# ONNX Image Classification

`inference.classification` runs an image through an ONNX model and returns the top classes, their scores, and the winning label/index. Configure the input dimensions and normalization to match the model export. Use `swapRB=true` for RGB-trained models loaded through OpenCV's BGR image path.

## Output layout and labels

- `layout=scores` accepts `[1,C]` and `[1,C,1,1]` outputs. `applySoftmax=true` converts logits to probabilities using a numerically stable softmax; disable it only when the model already emits scores in the units you need.
- `layout=yolo26` is for Ultralytics YOLO26 classification exports whose graph already applies Softmax. The node validates that values lie in `[0,1]` and sum approximately to one.
- `labels` is an optional comma-separated list in model class order. Its item count must match `C`, or leave it empty to use numeric class indexes.
- `topK` accepts values from 1 to 100 and returns no more than the class count.

## Confidence routing

Set `minimumConfidence` between 0 and 1 to get an `accepted` boolean. The node returns `accepted=false` when the winning probability is below the threshold; this is a normal classification result and does not throw an execution error. A value of 0 disables the gate and returns `accepted=true`. The gate requires probabilities, so use `applySoftmax=true` with `layout=scores`, or use `layout=yolo26`.

Connect `accepted` to a branch node when low-confidence images should take a review, retry, or reject path. The JSON `result` also includes `minimumConfidence` and `accepted` for run records.

## Multi-crop views

`multiCrop=five` evaluates center and four corner crops whose aspect ratio matches the model input. Each crop uses `viewCropFraction` of the largest matching crop, defaulting to 0.9. If `viewCropFraction=1` and the source already matches the model aspect ratio, there are no distinct crops and the node reports a configuration error. The node averages per-view probabilities when softmax is enabled; with raw scores, it averages those scores. `multiCrop=ten` also evaluates a horizontal flip of each crop and is appropriate only when horizontal orientation does not change the class. Result JSON records each view's winning class and score. Every view runs as a separate ONNX call, so ten-view inference takes roughly ten times the single-view work. The default `none` keeps one-call behavior.

The node expects one source image at a time and does not train or evaluate models. Use a representative validation set to choose aggregation and confidence settings.
