# ONNX Anomaly Inference

`inference.anomaly` accepts an image-level score, a spatial anomaly map, or both from one ONNX model run.

## Selecting outputs

- For a one-output model, leave `scoreOutputName` and `mapOutputName` empty. `outputName` selects one output; if it is empty, the first output in model order is used. Scalar output keeps the existing score-only behavior. A spatial output emits a heatmap and uses its peak as the score.
- For a model with separate image and pixel outputs, set both `scoreOutputName` and `mapOutputName`. The score output must contain one value; the map output must be `[1,1,H,W]`, `[1,H,W]`, `[H,W]`, `[1,H,W,1]`, or `[H,W,1]`.
- If the map layout is ambiguous because a spatial dimension is one, set `mapLayout` to `channelsFirst` or `channelsLast`. The score and map output names must be different.

The image-level `threshold` controls `isAnomaly` when a scalar score is selected. If there is no scalar output, the map peak controls that result. `useSeparateMapThreshold` lets pixel-area statistics use `mapThreshold` while leaving the image-level threshold independent. With it off, the map inherits `threshold` for backwards compatibility. `thresholdMode=otsu` derives a threshold from the map and affects map pixel statistics only; scalar scores always use the manual threshold.

## Outputs

`score` is the scalar image score when present, otherwise the map peak. `mapPeak`, `mapMean`, `mapMin`, and `mapMax` expose raw map statistics. When a map is selected, the node also returns a normalized heatmap resized to the source image, peak coordinates in source pixels, the fraction of pixels at or above the effective map threshold, and the suggested Otsu threshold. The `result` JSON includes the selected output names and tensor shapes, score source, thresholds, map statistics, and decision.

For legacy single-output workflows, the JSON and execution metadata retain `threshold`, `mean`, `min`, `max`, and `outputShape`; the named multi-output path also reports `scoreOutputShape` and `mapOutputShape` separately.

This node performs inference and thresholding; it does not train an anomaly model or calibrate thresholds from a dataset. For dependable production thresholds, validate the exported model and threshold against representative good and defective samples.
