# Reference Defect Compare

`defect.referenceCompare` compares an inspected grayscale image with one or more known-good references. Use the optional `reference2`–`reference5` inputs to build a per-pixel normal-variation model from up to five images.

## Wiring

- Connect the inspected `image` or `domainImage` and the first known-good image to `reference`.
- Connect additional good images to `reference2`–`reference5`. All images must have the same width, height, and pixel depth. Unconnected optional inputs are ignored.
- Add an optional `region` input or pass a domain image to restrict inspection. Registration and illumination fitting use this effective inspection area.

## Multi-reference model

When two or more references are connected, the node optionally aligns each extra reference to the first one, then calculates the per-pixel mean and unbiased sample standard deviation. Pixels outside an aligned sample's image bounds do not contribute to that pixel's sample count.

With `variationK > 0`, the defect threshold at each pixel is:

```text
threshold + variationK × sampleStandardDeviation
```

With one reference, the node retains the local-texture standard-deviation estimate controlled by `variationWindow`. With `variationK = 0`, the node still compares a multi-reference image against the learned mean, but does not add a variation allowance.

`registrationTolerance` searches integer translations within the requested pixel range. It applies to extra references and to the inspected image. Registration scoring is sampled to shortlist shifts and then re-evaluated at full resolution inside the inspection domain. Optional illumination compensation normalizes extra references to the first sample and fits the model to the inspected image. `maxRegistrationError` can reject alignment whose mean absolute grayscale difference exceeds the configured limit; it requires `registrationTolerance > 0`.

Threshold and registration error use the source grayscale intensity units. UInt16 images retain their full intensity range. `referenceSampleCount` reports the number of connected references used; the summary includes the inspected-image registration error and extra-reference alignment errors.

## Current limits

- Up to five image inputs; the per-pixel model is rebuilt for each node execution and is not stored as a trained workflow asset.
- Registration handles integer X/Y translation only. It does not handle subpixel shift, rotation, scale, or local deformation.
- A nonzero registration-error gate raises a node execution error when the limit is exceeded, which should be treated as an inspection-system fault rather than an ordinary NG result.
