# Template Matching

`match.template` uses a cached NCC pyramid to locate a grayscale template in one image. It supports subpixel translation, optional angle search, optional discrete uniform scale samples, search ROI, template ROI mask, and up to 50 accepted matches. The first accepted result remains available through the single-result outputs; all accepted results are returned through `detections`.

## Multiple matches

When `maxTargets` is greater than one, candidates are processed from highest score to lowest. `duplicateOverlapThreshold` defaults to `0.3` and suppresses a candidate when its axis-aligned bounding box has IoU at or above that value with an already accepted match. This removes nearby copies of the same textured target, including rotated matches whose reported box encloses the rotated template. Set the threshold to `0` to disable overlap suppression. `minCenterDistance` can also remove candidates closer than a configured number of pixels.

The default `maxTargets=1` keeps existing workflows unchanged. Multiple-target detection can return fewer than the configured count when remaining candidates overlap accepted boxes, fall below `minScore`, or lie outside `searchRoi`.

## Scale and method limits

The optional scale range resamples the template at discrete percentages and runs NCC at each scale. It is not a trained continuous-scale model. Use `match.shape` when the target needs continuous scale handling, ignored polarity, clutter scoring, or deformation tolerance. `sqdiff-normed` verifies NCC proposals using native squared-difference scoring.
